using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class SipGatewayModemAdmissionTests
{
    [Fact]
    public async Task WifiOnlyModuleUsesItsOwnRegisteredWifiWhenCellularIsStopped()
    {
        await using var kernel = new VoKernel();
        await using var wifi = new ModemSlot("wifi-only", "TEST-WIFI")
        { TelephonyEnabled = false, CellularImsMode = TelephonyMode.ModemBridge, PreferredTelephonyMode = TelephonyMode.VoWifi };
        await using var host = new ModemSlot("host-only", "TEST-HOST")
        { TelephonyEnabled = true, CellularImsMode = TelephonyMode.HostIms, PreferredTelephonyMode = TelephonyMode.HostIms };
        typeof(VoWifiManager).GetProperty(nameof(VoWifiManager.State))!.SetValue(wifi.VoWifi, VoWifiState.ImsRegistered);
        using var controller = new KernelSipGatewayCallController(kernel);
        Assert.Equal(TelephonyMode.VoWifi, controller.ResolveCarrierRoute(wifi));
        Assert.Throws<InvalidOperationException>(() => controller.ResolveCarrierRoute(host));
        Assert.Equal(VoWifiState.ImsRegistered, wifi.VoWifi.State);
        Assert.False(wifi.TelephonyEnabled);
        Assert.Equal(TelephonyMode.HostIms, host.CellularImsMode);
    }

    [Fact]
    public async Task AnExplicitHostLineCannotBorrowReadyWifiFromAnotherModuleOrItsOwnFallback()
    {
        await using var kernel = new VoKernel();
        await using var slot = new ModemSlot("host", "TEST-HOST")
        { TelephonyEnabled = true, CellularImsMode = TelephonyMode.HostIms, PreferredTelephonyMode = TelephonyMode.HostIms };
        typeof(VoWifiManager).GetProperty(nameof(VoWifiManager.State))!.SetValue(slot.VoWifi, VoWifiState.ImsRegistered);
        using var controller = new KernelSipGatewayCallController(kernel);
        var error = Assert.Throws<InvalidOperationException>(() => controller.ResolveCarrierRoute(slot));
        Assert.Contains("Host IMS", error.Message);
    }

    [Fact]
    public async Task DialingOneModuleDoesNotBlockAdmissionOnAnotherAndSameModuleStillSerializes()
    {
        await using var kernel = new VoKernel();
        using var controller = new KernelSipGatewayCallController(kernel);
        var china = controller.GetDialAdmission("com6");
        var philippines = controller.GetDialAdmission("com10");
        Assert.Same(china, controller.GetDialAdmission("COM6"));
        Assert.NotSame(china, philippines);
        await china.WaitAsync();
        try
        {
            Assert.True(await philippines.WaitAsync(TimeSpan.FromSeconds(1)));
            philippines.Release();
            Assert.False(await controller.GetDialAdmission("COM6").WaitAsync(TimeSpan.FromMilliseconds(30)));
        }
        finally { china.Release(); }
        Assert.True(await china.WaitAsync(TimeSpan.FromSeconds(1)));
        china.Release();
    }
}
