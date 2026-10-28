using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoWin.Services;

internal sealed partial record SipGatewayDeploymentProfile
{
    private const int MaximumDeploymentBytes = 65536;
    private static readonly UTF8Encoding StrictDeploymentUtf8 = new(false, true);
    private static readonly JsonSerializerOptions DeploymentSettingsJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 64
    };

    private static InvalidDataException InvalidDeployment() =>
        new("部署配置无效，未导入；请检查版本、字段和独立凭据。");

    private static async Task<SipGatewayDeploymentProfile> ReadProfileAsync(string path)
    {
        // Reading max+1, rather than trusting a prior file-length check, also
        // bounds files that change while an already-open writer owns a handle.
        var buffer = new byte[MaximumDeploymentBytes + 1];
        try
        {
            int count = 0;
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                while (count < buffer.Length)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(count)).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
            }
            if (count is <= 0 or > MaximumDeploymentBytes) throw InvalidDeployment();
            StrictDeploymentUtf8.GetCharCount(buffer, 0, count);
            int start = count >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf ? 3 : 0;
            using var document = JsonDocument.Parse(buffer.AsMemory(start, count - start), new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidDeployment();
            ValidateDeploymentJsonTree(root);

            if (!root.TryGetProperty("Version", out var version))
            {
                // An incomplete new envelope must not downgrade into an old
                // shared-secret draft merely by losing/changing its version key.
                if (root.EnumerateObject().Any(property =>
                    property.Name.Equals("Version", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("Credentials", StringComparison.OrdinalIgnoreCase)))
                    throw InvalidDeployment();
                var legacy = root.Deserialize<SipGatewayDeploymentProfile>() ?? throw InvalidDeployment();
                if (legacy.Settings == null) throw InvalidDeployment();
                // JsonIgnore on the existing public record still prevents old
                // files from supplying either new peer role. Invalid legacy
                // public settings remain editable, never implicitly enabled.
                return legacy with { Settings = SanitizeImportedSettings(legacy.Settings) };
            }

            RequireDeploymentMembers(root, ["Version", "Settings", "Credentials"]);
            if (!DeploymentVersionIsTwo(version) || !root.TryGetProperty("Settings", out var settingsJson) ||
                !root.TryGetProperty("Credentials", out var credentialsJson)) throw InvalidDeployment();
            if (settingsJson.ValueKind != JsonValueKind.Object ||
                !settingsJson.TryGetProperty("CredentialVersion", out var credentialVersion) ||
                !DeploymentVersionIsTwo(credentialVersion)) throw InvalidDeployment();

            // Inspect the real preferences/policy types, not a second hardcoded
            // copy of their field names. Newly added nullable policies and ACLs
            // therefore retain their actual type/JsonIgnore/nullability rules.
            ValidateDeploymentValue(settingsJson, typeof(SipGatewayPreferences), "Settings", null, new NullabilityInfoContext());
            var settings = settingsJson.Deserialize<SipGatewayPreferences>(DeploymentSettingsJson) ?? throw InvalidDeployment();
            RequireDeploymentProperty(settingsJson, "BindAddress");
            if (settings.UseTrunk)
                foreach (var name in new[] { "ServerAddress", "TrunkUsername", "PeerOutboundUsername", "PeerExpectedRealm" })
                    RequireDeploymentProperty(settingsJson, name);
            if (settings.HasLocalRegistrar) RequireDeploymentProperty(settingsJson, "Extension");

            var credentials = ReadDeploymentCredentials(credentialsJson, settings);
            settings = SanitizeImportedSettings(settings);
            // Pure validation only: no socket, route, backend or modem is opened.
            _ = BuildOptions(settings, credentials, allowEmptyDraft: true);
            return new(settings, credentials.MainPassword, credentials.Extensions,
                credentials.PeerInboundPassword, credentials.PeerOutboundPassword);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or NotSupportedException or FormatException or OverflowException)
        {
            // Do not retain an inner JSON/Unicode exception: its path or message
            // can contain an unknown property name or a secret-bearing token.
            throw InvalidDeployment();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static SipGatewayPreferences SanitizeImportedSettings(SipGatewayPreferences settings) => settings with
    {
        WasEnabled = false, LegacyDeploymentPath = null, LocalAccountEpochs = null, PeerCredentialEpoch = null
    };

    private static bool DeploymentVersionIsTwo(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var version) && version == 2;

    private static void RequireDeploymentProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out _)) throw InvalidDeployment();
    }

    private static void RequireDeploymentMembers(JsonElement element, IReadOnlyCollection<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            throw InvalidDeployment();
    }

    private static void ValidateDeploymentJsonTree(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                StrictDeploymentUtf8.GetByteCount(property.Name);
                if (!names.Add(property.Name)) throw InvalidDeployment();
                ValidateDeploymentJsonTree(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateDeploymentJsonTree(child);
        else if (element.ValueKind == JsonValueKind.String)
            StrictDeploymentUtf8.GetByteCount(element.GetString() ?? throw InvalidDeployment());
    }

    private static SipGatewayCredentials ReadDeploymentCredentials(JsonElement element, SipGatewayPreferences settings)
    {
        RequireDeploymentMembers(element, ["MainPassword", "Extensions", "PeerInboundPassword", "PeerOutboundPassword"]);
        var main = DeploymentSecret(element, "MainPassword");
        var inbound = DeploymentSecret(element, "PeerInboundPassword");
        var outbound = DeploymentSecret(element, "PeerOutboundPassword");
        var supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("Extensions", out var extraJson))
        {
            if (extraJson.ValueKind != JsonValueKind.Object) throw InvalidDeployment();
            foreach (var property in extraJson.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || !supplied.TryAdd(property.Name, property.Value.GetString()!))
                    throw InvalidDeployment();
            }
        }
        if (settings.UseTrunk ? string.IsNullOrEmpty(inbound) || string.IsNullOrEmpty(outbound) : inbound.Length != 0 || outbound.Length != 0)
            throw InvalidDeployment();

        var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings.HasLocalRegistrar)
        {
            if (string.IsNullOrEmpty(main)) throw InvalidDeployment();
            foreach (var account in settings.Extensions ?? [])
            {
                if (!supplied.TryGetValue(account.Username, out var secret) || string.IsNullOrEmpty(secret) ||
                    !extras.TryAdd(account.Username, secret)) throw InvalidDeployment();
            }
        }
        else if (main.Length != 0) throw InvalidDeployment();
        if (supplied.Count != extras.Count) throw InvalidDeployment();
        return new(main, extras, inbound, outbound);
    }

    private static string DeploymentSecret(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) throw InvalidDeployment();
        return value.GetString() ?? throw InvalidDeployment();
    }

    private static bool DeploymentNullAllowed(Type type, NullabilityInfo? nullability, string path) =>
        path is not ("Settings.Extensions" or "Settings.LineRoutes" or "Settings.DialRules") &&
        (Nullable.GetUnderlyingType(type) != null || nullability?.WriteState == NullabilityState.Nullable);

    private static void ValidateDeploymentValue(JsonElement element, Type type, string path,
        NullabilityInfo? nullability, NullabilityInfoContext context)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (!DeploymentNullAllowed(type, nullability, path)) throw InvalidDeployment();
            return;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
        {
            if (element.ValueKind != JsonValueKind.String) throw InvalidDeployment();
            StrictDeploymentUtf8.GetByteCount(element.GetString() ?? throw InvalidDeployment());
            return;
        }
        if (type == typeof(bool))
        {
            if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidDeployment();
            return;
        }
        if (type == typeof(int))
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out _)) throw InvalidDeployment();
            return;
        }

        var dictionary = type.GetInterfaces().Prepend(type).FirstOrDefault(candidate => candidate.IsGenericType &&
            (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionary != null)
        {
            if (dictionary.GetGenericArguments()[0] != typeof(string) || element.ValueKind != JsonValueKind.Object) throw InvalidDeployment();
            var childType = dictionary.GetGenericArguments()[1];
            var childNullability = nullability?.GenericTypeArguments.ElementAtOrDefault(1);
            foreach (var property in element.EnumerateObject())
                ValidateDeploymentValue(property.Value, childType, path + ".*", childNullability, context);
            return;
        }
        var enumerable = type.GetInterfaces().Prepend(type).FirstOrDefault(candidate => candidate.IsGenericType &&
            candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable != null)
        {
            if (element.ValueKind != JsonValueKind.Array) throw InvalidDeployment();
            var childType = enumerable.GetGenericArguments()[0];
            var childNullability = type.IsArray ? nullability?.ElementType : nullability?.GenericTypeArguments.ElementAtOrDefault(0);
            foreach (var child in element.EnumerateArray())
                ValidateDeploymentValue(child, childType, path + "[]", childNullability, context);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) throw InvalidDeployment();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0 &&
                property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
            .ToDictionary(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryGetValue(property.Name, out var declared)) throw InvalidDeployment();
            ValidateDeploymentValue(property.Value, declared.PropertyType, path + "." + property.Name, context.Create(declared), context);
        }
    }
}
