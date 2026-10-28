using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

/// <summary>Only temporary files, DPAPI and in-memory settings; no sockets, modems or live application.</summary>
public sealed class SipPeerSettingsV2Tests
{
    private static SipGatewayPreferences PeerSettings() => new()
    {
        CredentialVersion = 2, LocalRegistrarEnabled = false, UseTrunk = true,
        BindAddress = "127.0.0.1", Port = 5062, Realm = "desk-test.local",
        Extension = "local-main", TrunkUsername = "peer-in", ServerAddress = "127.0.0.2",
        ServerPort = 5060, IncomingTarget = "ring-test", PeerInboundAlgorithm = "SHA-256",
        PeerOutboundUsername = "peer-out", PeerExpectedRealm = "pbx-test.local",
        PeerAllowedAlgorithms = ["SHA-256"], RestoreOnStartup = true,
        LineRoutes = new() { ["offline-line"] = new() { LocalIncomingTarget = "local-main", LocalSmsTarget = "local-extra" } }
    };

    private static SipGatewayCredentials Credentials() => new("main-only-secret",
        new() { ["local-extra"] = "extra-only-secret" }, "in-only-secret", "out-only-secret");

    [Fact]
    public void PeerOnlyDoesNotCreateALocalAccountOrReuseCredentials()
    {
        var options = SipGatewayDeploymentProfile.BuildOptions(PeerSettings(), Credentials());
        Assert.Empty(options.Accounts);
        Assert.Equal("peer-in", options.Peer!.InboundAuth!.Username);
        Assert.Equal("in-only-secret", options.Peer.InboundAuth.Password);
        Assert.Equal("peer-out", options.Peer.OutboundAuth!.Username);
        Assert.Equal("out-only-secret", options.Peer.OutboundAuth.Password);
        Assert.Equal("pbx-test.local", options.Peer.OutboundAuth.ExpectedRealm);
        Assert.Equal(["SHA-256"], options.Peer.OutboundAuth.AllowedAlgorithms);
        Assert.Empty(options.Peer.Password);
        Assert.False(options.Peer.AllowOutboundCalls); Assert.False(options.Peer.AllowOutboundMessages);
    }

    [Fact]
    public void LocalRegistrarCanCoexistWithIndependentPeerCredentials()
    {
        var settings = PeerSettings() with { LocalRegistrarEnabled = true,
            Extensions = [new("local-extra", new() { AllowCalls = false, AllowSms = false })] };
        var options = SipGatewayDeploymentProfile.BuildOptions(settings, Credentials());
        Assert.Equal(2, options.Accounts.Count);
        Assert.Equal("main-only-secret", options.Accounts["local-main"]);
        Assert.Equal("extra-only-secret", options.Accounts["local-extra"]);
        Assert.DoesNotContain("peer-in", options.Accounts.Keys);
        Assert.DoesNotContain("peer-out", options.Accounts.Keys);
        Assert.Equal("local-main", options.LineRoutes["offline-line"].LocalIncomingTarget);
        Assert.Equal("local-extra", options.LineRoutes["offline-line"].LocalSmsTarget);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData(" ")]
    public void MissingOrWildcardPeerRealmCannotEnable(string realm) => Assert.Throws<ArgumentException>(() =>
        SipGatewayDeploymentProfile.BuildOptions(PeerSettings() with { PeerExpectedRealm = realm }, Credentials()));

    [Fact]
    public async Task V2RolesAreProtectedTogetherAndBoundToCompletePublicSettings()
    {
        await WithStore(async (store, path) =>
        {
            await store.SaveConnectionAsync(PeerSettings(), Credentials(), enabled: true);
            var loaded = await store.LoadAsync();
            Assert.Equal(2, loaded.CredentialVersion); Assert.True(loaded.WasEnabled);
            var restored = await new SipGatewaySettingsStore(path).LoadCredentialsAsync(loaded);
            Assert.Equal("main-only-secret", restored.MainPassword);
            Assert.Equal("in-only-secret", restored.PeerInboundPassword);
            Assert.Equal("out-only-secret", restored.PeerOutboundPassword);
            Assert.Equal("extra-only-secret", restored.Extensions!["local-extra"]);
            var json = await File.ReadAllTextAsync(path);
            var encrypted = await File.ReadAllBytesAsync(path + ".protected");
            foreach (var secret in new[] { "main-only-secret", "extra-only-secret", "in-only-secret", "out-only-secret" })
            {
                Assert.DoesNotContain(secret, json);
                Assert.DoesNotContain(secret, Encoding.UTF8.GetString(encrypted));
                Assert.DoesNotContain(secret, Encoding.Unicode.GetString(encrypted));
                Assert.DoesNotContain(secret, restored.ToString());
            }
            SipGatewayPreferences[] mutations = [
                loaded with { Realm = "other.local" }, loaded with { PeerExpectedRealm = "other-pbx.local" },
                loaded with { PeerOutboundUsername = "different" }, loaded with { TrunkUsername = "different" },
                loaded with { PeerAllowedAlgorithms = ["SHA-256", "MD5"] }, loaded with { PeerInboundAlgorithm = "MD5" },
                loaded with { LocalRegistrarEnabled = true }, loaded with { AllowOutboundCalls = true },
                loaded with { WasEnabled = false }, loaded with { ServerPort = 5099 },
                loaded with { LineRoutes = new() { ["offline-line"] = new() { LocalSmsTarget = "changed" } } }
            ];
            foreach (var mutation in mutations)
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadCredentialsAsync(mutation));
        });
    }

    [Fact]
    public async Task V2CannotFallBackToLegacyHashOrEnvelope()
    {
        await WithStore(async (store, path) =>
        {
            await store.SaveConnectionAsync(PeerSettings(), Credentials(), enabled: false);
            var loaded = await store.LoadAsync();
            var downgraded = loaded with { CredentialVersion = 0, LocalRegistrarEnabled = null,
                PeerInboundAlgorithm = null, PeerOutboundUsername = null, PeerExpectedRealm = null, PeerAllowedAlgorithms = null };
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadCredentialsAsync(downgraded));
            var legacyPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SettingsHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(loaded))), Password = "legacy-secret"
            });
            try { await File.WriteAllBytesAsync(path + ".protected", SipStartupSecretProtection.Protect(legacyPayload)); }
            finally { CryptographicOperations.ZeroMemory(legacyPayload); }
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadCredentialsAsync(loaded));
        });
    }

    [Fact]
    public async Task LegacyTrunkImportRemainsEditableButSharedSecretNeverBecomesARole()
    {
        await WithStore(async (store, path) =>
        {
            var legacy = PeerSettings() with { CredentialVersion = 0, LocalRegistrarEnabled = null,
                PeerInboundAlgorithm = null, PeerOutboundUsername = null, PeerExpectedRealm = null, PeerAllowedAlgorithms = null };
            var profilePath = path + ".import.json";
            await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(new SipGatewayDeploymentProfile(legacy, "old-shared-secret")));
            var profile = await SipGatewayDeploymentProfile.ReadAsync(profilePath);
            Assert.False(profile.Settings.HasLocalRegistrar);
            Assert.Equal("old-shared-secret", profile.Credentials.LegacyTrunkPassword);
            Assert.Empty(profile.Credentials.MainPassword); Assert.Empty(profile.Credentials.PeerInboundPassword);
            Assert.Empty(profile.Credentials.PeerOutboundPassword);
            Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(profile.Settings, profile.Credentials));
            await using var kernel = new VoKernel();
            var model = new SettingsViewModel(FakeService(kernel), store);
            await model.LoadSipDeploymentAsync(profilePath);
            Assert.True(model.SipGatewayUseTrunk); Assert.False(model.SipLocalRegistrarEnabled);
            Assert.Equal("127.0.0.2", model.SipGatewayServerAddress);
            Assert.Empty(model.SipGatewayPassword); Assert.Empty(model.SipPeerInboundPassword); Assert.Empty(model.SipPeerOutboundPassword);
            Assert.Empty(model.SipPeerExpectedRealm); Assert.False((await store.LoadAsync()).WasEnabled);
            Assert.Throws<ArgumentException>(() => model.BuildSipGatewayOptions());
            var protectedRoles = await store.LoadCredentialsAsync(await store.LoadAsync());
            Assert.Equal("old-shared-secret", protectedRoles.LegacyTrunkPassword);
            Assert.Null(kernel.LocalSipGateway);
        });
    }

    [Fact]
    public async Task PublicPeerProfileDoesNotExportOrImportNewSecrets()
    {
        await WithStore(async (_, path) =>
        {
            var profile = new SipGatewayDeploymentProfile(PeerSettings(), "", PeerInboundPassword: "never-plaintext-in",
                PeerOutboundPassword: "never-plaintext-out");
            var json = JsonSerializer.Serialize(profile);
            Assert.DoesNotContain("never-plaintext", json);
            Assert.DoesNotContain("PeerInboundPassword", json); Assert.DoesNotContain("PeerOutboundPassword", json);
            await File.WriteAllTextAsync(path, json);
            var imported = await SipGatewayDeploymentProfile.ReadAsync(path);
            Assert.Empty(imported.Credentials.PeerInboundPassword); Assert.Empty(imported.Credentials.PeerOutboundPassword);
            Assert.Equal("pbx-test.local", imported.Settings.PeerExpectedRealm);
            Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(imported.Settings, imported.Credentials));
        });
    }

    [Fact]
    public async Task IncompleteDraftCanBeRememberedButNotEnabledAndStopNeedsNoCredentials()
    {
        await WithStore(async (store, path) =>
        {
            var incomplete = PeerSettings() with { PeerExpectedRealm = "" };
            await store.SaveConnectionAsync(incomplete, new SipGatewayCredentials(), enabled: false);
            Assert.False((await store.LoadAsync()).WasEnabled);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveConnectionAsync(incomplete, new SipGatewayCredentials(), enabled: true));
            await File.WriteAllBytesAsync(path + ".protected", [1, 2, 3]);
            await store.MarkDisabledAsync();
            var loaded = await store.LoadAsync();
            Assert.False(loaded.WasEnabled); Assert.True(loaded.UseTrunk); Assert.Equal("127.0.0.2", loaded.ServerAddress);
        });
    }

    [Fact]
    public async Task ViewModelIdentityEditsInvalidateOnlyTheRelevantTypedPasswordAndRoutesSurvive()
    {
        await WithStore(async (store, _) =>
        {
            var settings = PeerSettings();
            await store.SaveConnectionAsync(settings, Credentials(), enabled: false);
            await using var kernel = new VoKernel();
            var model = new SettingsViewModel(FakeService(kernel), store);
            await model.LoadSipSettingsAsync();
            Assert.Equal("in-only-secret", model.SipPeerInboundPassword); Assert.Equal("out-only-secret", model.SipPeerOutboundPassword);
            model.SipPeerExpectedRealm = "new.realm";
            Assert.Empty(model.SipPeerOutboundPassword); Assert.Equal("in-only-secret", model.SipPeerInboundPassword);
            model.SipPeerOutboundPassword = "new-out-secret";
            model.SipGatewayTrunkUsername = "new-in";
            Assert.Empty(model.SipPeerInboundPassword); Assert.Equal("new-out-secret", model.SipPeerOutboundPassword);
            Assert.Equal("main-only-secret", model.SipGatewayPassword);
            var route = Assert.Single(model.SipModuleRoutes);
            Assert.Equal("local-main", route.Snapshot().LocalIncomingTarget);
            Assert.Equal("local-extra", route.Snapshot().LocalSmsTarget);
            model.RefreshSipGatewayCommand.Execute(null);
            Assert.Equal("new.realm", model.SipPeerExpectedRealm); Assert.Equal("new-out-secret", model.SipPeerOutboundPassword);
            model.SipPeerInboundPassword = "new-in-secret";
            model.SipGatewayServerAddress = "127.0.0.3";
            Assert.Empty(model.SipPeerInboundPassword); Assert.Empty(model.SipPeerOutboundPassword);
            model.SipPeerInboundPassword = "another-in-secret"; model.SipPeerOutboundPassword = "another-out-secret";
            model.SipGatewayServerPort++;
            Assert.Empty(model.SipPeerInboundPassword); Assert.Empty(model.SipPeerOutboundPassword);
            Assert.Equal("main-only-secret", model.SipGatewayPassword);
            await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
            var saved = await store.LoadAsync();
            Assert.Equal("local-extra", saved.LineRoutes["offline-line"].LocalSmsTarget);
        });
    }

    [Fact]
    public async Task StopWorksWithIncompletePeerDraftAndCannotBeBlockedBySecretDecryption()
    {
        await WithStore(async (store, path) =>
        {
            await store.SaveConnectionAsync(PeerSettings() with { PeerExpectedRealm = "" }, new SipGatewayCredentials(), enabled: false);
            await File.WriteAllBytesAsync(path + ".protected", [1, 2, 3]);
            await using var kernel = new VoKernel();
            var running = true; var stops = 0; var starts = 0;
            var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
            ((PhoneCallServiceProxy)service).Handler = (method, _) => method.Name switch
            {
                "get_Kernel" => kernel,
                "GetSipGatewayStatus" => new SipGatewayStatus(running, null, [], 0),
                "StopSipGatewayAsync" => Stop(),
                "StartSipGatewayAsync" => Start(),
                _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
                _ => throw new NotSupportedException(method.Name)
            };
            Task Stop() { stops++; running = false; return Task.CompletedTask; }
            Task Start() { starts++; throw new InvalidOperationException("No start expected"); }
            var model = new SettingsViewModel(service, store) { SipGatewayUseTrunk = true };
            Assert.False(model.CanEditSipGateway); // Even before the UI has polled its running flag.
            await model.ToggleSipGatewayCommand.ExecuteAsync(null);
            Assert.Equal(1, stops); Assert.Equal(0, starts); Assert.False(running);
            Assert.False((await store.LoadAsync()).WasEnabled);
            Assert.True(model.CanEditSipGateway);
        });
    }

    [Fact]
    public async Task NewConfigurationHasNoPreassignedAccountPeerOrTargetAndForgettingRemovesAllSecrets()
    {
        await WithStore(async (store, path) =>
        {
            var initial = await store.LoadAsync();
            Assert.Empty(initial.Extension); Assert.Empty(initial.TrunkUsername); Assert.Empty(initial.IncomingTarget);
            Assert.False(initial.UseTrunk); Assert.True(initial.HasLocalRegistrar);
            await store.SaveConnectionAsync(PeerSettings(), Credentials(), enabled: false);
            await store.SaveConnectionAsync(PeerSettings() with { RestoreOnStartup = false }, new SipGatewayCredentials(), enabled: false);
            Assert.False(File.Exists(path + ".protected"));
            Assert.Equal(new SipGatewayCredentials(), await store.LoadCredentialsAsync(await store.LoadAsync()));
        });
    }

    private static IVoKernelService FakeService(VoKernel kernel)
    {
        var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)service).Handler = (method, _) => method.Name switch
        {
            "get_Kernel" => kernel, "GetSipGatewayStatus" => null,
            _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
            _ => throw new NotSupportedException(method.Name)
        };
        return service;
    }

    private static async Task WithStore(Func<SipGatewaySettingsStore, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-peer-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { var path = Path.Combine(directory, "sip.json"); await action(new(path), path); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
