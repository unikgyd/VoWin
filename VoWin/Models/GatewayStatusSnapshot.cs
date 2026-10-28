using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Kernel.SipGateway;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Telephony.VoWifi;

namespace VoWin.Models;

public enum GatewayStatusTone { Neutral, Information, Success, Warning, Error }
public enum StatusLight { Inactive, Ready, Busy, Error }
public sealed record GatewayStatusItem(string Label, string Value, string Detail,
    GatewayStatusTone Tone = GatewayStatusTone.Neutral)
{
    public string InlineText => $"{Label}：{Value}";
    // Presentation state is separate from diagnostic severity and protocol evidence.
    public StatusLight Light { get; init; } = Tone switch
    {
        GatewayStatusTone.Success => StatusLight.Ready,
        GatewayStatusTone.Information or GatewayStatusTone.Warning => StatusLight.Busy,
        GatewayStatusTone.Error => StatusLight.Error,
        _ => StatusLight.Inactive
    };
    public string? ShortStatus { get; init; }
    public string StatusText => ShortStatus ?? Light switch
    {
        StatusLight.Ready => "正常", StatusLight.Busy => "处理中", StatusLight.Error => "异常", _ => "未启用"
    };
    public string ShortLabel => Label switch
    {
        "设备 / SIM" => "设备", "控制通道" => Value.StartsWith("PC/SC") ? "PC/SC" : Value.StartsWith("QMI") ? "QMI / AT" : Value.StartsWith("AT") ? "AT" : "控制",
        "蜂窝网络" => "蜂窝网络", "模组语音桥接" => "模组语音",
        "SIP 网关（全局可选）" or "SIP · 当前模组" => "SIP", _ => Label
    };
    public string AccessibleText => $"{ShortLabel}，{StatusText}。{InlineText}。{Detail}";
}

/// <summary>Cached observations only. Endpoint presence is not QMI verification or IMS registration.</summary>
public sealed record GatewayStatusInput
{
    public string? SlotId { get; init; }
    public string Title { get; init; } = "未选择设备";
    public string? Port { get; init; }
    public bool IsOnline { get; init; }
    public bool DeviceError { get; init; }
    public bool DeviceBusy { get; init; }
    public bool HasSim { get; init; }
    public bool IsReader { get; init; }
    public bool FlightMode { get; init; }
    public bool HasQmiEndpoint { get; init; }
    public bool QmiPreferred { get; init; }
    public bool AtOnly { get; init; }
    public string? QmiFallback { get; init; }
    public NetworkRegistration? Registration { get; init; }
    public SignalQuality? Signal { get; init; }
    public bool CellularEnabled { get; init; }
    public TelephonyMode? CellularMode { get; init; }
    public TelephonyMode? PreferredRoute { get; init; }
    public HostImsRegistrationStatus? HostIms { get; init; }
    public HostImsPppBearerStatus? Bearer { get; init; }
    public HostImsRecoveryStatus? HostRecovery { get; init; }
    public VoWifiState WifiState { get; init; }
    public bool WifiBusy { get; init; }
    public string? WifiError { get; init; }
    public bool WifiKeepOnlineRequested { get; init; }
    public bool WifiRecoveryPaused { get; init; }
    public DateTime? WifiRetryNotBeforeUtc { get; init; }
    public IkeRetryStatus? WifiIkeRetry { get; init; }
    public TelephonyModeStatus? Operation { get; init; }
}

public sealed record GatewayStatusSnapshot(
    string Scope, GatewayStatusItem Device, GatewayStatusItem Control, GatewayStatusItem Network,
    GatewayStatusItem Cellular, GatewayStatusItem Bearer, GatewayStatusItem Wifi, GatewayStatusItem Route, GatewayStatusItem Sip,
    string SmsSummary)
{
    public IReadOnlyList<GatewayStatusItem> Layers => [Device, Control, Network, Cellular, Bearer, Wifi, Sip];
    public IReadOnlyList<GatewayStatusItem> OverviewLayers => [Device, Control, Network, Cellular, Wifi, Sip];
    public IReadOnlyList<GatewayStatusItem> CompactLayers => [Cellular, Wifi];
    public string RouteSummary => $"呼出：{Route.Value.Split(" · ")[0]}";
    public string ListSummary => $"{Control.Value} · {Network.Value}\n{Cellular.Label} · {Cellular.Value}\nVoWiFi · {Wifi.Value}\n默认呼出 · {Route.Value}";
    public string ShellSummary => $"{Scope}  |  {Route.Value}  |  SIP · {Sip.Value}";
    public string AccessSummary => $"{Control.InlineText}  |  {Cellular.InlineText}  |  {Bearer.InlineText}  |  {Wifi.InlineText}";
    // Native tray tips have a 127-character limit. Reserve a line for every access mode,
    // so a long SIM name or error cannot hide Host IMS / VoWiFi status at the end.
    public string TraySummary => $"VoWin · {Short(Scope, 20)}\n{Short(Control.InlineText, 25)}\n{Short(Cellular.InlineText, 25)}\n{Short(Wifi.InlineText, 22)}\nSIP：{Short(Sip.Value, 18)}";
    public string Detail => string.Join("\n", Layers.Append(Route).Select(x => $"{x.Label}：{x.Value} — {x.Detail}"));

    private static string Short(string value, int maxLength)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        if (singleLine.Length <= maxLength) return singleLine;
        var length = maxLength - 1;
        if (char.IsHighSurrogate(singleLine[length - 1])) length--;
        return singleLine[..length] + "…";
    }

    public static string ModeName(TelephonyMode mode) => mode switch
    {
        TelephonyMode.HostIms => "Host IMS", TelephonyMode.VoWifi => "VoWiFi", _ => "模组语音桥接"
    };

    public static GatewayStatusSnapshot Create(GatewayStatusInput input, SipGatewayStatus? sip = null)
    {
        var present = input.SlotId != null;
        var online = present && input.IsOnline;
        var simReady = online && input.HasSim;
        var hostExpired = input.HostIms is { IsRegistered: true, ExpiresAtUtc: { } expiresAt } && expiresAt <= DateTime.UtcNow;
        var hostReady = simReady && input.HostIms?.IsRegistered == true && !hostExpired;
        var wifiReady = simReady && input.WifiState == VoWifiState.ImsRegistered && input.WifiError == null && !input.WifiRecoveryPaused;
        var wifiWaiting = input.WifiKeepOnlineRequested && !input.WifiRecoveryPaused &&
            input.WifiState is VoWifiState.Disconnected or VoWifiState.Failed;
        var wifiBackoff = input.WifiKeepOnlineRequested && input.WifiRetryNotBeforeUtc > DateTime.UtcNow;
        var device = new GatewayStatusItem("设备 / SIM", !present ? "未选择设备" : input.DeviceError ? "设备异常 · 查看日志" : !online
            ? input.HasSim ? "已断开 · SIM 资料已缓存" : "设备已断开"
            : input.HasSim ? "已连接 · SIM 已识别" : "已连接 · SIM 未就绪",
            $"{input.Port ?? "无设备"}；缓存的 SIM 身份不代表当前 SIM 已就绪。",
            input.DeviceError ? GatewayStatusTone.Error : simReady ? GatewayStatusTone.Success : GatewayStatusTone.Warning);
        var control = new GatewayStatusItem("控制通道", !online ? input.DeviceError ? "状态异常" : "未连接" : input.IsReader ? "PC/SC 读卡器"
            : input.AtOnly ? "AT · 手动选择" : !input.HasQmiEndpoint ? "AT · 无 QMI 端点"
            : input.QmiPreferred ? input.QmiFallback != null ? "QMI 优先 · 部分 AT 回退" : "QMI 优先 · AT 回退" : "AT · QMI 已回退",
            input.QmiFallback ?? (input.IsReader ? "仅提供 SIM 访问，不提供蜂窝语音桥接。"
                : "显示当前控制策略；QMI 端点可见或被选中，不等于 QMI 会话已验证，也不等于 IMS 已注册。"),
            input.QmiFallback != null && online ? GatewayStatusTone.Warning : GatewayStatusTone.Neutral);
        var network = new GatewayStatusItem("蜂窝网络", !online ? "未连接" : input.IsReader ? "不适用"
            : input.FlightMode ? "飞行模式" : input.Registration?.StatusDisplay ?? "尚无网络状态",
            $"{input.Registration?.AccessTechnology ?? input.Signal?.Rat ?? "制式未知"} · " +
            (online && input.Signal != null ? $"{input.Signal.RssiDbm} dBm；" : "") +
            "最近一次采样；蜂窝在网不等于 IMS 注册。",
            !online || input.IsReader || input.FlightMode ? GatewayStatusTone.Neutral
                : input.Registration?.Status is NetworkRegStatus.Home or NetworkRegStatus.Roaming
                    ? GatewayStatusTone.Success : GatewayStatusTone.Warning);
        var cellularMode = input.CellularMode ?? (input.PreferredRoute == TelephonyMode.HostIms ? TelephonyMode.HostIms : TelephonyMode.ModemBridge);
        var hostBackoff = cellularMode == TelephonyMode.HostIms && input.HostIms?.RetryNotBeforeUtc > DateTime.UtcNow;
        var hostPaused = cellularMode == TelephonyMode.HostIms && input.HostIms?.AutomaticRecoveryPaused == true;
        var hostRetryDetail = hostBackoff ? $" 运营商 Retry-After：{input.HostIms!.RetryNotBeforeUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 后才可重试；网络恢复或手动点击不会跳过等待。"
            : hostPaused ? " 注册重试已暂停；请检查运营商拒绝或无效 Retry-After，再手动重试。" : "";
        var cellularValue = input.Operation?.IsBusy == true ? input.Operation.Message
            : input.HostRecovery is { IsWaiting: true } or { IsPaused: true } ? input.HostRecovery.Message
            : !online ? input.DeviceError ? "设备未就绪" : "设备已断开" : input.IsReader ? "读卡器不支持"
            : hostBackoff ? input.CellularEnabled || input.Operation?.HasError == true ? "等待运营商退避" : "已停止 · 退避未结束"
            : hostPaused && (input.CellularEnabled || input.Operation?.HasError == true) ? "注册重试已暂停"
            : !input.CellularEnabled ? "已停止"
            : input.Operation?.HasError == true ? "连接失败"
            : cellularMode == TelephonyMode.HostIms ? hostReady ? "已注册" : !simReady ? "等待 SIM" : hostExpired ? "注册已过期" : input.HostIms?.LastError != null ? "注册失败" : "未注册"
            : input.FlightMode ? "飞行模式 · 不可用" : !simReady ? "等待 SIM" : "桥接已启用";
        var cellular = new GatewayStatusItem(ModeName(cellularMode), cellularValue,
            (input.Operation is { HasError: true } operation ? operation.Message
            : cellularMode == TelephonyMode.HostIms
                ? input.HostIms?.LastError ?? $"Windows 负责 IMS 协议；P-CSCF {input.HostIms?.Endpoint ?? "尚未发现"}；" +
                    $"承载{(input.Bearer?.IsActive == true ? "已建立" : "未建立")}；媒体路由 {input.Bearer?.MediaRouteCount ?? 0}；" +
                    $"注册到期 {input.HostIms?.ExpiresAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}；" +
                    $"短信接收{(hostReady && input.HostIms?.SmsReceiveEnabled == true ? "已启用" : "未启用")}。"
                : "语音由模组处理；桥接开关和蜂窝在网均不能证明模组内部 IMS 已注册或媒体已验证。") + hostRetryDetail +
                (input.HostRecovery is { } recovery ? $" 后台恢复：{recovery.Message}；自动重建 {recovery.Attempts} 次；最近成功 {recovery.LastRecoveredAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}。" : ""),
            input.Operation?.IsBusy == true ? GatewayStatusTone.Information
            : input.HostRecovery?.IsPaused == true ? GatewayStatusTone.Warning
            : input.HostRecovery?.IsWaiting == true ? GatewayStatusTone.Information
            : online && (hostBackoff || hostPaused) ? GatewayStatusTone.Warning
            : online && (input.Operation?.HasError == true || cellularMode == TelephonyMode.HostIms && input.HostIms?.LastError != null)
                ? GatewayStatusTone.Error : hostReady && input.CellularEnabled && cellularMode == TelephonyMode.HostIms
                    ? GatewayStatusTone.Success : online && input.CellularEnabled && cellularMode == TelephonyMode.HostIms && hostExpired
                        ? GatewayStatusTone.Warning : GatewayStatusTone.Neutral);
        var usesHostBearer = cellularMode == TelephonyMode.HostIms || input.Bearer?.IsActive == true;
        var bearerActive = online && simReady && !input.IsReader && input.Bearer?.IsActive == true;
        var bearer = new GatewayStatusItem("Host IMS 承载", !online ? "设备已断开"
            : input.IsReader ? "不适用" : !usesHostBearer ? "未使用"
            : input.Bearer?.LastError != null ? "承载异常"
            : bearerActive ? input.CellularEnabled ? "PPP 已建立" : "承载仍存在 · 待释放"
            : !simReady ? "等待 SIM" : "未建立",
            !usesHostBearer || input.IsReader ? "仅 Host IMS 使用 Windows PPP 承载；模组桥接与 VoWiFi 使用各自的媒体通道。"
                : input.Bearer?.LastError ?? (input.Bearer?.Bearer is { } info
                    ? $"接口 {info.InterfaceIndex}；本地 {info.LocalAddress}；P-CSCF {string.Join(", ", info.PcscfAddresses)}；"
                    : "尚无 Windows PPP 接口资料；") +
                    $"租约续期 {input.Bearer?.RenewalCount ?? 0} 次；最近续期 {input.Bearer?.LastRenewedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}；媒体路由 {input.Bearer?.MediaRouteCount ?? 0}；默认路由隔离修复 {input.Bearer?.DefaultRouteRepairCount ?? 0} 次。" +
                    "承载建立或媒体路由存在，不等于 IMS 已注册或双向语音已验证。",
            online && usesHostBearer && input.Bearer?.LastError != null ? GatewayStatusTone.Error
                : bearerActive ? input.CellularEnabled ? GatewayStatusTone.Information : GatewayStatusTone.Warning
                : GatewayStatusTone.Neutral);
        var wifiDetail = input.WifiError ?? "独立的 ePDG / IKEv2 / IPsec / IMS 线路；不影响 Host IMS 或模组桥接的状态。";
        if (wifiBackoff)
            wifiDetail += $" 运营商 Retry-After：{input.WifiRetryNotBeforeUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 后才可重试；换网络不会跳过等待。";
        if (input.WifiRecoveryPaused)
            wifiDetail += " 自动重连已暂停；请先检查 SIM / APN 授权和运营商退避要求，再手动重试。";
        else if (wifiWaiting)
            wifiDetail += " 正在等待原 SIM / 读卡器与网络可用后自动重连；不会借用其他 SIM。可在系统状态中停止自动重连。";
        var wifi = new GatewayStatusItem("VoWiFi", input.WifiRecoveryPaused ? "自动重连已暂停"
            : wifiBackoff ? "等待运营商退避"
            : !online ? wifiWaiting ? "设备断开 · 等待恢复" : "设备已断开"
            : wifiWaiting ? "等待自动重连" : input.WifiError != null ? "连接失败"
            : !simReady && input.WifiState == VoWifiState.ImsRegistered ? "SIM 未就绪 · 注册待核验"
            : input.WifiBusy && input.WifiState == VoWifiState.Disconnected ? "启动中"
            : input.WifiState switch
        {
            VoWifiState.ImsRegistered => "IMS 已注册", VoWifiState.ResolvingEpdg => "发现 ePDG",
            VoWifiState.ConnectingIkev2 => "建立 IKEv2", VoWifiState.AuthenticatingEapAka => "SIM 鉴权中",
            VoWifiState.IpsecTunnelEstablished => "隧道已建立 · 待 IMS", VoWifiState.ImsRegistering => "IMS 注册中",
            VoWifiState.Failed => "连接失败", _ => "未连接"
        }, wifiDetail,
            input.WifiRecoveryPaused ? GatewayStatusTone.Warning : wifiWaiting ? GatewayStatusTone.Information
                : online && (input.WifiError != null || input.WifiState == VoWifiState.Failed) ? GatewayStatusTone.Error
                : wifiReady ? GatewayStatusTone.Success : online && input.WifiBusy ? GatewayStatusTone.Information : GatewayStatusTone.Neutral);
        TelephonyMode? routeMode = null;
        string? routeError = null;
        if (simReady)
        {
            try
            {
                routeMode = TelephonyRoutePolicy.Resolve(input.PreferredRoute, input.CellularEnabled, wifiReady,
                    hostReady, !input.IsReader && !input.FlightMode, cellularImsMode: input.CellularMode);
            }
            catch (InvalidOperationException error) { routeError = error.Message; }
        }
        var requested = input.PreferredRoute ?? (wifiReady ? TelephonyMode.VoWifi : hostReady ? TelephonyMode.HostIms : cellularMode);
        if (input.CellularMode.HasValue && requested != TelephonyMode.VoWifi) requested = input.CellularMode.Value;
        var route = new GatewayStatusItem("默认呼出", routeMode.HasValue
            ? $"{ModeName(routeMode.Value)}{(routeMode == TelephonyMode.ModemBridge ? " · 待呼叫验证" : " · 已注册")}" :
            !present ? "请先选择设备" : $"{ModeName(requested)} · 当前不可用",
            routeError ?? "根据所选设备的呼出偏好与现有状态判断；不代表正在通话的线路，也不验证对端可达。",
            routeMode.HasValue ? GatewayStatusTone.Information : GatewayStatusTone.Warning);
        var sipItem = new GatewayStatusItem("SIP 网关（全局可选）", sip?.IsRunning != true ? "未启用"
            : sip.PeerEndPoint != null ? "中继已启动 · 对端待验证" : $"监听中 · {sip.Registrations.Count} 个分机",
            sip?.IsRunning != true ? "默认关闭，在设置中手动启用；不影响本机通话和短信。"
                : $"本地 {sip.LocalEndPoint}；活动会话 {sip.ActiveDialogs}；" +
                    (sip.PeerEndPoint != null ? "固定中继不执行 REGISTER；启动监听不代表 Asterisk 已连通。"
                        : "已注册分机数量不代表实时在线或双向语音已验证。"), GatewayStatusTone.Neutral);
        if (sip is { IsRunning: true, RequiresConfiguredLines: true } && input.SlotId is { } lineId)
        {
            var line = sip.LineRoutes.FirstOrDefault(pair => pair.Key.Equals(lineId, StringComparison.OrdinalIgnoreCase)).Value;
            var exposed = line?.Enabled == true;
            var outgoing = exposed && line!.AllowOutgoingCalls && (sip.PeerEndPoint is null || sip.PeerAllowsOutboundCalls);
            var target = string.IsNullOrWhiteSpace(line?.IncomingTarget) ? sip.IncomingTarget ?? "已注册分机" : line.IncomingTarget;
            sipItem = new("SIP · 当前模组", exposed ? "已配置 VoIP 接入 · 对端待验证" : "该模组未接入 VoIP",
                exposed ? $"线路 {lineId}；来电{(line!.AllowIncomingCalls ? $"转接 → {target}" : "转接关闭")}；外拨{(outgoing ? "允许" : "关闭")}。" +
                    "SIP 监听与 IMS/VoWiFi 注册独立，不代表已验证对端或双向语音。"
                    : "此模组仍可独立使用 IMS/VoWiFi；其他模组的 VoIP 接入不受影响。", GatewayStatusTone.Neutral);
        }
        var sms = routeMode switch
        {
            TelephonyMode.HostIms => $"短信：Host IMS · 接收{(input.HostIms?.SmsReceiveEnabled == true ? "已启用" : "未启用")}",
            TelephonyMode.VoWifi => "短信：VoWiFi IMS · 发送结果以网络确认及投递状态为准",
            TelephonyMode.ModemBridge => "短信：模组 AT · 网络提交能力待实际发送验证",
            _ => "短信：当前默认线路不可用"
        };
        device = device with { Light = !present ? StatusLight.Inactive : input.DeviceError ? StatusLight.Error : input.DeviceBusy ? StatusLight.Busy
            : input.DeviceError || !online || !simReady ? StatusLight.Error : StatusLight.Ready,
            ShortStatus = !present ? "未连接" : input.DeviceBusy ? "检测中" : !online ? "已断开" : !simReady ? "SIM 未就绪" : null };
        control = control with { Light = !online ? StatusLight.Inactive : StatusLight.Ready, ShortStatus = !online ? "未连接" : null };
        network = network with { Light = !online || input.IsReader || input.FlightMode ? StatusLight.Inactive
            : input.Registration?.Status is NetworkRegStatus.Home or NetworkRegStatus.Roaming ? StatusLight.Ready
            : input.Registration?.Status == NetworkRegStatus.Denied ? StatusLight.Error
            : input.Registration?.Status == NetworkRegStatus.Searching ? StatusLight.Busy : StatusLight.Inactive,
            ShortStatus = input.IsReader ? "不适用" : input.FlightMode ? "飞行模式" : !online ? "未连接"
                : input.Registration?.Status == NetworkRegStatus.Searching ? "搜网中"
                : input.Registration?.Status == NetworkRegStatus.Denied ? "注册被拒绝"
                : input.Registration?.Status is NetworkRegStatus.Home or NetworkRegStatus.Roaming ? null : "未在网" };
        var cellularBusy = input.Operation?.IsBusy == true || input.HostRecovery?.IsWaiting == true || input.CellularEnabled && hostBackoff;
        var cellularPaused = input.HostRecovery?.IsPaused == true || input.CellularEnabled && hostPaused;
        cellular = cellular with { Light = cellularPaused ? StatusLight.Error
            : cellularBusy ? StatusLight.Busy : !online || input.IsReader || !input.CellularEnabled ? StatusLight.Inactive
            : input.Operation?.HasError == true || !simReady || input.FlightMode || cellularMode == TelephonyMode.HostIms && !hostReady
                ? StatusLight.Error : StatusLight.Ready,
            ShortStatus = cellularPaused ? "已暂停" : input.CellularEnabled && hostBackoff ? "等待重试"
                : cellularBusy ? "连接中" : !online ? "未连接" : input.IsReader ? "不适用" : null };
        var wifiConnecting = input.WifiBusy || wifiWaiting || wifiBackoff || input.WifiState is
            VoWifiState.ResolvingEpdg or VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka or
            VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering;
        wifi = wifi with { Light = input.WifiRecoveryPaused ? StatusLight.Error : wifiReady ? StatusLight.Ready
            : wifiConnecting ? StatusLight.Busy : input.WifiError != null || input.WifiState == VoWifiState.Failed ? StatusLight.Error : StatusLight.Inactive,
            ShortStatus = input.WifiRecoveryPaused ? "已暂停" : wifiBackoff || wifiWaiting ? "等待重连" : wifiConnecting ? "连接中" : !online ? "未连接" : null };
        // Typed IKE observations are scoped by the service to this card. They
        // describe a new-handshake restriction, not the health of an existing SA.
        if (present && input.WifiKeepOnlineRequested && !wifiReady && input.WifiIkeRetry is { } ikeRetry)
            wifi = WifiIkeRetryPresentation.Create(ikeRetry, input.WifiError, input.WifiRecoveryPaused);
        // A green SIP lamp describes the local listener/configuration, not remote media reachability.
        sipItem = sipItem with { Light = sip?.IsRunning != true ||
            sip.RequiresConfiguredLines && input.SlotId != null && !sip.LineRoutes.Any(pair =>
                pair.Key.Equals(input.SlotId, StringComparison.OrdinalIgnoreCase) && pair.Value.Enabled)
                ? StatusLight.Inactive : StatusLight.Ready };
        return new(present ? input.Title : "未选择设备", device, control, network, cellular, bearer, wifi, route, sipItem, sms);
    }
}
