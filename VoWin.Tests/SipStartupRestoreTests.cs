using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class SipStartupRestoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vowin-sip-startup-" + Guid.NewGuid().ToString("N"));
    private SipGatewaySettingsStore _store = null!;
    private VoKernelService _service = null!;
    private string SettingsPath => Path.Combine(_directory, "sip.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new(SettingsPath);
        _service = new(new PreferenceDatabaseService(Path.Combine(_directory, "preferences.db")),
            new(Path.Combine(_directory, "telephony.json")), Path.Combine(_directory, "test.log"));
        return Task.CompletedTask;
    }

    private static SipGatewayPreferences Settings(int port = 0)
    {
        if (port == 0)
        {
            using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        }
        return new()
        {
            UseTrunk = true, BindAddress = "127.0.0.1", Port = port,
            ServerAddress = "127.0.0.1", ServerPort = 5060,
            CredentialVersion = 2, LocalRegistrarEnabled = true,
            PeerInboundAlgorithm = "SHA-256", PeerOutboundUsername = "pbx-out", PeerExpectedRealm = "test-pbx.local",
            PeerAllowedAlgorithms = ["SHA-256"],
            RestoreOnStartup = true,
            LineRoutes = new() { ["remembered-offline-module"] = new SipGatewayLineRoute() }
        };
    }

    private Task SaveConnectionAsync(SipGatewayPreferences settings, string password, bool enabled) =>
        _store.SaveConnectionAsync(settings, new SipGatewayCredentials(password, PeerInboundPassword: password + "-in",
            PeerOutboundPassword: password + "-out"), enabled);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordIsCurrentUserProtectedAndSurvivesStoreRecreation(bool enabled)
    {
        const string password = "offline-dpapi-startup-secret";
        await SaveConnectionAsync(Settings(), password, enabled);
        var reloadedStore = new SipGatewaySettingsStore(SettingsPath);
        var settings = await reloadedStore.LoadAsync();
        Assert.Equal(enabled, settings.WasEnabled);
        Assert.Equal(password, await reloadedStore.LoadRememberedPasswordAsync(settings));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(SettingsPath));
        Assert.DoesNotContain("Password", await File.ReadAllTextAsync(SettingsPath));
        var encrypted = await File.ReadAllBytesAsync(SettingsPath + ".protected");
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain(password, Encoding.Unicode.GetString(encrypted));
        Assert.Null(_service.Kernel.LocalSipGateway);
    }

    [Fact]
    public async Task ChangedConfigurationCannotReuseAnUnrelatedRememberedPassword()
    {
        await SaveConnectionAsync(Settings(), "offline-binding-secret", true);
        var settings = await _store.LoadAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.LoadRememberedPasswordAsync(settings with { ServerAddress = "10.0.0.9" }));
    }

    [Fact]
    public async Task DisablingRememberClearsCredentialAndStartupIntent()
    {
        var settings = Settings();
        await SaveConnectionAsync(settings, "offline-forget-secret", true);
        await _store.SaveConnectionAsync(settings with { RestoreOnStartup = false }, "", false);
        var loaded = await _store.LoadAsync();
        Assert.False(loaded.RestoreOnStartup);
        Assert.False(loaded.WasEnabled);
        Assert.False(File.Exists(SettingsPath + ".protected"));
        Assert.Empty(await _store.LoadRememberedPasswordAsync(loaded));
        await _service.RestoreSipGatewayOnStartupAsync(_store);
        Assert.Null(_service.Kernel.LocalSipGateway);
    }

    [Fact]
    public async Task StartupRestoresEnabledListenerWithoutSettingsNavigationOrAnyModem()
    {
        await SaveConnectionAsync(Settings(), "offline-auto-start-secret", true);
        await _service.RestoreSipGatewayOnStartupAsync(_store);
        Assert.True(_service.GetSipGatewayStatus()?.IsRunning);
        Assert.True(_service.GetSipGatewayStatus()?.LocalIncomingEnabled);
        Assert.Empty(_service.Kernel.Pool.Slots);
        Assert.Null(_service.Kernel.ActiveCall);
    }

    [Fact]
    public async Task RememberedButDisabledLoadsPasswordIntoSettingsWithoutStartingSip()
    {
        await SaveConnectionAsync(Settings(), "offline-remember-disabled", false);
        await _service.RestoreSipGatewayOnStartupAsync(_store);
        var model = new SettingsViewModel(_service, _store);
        await model.LoadSipSettingsAsync();
        Assert.True(model.SipGatewayRestoreOnStartup);
        Assert.Equal("offline-remember-disabled", model.SipGatewayPassword);
        Assert.False(model.IsSipGatewayRunning);
        Assert.Null(_service.Kernel.LocalSipGateway);
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("missing")]
    [InlineData("public-peer")]
    public async Task InvalidStartupConfigurationFailsClosed(string failure)
    {
        await SaveConnectionAsync(Settings(), "offline-fail-closed-secret", true);
        if (failure == "tamper") await File.WriteAllBytesAsync(SettingsPath + ".protected", [1, 2, 3, 4]);
        else if (failure == "missing") File.Delete(SettingsPath + ".protected");
        else
        {
            var settings = await _store.LoadAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => SaveConnectionAsync(settings with { ServerAddress = "8.8.8.8" }, "offline-fail-closed-secret", true));
            // Simulate editing the public JSON around the guarded save API as well.
            await _store.SaveAsync(settings with { ServerAddress = "8.8.8.8" });
        }
        await _service.RestoreSipGatewayOnStartupAsync(_store);
        Assert.Null(_service.Kernel.LocalSipGateway);
        Assert.Empty(_service.Kernel.Pool.Slots);
    }

    [Fact]
    public async Task UserTogglePersistsLastStateAndDoesNotTreatProcessExitAsUserDisable()
    {
        await SaveConnectionAsync(Settings(), "offline-toggle-secret", false);
        var model = new SettingsViewModel(_service, _store);
        await model.LoadSipSettingsAsync();
        await model.ToggleSipGatewayCommand.ExecuteAsync(null);
        Assert.True(model.IsSipGatewayRunning);
        Assert.True((await _store.LoadAsync()).WasEnabled);
        await model.ToggleSipGatewayCommand.ExecuteAsync(null);
        Assert.False(model.IsSipGatewayRunning);
        Assert.False((await _store.LoadAsync()).WasEnabled);
        await model.ToggleSipGatewayCommand.ExecuteAsync(null);
        Assert.True((await _store.LoadAsync()).WasEnabled);
        await _service.DisposeAsync();
        Assert.True((await _store.LoadAsync()).WasEnabled);
    }

    [Fact]
    public async Task ConcurrentSavesCannotMixProtectedPasswordAndSettings()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            SaveConnectionAsync(Settings(20000 + index), "offline-concurrent-" + (20000 + index), true)));
        var settings = await _store.LoadAsync();
        Assert.Equal("offline-concurrent-" + settings.Port, await _store.LoadRememberedPasswordAsync(settings));
    }

    [Fact]
    public async Task ExplicitLegacyFileMigratesOnceAsDisabledDraftWithoutReusingSharedCredentials()
    {
        var path = Path.Combine(_directory, "legacy-profile.json");
        var settings = Settings() with { CredentialVersion = 0, LocalRegistrarEnabled = null,
            PeerInboundAlgorithm = null, PeerOutboundUsername = null, PeerExpectedRealm = null, PeerAllowedAlgorithms = null };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new SipGatewayDeploymentProfile(settings, "offline-legacy-secret")));
        await _store.SaveAsync(settings with { LegacyDeploymentPath = path, WasEnabled = true });
        await _service.RestoreSipGatewayOnStartupAsync(_store);
        Assert.Null(_service.Kernel.LocalSipGateway);
        var migrated = await _store.LoadAsync();
        Assert.Null(migrated.LegacyDeploymentPath);
        Assert.False(migrated.WasEnabled); Assert.False(migrated.HasLocalRegistrar);
        File.Delete(path);
        var credentials = await _store.LoadCredentialsAsync(migrated);
        Assert.Equal("offline-legacy-secret", credentials.LegacyTrunkPassword);
        Assert.Empty(credentials.MainPassword); Assert.Empty(credentials.PeerInboundPassword); Assert.Empty(credentials.PeerOutboundPassword);
        Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(migrated, credentials));
    }

    [Fact]
    public async Task LegacyMigrationRejectsNetworkFilesAndMismatchedAuthenticationEndpoints()
    {
        var settings = Settings() with { WasEnabled = true, LegacyDeploymentPath = @"\\unreachable.invalid\share\profile.json" };
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.MigrateLegacyDeploymentAsync(settings));
        var path = Path.Combine(_directory, "wrong-profile.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new SipGatewayDeploymentProfile(
            settings with { ServerAddress = "10.0.0.9", LegacyDeploymentPath = null }, "offline-wrong-endpoint")));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.MigrateLegacyDeploymentAsync(settings with { LegacyDeploymentPath = path }));
        Assert.False(File.Exists(SettingsPath + ".protected"));
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
