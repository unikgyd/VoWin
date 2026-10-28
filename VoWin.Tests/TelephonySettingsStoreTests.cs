using System.IO;
using System.Text.Json;
using VoSharp.Kernel;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class TelephonySettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoWin-settings-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "telephony.json");

    [Fact]
    public async Task FreshInstallHasUsefulDefaults()
    {
        var settings = await new TelephonySettingsStore(SettingsPath).LoadAsync();
        Assert.True(settings.AutoDiscoverModems);
        Assert.Empty(settings.Sims);
        Assert.Equal(TelephonyMode.ModemBridge, new SimTelephonyPreferences().Mode);
    }

    [Fact]
    public async Task SimSelectionAndHostComponentsSurviveRestart()
    {
        await new TelephonySettingsStore(SettingsPath).UpdateAsync(settings =>
        {
            settings.AutoDiscoverModems = false;
            settings.WinDivertDllPath = @"C:\Program Files\VoWin\host-ims\WinDivert.dll";
            settings.Sims["test-sim-a"] = new(TelephonyMode.HostIms, true);
            settings.Sims["test-sim-b"] = new(TelephonyMode.VoWifi, false);
        });
        var restored = await new TelephonySettingsStore(SettingsPath).LoadAsync();
        Assert.False(restored.AutoDiscoverModems);
        Assert.Contains("Program Files", restored.WinDivertDllPath);
        Assert.Equal(new(TelephonyMode.HostIms, true), restored.Sims["test-sim-a"]);
        Assert.Equal(new(TelephonyMode.VoWifi, false), restored.Sims["test-sim-b"]);
    }

    [Fact]
    public async Task ConcurrentUpdatesDoNotLoseOtherSimPreferences()
    {
        var store = new TelephonySettingsStore(SettingsPath);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            store.UpdateAsync(settings => settings.Sims[index.ToString()] = new(TelephonyMode.HostIms))));
        Assert.Equal(20, (await store.LoadAsync()).Sims.Count);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public async Task FailedUpdatePreservesPreviousSettings()
    {
        var store = new TelephonySettingsStore(SettingsPath);
        await store.UpdateAsync(settings => settings.Sims["test-sim"] = new(TelephonyMode.VoWifi));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(settings =>
        {
            settings.Sims.Clear();
            throw new InvalidOperationException("simulated failure");
        }));
        Assert.Equal(TelephonyMode.VoWifi, (await store.LoadAsync()).Sims["test-sim"].Mode);
    }

    [Fact]
    public async Task DiscoverySettingsSurviveRestartPerSimWithoutChangingTelephonyIntent()
    {
        var store = new TelephonySettingsStore(SettingsPath);
        await store.UpdateAsync(settings =>
        {
            settings.Sims["sim-a"] = new(TelephonyMode.HostIms, false, true, TelephonyMode.VoWifi)
            { VoWifiDiscovery = new("epdg.example.test\n192.0.2.10", "51566,20404", true) };
            settings.Sims["sim-b"] = new(TelephonyMode.ModemBridge, true, false);
        });
        var restored = await new TelephonySettingsStore(SettingsPath).LoadAsync();
        Assert.Equal("epdg.example.test\n192.0.2.10", restored.Sims["sim-a"].VoWifiDiscovery!.PreferredAddresses);
        Assert.True(restored.Sims["sim-a"].VoWifiDiscovery!.ManualOnly);
        Assert.Equal(TelephonyMode.HostIms, restored.Sims["sim-a"].Mode);
        Assert.False(restored.Sims["sim-a"].AutoConnectCellular);
        Assert.True(restored.Sims["sim-a"].AutoConnectWifi);
        Assert.Null(restored.Sims["sim-b"].VoWifiDiscovery);
    }

    [Fact]
    public async Task CorruptSettingsAreNotSilentlyOverwritten()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "not-json");
        var store = new TelephonySettingsStore(SettingsPath);
        await Assert.ThrowsAsync<JsonException>(() => store.UpdateAsync(settings => settings.Sims.Clear()));
        Assert.Equal("not-json", await File.ReadAllTextAsync(SettingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
