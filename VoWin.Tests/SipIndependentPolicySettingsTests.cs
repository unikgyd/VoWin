using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class SipIndependentPolicySettingsTests
{
    private static IVoKernelService ServiceFor(VoKernel kernel)
    {
        var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)service).Handler = (method, _) => method.Name switch
        {
            "get_Kernel" => kernel, "GetSipGatewayStatus" => null,
            _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
            _ => throw new NotSupportedException("No operation authorized by this synthetic test")
        };
        return service;
    }
    private static SipGatewayPreferences Settings() => new()
    {
        CredentialVersion = 2, BindAddress = "127.0.0.1", Realm = "gateway.invalid", Extension = "main",
        LocalRegistrarEnabled = true, UseTrunk = true, ServerAddress = "127.0.0.2", TrunkUsername = "shared-user",
        PeerOutboundUsername = "peer-out", PeerExpectedRealm = "pbx.invalid", PeerAllowedAlgorithms = ["MD5"],
        RestoreOnStartup = true, AllowOutboundCalls = true, MessagesEnabled = true, ForwardIncomingSms = true, AllowOutgoingSms = true,
        PrimaryPolicy = new() { AllowCalls = false, AllowSms = true, AllowInternalCalls = false, AllowMessages = true,
            OutgoingLines = [], SmsSendLines = ["b"], DefaultLineId = "b" },
        Extensions = [new("shared-user", new() { AllowCalls = true, AllowSms = false, AllowInternalCalls = true,
            AllowMessages = false, OutgoingLines = ["a"], SmsSendLines = [] })],
        PeerAllowOutboundCalls = false, PeerAllowOutboundMessages = true, PeerAllowIncomingCalls = false, PeerAllowIncomingMessages = true,
        PeerOutgoingLines = [], PeerSmsSendLines = ["a"],
        LineRoutes = new()
        {
            ["a"] = new() { AllowOutgoingSms = true, ForwardIncomingSms = true, LocalIncomingExtensions = ["main"],
                LocalSmsRecipients = ["shared-user"], ForwardCallsToPeer = false, ForwardSmsToPeer = true, SmsTarget = "600" },
            ["b"] = new() { AllowOutgoingSms = true, LocalIncomingExtensions = [], LocalSmsRecipients = [],
                ForwardCallsToPeer = false, ForwardSmsToPeer = false }
        }
    };
    private static SipGatewayCredentials Credentials() => new("synthetic-primary", new() { ["shared-user"] = "synthetic-extra" },
        "synthetic-peer-in", "synthetic-peer-out");

    [Fact]
    public void BuildOptionsMapsIndependentMainExtraAndPeerPermissions()
    {
        var settings = Settings();
        var options = SipGatewayDeploymentProfile.BuildOptions(settings, Credentials());
        Assert.True(options.AllowCarrierCalls); Assert.True(options.AllowOutgoingSms);
        var main = options.ExtensionPolicies["main"];
        Assert.False(main.AllowCalls); Assert.True(main.AllowSms); Assert.False(main.AllowInternalCalls); Assert.True(main.AllowMessages);
        Assert.Empty(main.OutgoingLines!); Assert.Equal(["b"], main.SmsSendLines); Assert.Equal("b", main.DefaultLineId);
        var extra = options.ExtensionPolicies["shared-user"];
        Assert.True(extra.AllowCalls); Assert.False(extra.AllowSms); Assert.True(extra.AllowInternalCalls); Assert.False(extra.AllowMessages);
        Assert.Equal(["a"], extra.OutgoingLines); Assert.Empty(extra.SmsSendLines!);
        Assert.False(options.Peer!.AllowOutboundCalls); Assert.True(options.Peer.AllowOutboundMessages);
        Assert.False(options.Peer.AllowIncomingCalls); Assert.True(options.Peer.AllowIncomingMessages);
        Assert.Empty(options.Peer.OutgoingLines!); Assert.Equal(["a"], options.Peer.SmsSendLines);
        Assert.Equal(["main"], options.LineRoutes["a"].LocalIncomingExtensions);
        Assert.Equal(["shared-user"], options.LineRoutes["a"].LocalSmsRecipients);
        Assert.False(options.LineRoutes["a"].ForwardCallsToPeer); Assert.True(options.LineRoutes["a"].ForwardSmsToPeer);
        // Returned options must not retain mutable lists owned by preferences.
        settings.PeerSmsSendLines!.Clear();
        Assert.Equal(["a"], options.Peer.SmsSendLines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyNullPeerPermissionsKeepOnlyTheEstablishedFallback(bool enabled)
    {
        var settings = Settings() with { PrimaryPolicy = null, PeerAllowOutboundCalls = null, PeerAllowOutboundMessages = null,
            PeerAllowIncomingCalls = null, PeerAllowIncomingMessages = null, PeerOutgoingLines = null, PeerSmsSendLines = null,
            AllowOutboundCalls = enabled, AllowOutgoingSms = enabled };
        var options = SipGatewayDeploymentProfile.BuildOptions(settings, Credentials());
        Assert.Equal(enabled, options.Peer!.AllowOutboundCalls); Assert.Equal(enabled, options.Peer.AllowOutboundMessages);
        Assert.Null(options.Peer.AllowIncomingCalls); Assert.Null(options.Peer.AllowIncomingMessages);
        Assert.Null(options.Peer.OutgoingLines); Assert.Null(options.Peer.SmsSendLines);
        Assert.False(options.ExtensionPolicies.ContainsKey("main"));
    }

    [Fact]
    public void EditorPreservesNullVersusEmptyAndDoesNotDropUnknownEntries()
    {
        var editor = new SipPolicyEditor(new() { AllowCalls = true });
        Assert.Equal("*", editor.OutgoingLines); Assert.Equal("*", editor.SmsSendLines);
        Assert.Null(editor.SnapshotPolicy().AllowMessages); Assert.Null(editor.SnapshotPolicy().OutgoingLines);
        editor.OutgoingLines = ""; editor.SmsSendLines = "a, unknown";
        editor.AllowInternalCalls = false; editor.AllowMessages = false;
        var snapshot = editor.SnapshotPolicy();
        Assert.Empty(snapshot.OutgoingLines!); Assert.Equal(["a", "unknown"], snapshot.SmsSendLines);
        Assert.False(snapshot.AllowInternalCalls); Assert.False(snapshot.AllowMessages);
        var settings = Settings() with { PrimaryPolicy = snapshot };
        Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(settings, Credentials()));
    }

    [Fact]
    public async Task SettingsUiRoundTripsEveryGrantAndMd5OnlyWithoutStartingAnyService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-independent-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json"); var store = new SipGatewaySettingsStore(path);
            await store.SaveConnectionAsync(Settings(), Credentials(), enabled: false);
            await using var kernel = new VoKernel();
            var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
            ((PhoneCallServiceProxy)service).Handler = (method, _) => method.Name switch
            {
                "get_Kernel" => kernel, "GetSipGatewayStatus" => null,
                _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
                _ => throw new NotSupportedException("No operation authorized by this synthetic test")
            };
            var model = new SettingsViewModel(service, store); await model.LoadSipSettingsAsync();
            Assert.False(model.SipPeerAllowSha256); Assert.True(model.SipPeerAllowMd5);
            Assert.False(model.SipPrimaryPolicy.AllowCalls); Assert.Equal("", model.SipPrimaryPolicy.OutgoingLines);
            Assert.Equal("b", model.SipPrimaryPolicy.SmsSendLines);
            Assert.Equal("a", Assert.Single(model.SipExtensions).OutgoingLines);
            Assert.Equal("", model.SipPeerOutgoingLines); Assert.Equal("a", model.SipPeerSmsSendLines);
            Assert.Equal("main", model.SipModuleRoutes.Single(row => row.SlotId == "a").LocalIncomingExtensions);
            model.RefreshSipModuleRoutesCore(); Assert.Equal("b", model.SipPrimaryPolicy.DefaultLineId);
            var options = model.BuildSipGatewayOptions(); Assert.Equal(["MD5"], options.Peer!.OutboundAuth!.AllowedAlgorithms);
            Assert.False(options.ExtensionPolicies["main"].AllowCalls); Assert.True(options.ExtensionPolicies["main"].AllowSms);
            Assert.False(options.Peer.AllowOutboundCalls); Assert.True(options.Peer.AllowOutboundMessages);
            await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
            var reloaded = await new SipGatewaySettingsStore(path).LoadAsync();
            Assert.False(reloaded.WasEnabled); Assert.Equal(["MD5"], reloaded.PeerAllowedAlgorithms);
            Assert.Equal(["b"], reloaded.PrimaryPolicy!.SmsSendLines); Assert.Empty(reloaded.PrimaryPolicy.OutgoingLines!);
            Assert.False(reloaded.Extensions![0].Policy.AllowMessages); Assert.Empty(reloaded.Extensions[0].Policy.SmsSendLines!);
            Assert.Equal(["a"], reloaded.PeerSmsSendLines); Assert.Empty(reloaded.PeerOutgoingLines!);
            Assert.False(reloaded.PeerAllowIncomingCalls); Assert.True(reloaded.PeerAllowIncomingMessages);
            Assert.Equal(["shared-user"], reloaded.LineRoutes["a"].LocalSmsRecipients);
            Assert.True(reloaded.LineRoutes["a"].ForwardSmsToPeer); Assert.Empty(reloaded.LineRoutes["b"].LocalIncomingExtensions!);
            var restored = await new SipGatewaySettingsStore(path).LoadCredentialsAsync(reloaded);
            Assert.Equal(Credentials().MainPassword, restored.MainPassword); Assert.Equal(Credentials().PeerOutboundPassword, restored.PeerOutboundPassword);
            Assert.Null(kernel.LocalSipGateway); Assert.Empty(kernel.Pool.Slots);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task V2ImportPreservesTypedPoliciesAndRejectsInvalidAclShapesWithoutEcho()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-independent-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var credentials = Credentials();
            var envelope = JsonSerializer.SerializeToNode(new { Version = 2, Settings = Settings(), Credentials = new
                { credentials.MainPassword, credentials.Extensions, credentials.PeerInboundPassword, credentials.PeerOutboundPassword } })!;
            var path = Path.Combine(directory, "import.json");
            await File.WriteAllTextAsync(path, envelope.ToJsonString());
            var profile = await SipGatewayDeploymentProfile.ReadAsync(path);
            var options = SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials);
            Assert.Empty(options.ExtensionPolicies["main"].OutgoingLines!);
            Assert.Equal(["b"], options.ExtensionPolicies["main"].SmsSendLines);
            Assert.False(options.ExtensionPolicies["shared-user"].AllowMessages);
            Assert.Empty(options.Peer!.OutgoingLines!); Assert.Equal(["a"], options.Peer.SmsSendLines);
            Assert.Equal(["MD5"], options.Peer.OutboundAuth!.AllowedAlgorithms);
            Assert.Equal(["main"], options.LineRoutes["a"].LocalIncomingExtensions);
            Assert.Empty(options.LineRoutes["b"].LocalSmsRecipients!);
            foreach (var invalid in new[] { "\"synthetic-no-echo-acl\"", "[null]", "[\"unknown\"]", "[\"a\",\"A\"]", "[false]" })
            {
                var changed = envelope.DeepClone();
                changed["Settings"]!["PrimaryPolicy"]!["OutgoingLines"] = JsonNode.Parse(invalid);
                await File.WriteAllTextAsync(path, changed.ToJsonString());
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => SipGatewayDeploymentProfile.ReadAsync(path));
                Assert.DoesNotContain("synthetic-no-echo-acl", error.ToString());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExplicitDefaultShapedMainPolicyCannotDisappearAndGrantPaidSms()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-main-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json"); var store = new SipGatewaySettingsStore(path);
            var explicitPolicy = Settings() with { PrimaryPolicy = new() { AllowCalls = true, AllowSms = false } };
            await store.SaveConnectionAsync(explicitPolicy, Credentials(), enabled: false);
            await using var kernel = new VoKernel(); var model = new SettingsViewModel(ServiceFor(kernel), store);
            await model.LoadSipSettingsAsync(); Assert.False(model.SipPrimaryPolicy.AllowSms);
            await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
            var saved = await store.LoadAsync();
            Assert.NotNull(saved.PrimaryPolicy); Assert.False(saved.PrimaryPolicy.AllowSms); Assert.True(saved.AllowOutgoingSms);
            var options = model.BuildSipGatewayOptions();
            Assert.True(options.AllowOutgoingSms); Assert.False(options.ExtensionPolicies["main"].AllowSms);
            var legacyStore = new SipGatewaySettingsStore(Path.Combine(directory, "legacy.json"));
            await legacyStore.SaveConnectionAsync(explicitPolicy with { PrimaryPolicy = null }, Credentials(), enabled: false);
            var legacyModel = new SettingsViewModel(ServiceFor(kernel), legacyStore); await legacyModel.LoadSipSettingsAsync();
            Assert.True(legacyModel.SipPrimaryPolicy.AllowSms); // Actual absent-policy semantics, not a false-looking grant.
            await legacyModel.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
            Assert.Null((await legacyStore.LoadAsync()).PrimaryPolicy);
            Assert.False(legacyModel.BuildSipGatewayOptions().ExtensionPolicies.ContainsKey("main"));
            Assert.Null(kernel.LocalSipGateway);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.2")]
    [InlineData("fd00::1", "fd00::2")]
    [InlineData("fe80::1%5", "fe80::2%6")]
    public void PublicDiagnosticsSerializeNetworkValuesAsStringsWithoutAnyCredentials(string local, string peer)
    {
        var options = SipGatewayDeploymentProfile.BuildOptions(Settings() with { BindAddress = local, ServerAddress = peer }, Credentials());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(options));
        Assert.Equal(local, document.RootElement.GetProperty("BindAddress").GetString());
        Assert.Equal(options.Peer!.EndPoint.ToString(), document.RootElement.GetProperty("Peer").GetProperty("EndPoint").GetString());
        var json = document.RootElement.GetRawText();
        foreach (var secret in new[] { Credentials().MainPassword, Credentials().Extensions!["shared-user"], Credentials().PeerInboundPassword, Credentials().PeerOutboundPassword })
            Assert.DoesNotContain(secret, json);
        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SipGatewayOptions>("{\"BindAddress\":\"synthetic-do-not-echo\"}"));
        Assert.DoesNotContain("synthetic-do-not-echo", error.ToString());
    }

    [Fact]
    public void NewOptionalPreferencesDoNotChangeLegacyPublicJson()
    {
        var settings = new SipGatewayPreferences();
        var json = JsonSerializer.Serialize(settings);
        foreach (var name in new[] { "PrimaryPolicy", "PeerAllowOutboundCalls", "PeerAllowOutboundMessages", "PeerAllowIncomingCalls",
            "PeerAllowIncomingMessages", "PeerOutgoingLines", "PeerSmsSendLines" }) Assert.DoesNotContain(name, json);
    }
}
