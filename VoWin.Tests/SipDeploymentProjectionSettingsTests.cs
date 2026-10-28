using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

/// <summary>Synthetic private imports, pure options and fake services; no listener or modem starts.</summary>
public sealed class SipDeploymentProjectionSettingsTests : IDisposable
{
    private const string MainSecret = "synthetic-projection-main";
    private const string ExtraSecret = "synthetic-projection-extra";
    private const string InboundSecret = "synthetic-projection-inbound";
    private const string OutboundSecret = "synthetic-projection-outbound";
    private const string Sentinel = "synthetic-projection-never-echo";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vowin-projection-" + Guid.NewGuid().ToString("N"));

    public SipDeploymentProjectionSettingsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static SipGatewayPreferences Settings() => new()
    {
        CredentialVersion = 2, UseTrunk = true, LocalRegistrarEnabled = true,
        BindAddress = "127.0.0.1", Port = 5062, Realm = "gateway-auth.example.invalid",
        IdentityDomain = "reply.example.invalid", TrustedNetworks = ["127.0.0.1/32", "127.0.0.2/32"],
        LocalRegistrarAlgorithm = "SHA-256", Extension = "projection-main", ServerAddress = "127.0.0.2", ServerPort = 5060,
        TrunkUsername = "projection-in", IncomingTarget = "projection-ring", PeerInboundAlgorithm = "SHA-256",
        PeerOutboundUsername = "projection-out", PeerExpectedRealm = "pbx-auth.example.invalid", PeerAllowedAlgorithms = ["MD5"],
        WasEnabled = true, RestoreOnStartup = true, AllowOutboundCalls = false, MessagesEnabled = true,
        PrimaryPolicy = new() { AllowCalls = false, AllowSms = false, AllowInternalCalls = true, AllowMessages = true,
            OutgoingLines = [], SmsSendLines = [] },
        Extensions = [new("projection-extra", new() { AllowCalls = false, AllowSms = false, AllowInternalCalls = false,
            AllowMessages = true, OutgoingLines = [], SmsSendLines = [] })],
        PeerAllowOutboundCalls = false, PeerAllowOutboundMessages = false, PeerAllowIncomingCalls = false, PeerAllowIncomingMessages = false,
        PeerOutgoingLines = [], PeerSmsSendLines = [],
        LineRoutes = new()
        {
            ["projection-line"] = new() { AllowIncomingCalls = false, AllowOutgoingCalls = false,
                LocalIncomingExtensions = [], LocalSmsRecipients = [], ForwardCallsToPeer = false, ForwardSmsToPeer = false }
        }
    };

    private static SipGatewayCredentials Credentials() => new(MainSecret, new() { ["projection-extra"] = ExtraSecret }, InboundSecret, OutboundSecret);

    private static JsonObject Envelope(SipGatewayPreferences? settings = null, SipGatewayCredentials? credentials = null)
    {
        credentials ??= Credentials();
        return JsonSerializer.SerializeToNode(new
        {
            Version = 2, Settings = settings ?? Settings(),
            Credentials = new { credentials.MainPassword, credentials.Extensions, credentials.PeerInboundPassword, credentials.PeerOutboundPassword }
        })!.AsObject();
    }

    private async Task<string> WriteAsync(JsonObject value)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, value.ToJsonString());
        return path;
    }

    private static IVoKernelService ServiceFor(VoKernel kernel, List<string> calls)
    {
        var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)service).Handler = (method, _) =>
        {
            calls.Add(method.Name);
            return method.Name switch
            {
                "get_Kernel" => kernel, "GetSipGatewayStatus" => null,
                _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
                _ => throw new NotSupportedException("Synthetic projection test forbids service operations")
            };
        };
        return service;
    }

    [Fact]
    public void ProjectionBuildOptionsPreservesNetworkAlgorithmAndDomainWithoutCredentialOrRealmConflation()
    {
        var settings = Settings();
        var options = SipGatewayDeploymentProfile.BuildOptions(settings, Credentials());
        Assert.Equal("gateway-auth.example.invalid", options.Realm);
        Assert.Equal("reply.example.invalid", options.IdentityDomain);
        Assert.Equal("SHA-256", options.LocalRegistrarAlgorithm);
        Assert.Equal(["127.0.0.1/32", "127.0.0.2/32"], options.TrustedNetworks);
        Assert.Equal("SHA-256", options.Peer!.InboundAuth!.Algorithm);
        Assert.Equal("pbx-auth.example.invalid", options.Peer.OutboundAuth!.ExpectedRealm);
        Assert.Equal(["MD5"], options.Peer.OutboundAuth.AllowedAlgorithms);
        Assert.Equal(MainSecret, options.Accounts["projection-main"]);
        Assert.Equal(ExtraSecret, options.Accounts["projection-extra"]);
        Assert.Equal(InboundSecret, options.Peer.InboundAuth.Password);
        Assert.Equal(OutboundSecret, options.Peer.OutboundAuth.Password);
        settings.TrustedNetworks!.Clear();
        Assert.Equal(2, options.TrustedNetworks!.Count);
        var copy = options.DeepClone();
        ((string[])options.TrustedNetworks)[0] = "127.0.0.9/32";
        Assert.Equal("127.0.0.1/32", copy.TrustedNetworks![0]);
    }

    [Theory]
    [InlineData("MD5")]
    [InlineData("SHA-256")]
    public async Task ProjectionPrivateImportAndUiRoundTripKeepFieldsProtectedDisabledAndNeverStart(string algorithm)
    {
        var input = await WriteAsync(Envelope(Settings() with { LocalRegistrarAlgorithm = algorithm }));
        var savedPath = Path.Combine(_directory, "remembered.json");
        var store = new SipGatewaySettingsStore(savedPath);
        await using var kernel = new VoKernel();
        var calls = new List<string>();
        var service = ServiceFor(kernel, calls);
        var model = new SettingsViewModel(service, store);
        await model.LoadSipDeploymentAsync(input);
        Assert.Equal("reply.example.invalid", model.SipIdentityDomain);
        Assert.Equal("127.0.0.1/32, 127.0.0.2/32", model.SipTrustedNetworks);
        Assert.Equal(algorithm, model.SipLocalRegistrarAlgorithm);
        Assert.Equal("gateway-auth.example.invalid", model.SipGatewayRealm);
        await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);

        var restoredStore = new SipGatewaySettingsStore(savedPath);
        var restored = await restoredStore.LoadAsync();
        Assert.False(restored.WasEnabled);
        Assert.Equal("reply.example.invalid", restored.IdentityDomain);
        Assert.Equal(["127.0.0.1/32", "127.0.0.2/32"], restored.TrustedNetworks);
        Assert.Equal(algorithm, restored.LocalRegistrarAlgorithm);
        Assert.Equal(["MD5"], restored.PeerAllowedAlgorithms);
        var secrets = await restoredStore.LoadCredentialsAsync(restored);
        Assert.Equal(MainSecret, secrets.MainPassword);
        Assert.Equal(ExtraSecret, secrets.Extensions!["projection-extra"]);
        Assert.Equal(InboundSecret, secrets.PeerInboundPassword);
        Assert.Equal(OutboundSecret, secrets.PeerOutboundPassword);
        var publicJson = await File.ReadAllTextAsync(savedPath);
        var encrypted = await File.ReadAllBytesAsync(savedPath + ".protected");
        foreach (var secret in new[] { MainSecret, ExtraSecret, InboundSecret, OutboundSecret })
        {
            Assert.DoesNotContain(secret, publicJson);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(encrypted));
            Assert.DoesNotContain(secret, Encoding.Unicode.GetString(encrypted));
        }
        var restarted = new SettingsViewModel(service, restoredStore);
        await restarted.LoadSipSettingsAsync();
        Assert.Equal(model.SipIdentityDomain, restarted.SipIdentityDomain);
        Assert.Equal(model.SipTrustedNetworks, restarted.SipTrustedNetworks);
        Assert.Equal(algorithm, restarted.BuildSipGatewayOptions().LocalRegistrarAlgorithm);
        Assert.DoesNotContain(calls, name => name.StartsWith("Start", StringComparison.Ordinal));
        Assert.Null(kernel.LocalSipGateway);
        Assert.Empty(kernel.Pool.Slots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionUiDistinguishesLegacyNullNetworksFromExplicitDenyAll(bool denyAll)
    {
        var settings = Settings() with { TrustedNetworks = denyAll ? [] : null, LocalRegistrarAlgorithm = null, IdentityDomain = null };
        var store = new SipGatewaySettingsStore(Path.Combine(_directory, "network-roundtrip.json"));
        await store.SaveConnectionAsync(settings, Credentials(), enabled: false);
        await using var kernel = new VoKernel();
        var model = new SettingsViewModel(ServiceFor(kernel, new()), store);
        await model.LoadSipSettingsAsync();
        Assert.Equal(denyAll ? "" : "*", model.SipTrustedNetworks);
        Assert.Equal("MD5", model.SipLocalRegistrarAlgorithm);
        Assert.Equal("", model.SipIdentityDomain);
        await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
        var saved = await store.LoadAsync();
        if (denyAll) Assert.Empty(saved.TrustedNetworks!);
        else Assert.Null(saved.TrustedNetworks);
        Assert.Null(saved.IdentityDomain);
        Assert.Null(saved.LocalRegistrarAlgorithm);
        Assert.Equal("MD5", model.BuildSipGatewayOptions().LocalRegistrarAlgorithm);
        Assert.Null(kernel.LocalSipGateway);
    }

    [Fact]
    public void ProjectionNewOptionalPreferencesLeaveLegacyJsonUnchanged()
    {
        var json = JsonSerializer.Serialize(new SipGatewayPreferences());
        Assert.DoesNotContain("IdentityDomain", json);
        Assert.DoesNotContain("TrustedNetworks", json);
        Assert.DoesNotContain("LocalRegistrarAlgorithm", json);
        var legacy = SipGatewayDeploymentProfile.BuildOptions(Settings() with
            { IdentityDomain = null, TrustedNetworks = null, LocalRegistrarAlgorithm = null }, Credentials());
        Assert.Null(legacy.IdentityDomain);
        Assert.Null(legacy.TrustedNetworks);
        Assert.Equal("MD5", legacy.LocalRegistrarAlgorithm);
    }

    [Fact]
    public async Task ProjectionFullyDisabledDraftImportsAndPersistsWithoutInventingRuntimeIdentity()
    {
        var settings = Settings() with
        {
            UseTrunk = false, LocalRegistrarEnabled = false, Extension = "", Extensions = [],
            PrimaryPolicy = new() { AllowCalls = false, AllowSms = false, AllowInternalCalls = false, AllowMessages = false,
                OutgoingLines = [], SmsSendLines = [] },
            TrustedNetworks = [], WasEnabled = true
        };
        var empty = new SipGatewayCredentials(Extensions: new());
        var input = await WriteAsync(Envelope(settings, empty));
        var profile = await SipGatewayDeploymentProfile.ReadAsync(input);
        Assert.False(profile.Settings.WasEnabled);
        Assert.False(profile.Settings.UseTrunk);
        Assert.False(profile.Settings.HasLocalRegistrar);
        Assert.Empty(profile.Credentials.MainPassword);
        Assert.Empty(profile.Credentials.Extensions!);
        Assert.Empty(profile.Credentials.PeerInboundPassword);
        Assert.Empty(profile.Credentials.PeerOutboundPassword);
        var draft = SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials, allowEmptyDraft: true);
        draft.ValidateForImport();
        Assert.Empty(draft.Accounts);
        Assert.Null(draft.Peer);
        Assert.Throws<ArgumentException>(draft.Validate);
        Assert.Throws<ArgumentException>(() => new SipGateway(draft, (ISipGatewayCallController)null!));

        var store = new SipGatewaySettingsStore(Path.Combine(_directory, "disabled.json"));
        await using var kernel = new VoKernel();
        var calls = new List<string>();
        var model = new SettingsViewModel(ServiceFor(kernel, calls), store);
        await model.LoadSipDeploymentAsync(input);
        await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
        var saved = await store.LoadAsync();
        Assert.False(saved.UseTrunk);
        Assert.False(saved.HasLocalRegistrar);
        Assert.False(saved.WasEnabled);
        Assert.Empty((await store.LoadCredentialsAsync(saved)).MainPassword);
        Assert.Throws<ArgumentException>(() => model.BuildSipGatewayOptions());
        Assert.DoesNotContain(calls, name => name.StartsWith("Start", StringComparison.Ordinal));
        Assert.Null(kernel.LocalSipGateway);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("peer")]
    public void ProjectionDraftValidationCannotBypassAnEnabledRoleWithMissingSecrets(string role)
    {
        var settings = Settings() with { UseTrunk = role == "peer", LocalRegistrarEnabled = role == "local", Extensions = [] };
        Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(settings, new SipGatewayCredentials(), allowEmptyDraft: true));
    }

    [Theory]
    [InlineData("TrustedNetworks", "\"synthetic-projection-never-echo\"")]
    [InlineData("TrustedNetworks", "[null]")]
    [InlineData("TrustedNetworks", "[\"127.0.0.2/24\"]")]
    [InlineData("LocalRegistrarAlgorithm", "\"synthetic-projection-never-echo\"")]
    [InlineData("IdentityDomain", "\"synthetic-projection-never-echo:5060\"")]
    [InlineData("IdentityDomain", "\"synthetic-projection-never-echo.example\\n\"")]
    public async Task ProjectionInvalidInputIsRejectedWithSafeErrorsBeforeAnyUiMutation(string field, string rawValue)
    {
        var store = new SipGatewaySettingsStore(Path.Combine(_directory, "unchanged.json"));
        await store.SaveConnectionAsync(Settings(), Credentials(), enabled: false);
        await using var kernel = new VoKernel();
        var model = new SettingsViewModel(ServiceFor(kernel, new()), store);
        await model.LoadSipSettingsAsync();
        var before = await File.ReadAllBytesAsync(Path.Combine(_directory, "unchanged.json"));
        var protectedBefore = await File.ReadAllBytesAsync(Path.Combine(_directory, "unchanged.json.protected"));
        var invalid = Envelope();
        invalid["Settings"]![field] = JsonNode.Parse(rawValue);
        var input = await WriteAsync(invalid);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => model.LoadSipDeploymentAsync(input));
        Assert.DoesNotContain(Sentinel, error.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_directory, "unchanged.json")));
        Assert.Equal(protectedBefore, await File.ReadAllBytesAsync(Path.Combine(_directory, "unchanged.json.protected")));
        Assert.Equal("reply.example.invalid", model.SipIdentityDomain);
        Assert.Equal("SHA-256", model.SipLocalRegistrarAlgorithm);
        Assert.Equal("127.0.0.1/32, 127.0.0.2/32", model.SipTrustedNetworks);
        Assert.Equal(MainSecret, model.SipGatewayPassword);
        Assert.Null(kernel.LocalSipGateway);
    }

    [Theory]
    [InlineData("reply.example.invalid")]
    [InlineData("127.0.0.9")]
    [InlineData("fd00:77::9")]
    public void ProjectionPublicDiagnosticsExposeConfiguredDomainButNoRoleSecrets(string domain)
    {
        var options = SipGatewayDeploymentProfile.BuildOptions(Settings() with { IdentityDomain = domain }, Credentials());
        var json = JsonSerializer.Serialize(options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(domain, document.RootElement.GetProperty("IdentityDomain").GetString());
        Assert.Equal("gateway-auth.example.invalid", document.RootElement.GetProperty("Realm").GetString());
        foreach (var secret in new[] { MainSecret, ExtraSecret, InboundSecret, OutboundSecret })
        {
            Assert.DoesNotContain(secret, json);
            Assert.DoesNotContain(secret, options.Peer!.ToString());
        }
    }
}
