using System.Net;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Kernel.SipGateway;
using VoSharp.Modem.At;
using VoSharp.Modem;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;

namespace VoWin.Tests;

public sealed class GatewayStatusTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostRecoveryStateRemainsVisibleWithoutClaimingAUsableRoute(bool paused)
    {
        var recovery = new HostImsRecoveryStatus(paused ? "paused" : "waiting-source", !paused, !paused,
            paused, 2, DateTime.UtcNow.AddMinutes(-5), paused ? "恢复已暂停" : "等待原模组恢复");
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            IsOnline = false, HasSim = false, HostRecovery = recovery,
            Operation = new(TelephonyMode.HostIms, false, false, "上次连接失败", HasError: true)
        });
        Assert.Equal(recovery.Message, snapshot.Cellular.Value);
        Assert.Equal(paused ? GatewayStatusTone.Warning : GatewayStatusTone.Information, snapshot.Cellular.Tone);
        Assert.Contains("自动重建 2 次", snapshot.Cellular.Detail);
        Assert.Contains("不可用", snapshot.Route.Value);
    }

    [Fact]
    public void ActiveHostRecoveryOperationTakesPriorityOverTheWaitingSummary()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            HostRecovery = new("reconnecting", true, true, false, 1, null, "自动恢复中"),
            Operation = new(TelephonyMode.HostIms, false, true, "正在移交 IMS 承载", CanCancel: true)
        });
        Assert.Equal("正在移交 IMS 承载", snapshot.Cellular.Value);
        Assert.Equal(GatewayStatusTone.Information, snapshot.Cellular.Tone);
        Assert.Contains("不可用", snapshot.Route.Value);
    }

    [Fact]
    public void CarrierBackoffAndPauseAreDistinctFromGenericHostOrWifiFailure()
    {
        var retryAt = DateTime.UtcNow.AddMinutes(5);
        var waiting = GatewayStatusSnapshot.Create(Online with
        {
            HostIms = new(false, "failed", null, null, null, "registrar unavailable") { RetryNotBeforeUtc = retryAt },
            WifiState = VoWifiState.Failed, WifiKeepOnlineRequested = true, WifiRetryNotBeforeUtc = retryAt,
            WifiError = "registrar unavailable"
        });
        Assert.Equal("等待运营商退避", waiting.Cellular.Value);
        Assert.Equal(GatewayStatusTone.Warning, waiting.Cellular.Tone);
        Assert.Contains("Retry-After", waiting.Cellular.Detail);
        Assert.Equal("等待运营商退避", waiting.Wifi.Value);
        Assert.Contains("Retry-After", waiting.Wifi.Detail);
        Assert.Contains("不可用", waiting.Route.Value);
        var paused = GatewayStatusSnapshot.Create(Online with
        {
            HostIms = new(false, "failed", null, null, null, "invalid Retry-After") { AutomaticRecoveryPaused = true }
        });
        Assert.Equal("注册重试已暂停", paused.Cellular.Value);
        Assert.Contains("手动重试", paused.Cellular.Detail);
        Assert.Equal(GatewayStatusTone.Warning, paused.Cellular.Tone);
    }

    private static GatewayStatusInput Online => new()
    {
        SlotId = "a", Title = "Module A", Port = "COM96", IsOnline = true, HasSim = true,
        CellularEnabled = true, CellularMode = TelephonyMode.HostIms, PreferredRoute = TelephonyMode.HostIms
    };
    private static HostImsRegistrationStatus Registered => new(true, "registered", "private-pcscf", DateTime.UtcNow,
        DateTime.UtcNow.AddMinutes(30), null, SmsReceiveEnabled: true);

    [Fact]
    public void HostImsRegistrationDoesNotRequireVoWifi()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with { HostIms = Registered });
        Assert.Equal("已注册", snapshot.Cellular.Value);
        Assert.Contains("private-pcscf", snapshot.Cellular.Detail);
        Assert.Contains("注册到期", snapshot.Cellular.Detail);
        Assert.Equal("未连接", snapshot.Wifi.Value);
        Assert.Contains("Host IMS", snapshot.Route.Value);
        Assert.Contains("接收已启用", snapshot.SmsSummary);
        Assert.Equal("未启用", snapshot.Sip.Value);
    }

    [Fact]
    public void OfflineSimAndStaleRegistrationsCannotAppearReady()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { IsOnline = false, HostIms = Registered, WifiState = VoWifiState.ImsRegistered });
        Assert.Contains("缓存", snapshot.Device.Value);
        Assert.Contains("不可用", snapshot.Route.Value);
        Assert.Equal("设备已断开", snapshot.Cellular.Value);
        Assert.Equal("设备已断开", snapshot.Wifi.Value);
        Assert.DoesNotContain(snapshot.Layers, layer => layer.Tone == GatewayStatusTone.Success);
    }

    [Theory]
    [InlineData(true, true, false, null, "QMI 优先")]
    [InlineData(true, false, false, "identity mismatch", "QMI 已回退")]
    [InlineData(true, false, true, null, "手动选择")]
    [InlineData(false, false, false, null, "无 QMI 端点")]
    public void ControlStrategyNeverClaimsImsReadiness(bool endpoint, bool preferred, bool atOnly, string? error, string expected)
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { HasQmiEndpoint = endpoint, QmiPreferred = preferred, AtOnly = atOnly, QmiFallback = error });
        Assert.Contains(expected, snapshot.Control.Value);
        Assert.NotEqual(GatewayStatusTone.Success, snapshot.Control.Tone);
        Assert.Equal("未注册", snapshot.Cellular.Value);
        Assert.Contains("不可用", snapshot.Route.Value);
        if (error != null) Assert.Equal(error, snapshot.Control.Detail);
    }

    [Theory]
    [InlineData(TelephonyMode.HostIms, "Host IMS")]
    [InlineData(TelephonyMode.VoWifi, "VoWiFi")]
    public void SimultaneousRegistrationsRespectPreferredRoute(TelephonyMode route, string expected)
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { HostIms = Registered, WifiState = VoWifiState.ImsRegistered, PreferredRoute = route });
        Assert.Contains(expected, snapshot.Route.Value);
        Assert.Equal(GatewayStatusTone.Success, snapshot.Cellular.Tone);
        Assert.Equal(GatewayStatusTone.Success, snapshot.Wifi.Tone);
    }

    [Fact]
    public void DisabledCellularDoesNotDisableIndependentVoWifi()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { CellularEnabled = false, HostIms = Registered, WifiState = VoWifiState.ImsRegistered, PreferredRoute = TelephonyMode.VoWifi });
        Assert.Equal("已停止", snapshot.Cellular.Value);
        Assert.Contains("VoWiFi", snapshot.Route.Value);
        Assert.NotEqual(GatewayStatusTone.Success, snapshot.Cellular.Tone);
    }

    [Fact]
    public void ModuleBridgeAndNetworkRegistrationDoNotClaimInternalImsRegistration()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            CellularMode = TelephonyMode.ModemBridge, PreferredRoute = TelephonyMode.ModemBridge,
            Registration = new(NetworkRegStatus.Home, "Test", "LTE", null)
        });
        Assert.Equal("桥接已启用", snapshot.Cellular.Value);
        Assert.NotEqual(GatewayStatusTone.Success, snapshot.Cellular.Tone);
        Assert.Contains("待呼叫验证", snapshot.Route.Value);
        Assert.Contains("模组 AT", snapshot.SmsSummary);
    }

    [Fact]
    public void FailedRouteNamesTheEffectiveCellularOwnerNotTheStalePreference()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with { PreferredRoute = TelephonyMode.ModemBridge });
        Assert.Equal("Host IMS · 当前不可用", snapshot.Route.Value);
    }

    [Fact]
    public void PcscReaderDoesNotAdvertiseQmiOrCellularBridge()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { IsReader = true, CellularMode = null, PreferredRoute = TelephonyMode.VoWifi, WifiState = VoWifiState.ImsRegistered });
        Assert.Equal("PC/SC 读卡器", snapshot.Control.Value);
        Assert.Equal("不适用", snapshot.Network.Value);
        Assert.Equal("读卡器不支持", snapshot.Cellular.Value);
        Assert.Contains("VoWiFi", snapshot.Route.Value);
    }

    [Fact]
    public void SipStatusShowsOnlyTheSelectedModulesExposureAndPermissions()
    {
        var sip = new SipGatewayStatus(true, new(IPAddress.Loopback, 5062), [], 0)
        {
            RequiresConfiguredLines = true, PeerEndPoint = new(IPAddress.Parse("10.1.1.2"), 5060), IncomingTarget = "600",
            LineRoutes = new Dictionary<string, SipGatewayLineRoute>
            { ["module-a"] = new() { IncomingTarget = "1001" }, ["module-b"] = new() { Enabled = false } }
        };
        var first = GatewayStatusSnapshot.Create(new() { SlotId = "MODULE-A" }, sip);
        var second = GatewayStatusSnapshot.Create(new() { SlotId = "module-b" }, sip);
        Assert.Contains("待验证", first.Sip.Value);
        Assert.Contains("1001", first.Sip.Detail);
        Assert.Contains("外拨关闭", first.Sip.Detail); // The trunk-wide toll gate still wins.
        Assert.Equal("该模组未接入 VoIP", second.Sip.Value);
        Assert.DoesNotContain("1001", second.Sip.Detail);
        Assert.Equal("该模组未接入 VoIP", GatewayStatusSnapshot.Create(new() { SlotId = "missing-module" }, sip).Sip.Value);
        Assert.NotEqual(GatewayStatusTone.Success, first.Sip.Tone);
    }

    [Fact]
    public void SipListenerIsIndependentAndNeverClaimsAsteriskWasVerified()
    {
        var sip = new SipGatewayStatus(true, new(IPAddress.Loopback, 5060), [], 0)
        { PeerEndPoint = new(IPAddress.Parse("10.1.1.2"), 5060) };
        var snapshot = GatewayStatusSnapshot.Create(new(), sip);
        Assert.Contains("待验证", snapshot.Sip.Value);
        Assert.Contains("不执行 REGISTER", snapshot.Sip.Detail);
        Assert.Equal("未选择设备", snapshot.Device.Value);
        Assert.NotEqual(GatewayStatusTone.Success, snapshot.Sip.Tone);
    }

    [Theory]
    [InlineData(VoWifiState.ResolvingEpdg, "发现 ePDG")]
    [InlineData(VoWifiState.ConnectingIkev2, "建立 IKEv2")]
    [InlineData(VoWifiState.AuthenticatingEapAka, "SIM 鉴权中")]
    [InlineData(VoWifiState.IpsecTunnelEstablished, "隧道已建立 · 待 IMS")]
    [InlineData(VoWifiState.ImsRegistering, "IMS 注册中")]
    [InlineData(VoWifiState.Failed, "连接失败")]
    public void WifiStagesStaySpecificToWifi(VoWifiState state, string expected)
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with { WifiState = state, HostIms = Registered });
        Assert.Equal(expected, snapshot.Wifi.Value);
        Assert.Equal("已注册", snapshot.Cellular.Value);
    }

    [Fact]
    public void HostRegistrationErrorAndSmsReceptionAreNotHidden()
    {
        var failed = GatewayStatusSnapshot.Create(Online with
        { HostIms = Registered with { IsRegistered = false, LastError = "test registration error" } });
        Assert.Equal(GatewayStatusTone.Error, failed.Cellular.Tone);
        Assert.Contains("test registration error", failed.Cellular.Detail);
        var noSms = GatewayStatusSnapshot.Create(Online with { HostIms = Registered with { SmsReceiveEnabled = false } });
        Assert.Contains("接收未启用", noSms.SmsSummary);
    }

    [Fact]
    public void WifiSetupFailureCannotLeaveAReadyRoute()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { PreferredRoute = TelephonyMode.VoWifi, WifiState = VoWifiState.ImsRegistered, WifiError = "test setup error" });
        Assert.Equal("连接失败", snapshot.Wifi.Value);
        Assert.Equal("test setup error", snapshot.Wifi.Detail);
        Assert.Equal(GatewayStatusTone.Error, snapshot.Wifi.Tone);
        Assert.Contains("不可用", snapshot.Route.Value);
    }

    [Fact]
    public void BusyStartupAndCellularFlightModeRemainDistinct()
    {
        var starting = GatewayStatusSnapshot.Create(Online with { WifiBusy = true });
        Assert.Equal("启动中", starting.Wifi.Value);
        Assert.Equal(GatewayStatusTone.Information, starting.Wifi.Tone);
        var flight = GatewayStatusSnapshot.Create(Online with
        { FlightMode = true, CellularMode = TelephonyMode.ModemBridge, PreferredRoute = TelephonyMode.ModemBridge });
        Assert.Equal("飞行模式", flight.Network.Value);
        Assert.Contains("不可用", flight.Route.Value);
        Assert.Contains("不可用", flight.Cellular.Value);
    }

    [Fact]
    public void NoSimCannotAppearReadyEvenWithStaleImsStatus()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { HasSim = false, HostIms = Registered, WifiState = VoWifiState.ImsRegistered });
        Assert.Contains("SIM 未就绪", snapshot.Device.Value);
        Assert.Contains("注册待核验", snapshot.Wifi.Value);
        Assert.Contains("不可用", snapshot.Route.Value);
        Assert.NotEqual(GatewayStatusTone.Success, snapshot.Cellular.Tone);
    }

    [Fact]
    public void HandoffProgressSurvivesTheExpectedTemporaryDeviceDisconnection()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { IsOnline = false, Operation = new(TelephonyMode.HostIms, false, true, "正在交接承载") });
        Assert.Equal("正在交接承载", snapshot.Cellular.Value);
        Assert.Equal(GatewayStatusTone.Information, snapshot.Cellular.Tone);
        Assert.Contains("不可用", snapshot.Route.Value);
    }

    [Fact]
    public void HardwareFaultIsNotReportedAsAnOrdinaryDisconnection()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with { IsOnline = false, DeviceError = true });
        Assert.Contains("异常", snapshot.Device.Value);
        Assert.Equal(GatewayStatusTone.Error, snapshot.Device.Tone);
        Assert.Equal("状态异常", snapshot.Control.Value);
        Assert.Equal("设备未就绪", snapshot.Cellular.Value);
    }

    [Fact]
    public void HostBearerHasIndependentStateAndLeaseDetailsWithoutClaimingRegistration()
    {
        var renewedAt = DateTime.UtcNow.AddMinutes(-2);
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            Bearer = new(true, new(42, IPAddress.Parse("2001:db8::2"), [IPAddress.Parse("2001:db8::3")]),
                null, 14, renewedAt, 2) { DefaultRouteRepairCount = 3 }
        });
        Assert.Equal("PPP 已建立", snapshot.Bearer.Value);
        Assert.Equal("未注册", snapshot.Cellular.Value);
        Assert.Contains("接口 42", snapshot.Bearer.Detail);
        Assert.Contains("2001:db8::3", snapshot.Bearer.Detail);
        Assert.Contains("续期 14 次", snapshot.Bearer.Detail);
        Assert.Contains("媒体路由 2", snapshot.Bearer.Detail);
        Assert.Contains("默认路由隔离修复 3 次", snapshot.Bearer.Detail);
        Assert.Contains("不可用", snapshot.Route.Value);
        Assert.NotEqual(GatewayStatusTone.Success, snapshot.Bearer.Tone);
        Assert.Contains(snapshot.Bearer, snapshot.Layers);
    }

    [Fact]
    public void BearerFailureAndPendingCleanupAreNotHiddenByCachedRegistration()
    {
        var failed = GatewayStatusSnapshot.Create(Online with { Bearer = new(false, null, "test bearer failure") });
        Assert.Equal("承载异常", failed.Bearer.Value);
        Assert.Equal("test bearer failure", failed.Bearer.Detail);
        Assert.Equal(GatewayStatusTone.Error, failed.Bearer.Tone);
        var stopping = GatewayStatusSnapshot.Create(Online with
        { CellularEnabled = false, Bearer = new(true, null, null) });
        Assert.Equal("承载仍存在 · 待释放", stopping.Bearer.Value);
        Assert.Equal(GatewayStatusTone.Warning, stopping.Bearer.Tone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherModesDoNotAdvertiseHostBearerReadiness(bool reader)
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            IsReader = reader, CellularMode = TelephonyMode.ModemBridge, PreferredRoute = TelephonyMode.ModemBridge,
            Bearer = new(false, null, "previous host bearer error")
        });
        Assert.Equal(reader ? "不适用" : "未使用", snapshot.Bearer.Value);
        Assert.Equal(GatewayStatusTone.Neutral, snapshot.Bearer.Tone);
    }

    [Fact]
    public void ExpiredRegistrationCannotKeepShowingAReadyHostImsRoute()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { HostIms = Registered with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) } });
        Assert.Equal("注册已过期", snapshot.Cellular.Value);
        Assert.Equal(GatewayStatusTone.Warning, snapshot.Cellular.Tone);
        Assert.Contains("不可用", snapshot.Route.Value);
        Assert.DoesNotContain("接收已启用", snapshot.SmsSummary);
    }

    [Fact]
    public void PartialQmiFallbackNamesBothControlPathsWithoutCallingQmiVerified()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        { HasQmiEndpoint = true, QmiPreferred = true, QmiFallback = "test NAS fallback" });
        Assert.Equal("QMI 优先 · 部分 AT 回退", snapshot.Control.Value);
        Assert.Equal("test NAS fallback", snapshot.Control.Detail);
        Assert.Equal(GatewayStatusTone.Warning, snapshot.Control.Tone);
    }

    [Fact]
    public void CompactSummariesPreserveBothAccessModesAndControlWithLongOrMultilineNames()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            Title = new string('长', 200) + "\r\nSIM A", HasQmiEndpoint = true, QmiPreferred = true,
            HostIms = Registered, WifiState = VoWifiState.ResolvingEpdg,
            QmiFallback = new string('x', 200)
        });
        Assert.Contains("QMI", snapshot.AccessSummary);
        Assert.Contains("Host IMS", snapshot.AccessSummary);
        Assert.Contains("VoWiFi", snapshot.AccessSummary);
        Assert.Contains("默认呼出", snapshot.ListSummary);
        Assert.Contains("Host IMS", snapshot.TraySummary);
        Assert.Contains("VoWiFi", snapshot.TraySummary);
        Assert.Contains("SIP", snapshot.TraySummary);
        Assert.Equal(5, snapshot.TraySummary.Split('\n').Length);
        Assert.DoesNotContain('\r', snapshot.TraySummary);
        Assert.True(snapshot.TraySummary.Length <= 127);
    }

    [Theory]
    [InlineData(VoWifiState.Disconnected)]
    [InlineData(VoWifiState.Failed)]
    public void WifiRecoveryWaitDoesNotHideTheWorkingHostImsLine(VoWifiState state)
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            HostIms = Registered, WifiState = state, WifiKeepOnlineRequested = true,
            WifiError = "test recovery reason"
        });
        Assert.Equal("等待自动重连", snapshot.Wifi.Value);
        Assert.Equal(GatewayStatusTone.Information, snapshot.Wifi.Tone);
        Assert.Contains("test recovery reason", snapshot.Wifi.Detail);
        Assert.Contains("原 SIM", snapshot.Wifi.Detail);
        Assert.Contains("停止自动重连", snapshot.Wifi.Detail);
        Assert.Equal("已注册", snapshot.Cellular.Value);
        Assert.Contains("Host IMS", snapshot.Route.Value);
        Assert.Contains("等待自动重连", snapshot.TraySummary);
    }

    [Fact]
    public void AuthorizationPauseIsVisibleAndCannotAdvertiseAStaleWifiRegistration()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            HostIms = Registered, WifiState = VoWifiState.ImsRegistered,
            PreferredRoute = TelephonyMode.VoWifi, WifiKeepOnlineRequested = true,
            WifiRecoveryPaused = true, WifiError = "AUTHORIZATION_REJECTED"
        });
        Assert.Equal("自动重连已暂停", snapshot.Wifi.Value);
        Assert.Equal(GatewayStatusTone.Warning, snapshot.Wifi.Tone);
        Assert.Contains("AUTHORIZATION_REJECTED", snapshot.Wifi.Detail);
        Assert.Contains("退避", snapshot.Wifi.Detail);
        Assert.Contains("不可用", snapshot.Route.Value);
        Assert.Equal("已注册", snapshot.Cellular.Value);
    }

    [Fact]
    public void MissingRecoverySourceIsNotShownAsAWorkingWifiRoute()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            IsOnline = false, WifiState = VoWifiState.Disconnected, WifiKeepOnlineRequested = true,
            PreferredRoute = TelephonyMode.VoWifi
        });
        Assert.Equal("设备断开 · 等待恢复", snapshot.Wifi.Value);
        Assert.Contains("不可用", snapshot.Route.Value);
        Assert.NotEqual(GatewayStatusTone.Success, snapshot.Wifi.Tone);
    }

    [Fact]
    public void KeepOnlineIntentDoesNotHideAHealthyWifiRegistration()
    {
        var snapshot = GatewayStatusSnapshot.Create(Online with
        {
            WifiState = VoWifiState.ImsRegistered, WifiKeepOnlineRequested = true,
            PreferredRoute = TelephonyMode.VoWifi
        });
        Assert.Equal("IMS 已注册", snapshot.Wifi.Value);
        Assert.Equal(GatewayStatusTone.Success, snapshot.Wifi.Tone);
        Assert.Contains("VoWiFi · 已注册", snapshot.Route.Value);
    }
}
