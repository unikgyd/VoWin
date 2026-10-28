namespace VoSharp.Kernel;

public enum TelephonyMode
{
    ModemBridge,
    HostIms,
    VoWifi
}

/// <summary>One routing decision shared by the desktop client and SIP gateway.</summary>
public static class TelephonyRoutePolicy
{
    public static TelephonyMode Resolve(TelephonyMode? preferred, bool enabled,
        bool voWifiRegistered, bool hostImsRegistered, bool modemAvailable,
        bool forceCellular = false, bool forceVoWifi = false, TelephonyMode? cellularImsMode = null)
    {
        if (forceCellular && forceVoWifi)
            throw new ArgumentException("不能同时指定模块和 VoWiFi 线路。");
        if (cellularImsMode == TelephonyMode.VoWifi) throw new ArgumentOutOfRangeException(nameof(cellularImsMode));
        if (!enabled && (cellularImsMode is null || !voWifiRegistered))
            throw new InvalidOperationException("此 SIM 的通信已停止，请先在系统状态页启用。");
        var selected = forceCellular ? cellularImsMode ?? TelephonyMode.ModemBridge
            : forceVoWifi ? TelephonyMode.VoWifi : preferred;
        selected ??= voWifiRegistered ? TelephonyMode.VoWifi
            : hostImsRegistered ? TelephonyMode.HostIms : TelephonyMode.ModemBridge;
        if (cellularImsMode is not null && selected != TelephonyMode.VoWifi) selected = cellularImsMode;
        var ready = selected switch
        {
            TelephonyMode.VoWifi => voWifiRegistered,
            TelephonyMode.HostIms => enabled && hostImsRegistered,
            TelephonyMode.ModemBridge => enabled && modemAvailable,
            _ => false
        };
        if (!ready)
            throw new InvalidOperationException(selected switch
            {
                TelephonyMode.HostIms => "Windows Host IMS 尚未注册，请在系统状态页连接后重试。",
                TelephonyMode.VoWifi => "VoWiFi 尚未注册，请在系统状态页连接后重试。",
                _ => "通信模组尚未连接，请检查 USB 连接和 SIM。"
            });
        return selected.Value;
    }
}
