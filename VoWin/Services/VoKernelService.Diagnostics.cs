using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using VoSharp.Modem;

namespace VoWin.Services;

public partial class VoKernelService
{
    private readonly DiagnosticRecorder _diagnosticRecorder;
    private readonly SemaphoreSlim _diagnosticSettingsGate = new(1, 1);
    private readonly CancellationTokenSource _diagnosticSamplerCts = new();
    private readonly Task _diagnosticSampler;

    public bool DetailedDiagnosticsEnabled => _diagnosticRecorder.Enabled;
    public string DiagnosticRecordingStatus => _diagnosticRecorder.LastError is { } error
        ? $"写入失败：{error}"
        : DetailedDiagnosticsEnabled
            ? "记录中 · 最多 8 MiB · 已脱敏"
            : "详细记录已关闭";

    public async Task SetDetailedDiagnosticsAsync(bool enabled)
    {
        await _diagnosticSettingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _telephonySettingsStore.UpdateAsync(settings => settings.DetailedDiagnosticsEnabled = enabled).ConfigureAwait(false);
            _diagnosticRecorder.SetEnabled(enabled);
            if (enabled) RecordDiagnosticState("Recording started");
            PropertyChanged?.Invoke(this, new(nameof(DetailedDiagnosticsEnabled)));
            PropertyChanged?.Invoke(this, new(nameof(DiagnosticRecordingStatus)));
        }
        finally { _diagnosticSettingsGate.Release(); }
    }

    private string BuildCachedDiagnosticState(string? selectedSlot = null)
    {
        var result = new StringBuilder();
        var microphone = GetMicrophoneUplinkStatus();
        result.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        result.AppendLine($"VoWin: {typeof(VoKernelService).Assembly.GetName().Version}; .NET: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; Architecture: {RuntimeInformation.ProcessArchitecture}");
        result.AppendLine($"State: {StateMachineState}; Initial scan: {IsInitialScanRunning}; Presented call: {CurrentCallState}; Desktop input active: {microphone.IsCapturing}; Audio bridges running: {_cellularMedia.Values.Count(media => media.Bridge.IsRunning)}");
        result.AppendLine("Cached state only. No subscriber identity, raw packets, SMS body or audio samples collected.");
        if (microphone.HasSession)
            result.AppendLine($"Desktop audio input: recording={microphone.IsMessagePlayback}; device={microphone.DeviceName}; capturing={microphone.IsCapturing}; muted={microphone.IsMuted}; peak={microphone.InputPeak}; dataAgeMs={microphone.DataAge.TotalMilliseconds:F0}; {microphone.DetailText}; {microphone.StatusText}");
        // The pool is concurrent; do not enumerate UI-bound ObservableCollections on a worker.
        foreach (var slot in Kernel.Pool.Slots.Values)
        {
            try
            {
                var diag = slot.VoWifiDiag;
                var host = slot.GetHostImsRegistrationStatus();
                var bearer = slot.GetHostImsPppBearerStatus();
                result.AppendLine($"Slot: {slot.Id}; selected={slot.Id == selectedSlot}; port={slot.PortName}; SIM present={slot.Sim != null}; PLMN={slot.Sim?.Mcc}-{slot.Sim?.Mnc}; source={slot.ImsiIdentitySource}; multi-IMSI conflict={slot.HasImsiPlmnConflict}");
                result.AppendLine($"Mode={slot.PreferredTelephonyMode}; control={slot.ControlPreference}; QMI active={slot.Modem?.IsQmiActive}; QMI fallback={slot.Modem?.LastQmiFallbackReason}; enabled={slot.TelephonyEnabled}; modem open={slot.Modem?.IsOpen}; firmware={slot.Modem?.FirmwareRevision}; flight mode={slot.IsFlightMode}; proxy configured={!string.IsNullOrWhiteSpace(slot.ProxyUrl)}");
                result.AppendLine($"VoWiFi={diag?.State}; keep online requested={slot.VoWifi.KeepOnlineRequested}; automatic recovery paused={slot.VoWifi.AutomaticRecoveryPaused}; failure stage={diag?.FailureStage}; category={diag?.FailureCategory}; error={diag?.LastError}");
                result.AppendLine($"VoWiFi remote deregistered={slot.VoWifi.RemoteDeregistered}; removal error={slot.VoWifi.LastDeregistrationError}");
                result.AppendLine($"Registrar backoff: VoWiFi retry not before={slot.VoWifi.ImsRetryNotBeforeUtc:O}; Host IMS retry not before={host.RetryNotBeforeUtc:O}; Host IMS retries paused={host.AutomaticRecoveryPaused}; SIP failure={host.Failure?.StatusCode}; invalid Retry-After={host.Failure?.InvalidRetryAfter}");
                result.AppendLine($"ePDG={diag?.EpdgFqdn}; endpoint={diag?.EpdgIp}:{diag?.EpdgPort}; IKE suite={diag?.Suite}; DH={diag?.DhGroup}; EAP={diag?.EapMethod}");
                result.AppendLine($"VoWiFi network path={slot.VoWifi.NetworkPathStatus}");
                result.AppendLine($"Tunnel={diag?.Tunnel?.AccessStandard}; cipher={diag?.Tunnel?.EncryptionAlgorithm}; integrity={diag?.Tunnel?.IntegrityAlgorithm}; assigned IPv4={diag?.Tunnel?.AssignedIPv4}; assigned IPv6={diag?.Tunnel?.AssignedIPv6}; P-CSCF={diag?.Tunnel?.PcscfIp}");
                result.AppendLine($"IMS={diag?.Ims?.RegistrationState}; expiry={diag?.Ims?.ExpiresSeconds}; probes sent={diag?.SipProbesSent}; ack={diag?.SipProbesSuccess}; failed={diag?.SipProbesFailed}; RTT ms={diag?.LastProbeRttMs}; last result={diag?.LastProbeResult}");
                result.AppendLine($"Host IMS={host.State}; registered={host.IsRegistered}; expiry={host.ExpiresAtUtc:O}; remote deregistered={host.RemoteDeregistered}; endpoint={host.Endpoint}; error={host.LastError}; PPP active={bearer.IsActive}; renewals={bearer.RenewalCount}; media routes={bearer.MediaRouteCount}; default route repairs={bearer.DefaultRouteRepairCount}; error={bearer.LastError}; call={slot.Calls.ActiveCall?.State}");
                if (_moduleInstances.TryGetValue(slot.Id, out var instance) && instance.HostRecoveryStatus is { } recovery)
                    result.AppendLine($"Host IMS recovery: state={recovery.State}; requested={recovery.KeepOnlineRequested}; paused={recovery.IsPaused}; attempts={recovery.Attempts}; last success={recovery.LastRecoveredAtUtc:O}; reason={recovery.Message}; loss kind={host.LossKind}");
                if (_cellularMedia.TryGetValue(slot.Id, out var usb))
                    result.AppendLine($"USB audio: bridge running={usb.Bridge.IsRunning}; QDC507 route active={usb.RouteActive}; reserved for SIP={IsReserved(slot.Id)}");
                if (slot.Calls.GetMediaStats() is { } media)
                    result.AppendLine($"Media: codec={slot.Calls.ActiveCall?.Codec}; RTP sent={media.PacketsSent}; received={media.PacketsReceived}; decoded={media.AudioFramesDecoded}; PCM samples={media.PcmSamplesReceived}; encoded={media.AudioFramesEncoded}; audio packets sent={media.AudioPacketsSent}; encode errors={media.AudioEncodeErrors}; send errors={media.SendErrors}; playback open={media.Playback?.IsOpen}; playback frames={media.Playback?.FramesWritten}; playback drops={media.Playback?.FramesDropped}; playback errors={media.Playback?.WriteErrors}");
            }
            catch (Exception ex) { result.AppendLine($"Slot snapshot unavailable: {ex.GetType().Name}"); }
        }
        return DiagnosticLogRedactor.Redact(result.ToString());
    }

    private void RecordDiagnosticState(string reason)
    {
        if (!DetailedDiagnosticsEnabled) return;
        _diagnosticRecorder.Record("TRACE", "Snapshot", reason + "\n" + BuildCachedDiagnosticState());
    }

    private void LogImsMediaSnapshot(string? slotId, string reason)
    {
        if (slotId == null || !Kernel.Pool.Slots.TryGetValue(slotId, out var slot)) return;
        try
        {
            if (slot.Calls.GetMediaConfiguration() is not { } config ||
                slot.Calls.GetMediaStats() is not { } stats) return;
            var route = "not-interface-pinned";
            if (config.BoundInterfaceIndex is { } bound && config.RemoteAddress is { } remote)
            {
                var check = HostImsRouteVerifier.Check([new HostImsEndpointCandidate(5, config.LocalAddress, remote)]).Single();
                route = $"verified={check.IsVerified && check.InterfaceIndex == bound}; bestIf={check.InterfaceIndex}; boundIf={bound}";
            }
            AddLog("INFO", "ImsMedia", $"{reason}; slot={slotId}; codec={config.Codec}; PT={config.PayloadType}; octetAligned={config.AmrOctetAligned}; AMR mode={config.AmrEncodeMode}; modeSet={config.AmrModeSet ?? "default"}; local={config.LocalAddress}:{config.LocalPort}; remote={config.RemoteAddress}:{config.RemotePort}; enabled={config.MediaEnabled}; route={route}; mediaRoutes={slot.GetHostImsPppBearerStatus().MediaRouteCount}; RTP sent={stats.PacketsSent}; received={stats.PacketsReceived}; decoded={stats.AudioFramesDecoded}; audioSent={stats.AudioPacketsSent}; errors={stats.SendErrors}; local-send-success-is-not-far-end-audibility; no raw SDP, packets or audio logged.");
        }
        catch (Exception ex) { AddLog("WARN", "ImsMedia", $"Media snapshot unavailable ({ex.GetType().Name})."); }
    }

    private async Task SampleDiagnosticsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                RecordDiagnosticState("10-second health sample");
                var microphone = GetMicrophoneUplinkStatus();
                if (microphone.HasSession)
                {
                    AddLog("INFO", "AudioUplink", $"{microphone.DetailText}; capturing={microphone.IsCapturing}; muted={microphone.IsMuted}; peak={microphone.InputPeak}; dataAgeMs={microphone.DataAge.TotalMilliseconds:F0}; {microphone.StatusText}; no audio samples logged.");
                    LogImsMediaSnapshot(microphone.SlotId, "Uplink health sample");
                }
                PropertyChanged?.Invoke(this, new(nameof(DiagnosticRecordingStatus)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public Task ExportDiagnosticBundleAsync(string destination, string? slotId = null, CancellationToken cancellationToken = default)
    {
        var snapshot = new StringBuilder(BuildCachedDiagnosticState(slotId));
        snapshot.AppendLine("=== Windows network configuration (read-only; no connectivity probes) ===");
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
            {
                var properties = adapter.GetIPProperties();
                snapshot.AppendLine($"Type={adapter.NetworkInterfaceType}; IP=[{string.Join(',', properties.UnicastAddresses.Select(item => item.Address))}]; DNS=[{string.Join(',', properties.DnsAddresses)}]; gateways=[{string.Join(',', properties.GatewayAddresses.Select(item => item.Address))}]");
            }
        }
        catch (Exception ex) { snapshot.AppendLine($"Network configuration unavailable: {ex.GetType().Name}"); }
        return _diagnosticRecorder.ExportAsync(destination, snapshot.ToString(), cancellationToken);
    }
}
