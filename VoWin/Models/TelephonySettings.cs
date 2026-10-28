using VoSharp.Kernel;
using VoSharp.Modem;
using VoSharp.Telephony.VoWifi;
using System.Text.Json.Serialization;

namespace VoWin.Models;

public sealed record SimTelephonyPreferences(TelephonyMode Mode = TelephonyMode.ModemBridge, bool AutoConnect = true,
    bool? VoWifiAutoConnect = null, TelephonyMode? PreferredRoute = null)
{
    public EpdgUserConfiguration? VoWifiDiscovery { get; init; }
    // Keep legacy VoWiFi-only records intact while exposing independent access settings.
    [JsonIgnore] public TelephonyMode CellularMode => Mode == TelephonyMode.HostIms ? TelephonyMode.HostIms : TelephonyMode.ModemBridge;
    [JsonIgnore] public bool AutoConnectCellular => Mode != TelephonyMode.VoWifi && AutoConnect;
    [JsonIgnore] public bool AutoConnectWifi => VoWifiAutoConnect ?? (Mode == TelephonyMode.VoWifi && AutoConnect);
    [JsonIgnore] public TelephonyMode DefaultRoute => PreferredRoute ?? Mode;
}

public sealed class TelephonySettings
{
    public bool DetailedDiagnosticsEnabled { get; set; }
    public ModemControlPreference ControlPreference { get; set; } = ModemControlPreference.QmiPreferred;
    public bool AutoDiscoverModems { get; set; } = true;
    public string? WinDivertDllPath { get; set; }
    public string? HandoffScriptPath { get; set; }
    public string? RasHelperPath { get; set; }
    public Dictionary<string, SimTelephonyPreferences> Sims { get; set; } = new(StringComparer.Ordinal);
}

public sealed record TelephonyModeStatus(TelephonyMode Mode, bool IsEnabled, bool IsBusy, string Message, bool HasError = false, bool CanCancel = false);
