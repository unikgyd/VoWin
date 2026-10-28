using System.IO;
using System.Reflection;
using System.Text.Json;
using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

/// <summary>Disposable settings/DPAPI and synthetic service proxies; no sockets or real application.</summary>
public sealed class SipSmsRecipientEpochSettingsTests
{
    private static SipGatewayPreferences Settings() => new()
    {
        CredentialVersion = 2, BindAddress = "127.0.0.1", Extension = "desk-main", RestoreOnStartup = true,
        UseTrunk = true, LocalRegistrarEnabled = true, Realm = "gateway.local", ServerAddress = "127.0.0.2",
        TrunkUsername = "pbx-in", IncomingTarget = "all", PeerOutboundUsername = "pbx-out",
        PeerExpectedRealm = "pbx.local", PeerInboundAlgorithm = "SHA-256", PeerAllowedAlgorithms = ["SHA-256"],
        Extensions = [new("desk-extra", new() { AllowSms = true })]
    };
    private static SipGatewayCredentials Credentials() => new("same-main-password", new() { ["desk-extra"] = "same-extra-password" },
        "same-in-password", "same-out-password");

    [Theory]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("downgrade")]
    public void PublicEpochMetadataCannotBeSilentlyProjectedOrDowngraded(string mutation)
    {
        var settings = Settings() with { UseTrunk = false };
        settings = mutation switch
        {
            "duplicate" => settings with { LocalAccountEpochs = new() { ["desk-main"] = new('a', 32), ["DESK-MAIN"] = new('b', 32) } },
            "malformed" => settings with { PeerCredentialEpoch = "unknown" },
            _ => settings with { CredentialVersion = 0, LocalAccountEpochs = new() { ["desk-main"] = new('a', 32) } }
        };
        Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(settings, Credentials()));
    }

    [Fact]
    public async Task SavingDisablingAndReopeningPreserveVerifiedReceiverIncarnations()
    {
        await WithStore(async (store, path) =>
        {
            var first = await store.PrepareConnectionAsync(Settings(), Credentials(), enabled: true);
            var second = await new SipGatewaySettingsStore(path).PrepareConnectionAsync(first, Credentials(), enabled: true);
            Assert.Equal(first.LocalAccountEpochs, second.LocalAccountEpochs); Assert.Equal(first.PeerCredentialEpoch, second.PeerCredentialEpoch);
            await store.MarkDisabledAsync(); var disabled = await store.LoadAsync();
            Assert.False(disabled.WasEnabled); Assert.Equal(first.LocalAccountEpochs, disabled.LocalAccountEpochs);
            Assert.Equal(first.PeerCredentialEpoch, disabled.PeerCredentialEpoch);
            var rebuilt = SipGatewayDeploymentProfile.BuildOptions(disabled, await store.LoadCredentialsAsync(disabled));
            Assert.Equal(first.LocalAccountEpochs!["desk-main"], rebuilt.LocalAccountEpochs["desk-main"]);
        });
    }

    [Fact]
    public async Task DeleteThenSamePasswordRecreateCannotReuseEvenAStaleSuppliedEpoch()
    {
        await WithStore(async (store, _) =>
        {
            var first = await store.PrepareConnectionAsync(Settings(), Credentials(), false);
            await store.PrepareConnectionAsync(first with { Extensions = [], LocalAccountEpochs = first.LocalAccountEpochs }, Credentials(), false);
            var recreated = await store.PrepareConnectionAsync(first, Credentials(), false);
            Assert.NotEqual(first.LocalAccountEpochs!["desk-extra"], recreated.LocalAccountEpochs!["desk-extra"]);
            Assert.Equal(first.LocalAccountEpochs["desk-main"], recreated.LocalAccountEpochs["desk-main"]);
        });
    }

    [Fact]
    public async Task ExplicitSamePasswordResetAndStaleMetadataCannotReactivateOldIdentity()
    {
        await WithStore(async (store, _) =>
        {
            var first = await store.PrepareConnectionAsync(Settings(), Credentials(), false);
            var requested = first with { LocalAccountEpochs = new(first.LocalAccountEpochs!) { ["desk-main"] = new('c', 32) }, PeerCredentialEpoch = new('d', 32) };
            var reset = await store.PrepareConnectionAsync(requested, Credentials(), false);
            Assert.NotEqual(first.LocalAccountEpochs!["desk-main"], reset.LocalAccountEpochs!["desk-main"]);
            Assert.NotEqual(new string('c', 32), reset.LocalAccountEpochs["desk-main"]);
            Assert.NotEqual(first.PeerCredentialEpoch, reset.PeerCredentialEpoch);
            var stale = await store.PrepareConnectionAsync(first, Credentials(), false);
            Assert.NotEqual(first.LocalAccountEpochs["desk-main"], stale.LocalAccountEpochs!["desk-main"]);
            Assert.NotEqual(reset.LocalAccountEpochs["desk-main"], stale.LocalAccountEpochs["desk-main"]);
            Assert.NotEqual(first.PeerCredentialEpoch, stale.PeerCredentialEpoch);
        });
    }

    [Fact]
    public async Task ChangedPasswordRotatesOnlyItsLocalReceiverAndPeerChangesRotatePeer()
    {
        await WithStore(async (store, _) =>
        {
            var original = Credentials(); var first = await store.PrepareConnectionAsync(Settings(), original, false);
            var changed = original with { MainPassword = "changed-main" };
            var second = await store.PrepareConnectionAsync(first, changed, false);
            Assert.NotEqual(first.LocalAccountEpochs!["desk-main"], second.LocalAccountEpochs!["desk-main"]);
            Assert.Equal(first.LocalAccountEpochs["desk-extra"], second.LocalAccountEpochs["desk-extra"]);
            Assert.Equal(first.PeerCredentialEpoch, second.PeerCredentialEpoch);
            var third = await store.PrepareConnectionAsync(second with { PeerExpectedRealm = "different.local" }, changed, false);
            Assert.NotEqual(second.PeerCredentialEpoch, third.PeerCredentialEpoch);
            Assert.Equal(second.LocalAccountEpochs, third.LocalAccountEpochs);
        });
    }

    [Fact]
    public async Task PublicEpochTamperingCannotReuseTheProtectedCredentialEnvelope()
    {
        await WithStore(async (store, _) =>
        {
            var saved = await store.PrepareConnectionAsync(Settings(), Credentials(), false);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadCredentialsAsync(saved with { PeerCredentialEpoch = new('a', 32) }));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadCredentialsAsync(saved with
                { LocalAccountEpochs = new(saved.LocalAccountEpochs!) { ["desk-main"] = new('a', 32) } }));
        });
    }

    [Fact]
    public async Task RealViewModelResetDeleteRecreateAndImportPathsRotateBeforeRuntimeBuild()
    {
        await WithStore(async (store, path) =>
        {
            var first = await store.PrepareConnectionAsync(Settings(), Credentials(), false);
            await using var kernel = new VoKernel(); var model = new SettingsViewModel(FakeService(kernel), store);
            await model.LoadSipSettingsAsync();
            Assert.Equal(first.LocalAccountEpochs!["desk-main"], model.BuildSipGatewayOptions().LocalAccountEpochs["desk-main"]);
            model.ResetSipMainRecipientIdentityCommand.Execute(null);
            model.ResetSipPeerRecipientIdentityCommand.Execute(null);
            model.ResetSipExtensionRecipientIdentityCommand.Execute(Assert.Single(model.SipExtensions));
            await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
            var reset = await store.LoadAsync();
            Assert.NotEqual(first.LocalAccountEpochs["desk-main"], reset.LocalAccountEpochs!["desk-main"]);
            Assert.NotEqual(first.LocalAccountEpochs["desk-extra"], reset.LocalAccountEpochs["desk-extra"]);
            Assert.NotEqual(first.PeerCredentialEpoch, reset.PeerCredentialEpoch);
            Assert.Equal("same-main-password", model.SipGatewayPassword);
            model.RemoveSipExtensionCommand.Execute(Assert.Single(model.SipExtensions));
            model.AddSipExtensionCommand.Execute(null);
            var row = Assert.Single(model.SipExtensions); row.Username = "desk-extra"; row.Password = "same-extra-password";
            await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
            var recreated = await store.LoadAsync();
            Assert.NotEqual(reset.LocalAccountEpochs["desk-extra"], recreated.LocalAccountEpochs!["desk-extra"]);
            var importedPath = path + ".import.json";
            await File.WriteAllTextAsync(importedPath, JsonSerializer.Serialize(new SipGatewayDeploymentProfile(recreated, "same-main-password",
                new() { ["desk-extra"] = "same-extra-password" })));
            await model.LoadSipDeploymentAsync(importedPath);
            var imported = await store.LoadAsync();
            Assert.NotEqual(recreated.LocalAccountEpochs["desk-main"], imported.LocalAccountEpochs!["desk-main"]);
            Assert.NotEqual(recreated.LocalAccountEpochs["desk-extra"], imported.LocalAccountEpochs["desk-extra"]);
            Assert.NotEqual(recreated.PeerCredentialEpoch, imported.PeerCredentialEpoch);
        });
    }

    [Fact]
    public async Task ListenerStartsOnlyAfterTheExactIncarnationsArePersisted()
    {
        await WithStore(async (store, _) =>
        {
            await store.PrepareConnectionAsync(Settings(), Credentials(), false);
            await using var kernel = new VoKernel(); var started = false;
            var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
            ((PhoneCallServiceProxy)service).Handler = (method, arguments) => method.Name switch
            {
                "get_Kernel" => kernel,
                "GetSipGatewayStatus" => new SipGatewayStatus(started, null, [], 0),
                "StartSipGatewayAsync" => Start((SipGatewayOptions)arguments![0]!),
                _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
                _ => throw new NotSupportedException(method.Name)
            };
            async Task Start(SipGatewayOptions options)
            {
                var saved = await store.LoadAsync();
                Assert.Equal(saved.LocalAccountEpochs, options.LocalAccountEpochs);
                Assert.Equal(saved.PeerCredentialEpoch, options.PeerCredentialEpoch);
                Assert.False(saved.WasEnabled); started = true;
            }
            var model = new SettingsViewModel(service, store); await model.LoadSipSettingsAsync();
            await model.ToggleSipGatewayCommand.ExecuteAsync(null);
            Assert.True(started); Assert.True((await store.LoadAsync()).WasEnabled);
        });
    }

    private static IVoKernelService FakeService(VoKernel kernel)
    {
        var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)service).Handler = (method, _) => method.Name switch
        { "get_Kernel" => kernel, "GetSipGatewayStatus" => null, _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
            _ => throw new NotSupportedException(method.Name) };
        return service;
    }
    private static async Task WithStore(Func<SipGatewaySettingsStore, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-sms-epochs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { var path = Path.Combine(directory, "sip.json"); await action(new(path), path); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
