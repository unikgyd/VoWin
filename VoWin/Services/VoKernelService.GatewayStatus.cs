using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoWin.Models;

namespace VoWin.Services;

public partial class VoKernelService
{
    // Explicit scope: an absent/removed selection must never silently show another SIM's status.
    // Every getter here reads cached state only: no AT/QMI command, DNS query or network registration.
    public GatewayStatusSnapshot GetGatewayStatus(string? slotId)
    {
        var slot = slotId == null ? null : Kernel.Pool.Slots.GetValueOrDefault(slotId);
        if (slot == null) return GatewayStatusSnapshot.Create(new(), GetSipGatewayStatus());
        var modem = slot.Modem;
        var operation = _moduleInstances.TryGetValue(slot.Id, out var instance) ? instance.CellularStatus : null;
        return GatewayStatusSnapshot.Create(new()
        {
            SlotId = slot.Id, Title = slot.DisplayTitle, Port = slot.PortName,
            DeviceError = slot.State == SlotState.Error,
            DeviceBusy = IsInitialScanRunning && slot.State is not (SlotState.Online or SlotState.Busy or SlotState.Error),
            IsOnline = slot.State is SlotState.Online or SlotState.Busy && (slot.IsPcscReader || modem?.IsOpen == true),
            IsReader = slot.IsPcscReader, HasSim = !string.IsNullOrWhiteSpace(slot.Sim?.Imsi),
            FlightMode = slot.IsFlightMode, HasQmiEndpoint = modem?.HasQmiEndpoint == true,
            QmiPreferred = modem?.IsQmiActive == true, AtOnly = modem?.ControlPreference == ModemControlPreference.AtOnly,
            QmiFallback = modem?.LastQmiFallbackReason, Registration = slot.Registration, Signal = slot.Signal,
            CellularEnabled = slot.TelephonyEnabled, CellularMode = slot.CellularImsMode,
            PreferredRoute = slot.PreferredTelephonyMode, HostIms = slot.GetHostImsRegistrationStatus(),
            Bearer = slot.GetHostImsPppBearerStatus(), WifiState = slot.VoWifi.State, Operation = operation,
            HostRecovery = instance?.HostRecoveryStatus,
            WifiBusy = instance?.WifiBusy == true,
            WifiKeepOnlineRequested = slot.VoWifi.KeepOnlineRequested,
            WifiRecoveryPaused = slot.VoWifi.AutomaticRecoveryPaused,
            WifiRetryNotBeforeUtc = slot.VoWifi.ImsRetryNotBeforeUtc,
            WifiIkeRetry = slot.VoWifi.GetIkeRetryStatusFor(slot.Sim?.Iccid),
            // A failed foreground attempt may have recovered in the core since then.
            // Keep its error only while no successful registration supersedes it.
            WifiError = slot.VoWifi.LastError ?? (slot.VoWifi.State == VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered
                ? null : instance?.WifiError)
        }, GetSipGatewayStatus());
    }
}
