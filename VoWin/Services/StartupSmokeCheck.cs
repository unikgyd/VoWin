using System.IO;
using System.Text.Json;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.VoWifi;

namespace VoWin.Services;

/// <summary>Opt-in published-application check, after the ordinary window and host have started.
/// Reads existing preferences; only their automatic registration flows may run. Never dials,
/// sends SMS, enables SIP or starts desktop audio. Subscriber identities are not exported.</summary>
internal static class StartupSmokeCheck
{
    internal sealed record SlotCheck(string Id, string Port, string Model, string? HomeMcc,
        bool UsbBindingVerified, bool AutoCellular, bool AutoWifi, TelephonyMode CellularMode,
        bool CellularEnabled, bool HostRegistered, string WifiState, string? Error)
    {
        public bool Passed => UsbBindingVerified &&
            (!AutoCellular || CellularEnabled && (CellularMode != TelephonyMode.HostIms || HostRegistered)) &&
            (!AutoWifi || WifiState == nameof(VoWifiState.ImsRegistered));
    }

    internal sealed record Report(bool Success, DateTimeOffset ObservedAt, bool InitialScanFinished,
        bool SipEnabled, bool DesktopAudioActive, bool CallObserved, SlotCheck[] Slots);

    internal static bool IsSuccessful(Report report) => report.InitialScanFinished && !report.SipEnabled &&
        !report.DesktopAudioActive && !report.CallObserved && report.Slots.Length >= 2 &&
        report.Slots.Select(slot => slot.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == report.Slots.Length &&
        report.Slots.All(slot => slot.Passed) && report.Slots.Any(slot => slot.AutoCellular) && report.Slots.Any(slot => slot.AutoWifi);

    internal static async Task<Report> ObserveAsync(IVoKernelService service)
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_DUAL_MODEM") != "1" ||
            Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED") != "1")
            throw new InvalidOperationException("Published startup verification requires explicit registration-lab flags.");
        var settings = await service.GetTelephonySettingsAsync();
        var deadline = DateTime.UtcNow.AddMinutes(5);
        Report report;
        do
        {
            var slots = service.Kernel.Pool.Slots.Values.Where(slot => !slot.IsPcscReader).Select(slot =>
            {
                settings.Sims.TryGetValue(slot.Sim?.Iccid ?? "", out var preferences);
                return new SlotCheck(slot.Id, slot.PortName, slot.DeviceModel ?? "Unknown", slot.Sim?.Mcc,
                    slot.UsbContainerId.HasValue, preferences?.AutoConnectCellular ?? false,
                    preferences?.AutoConnectWifi ?? false, preferences?.CellularMode ?? TelephonyMode.ModemBridge,
                    slot.TelephonyEnabled, slot.GetHostImsRegistrationStatus().IsRegistered,
                    slot.VoWifi.State.ToString(), DiagnosticLogRedactor.Redact(
                        slot.VoWifi.LastError ?? slot.GetHostImsRegistrationStatus().LastError ?? ""));
            }).OrderBy(slot => slot.Id).ToArray();
            report = new(false, DateTimeOffset.Now, !service.IsInitialScanRunning,
                service.GetSipGatewayStatus() is { IsRunning: true }, service.GetMicrophoneUplinkStatus().HasSession,
                service.Kernel.Pool.Slots.Values.Any(slot => slot.Calls.ActiveCall != null || slot.HasCellularCall), slots);
            if (IsSuccessful(report)) return report with { Success = true };
            if (report.SipEnabled || report.DesktopAudioActive || report.CallObserved) break;
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        return report;
    }

    internal static async Task SaveAsync(string path, Report report, IVoKernelService service, IReadOnlyList<ModemSlot> observedSlots)
    {
        var cleanup = observedSlots.Select(slot => new
        {
            slot.Id, HostPppActive = slot.GetHostImsPppBearerStatus().IsActive,
            WifiState = slot.VoWifi.State.ToString(), ModemOpen = slot.Modem?.IsOpen == true,
            HostRemoteDeregistered = slot.GetHostImsRegistrationStatus().RemoteDeregistered,
            WifiRemoteDeregistered = slot.VoWifi.RemoteDeregistered,
            WifiDeregistrationError = DiagnosticLogRedactor.Redact(slot.VoWifi.LastDeregistrationError ?? "")
        }).ToArray();
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, new { Startup = report, Cleanup = cleanup,
            LocalCleanupComplete = cleanup.All(slot => !slot.HostPppActive && !slot.ModemOpen && slot.WifiState == nameof(VoWifiState.Disconnected)),
            LocalPoolEmpty = service.Kernel.Pool.Slots.Count == 0, CarrierCallsPlacedByCheck = 0,
            SmsSentByCheck = 0, SubscriberIdentifiersExported = false }, new JsonSerializerOptions { WriteIndented = true });
    }
}
