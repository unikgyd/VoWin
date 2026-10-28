using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using VoSharp.Ike;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

public class WifiIkeRetryStatusTests
{
    private static GatewayStatusInput Input => new()
    {
        SlotId = "synthetic-a", IsOnline = true, HasSim = true, WifiKeepOnlineRequested = true,
        WifiState = VoWifiState.Failed, WifiError = "synthetic raw diagnostic", PreferredRoute = TelephonyMode.VoWifi
    };

    [Theory]
    [InlineData("network-timer", false, "等待 IKE 重试", StatusLight.Busy)]
    [InlineData("implementation-missing-timer", false, "等待 IKE 重试", StatusLight.Busy)]
    [InlineData("implementation-invalid-timer", false, "等待 IKE 重试", StatusLight.Busy)]
    [InlineData("uncertain-attempt", true, "握手结果待确认", StatusLight.Error)]
    [InlineData("store-unavailable", true, "重试记录不可用", StatusLight.Error)]
    [InlineData("binding-unavailable", true, "卡状态待核验", StatusLight.Error)]
    [InlineData("card-event", true, "卡状态待核验", StatusLight.Error)]
    [InlineData("future-unrecognized-state", true, "重试状态待核验", StatusLight.Error)]
    public void TypedSourcesUseShortChineseStateAndKeepDetailsOutOfTheOverview(string source, bool untilCard, string value, StatusLight light)
    {
        var snapshot = GatewayStatusSnapshot.Create(Input with
        { WifiIkeRetry = new(source, "epdg", 10500, untilCard ? null : TimeSpan.FromMinutes(10), untilCard) });
        Assert.Equal(value, snapshot.Wifi.Value); Assert.Equal(light, snapshot.Wifi.Light);
        Assert.Equal(light == StatusLight.Busy ? "等待重试" : "已暂停", snapshot.Wifi.StatusText);
        Assert.Contains("synthetic raw diagnostic", snapshot.Wifi.Detail);
        Assert.Contains("不会清空", snapshot.Wifi.Detail); Assert.Contains("10500", snapshot.Wifi.AccessibleText);
        Assert.DoesNotContain("synthetic", snapshot.Wifi.StatusText); Assert.True(snapshot.Wifi.StatusText.Length <= 6);
        Assert.Contains(value, snapshot.TraySummary); Assert.True(snapshot.TraySummary.Length <= 127);
        Assert.Contains("不可用", snapshot.Route.Value);
    }

    [Fact]
    public void CachedDurationIsExplicitlyLastObservedAndNeverAWallClockDeadline()
    {
        var snapshot = GatewayStatusSnapshot.Create(Input with
        { WifiIkeRetry = new("network-timer", "plmn-apn", 9002, TimeSpan.FromSeconds(60), false) });
        Assert.Contains("最近一次核对", snapshot.Wifi.Detail); Assert.Contains("不是实时倒计时", snapshot.Wifi.Detail);
        Assert.Contains("60 秒", snapshot.Wifi.Detail); Assert.Contains("运营商 / APN", snapshot.Wifi.Detail);
        Assert.DoesNotContain("Retry-After：", snapshot.Wifi.Detail);
    }

    [Fact]
    public void TimedRestrictionDoesNotHideASeparateCleanupPause()
    {
        var snapshot = GatewayStatusSnapshot.Create(Input with
        { WifiRecoveryPaused = true, WifiIkeRetry = new("network-timer", "epdg", 10500, TimeSpan.FromMinutes(5), false) });
        Assert.Equal(StatusLight.Error, snapshot.Wifi.Light); Assert.Equal("已暂停", snapshot.Wifi.StatusText);
        Assert.Contains("另有暂停原因", snapshot.Wifi.Detail);
    }

    [Fact]
    public void ExistingHealthyRegistrationIsNotBrokenByANewHandshakeObservation()
    {
        var snapshot = GatewayStatusSnapshot.Create(Input with
        { WifiState = VoWifiState.ImsRegistered, WifiError = null, CellularMode = TelephonyMode.HostIms,
            WifiIkeRetry = new("network-timer", "epdg", 10500, TimeSpan.FromMinutes(5), false) });
        Assert.Equal(StatusLight.Ready, snapshot.Wifi.Light); Assert.Equal("IMS 已注册", snapshot.Wifi.Value);
        Assert.Contains("已注册", snapshot.Route.Value); Assert.DoesNotContain("暂缓", snapshot.Wifi.Detail);
    }

    [Fact]
    public void StoppedOrUnselectedLineDoesNotShowACachedRestrictionAsAnActiveWait()
    {
        var stopped = Input with { WifiState = VoWifiState.Disconnected, WifiError = null, WifiKeepOnlineRequested = false,
            WifiIkeRetry = new("network-timer", "epdg", 10500, TimeSpan.FromMinutes(5), false) };
        Assert.Equal(StatusLight.Inactive, GatewayStatusSnapshot.Create(stopped).Wifi.Light);
        Assert.DoesNotContain("IKE 重试", GatewayStatusSnapshot.Create(stopped with { SlotId = null }).Wifi.Value);
    }

    [Fact]
    public void OtherAccessModeRemainsRegisteredWhileWifiWaits()
    {
        var snapshot = GatewayStatusSnapshot.Create(Input with
        { CellularEnabled = true, CellularMode = TelephonyMode.HostIms, PreferredRoute = TelephonyMode.HostIms,
            HostIms = new(true, "registered", "pcscf.example", null, DateTime.UtcNow.AddHours(1), null),
            WifiIkeRetry = new("network-timer", "epdg", 10500, TimeSpan.FromMinutes(5), false) });
        Assert.Equal(StatusLight.Ready, snapshot.Cellular.Light); Assert.Equal(StatusLight.Busy, snapshot.Wifi.Light);
        Assert.Contains("Host IMS", snapshot.Route.Value);
    }

    [Fact]
    public void ZeroOrInvalidObservedDurationNeverAnnouncesPermissionToReconnect()
    {
        var zero = GatewayStatusSnapshot.Create(Input with { WifiIkeRetry = new("network-timer", "epdg", 10500, TimeSpan.Zero, false) });
        Assert.Equal(StatusLight.Busy, zero.Wifi.Light); Assert.Contains("仍需重新核验", zero.Wifi.Detail);
        var invalid = GatewayStatusSnapshot.Create(Input with { WifiIkeRetry = new("network-timer", "epdg", 10500, null, false) });
        Assert.Equal(StatusLight.Error, invalid.Wifi.Light); Assert.Equal("重试状态待核验", invalid.Wifi.Value);
    }

    [Fact]
    public async Task RealDesktopServiceAndModuleTilesProjectOnlyTheSelectedCardObservation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-ike-ui-service-" + Guid.NewGuid().ToString("N"));
        var preferences = new PreferenceDatabaseService(Path.Combine(directory, "preferences.db"));
        await using var service = new VoKernelService(preferences, diagnosticLogPath: Path.Combine(directory, "diagnostic.log"));
        var gate = new IkeRetryGate(preferences, new WindowsIkeRetryClock());
        var sim = SimIdentity.FromImsiAndIccid("234150123456789", "8944100000000000053", mncLength: 2);
        var target = new IkeRetryTarget("epdg.example", "23415");
        var card = await gate.PrepareVerifiedCardAsync("synthetic-a", sim.Iccid); var token = await gate.BeginAsync(card, target);
        await gate.ObserveAsync(card, target, token, "192.0.2.9", new(10500, IkeRejectionScope.Epdg, IkeBackoffTimer.Decode([1, 0xAA])), true);
        await gate.CompleteAsync(card, token);
        var first = new ModemSlot("synthetic-a", "synthetic-port-a", ikeRetry: gate);
        var second = new ModemSlot("synthetic-b", "synthetic-port-b", ikeRetry: gate);
        var simProperty = typeof(ModemSlot).GetProperty(nameof(ModemSlot.Sim))!;
        simProperty.SetValue(first, sim); simProperty.SetValue(second, sim with { Iccid = "8944100000000000054" });
        var slots = (ConcurrentDictionary<string, ModemSlot>)typeof(ModemPool).GetField("_slots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service.Kernel.Pool)!;
        Assert.True(slots.TryAdd(first.Id, first)); Assert.True(slots.TryAdd(second.Id, second));
        first.VoWifi.EpdgDiscovery = new(ManualOnly: true);
        Assert.False(await first.VoWifi.StartVoWifiAsync(sim, target.Host));
        Assert.Equal("等待 IKE 重试", service.GetGatewayStatus(first.Id).Wifi.Value);
        using var tile = new ModuleManagementInstance(first); Assert.Contains("等待 IKE 重试", tile.WifiSummary);
        Assert.DoesNotContain("IKE 重试", service.GetGatewayStatus(second.Id).Wifi.Value);
        Assert.DoesNotContain("IKE 重试", service.GetGatewayStatus(null).Wifi.Value);
        simProperty.SetValue(first, sim with { Iccid = "8944100000000000054" });
        Assert.DoesNotContain("IKE 重试", service.GetGatewayStatus(first.Id).Wifi.Value);
        await first.VoWifi.StopVoWifiAsync();
    }
}
