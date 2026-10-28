using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using VoSharp.Kernel.SipGateway;

namespace VoWin.Services;

/// <summary>Only non-secret preferences. Loading never starts a listener or contacts a server.</summary>
public sealed record SipGatewayPreferences
{
    public bool UseTrunk { get; init; }
    public string BindAddress { get; init; } = "";
    public int Port { get; init; } = 5062;
    public string Realm { get; init; } = "vowin.local";
    public string Extension { get; init; } = "1001";
    public string ServerAddress { get; init; } = "";
    public int ServerPort { get; init; } = 5060;
    public string TrunkUsername { get; init; } = "hostims";
    public string IncomingTarget { get; init; } = "600";
    public bool AllowOutboundCalls { get; init; }
    public bool RestoreOnStartup { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyDeploymentPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WasEnabled { get; init; }
    public string? DefaultOutboundLineId { get; init; }
    public Dictionary<string, SipGatewayLineRoute> LineRoutes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool MessagesEnabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool ForwardIncomingSms { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AllowOutgoingSms { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<SipExtensionSettings>? Extensions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<SipDialRule>? DialRules { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int CredentialVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? LocalRegistrarEnabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PeerInboundAlgorithm { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PeerOutboundUsername { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PeerExpectedRealm { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? PeerAllowedAlgorithms { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, string>? LocalAccountEpochs { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PeerCredentialEpoch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SipExtensionPolicy? PrimaryPolicy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? PeerAllowOutboundCalls { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? PeerAllowOutboundMessages { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? PeerAllowIncomingCalls { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? PeerAllowIncomingMessages { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? PeerOutgoingLines { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? PeerSmsSendLines { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IdentityDomain { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? TrustedNetworks { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LocalRegistrarAlgorithm { get; init; }

    [JsonIgnore] public bool HasLocalRegistrar => LocalRegistrarEnabled ?? !UseTrunk;
}

public sealed record SipExtensionSettings(string Username, SipExtensionPolicy Policy);

internal sealed record SipGatewayCredentials(string MainPassword = "",
    Dictionary<string, string>? Extensions = null, string PeerInboundPassword = "", string PeerOutboundPassword = "",
    string LegacyTrunkPassword = "")
{
    public override string ToString() => "SIP credentials (secrets omitted)";
}

public sealed class SipGatewaySettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "sip-gateway.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private string? _preparedSettingsHash;
    private SipGatewayCredentials? _preparedCredentials;
    private sealed record StartupSecret(string SettingsHash, string Password, Dictionary<string, string>? Extensions = null)
    {
        public override string ToString() => "Protected SIP startup credential (secret omitted)";
    }
    private sealed record StartupSecretV2(int Version, string SettingsHash, SipGatewayCredentials Credentials)
    {
        public override string ToString() => "Protected SIP V2 credential (secrets omitted)";
    }

    private static string Fingerprint(SipGatewayPreferences settings) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));

    internal async Task<SipGatewayPreferences> MigrateLegacyDeploymentAsync(SipGatewayPreferences settings)
    {
        var path = settings.LegacyDeploymentPath;
        if (!settings.RestoreOnStartup || string.IsNullOrEmpty(path)) return settings;
        // Migration is explicitly staged for a known user-selected local file, never a network share.
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new InvalidDataException("启动迁移只接受已选择的本机部署文件。");
        var profile = await SipGatewayDeploymentProfile.ReadAsync(path);
        var source = profile.Settings;
        if (!settings.UseTrunk || !source.UseTrunk ||
            !string.Equals(settings.ServerAddress, source.ServerAddress, StringComparison.OrdinalIgnoreCase) ||
            settings.ServerPort != source.ServerPort || settings.TrunkUsername != source.TrunkUsername ||
            settings.Realm != source.Realm || settings.BindAddress != source.BindAddress || settings.Port != source.Port)
            throw new InvalidDataException("旧部署文件与当前 SIP 认证端点不匹配，未自动迁移。");
        // Old shared trunk credentials cannot establish the new independent peer identities.
        // Preserve the draft, but never guess an outbound realm or restore its former enabled state.
        await SaveConnectionAsync(settings with { LegacyDeploymentPath = null }, profile.Credentials, enabled: false);
        return await LoadAsync();
    }

    internal async Task<string> LoadRememberedPasswordAsync(SipGatewayPreferences settings)
    {
        await _connectionGate.WaitAsync();
        try { return (await LoadCredentialsCoreAsync(settings)).MainPassword; }
        finally { _connectionGate.Release(); }
    }

    internal async Task<Dictionary<string, string>> LoadRememberedExtensionsAsync(SipGatewayPreferences settings)
    {
        await _connectionGate.WaitAsync();
        try { return (await LoadCredentialsCoreAsync(settings)).Extensions ?? new(StringComparer.OrdinalIgnoreCase); }
        finally { _connectionGate.Release(); }
    }

    internal async Task<SipGatewayCredentials> LoadCredentialsAsync(SipGatewayPreferences settings)
    {
        await _connectionGate.WaitAsync();
        try { return await LoadCredentialsCoreAsync(settings); }
        finally { _connectionGate.Release(); }
    }

    private async Task<SipGatewayCredentials> LoadCredentialsCoreAsync(SipGatewayPreferences settings)
    {
        if (!settings.RestoreOnStartup) return new();
        if (new FileInfo(_path + ".protected").Length is <= 0 or > 65536)
            throw new InvalidDataException("SIP 启动凭据大小无效。");
        var encrypted = await File.ReadAllBytesAsync(_path + ".protected");
        if (encrypted.Length is <= 0 or > 65536) throw new InvalidDataException("SIP 启动凭据大小无效。");
        var plain = SipStartupSecretProtection.Unprotect(encrypted);
        try
        {
            using var document = JsonDocument.Parse(plain);
            if (settings.CredentialVersion == 2)
            {
                var secret = JsonSerializer.Deserialize<StartupSecretV2>(plain);
                if (secret is null || secret.Version != 2 || secret.SettingsHash != Fingerprint(settings) || secret.Credentials is null)
                    throw CredentialMismatch();
                return secret.Credentials;
            }
            // No projection/downgrade: a V2 public field or V2 envelope forbids legacy verification.
            if (settings.CredentialVersion != 0 || settings.LocalRegistrarEnabled is not null ||
                settings.PeerInboundAlgorithm is not null || settings.PeerOutboundUsername is not null ||
                settings.PeerExpectedRealm is not null || settings.PeerAllowedAlgorithms is not null ||
                settings.LocalAccountEpochs is not null || settings.PeerCredentialEpoch is not null ||
                document.RootElement.TryGetProperty("Version", out _)) throw CredentialMismatch();
            var legacy = JsonSerializer.Deserialize<StartupSecret>(plain);
            if (legacy is null || legacy.SettingsHash != Fingerprint(settings) || string.IsNullOrEmpty(legacy.Password))
                throw CredentialMismatch();
            return settings.UseTrunk
                ? new(LegacyTrunkPassword: legacy.Password)
                : new(legacy.Password, legacy.Extensions);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static InvalidDataException CredentialMismatch() =>
        new("SIP 配置与记住的凭据不匹配，请重新导入或保存；未自动启用。");

    internal async Task SaveConnectionAsync(SipGatewayPreferences settings, string password, bool enabled,
        Dictionary<string, string>? extensions = null)
    {
        await SaveConnectionAsync(settings, settings.UseTrunk
            ? new(Extensions: extensions, LegacyTrunkPassword: password)
            : new(password, extensions), enabled);
    }

    internal async Task SaveConnectionAsync(SipGatewayPreferences settings, SipGatewayCredentials credentials, bool enabled)
        => _ = await PrepareConnectionAsync(settings, credentials, enabled);

    // Returns the exact persisted incarnation metadata that must be used by the
    // runtime listener. Preparing only an object and starting before this commit
    // could bind the running queue to identities not recoverable after a crash.
    internal async Task<SipGatewayPreferences> PrepareConnectionAsync(SipGatewayPreferences settings,
        SipGatewayCredentials credentials, bool enabled)
    {
        await _connectionGate.WaitAsync();
        try { return await SaveConnectionCoreAsync(settings, credentials, enabled); }
        finally { _connectionGate.Release(); }
    }

    internal async Task MarkDisabledAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            var settings = await LoadAsync();
            if (!settings.RestoreOnStartup)
            {
                await SaveAsync(settings with { WasEnabled = false });
                return;
            }
            SipGatewayCredentials credentials;
            try { credentials = await LoadCredentialsCoreAsync(settings); }
            catch
            {
                // Stopping never depends on decrypting or completing a draft. The old blob
                // remains fail-closed (its public hash no longer matches).
                await SaveAsync(settings with { WasEnabled = false });
                return;
            }
            await SaveConnectionCoreAsync(settings, credentials, enabled: false);
        }
        finally { _connectionGate.Release(); }
    }

    private async Task<SipGatewayPreferences> SaveConnectionCoreAsync(SipGatewayPreferences settings, SipGatewayCredentials credentials, bool enabled)
    {
        settings = await PrepareRecipientEpochsAsync(settings, credentials);
        settings = settings with { CredentialVersion = 2, WasEnabled = settings.RestoreOnStartup && enabled };
        if (enabled) SipGatewayDeploymentProfile.BuildOptions(settings, credentials);
        if (settings.RestoreOnStartup)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(new StartupSecretV2(2, Fingerprint(settings), credentials));
            byte[] encrypted;
            try { encrypted = SipStartupSecretProtection.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporary = _path + ".protected.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, encrypted);
                File.Move(temporary, _path + ".protected", overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        await SaveAsync(settings);
        if (!settings.RestoreOnStartup && File.Exists(_path + ".protected")) File.Delete(_path + ".protected");
        _preparedSettingsHash = Fingerprint(settings);
        _preparedCredentials = credentials with { Extensions = credentials.Extensions is null ? null :
            new(credentials.Extensions, StringComparer.OrdinalIgnoreCase) };
        return settings;
    }

    private async Task<SipGatewayPreferences> PrepareRecipientEpochsAsync(SipGatewayPreferences desired, SipGatewayCredentials credentials)
    {
        SipGatewayPreferences? previous = File.Exists(_path) ? await LoadAsync() : null;
        SipGatewayCredentials? previousCredentials = null;
        if (previous != null)
        {
            if (_preparedSettingsHash == Fingerprint(previous)) previousCredentials = _preparedCredentials;
            else if (previous.RestoreOnStartup)
                try { previousCredentials = await LoadCredentialsCoreAsync(previous); }
                catch { /* An unverified old envelope cannot authorize an old receiver incarnation. */ }
        }
        var previousUsers = AccountNames(previous).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var epochs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var username in AccountNames(desired))
        {
            var oldEpoch = previous?.LocalAccountEpochs?.GetValueOrDefault(username);
            var requested = desired.LocalAccountEpochs?.GetValueOrDefault(username);
            var unchanged = previousUsers.Contains(username) && previousCredentials != null &&
                desired.Realm == previous?.Realm && Password(credentials, desired, username) == Password(previousCredentials, previous!, username) &&
                SipSmsRecipientAuthority.IsValidEpoch(oldEpoch) && (requested == null || requested == oldEpoch);
            // A requested reset is a signal, never a caller-chosen identifier.
            // Replaying stale settings cannot restore an old queued identity.
            epochs[username] = unchanged ? oldEpoch! : Guid.NewGuid().ToString("N");
        }
        var peerUnchanged = previous != null && previousCredentials != null &&
            SipSmsRecipientAuthority.IsValidEpoch(previous.PeerCredentialEpoch) &&
            (desired.PeerCredentialEpoch == null || desired.PeerCredentialEpoch == previous.PeerCredentialEpoch) &&
            PeerContext(desired, credentials) == PeerContext(previous, previousCredentials);
        return desired with { LocalAccountEpochs = epochs,
            PeerCredentialEpoch = peerUnchanged ? previous!.PeerCredentialEpoch : Guid.NewGuid().ToString("N") };
    }

    private static IEnumerable<string> AccountNames(SipGatewayPreferences? settings)
    {
        if (settings == null) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var username in new[] { settings.Extension }.Concat((settings.Extensions ?? []).Select(item => item.Username)))
            if (!string.IsNullOrWhiteSpace(username) && seen.Add(username)) yield return username;
    }
    private static string Password(SipGatewayCredentials credentials, SipGatewayPreferences settings, string username) =>
        username.Equals(settings.Extension, StringComparison.OrdinalIgnoreCase) ? credentials.MainPassword :
            credentials.Extensions?.FirstOrDefault(pair => pair.Key.Equals(username, StringComparison.OrdinalIgnoreCase)).Value ?? "";
    private static string PeerContext(SipGatewayPreferences settings, SipGatewayCredentials credentials) =>
        JsonSerializer.Serialize(new[] { settings.Realm, settings.ServerAddress, settings.ServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.TrunkUsername, settings.IncomingTarget, settings.PeerInboundAlgorithm, settings.PeerOutboundUsername,
            settings.PeerExpectedRealm, string.Join(",", settings.PeerAllowedAlgorithms ?? []), credentials.PeerInboundPassword, credentials.PeerOutboundPassword });

    public async Task<SipGatewayPreferences> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return new() { CredentialVersion = 2, LocalRegistrarEnabled = true,
                Extension = "", TrunkUsername = "", IncomingTarget = "" };
            await using var input = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<SipGatewayPreferences>(input) ?? new();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(SipGatewayPreferences settings)
    {
        await _gate.WaitAsync();
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            await using (var output = File.Create(temporary))
                await JsonSerializer.SerializeAsync(output, settings, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
}
