using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

/// <summary>Raw V2 input, temporary files, DPAPI and fake services only; never starts a SIP listener.</summary>
public sealed class SipDeploymentProfileV2ImportTests : IDisposable
{
    private const string MainSecret = "synthetic-main-only-secret";
    private const string ExtraSecret = "synthetic-extra-only-secret";
    private const string InboundSecret = "synthetic-inbound-only-secret";
    private const string OutboundSecret = "synthetic-outbound-only-secret";
    private const string Sentinel = "synthetic-never-echo-secret";
    private const string MainFileEpoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ExtraFileEpoch = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PeerFileEpoch = "cccccccccccccccccccccccccccccccc";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vowin-import-v2-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions RawOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public SipDeploymentProfileV2ImportTests() => Directory.CreateDirectory(_directory);

    private static JsonObject Envelope() => JsonNode.Parse($$$"""
        {
          "Version": 2,
          "Settings": {
            "CredentialVersion": 2, "UseTrunk": true, "LocalRegistrarEnabled": true,
            "BindAddress": "127.0.0.1", "Port": 5062, "Realm": "gateway.example.invalid",
            "Extension": "desk-main", "ServerAddress": "127.0.0.2", "ServerPort": 5060,
            "TrunkUsername": "peer-in", "IncomingTarget": "ring-service",
            "PeerInboundAlgorithm": "MD5", "PeerOutboundUsername": "peer-out",
            "PeerExpectedRealm": "pbx.example.invalid", "PeerAllowedAlgorithms": ["SHA-256", "MD5"],
            "AllowOutboundCalls": false, "RestoreOnStartup": true, "WasEnabled": true,
            "LegacyDeploymentPath": "C:\\synthetic\\never-open-this-file.json",
            "MessagesEnabled": false, "ForwardIncomingSms": false, "AllowOutgoingSms": false,
            "Extensions": [{"Username":"desk-extra","Policy":{"AllowCalls":false,"AllowSms":false,"DefaultLineId":"synthetic-a"}}],
            "LineRoutes": {
              "synthetic-a": {"Enabled":true,"AllowIncomingCalls":false,"AllowOutgoingCalls":false,
                "ForwardIncomingSms":false,"AllowOutgoingSms":false,"IncomingTarget":"ring-service",
                "SmsTarget":"sms-service","LocalIncomingTarget":"desk-main","LocalSmsTarget":"desk-extra"}
            },
            "DialRules": [{"Prefix":"81","TotalLength":0,"StripPrefix":true,"Prepend":"+","LineId":"synthetic-a"}],
            "LocalAccountEpochs": {"desk-main":"{{{MainFileEpoch}}}","desk-extra":"{{{ExtraFileEpoch}}}"},
            "PeerCredentialEpoch": "{{{PeerFileEpoch}}}"
          },
          "Credentials": {
            "MainPassword": "{{{MainSecret}}}", "Extensions": {"desk-extra":"{{{ExtraSecret}}}"},
            "PeerInboundPassword": "{{{InboundSecret}}}", "PeerOutboundPassword": "{{{OutboundSecret}}}"
          }
        }
        """)!.AsObject();

    private static JsonNode At(JsonNode root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            current = current is JsonArray array ? array[int.Parse(part)]! : current[part]!;
        return current;
    }

    private static void Set(JsonObject root, string path, JsonNode? value)
    {
        var split = path.LastIndexOf('.');
        At(root, split < 0 ? "" : path[..split]).AsObject()[path[(split + 1)..]] = value;
    }

    private static void Remove(JsonObject root, string path)
    {
        var split = path.LastIndexOf('.');
        Assert.True(At(root, split < 0 ? "" : path[..split]).AsObject().Remove(path[(split + 1)..]));
    }

    private static byte[] Raw(JsonObject envelope) => Encoding.UTF8.GetBytes(envelope.ToJsonString(RawOptions));

    private async Task<string> WriteAsync(byte[] raw)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllBytesAsync(path, raw);
        return path;
    }

    private async Task<SipGatewayDeploymentProfile> ReadAsync(JsonObject envelope) =>
        await SipGatewayDeploymentProfile.ReadAsync(await WriteAsync(Raw(envelope)));

    private async Task<InvalidDataException> RejectAsync(byte[] raw) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadRawAsync(raw));

    private async Task<SipGatewayDeploymentProfile> ReadRawAsync(byte[] raw) =>
        await SipGatewayDeploymentProfile.ReadAsync(await WriteAsync(raw));

    private static byte[] DuplicateMember(JsonObject root, string objectPath, string keyJson, string valueJson)
    {
        var target = At(root, objectPath);
        string Render(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                var members = obj.Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Render(pair.Value)).ToList();
                if (ReferenceEquals(node, target)) members.Insert(0, keyJson + ":" + valueJson);
                return "{" + string.Join(",", members) + "}";
            }
            if (node is JsonArray array) return "[" + string.Join(",", array.Select(Render)) + "]";
            return node?.ToJsonString() ?? "null";
        }
        return Encoding.UTF8.GetBytes(Render(root));
    }

    private static void AssertSecrets(SipGatewayCredentials credentials)
    {
        Assert.Equal(MainSecret, credentials.MainPassword);
        Assert.Equal(ExtraSecret, credentials.Extensions!["desk-extra"]);
        Assert.Equal(InboundSecret, credentials.PeerInboundPassword);
        Assert.Equal(OutboundSecret, credentials.PeerOutboundPassword);
        Assert.Empty(credentials.LegacyTrunkPassword);
    }

    [Fact]
    public async Task V2MapsFourIndependentCredentialsIntoCoexistingRegistrarAndPeer()
    {
        var profile = await ReadAsync(Envelope());
        AssertSecrets(profile.Credentials);
        var options = SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials);
        Assert.Equal(2, options.Accounts.Count);
        Assert.Equal(MainSecret, options.Accounts["desk-main"]);
        Assert.Equal(ExtraSecret, options.Accounts["desk-extra"]);
        Assert.DoesNotContain("peer-in", options.Accounts.Keys);
        Assert.DoesNotContain("peer-out", options.Accounts.Keys);
        Assert.Equal("gateway.example.invalid", options.Realm);
        Assert.Equal("peer-in", options.Peer!.InboundAuth!.Username);
        Assert.Equal(InboundSecret, options.Peer.InboundAuth.Password);
        Assert.Equal("MD5", options.Peer.InboundAuth.Algorithm);
        Assert.Equal("peer-out", options.Peer.OutboundAuth!.Username);
        Assert.Equal(OutboundSecret, options.Peer.OutboundAuth.Password);
        Assert.Equal("pbx.example.invalid", options.Peer.OutboundAuth.ExpectedRealm);
        Assert.Equal(["SHA-256", "MD5"], options.Peer.OutboundAuth.AllowedAlgorithms);
        Assert.Empty(options.Peer.Password);
        Assert.False(options.Peer.AllowOutboundCalls);
        Assert.False(options.Peer.AllowOutboundMessages);
        Assert.False(options.ExtensionPolicies["desk-extra"].AllowCalls);
        Assert.False(options.ExtensionPolicies["desk-extra"].AllowSms);
        Assert.Equal("synthetic-a", options.ExtensionPolicies["desk-extra"].DefaultLineId);
        Assert.Equal("desk-main", options.LineRoutes["synthetic-a"].LocalIncomingTarget);
        Assert.Equal("desk-extra", options.LineRoutes["synthetic-a"].LocalSmsTarget);
    }

    [Fact]
    public async Task V2AllowsExplicitSamePeerUsernameAndPasswordWithoutRoleFallback()
    {
        var envelope = Envelope();
        Set(envelope, "Settings.TrunkUsername", JsonValue.Create("shared-peer"));
        Set(envelope, "Settings.PeerOutboundUsername", JsonValue.Create("shared-peer"));
        Set(envelope, "Credentials.PeerInboundPassword", JsonValue.Create("synthetic-same-peer-secret"));
        Set(envelope, "Credentials.PeerOutboundPassword", JsonValue.Create("synthetic-same-peer-secret"));
        var profile = await ReadAsync(envelope);
        Assert.Equal(MainSecret, profile.Credentials.MainPassword);
        Assert.Equal(ExtraSecret, profile.Credentials.Extensions!["desk-extra"]);
        Assert.Equal("synthetic-same-peer-secret", profile.Credentials.PeerInboundPassword);
        Assert.Equal("synthetic-same-peer-secret", profile.Credentials.PeerOutboundPassword);
        var options = SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials);
        Assert.Equal("shared-peer", options.Peer!.InboundAuth!.Username);
        Assert.Equal("shared-peer", options.Peer.OutboundAuth!.Username);
        Assert.Equal("synthetic-same-peer-secret", options.Peer.InboundAuth.Password);
        Assert.Equal("synthetic-same-peer-secret", options.Peer.OutboundAuth.Password);
        Assert.Equal(MainSecret, options.Accounts["desk-main"]);
    }

    [Fact]
    public async Task V2PeerOnlyImportsWithoutInventingLocalAccounts()
    {
        var envelope = Envelope();
        Set(envelope, "Settings.LocalRegistrarEnabled", JsonValue.Create(false));
        Set(envelope, "Settings.Extension", JsonValue.Create(""));
        Set(envelope, "Settings.Extensions", new JsonArray());
        Set(envelope, "Settings.LineRoutes", new JsonObject());
        Set(envelope, "Settings.DialRules", new JsonArray());
        Remove(envelope, "Settings.LocalAccountEpochs");
        Set(envelope, "Credentials.MainPassword", JsonValue.Create(""));
        Set(envelope, "Credentials.Extensions", new JsonObject());
        var profile = await ReadAsync(envelope);
        Assert.Empty(profile.Credentials.MainPassword);
        Assert.Equal(InboundSecret, profile.Credentials.PeerInboundPassword);
        Assert.Equal(OutboundSecret, profile.Credentials.PeerOutboundPassword);
        var options = SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials);
        Assert.Empty(options.Accounts);
        Assert.Equal(InboundSecret, options.Peer!.InboundAuth!.Password);
        Assert.Equal(OutboundSecret, options.Peer.OutboundAuth!.Password);
        Assert.Empty(profile.Credentials.MainPassword);
    }

    [Fact]
    public async Task V2ReturnsDisabledSanitizedReceiverIdentities()
    {
        var profile = await ReadAsync(Envelope());
        Assert.False(profile.Settings.WasEnabled);
        Assert.Null(profile.Settings.LegacyDeploymentPath);
        Assert.NotEqual(PeerFileEpoch, profile.Settings.PeerCredentialEpoch);
        Assert.DoesNotContain(MainFileEpoch, profile.Settings.LocalAccountEpochs?.Values.AsEnumerable() ?? Enumerable.Empty<string>());
        Assert.DoesNotContain(ExtraFileEpoch, profile.Settings.LocalAccountEpochs?.Values.AsEnumerable() ?? Enumerable.Empty<string>());
        var options = SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials);
        Assert.NotEqual(MainFileEpoch, options.LocalAccountEpochs["desk-main"]);
        Assert.NotEqual(ExtraFileEpoch, options.LocalAccountEpochs["desk-extra"]);
        Assert.NotEqual(PeerFileEpoch, options.PeerCredentialEpoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2CompleteUtf8DocumentFitsTheExact64KiBBoundary(bool unicode)
    {
        var envelope = Envelope();
        var expectedMain = unicode ? "研究-only-local-secret" : MainSecret;
        Set(envelope, "Credentials.MainPassword", JsonValue.Create(expectedMain));
        var raw = Raw(envelope);
        if (unicode) Assert.True(raw.Length > Encoding.UTF8.GetString(raw).Length);
        var padded = Enumerable.Repeat((byte)' ', 65536).ToArray();
        raw.CopyTo(padded, 0);
        var profile = await ReadRawAsync(padded);
        Assert.Equal(expectedMain, profile.Credentials.MainPassword);
        Assert.Equal(InboundSecret, profile.Credentials.PeerInboundPassword);
        Assert.Equal(OutboundSecret, profile.Credentials.PeerOutboundPassword);
        Assert.False(profile.Settings.WasEnabled);
        var oversized = new byte[65537];
        padded.CopyTo(oversized, 0);
        oversized[^1] = (byte)' ';
        await RejectAsync(oversized);
    }

    [Fact]
    public async Task V2RejectsEmptyAndWhitespaceOnlyFiles()
    {
        await RejectAsync([]);
        await RejectAsync(Encoding.UTF8.GetBytes(" \r\n\t "));
    }

    public static IEnumerable<object[]> UnknownObjectPaths() => new[]
    {
        "", "Settings", "Credentials", "Settings.Extensions.0", "Settings.Extensions.0.Policy",
        "Settings.LineRoutes.synthetic-a", "Settings.DialRules.0"
    }.Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(UnknownObjectPaths))]
    public async Task V2RejectsUnknownEnvelopeAndNestedFields(string objectPath)
    {
        var envelope = Envelope();
        At(envelope, objectPath).AsObject()["UnknownField"] = "synthetic-unrecognized";
        await RejectAsync(Raw(envelope));
    }

    public static IEnumerable<object[]> DuplicateProperties()
    {
        foreach (var (path, key, escaped) in new[]
        {
            ("", "Version", "\\u0056ersion"),
            ("Settings", "BindAddress", "\\u0042indAddress"),
            ("Credentials", "PeerInboundPassword", "\\u0050eerInboundPassword"),
            ("Credentials.Extensions", "desk-extra", "\\u0064esk-extra"),
            ("Settings.Extensions.0", "Username", "\\u0055sername"),
            ("Settings.Extensions.0.Policy", "AllowCalls", "\\u0041llowCalls"),
            ("Settings.LineRoutes", "synthetic-a", "\\u0073ynthetic-a"),
            ("Settings.LineRoutes.synthetic-a", "Enabled", "\\u0045nabled"),
            ("Settings.DialRules.0", "Prefix", "\\u0050refix"),
            ("Settings.LocalAccountEpochs", "desk-main", "\\u0064esk-main")
        })
        {
            yield return [path, key, "exact", JsonSerializer.Serialize(key)];
            yield return [path, key, "escaped", "\"" + escaped + "\""];
            yield return [path, key, "casefold", JsonSerializer.Serialize(key.ToUpperInvariant())];
        }
    }

    [Theory]
    [MemberData(nameof(DuplicateProperties))]
    public async Task V2RejectsDuplicateEscapedAndCasefoldProperties(string objectPath, string key, string kind, string keyJson)
    {
        Assert.NotEmpty(kind);
        var envelope = Envelope();
        var original = At(envelope, objectPath).AsObject()[key]!.ToJsonString();
        await RejectAsync(DuplicateMember(envelope, objectPath, keyJson, original));
    }

    public static IEnumerable<object[]> RequiredFields() => new[]
    {
        "Version", "Settings", "Credentials", "Settings.CredentialVersion", "Settings.BindAddress",
        "Settings.ServerAddress", "Settings.TrunkUsername", "Settings.PeerOutboundUsername",
        "Settings.PeerExpectedRealm", "Settings.Extension", "Credentials.MainPassword",
        "Credentials.Extensions", "Credentials.Extensions.desk-extra", "Credentials.PeerInboundPassword",
        "Credentials.PeerOutboundPassword"
    }.Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(RequiredFields))]
    public async Task V2RejectsMissingRequiredRoleAndBindingFields(string path)
    {
        var envelope = Envelope();
        Remove(envelope, path);
        await RejectAsync(Raw(envelope));
    }

    public static IEnumerable<object[]> InvalidShapes() => new[]
    {
        ("Settings", "null"), ("Settings", "[]"), ("Credentials", "null"), ("Credentials", "[]"),
        ("Settings.Extensions", "null"), ("Settings.LineRoutes", "null"), ("Settings.DialRules", "null"),
        ("Settings.Extensions.0.Policy", "null"), ("Credentials.Extensions", "null"),
        ("Credentials.Extensions", "[]"), ("Credentials.MainPassword", "null"),
        ("Credentials.PeerInboundPassword", "null"), ("Credentials.PeerOutboundPassword", "null"),
        ("Credentials.PeerInboundPassword", "true"), ("Credentials.PeerOutboundPassword", "7"),
        ("Credentials.Extensions.desk-extra", "null")
    }.Select(item => new object[] { item.Item1, item.Item2 });

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task V2RejectsNullsAndWrongShapes(string path, string literal)
    {
        var envelope = Envelope();
        Set(envelope, path, JsonNode.Parse(literal));
        await RejectAsync(Raw(envelope));
    }

    [Theory]
    [InlineData("Version", "0")]
    [InlineData("Version", "1")]
    [InlineData("Version", "3")]
    [InlineData("Version", "true")]
    [InlineData("Version", "false")]
    [InlineData("Version", "null")]
    [InlineData("Version", "\"2\"")]
    [InlineData("Version", "2.0")]
    [InlineData("Settings.CredentialVersion", "0")]
    [InlineData("Settings.CredentialVersion", "1")]
    [InlineData("Settings.CredentialVersion", "3")]
    [InlineData("Settings.CredentialVersion", "true")]
    [InlineData("Settings.CredentialVersion", "null")]
    [InlineData("Settings.CredentialVersion", "\"2\"")]
    public async Task V2RejectsUnsupportedOrNonIntegerVersions(string path, string literal)
    {
        var envelope = Envelope();
        Set(envelope, path, JsonNode.Parse(literal));
        await RejectAsync(Raw(envelope));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("main-as-extra")]
    [InlineData("casefold-duplicate")]
    [InlineData("empty")]
    public async Task V2RejectsMissingUnknownOrCasefoldDuplicateExtraSecrets(string scenario)
    {
        var envelope = Envelope();
        var extras = At(envelope, "Credentials.Extensions").AsObject();
        switch (scenario)
        {
            case "missing": Assert.True(extras.Remove("desk-extra")); break;
            case "unknown": extras["desk-unknown"] = "synthetic-unknown-extra-secret"; break;
            case "main-as-extra": extras["desk-main"] = MainSecret; break;
            case "casefold-duplicate": extras["DESK-EXTRA"] = "synthetic-other-extra-secret"; break;
            case "empty": extras["desk-extra"] = ""; break;
            default: throw new ArgumentException(nameof(scenario));
        }
        await RejectAsync(Raw(envelope));
    }

    [Theory]
    [InlineData("credential-legacy-secret")]
    [InlineData("root-shared-secret")]
    [InlineData("root-extra-secrets")]
    [InlineData("missing-roles-with-shared-secret")]
    [InlineData("unused-peer-roles")]
    public async Task V2RejectsLegacySharedSecretFallbackAndUnusedPeerRoles(string scenario)
    {
        var envelope = Envelope();
        switch (scenario)
        {
            case "credential-legacy-secret": At(envelope, "Credentials").AsObject()["LegacyTrunkPassword"] = Sentinel; break;
            case "root-shared-secret": envelope["Password"] = Sentinel; break;
            case "root-extra-secrets": envelope["ExtensionPasswords"] = new JsonObject { ["desk-extra"] = ExtraSecret }; break;
            case "missing-roles-with-shared-secret":
                Remove(envelope, "Credentials.PeerInboundPassword"); Remove(envelope, "Credentials.PeerOutboundPassword");
                envelope["Password"] = Sentinel; break;
            case "unused-peer-roles": Set(envelope, "Settings.UseTrunk", JsonValue.Create(false)); break;
            default: throw new ArgumentException(nameof(scenario));
        }
        await RejectAsync(Raw(envelope));
    }

    [Theory]
    [InlineData("Settings.PeerExpectedRealm", "\"\"")]
    [InlineData("Settings.PeerExpectedRealm", "\"*\"")]
    [InlineData("Settings.PeerInboundAlgorithm", "\"SHA-512\"")]
    [InlineData("Settings.PeerInboundAlgorithm", "\"md5\"")]
    [InlineData("Settings.PeerAllowedAlgorithms", "[]")]
    [InlineData("Settings.PeerAllowedAlgorithms", "[\"SHA-256\",\"SHA-256\"]")]
    [InlineData("Settings.PeerAllowedAlgorithms", "[\"SHA-512\"]")]
    [InlineData("Credentials.MainPassword", "\"\"")]
    [InlineData("Credentials.PeerInboundPassword", "\"\"")]
    [InlineData("Credentials.PeerOutboundPassword", "\"\"")]
    public async Task V2RejectsIncompleteOrMismatchedTypedAuthentication(string path, string literal)
    {
        var envelope = Envelope();
        Set(envelope, path, JsonNode.Parse(literal));
        await RejectAsync(Raw(envelope));
    }

    [Theory]
    [InlineData("unknown-secret-key")]
    [InlineData("nested-secret-field")]
    [InlineData("malformed-json")]
    [InlineData("malformed-utf8")]
    [InlineData("escaped-surrogate")]
    public async Task V2ValidationErrorsNeverEchoSecretValuesOrUnknownKeyNames(string scenario)
    {
        var envelope = Envelope();
        byte[] raw;
        switch (scenario)
        {
            case "unknown-secret-key": envelope[Sentinel] = Sentinel; raw = Raw(envelope); break;
            case "nested-secret-field": At(envelope, "Credentials").AsObject()[Sentinel] = Sentinel; raw = Raw(envelope); break;
            case "malformed-json": raw = Encoding.UTF8.GetBytes("{\"" + Sentinel + "\":"); break;
            case "malformed-utf8": raw = Encoding.UTF8.GetBytes("{\"" + Sentinel + "\":\"").Concat(new byte[] { 0xff }).ToArray(); break;
            case "escaped-surrogate":
                var before = envelope.ToJsonString();
                var replaced = before.Replace("\"" + InboundSecret + "\"", "\"" + Sentinel + "\\uD800\"", StringComparison.Ordinal);
                Assert.NotEqual(before, replaced); // The raw JSON, not a serializer replacement rune, is tested.
                raw = Encoding.UTF8.GetBytes(replaced);
                break;
            default: throw new ArgumentException(nameof(scenario));
        }
        var error = await RejectAsync(raw);
        foreach (var secret in new[] { Sentinel, MainSecret, ExtraSecret, InboundSecret, OutboundSecret })
        {
            Assert.DoesNotContain(secret, error.Message);
            Assert.DoesNotContain(secret, error.ToString());
        }
    }

    [Fact]
    public async Task V2PublicSerializationAndToStringDoNotRevealPeerSecrets()
    {
        var profile = await ReadAsync(Envelope());
        AssertSecrets(profile.Credentials);
        var options = SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials);
        var profileJson = JsonSerializer.Serialize(profile);
        // Legacy Password/ExtensionPasswords remain a secret-bearing deployment
        // export. This assertion deliberately does not claim they are redacted.
        Assert.DoesNotContain(InboundSecret, profileJson);
        Assert.DoesNotContain(OutboundSecret, profileJson);
        Assert.DoesNotContain("PeerInboundPassword", profileJson);
        Assert.DoesNotContain("PeerOutboundPassword", profileJson);
        var optionsJson = JsonSerializer.Serialize(options);
        foreach (var secret in new[] { MainSecret, ExtraSecret, InboundSecret, OutboundSecret })
        {
            Assert.DoesNotContain(secret, optionsJson);
            Assert.DoesNotContain(secret, profile.ToString());
            Assert.DoesNotContain(secret, profile.Credentials.ToString());
            Assert.DoesNotContain(secret, options.Peer!.ToString());
            Assert.DoesNotContain(secret, options.Peer.InboundAuth!.ToString());
            Assert.DoesNotContain(secret, options.Peer.OutboundAuth!.ToString());
        }
    }

    [Fact]
    public async Task V2ViewModelImportIsProtectedDisabledAndNeverStarts()
    {
        var path = await WriteAsync(Raw(Envelope()));
        var savedPath = Path.Combine(_directory, "remembered.json");
        var store = new SipGatewaySettingsStore(savedPath);
        await using var kernel = new VoKernel();
        var calls = new ServiceCalls();
        var service = FakeService(kernel, calls);
        var model = new SettingsViewModel(service, store);
        await model.LoadSipDeploymentAsync(path);
        Assert.Equal(0, calls.Starts);
        Assert.Null(kernel.LocalSipGateway);
        Assert.Empty(kernel.Pool.Slots);
        Assert.False(model.IsSipGatewayRunning);
        Assert.Equal(MainSecret, model.SipGatewayPassword);
        Assert.Equal(ExtraSecret, Assert.Single(model.SipExtensions).Password);
        Assert.Equal(InboundSecret, model.SipPeerInboundPassword);
        Assert.Equal(OutboundSecret, model.SipPeerOutboundPassword);
        Assert.DoesNotContain("重新填写两份", model.SipGatewayStatus);
        var restartedStore = new SipGatewaySettingsStore(savedPath);
        var saved = await restartedStore.LoadAsync();
        Assert.False(saved.WasEnabled);
        Assert.True(saved.RestoreOnStartup);
        Assert.Null(saved.LegacyDeploymentPath);
        Assert.NotEqual(MainFileEpoch, saved.LocalAccountEpochs!["desk-main"]);
        Assert.NotEqual(ExtraFileEpoch, saved.LocalAccountEpochs["desk-extra"]);
        Assert.NotEqual(PeerFileEpoch, saved.PeerCredentialEpoch);
        AssertSecrets(await restartedStore.LoadCredentialsAsync(saved));
        var publicJson = await File.ReadAllTextAsync(savedPath);
        var encrypted = await File.ReadAllBytesAsync(savedPath + ".protected");
        foreach (var secret in new[] { MainSecret, ExtraSecret, InboundSecret, OutboundSecret })
        {
            Assert.DoesNotContain(secret, publicJson);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(encrypted));
            Assert.DoesNotContain(secret, Encoding.Unicode.GetString(encrypted));
        }
        var restartedModel = new SettingsViewModel(service, restartedStore);
        await restartedModel.LoadSipSettingsAsync();
        Assert.Equal(0, calls.Starts);
        Assert.False(restartedModel.IsSipGatewayRunning);
        Assert.Equal(InboundSecret, restartedModel.SipPeerInboundPassword);
        Assert.Equal(OutboundSecret, restartedModel.SipPeerOutboundPassword);
    }

    [Fact]
    public async Task V2FailedImportDoesNotMutateRememberedConfigurationOrViewModel()
    {
        var savedPath = Path.Combine(_directory, "previous.json");
        var store = new SipGatewaySettingsStore(savedPath);
        var baseline = JsonSerializer.Deserialize<SipGatewayPreferences>(At(Envelope(), "Settings").ToJsonString())! with
        {
            WasEnabled = false, LegacyDeploymentPath = null, BindAddress = "127.0.0.9", ServerAddress = "127.0.0.8",
            Realm = "saved.example.invalid", LocalAccountEpochs = null, PeerCredentialEpoch = null
        };
        var baselineCredentials = new SipGatewayCredentials("saved-main-secret", new() { ["desk-extra"] = "saved-extra-secret" },
            "saved-inbound-secret", "saved-outbound-secret");
        await store.SaveConnectionAsync(baseline, baselineCredentials, enabled: false);
        await using var kernel = new VoKernel();
        var calls = new ServiceCalls();
        var model = new SettingsViewModel(FakeService(kernel, calls), store);
        await model.LoadSipSettingsAsync();
        var beforePublic = await File.ReadAllBytesAsync(savedPath);
        var beforeProtected = await File.ReadAllBytesAsync(savedPath + ".protected");
        var invalid = Envelope();
        invalid[Sentinel] = "unknown-input";
        var invalidPath = await WriteAsync(Raw(invalid));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => model.LoadSipDeploymentAsync(invalidPath));
        Assert.DoesNotContain(Sentinel, error.ToString());
        Assert.Equal(beforePublic, await File.ReadAllBytesAsync(savedPath));
        Assert.Equal(beforeProtected, await File.ReadAllBytesAsync(savedPath + ".protected"));
        Assert.Equal("127.0.0.9", model.SipGatewayBindAddress);
        Assert.Equal("127.0.0.8", model.SipGatewayServerAddress);
        Assert.Equal("saved.example.invalid", model.SipGatewayRealm);
        Assert.Equal("saved-main-secret", model.SipGatewayPassword);
        Assert.Equal("saved-extra-secret", Assert.Single(model.SipExtensions).Password);
        Assert.Equal("saved-inbound-secret", model.SipPeerInboundPassword);
        Assert.Equal("saved-outbound-secret", model.SipPeerOutboundPassword);
        Assert.Equal(0, calls.Starts);
        Assert.Null(kernel.LocalSipGateway);
        var restartedStore = new SipGatewaySettingsStore(savedPath);
        var settings = await restartedStore.LoadAsync();
        Assert.False(settings.WasEnabled);
        var credentials = await restartedStore.LoadCredentialsAsync(settings);
        Assert.Equal("saved-main-secret", credentials.MainPassword);
        Assert.Equal("saved-extra-secret", credentials.Extensions!["desk-extra"]);
        Assert.Equal("saved-inbound-secret", credentials.PeerInboundPassword);
        Assert.Equal("saved-outbound-secret", credentials.PeerOutboundPassword);
    }

    [Fact]
    public async Task LegacyVersionlessTrunkRemainsEditableWithoutTypedRoleReuse()
    {
        var legacy = JsonSerializer.Deserialize<SipGatewayPreferences>(At(Envelope(), "Settings").ToJsonString())! with
        {
            CredentialVersion = 0, LocalRegistrarEnabled = null, WasEnabled = false, LegacyDeploymentPath = null,
            PeerInboundAlgorithm = null, PeerOutboundUsername = null, PeerExpectedRealm = null, PeerAllowedAlgorithms = null,
            LocalAccountEpochs = null, PeerCredentialEpoch = null, ServerAddress = "8.8.8.8"
        };
        var oldProfile = new SipGatewayDeploymentProfile(legacy, "synthetic-old-shared-secret");
        var profile = await ReadRawAsync(JsonSerializer.SerializeToUtf8Bytes(oldProfile));
        Assert.Equal("8.8.8.8", profile.Settings.ServerAddress); // Still editable, never a runnable peer.
        Assert.Equal("synthetic-old-shared-secret", profile.Credentials.LegacyTrunkPassword);
        Assert.Empty(profile.Credentials.MainPassword);
        Assert.Empty(profile.Credentials.PeerInboundPassword);
        Assert.Empty(profile.Credentials.PeerOutboundPassword);
        Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials));
        Assert.False(profile.Settings.WasEnabled);
    }

    [Fact]
    public async Task LegacyVersionlessPublicProfileStillDoesNotImportIgnoredPeerSecrets()
    {
        var settings = JsonSerializer.Deserialize<SipGatewayPreferences>(At(Envelope(), "Settings").ToJsonString())! with
        { WasEnabled = false, LegacyDeploymentPath = null, LocalAccountEpochs = null, PeerCredentialEpoch = null };
        var oldProfile = new SipGatewayDeploymentProfile(settings, MainSecret, new() { ["desk-extra"] = ExtraSecret },
            PeerInboundPassword: InboundSecret, PeerOutboundPassword: OutboundSecret);
        var raw = JsonSerializer.SerializeToUtf8Bytes(oldProfile);
        Assert.DoesNotContain(InboundSecret, Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain(OutboundSecret, Encoding.UTF8.GetString(raw));
        var imported = await ReadRawAsync(raw);
        Assert.Empty(imported.Credentials.PeerInboundPassword);
        Assert.Empty(imported.Credentials.PeerOutboundPassword);
        Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(imported.Settings, imported.Credentials));
    }

    private sealed class ServiceCalls { internal int Starts; }

    private static IVoKernelService FakeService(VoKernel kernel, ServiceCalls calls)
    {
        var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)service).Handler = (method, _) => method.Name switch
        {
            "get_Kernel" => kernel,
            "GetSipGatewayStatus" => null,
            "StartSipGatewayAsync" => Start(),
            _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
            _ => throw new NotSupportedException(method.Name)
        };
        Task Start() { calls.Starts++; return Task.CompletedTask; }
        return service;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
