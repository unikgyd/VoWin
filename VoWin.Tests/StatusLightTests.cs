using VoSharp.Kernel;
using VoSharp.Modem.At;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class StatusLightTests
{
    private static GatewayStatusInput Online => new()
    {
        SlotId = "synthetic-ui", IsOnline = true, HasSim = true,
        CellularMode = TelephonyMode.HostIms, CellularEnabled = true
    };

    [Theory]
    [InlineData(VoWifiState.ResolvingEpdg)]
    [InlineData(VoWifiState.ConnectingIkev2)]
    [InlineData(VoWifiState.AuthenticatingEapAka)]
    [InlineData(VoWifiState.IpsecTunnelEstablished)]
    [InlineData(VoWifiState.ImsRegistering)]
    public void AllConnectionStagesAreYellowEvenWithoutForegroundBusyFlag(VoWifiState stage)
    {
        var item = GatewayStatusSnapshot.Create(Online with { WifiState = stage }).Wifi;
        Assert.Equal(StatusLight.Busy, item.Light);
        Assert.Equal("连接中", item.StatusText);
    }

    [Fact]
    public void RegisteredIsGreenFailedOrPausedIsRedAndStoppedIsGray()
    {
        Assert.Equal(StatusLight.Ready, GatewayStatusSnapshot.Create(Online with { WifiState = VoWifiState.ImsRegistered }).Wifi.Light);
        Assert.Equal(StatusLight.Error, GatewayStatusSnapshot.Create(Online with { WifiState = VoWifiState.Failed }).Wifi.Light);
        Assert.Equal(StatusLight.Error, GatewayStatusSnapshot.Create(Online with { WifiRecoveryPaused = true }).Wifi.Light);
        Assert.Equal(StatusLight.Inactive, GatewayStatusSnapshot.Create(Online).Wifi.Light);
        Assert.Equal(StatusLight.Inactive, GatewayStatusSnapshot.Create(Online with { CellularEnabled = false }).Cellular.Light);
    }

    [Fact]
    public void MissingSimIsNotShownAsLoadingForeverAndDisabledBackoffStaysGray()
    {
        Assert.Equal(StatusLight.Error, GatewayStatusSnapshot.Create(Online with { HasSim = false }).Device.Light);
        Assert.Equal(StatusLight.Busy, GatewayStatusSnapshot.Create(Online with { HasSim = false, DeviceBusy = true }).Device.Light);
        var stopped = GatewayStatusSnapshot.Create(Online with { CellularEnabled = false,
            HostIms = new(false, "stopped", null, null, null, null) { RetryNotBeforeUtc = DateTime.UtcNow.AddMinutes(5) } });
        Assert.Equal(StatusLight.Inactive, stopped.Cellular.Light);
    }

    [Fact]
    public void WaitingToRetryIsYellowButExpiredRegistrationCannotBeGreen()
    {
        var waiting = GatewayStatusSnapshot.Create(Online with
        { WifiState = VoWifiState.Failed, WifiKeepOnlineRequested = true, WifiError = "timeout" });
        Assert.Equal(StatusLight.Busy, waiting.Wifi.Light);
        var expired = GatewayStatusSnapshot.Create(Online with
        { HostIms = new(true, "registered", null, null, DateTime.UtcNow.AddSeconds(-5), null) });
        Assert.Equal(StatusLight.Error, expired.Cellular.Light);
        var offline = GatewayStatusSnapshot.Create(Online with { IsOnline = false, WifiState = VoWifiState.ImsRegistered });
        Assert.DoesNotContain(offline.OverviewLayers, item => item.Light == StatusLight.Ready);
    }

    [Fact]
    public void NetworkDenialIsRedWhileSearchingIsYellow()
    {
        Assert.Equal(StatusLight.Error, GatewayStatusSnapshot.Create(Online with
        { Registration = new(NetworkRegStatus.Denied, null, null, null) }).Network.Light);
        Assert.Equal(StatusLight.Busy, GatewayStatusSnapshot.Create(Online with
        { Registration = new(NetworkRegStatus.Searching, null, null, null) }).Network.Light);
    }

    [Fact]
    public void DetailsRetainRawObservationsButOverviewOmitsBearerAndLongStatusText()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with { QmiFallback = "test-only fallback detail" });
        Assert.Equal(6, snapshot.OverviewLayers.Count);
        Assert.Equal(7, snapshot.Layers.Count);
        Assert.DoesNotContain(snapshot.Bearer, snapshot.OverviewLayers);
        Assert.Contains("test-only fallback detail", snapshot.Control.AccessibleText);
        Assert.Equal("Host IMS", snapshot.Cellular.ShortLabel);
        Assert.True(snapshot.Cellular.StatusText.Length <= 6);
    }

    [Fact]
    public void SipCountersContainingFailureDoNotTurnAHealthyListenerRed()
    {
        var service = System.Reflection.DispatchProxy.Create<VoWin.Services.IVoKernelService, PhoneCallServiceProxy>();
        var model = new SettingsViewModel(service) { IsSipGatewayRunning = true,
            SipGatewayStatus = "监听中 · MESSAGE 已启用 · 已转发 5 / 失败 0" };
        Assert.Equal(StatusLight.Ready, model.SipStatusIndicator.Light);
        Assert.Empty(model.SipOperationNotice);
        model.SipGatewayStatus = "操作失败：synthetic test error";
        Assert.Equal(StatusLight.Error, model.SipStatusIndicator.Light);
        Assert.Contains("详情", model.SipOperationNotice);
        model.IsSipGatewayBusy = true;
        Assert.Equal(StatusLight.Busy, model.SipStatusIndicator.Light);
    }
}
