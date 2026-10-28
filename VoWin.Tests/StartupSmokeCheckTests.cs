using VoSharp.Kernel;
using VoWin.Services;
using VoWin.Models;

namespace VoWin.Tests;

public sealed class StartupSmokeCheckTests
{
    [Fact]
    public void WifiOnlyStartupDoesNotInventAPacketAttachPreferenceButHonorsAnExplicitOne()
    {
        var wifiOnly = new SimTelephonyPreferences(TelephonyMode.ModemBridge, false, true, TelephonyMode.VoWifi);
        Assert.False(VoKernelService.ResolveCellularDataStartupPreference(null, wifiOnly));
        Assert.True(VoKernelService.ResolveCellularDataStartupPreference(new() { DefaultCellularData = true }, wifiOnly));
        Assert.True(VoKernelService.ResolveCellularDataStartupPreference(null, new(TelephonyMode.HostIms, true)));
        Assert.False(VoKernelService.ResolveCellularDataStartupPreference(new() { DefaultCellularData = false }, new(TelephonyMode.HostIms, true)));
    }

    private static StartupSmokeCheck.Report Healthy() => new(false, DateTimeOffset.Now, true, false, false, false,
    [new("com6", "COM6", "QDC507", "460", true, true, false, TelephonyMode.HostIms, true, true, "Disconnected", null),
     new("com10", "COM10", "EC20", "515", true, false, true, TelephonyMode.ModemBridge, false, false, "ImsRegistered", null)]);

    [Fact]
    public void StartupRequiresBothIntendedIndependentRegistrationStates()
    {
        var report = Healthy();
        Assert.True(StartupSmokeCheck.IsSuccessful(report));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { InitialScanFinished = false }));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { SipEnabled = true }));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { DesktopAudioActive = true }));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { CallObserved = true }));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { Slots = [report.Slots[0]] }));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { Slots = [report.Slots[0], report.Slots[0]] }));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { Slots = [report.Slots[0] with { HostRegistered = false }, report.Slots[1]] }));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { Slots = [report.Slots[0], report.Slots[1] with { WifiState = "Failed" }] }));
        Assert.False(StartupSmokeCheck.IsSuccessful(report with { Slots = [report.Slots[0], report.Slots[1] with { UsbBindingVerified = false }] }));
    }

    [Fact]
    public async Task PublishedStartupObserverRejectsUnapprovedLabWithoutAccessingService()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_DUAL_MODEM") == "1" &&
            Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED") == "1") return;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupSmokeCheck.ObserveAsync(null!));
    }
}
