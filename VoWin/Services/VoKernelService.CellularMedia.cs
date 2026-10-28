using System.Collections.Concurrent;
using qmiSharp.Transport;
using VoSharp.Telephony.Calls;

namespace VoWin.Services;

public partial class VoKernelService
{
    private readonly ConcurrentDictionary<string, CellularModuleMediaContext> _cellularMedia = new(StringComparer.OrdinalIgnoreCase);

    private CellularModuleMediaContext GetCellularMedia(string slotId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _serviceDisposed) != 0, this);
        var slot = Kernel.Pool.Slots.GetValueOrDefault(slotId) ?? throw new InvalidOperationException("呼叫所属模组已断开。");
        var container = UsbAudioModemBinding.RequireSelected(WindowsUsbTopology.EnumeratePresent(), slot.PortName, false);
        lock (_cellularMedia)
        {
            if (_cellularMedia.TryGetValue(slotId, out var existing))
            {
                if (existing.ContainerId != container || existing.AtPort != slot.PortName || existing.Imei != slot.Imei)
                    throw new InvalidOperationException("模组 USB 身份已变化，请重新扫描后再使用音频。");
                return existing;
            }
            var media = new CellularModuleMediaContext(slotId, slot.PortName, container, slot.Imei);
            _cellularMedia[slotId] = media;
            return media;
        }
    }

    private async Task<bool> OpenCellularAudioBridgeAsync(CellularModuleMediaContext media,
        string? messagePath, CancellationToken cancellationToken, bool externalPcm)
    {
        try
        {
            if (UsbAudioModemBinding.RequireSelected(WindowsUsbTopology.EnumeratePresent(), media.AtPort, true) != media.ContainerId)
                throw new InvalidOperationException("模组 USB 身份已变化。");
            await media.Bridge.StartAsync(media.AtPort, media.ContainerId, messagePath, cancellationToken, externalPcm).ConfigureAwait(false);
            AddLog("INFO", "CellularAudio", $"{media.AtPort}: " + (externalPcm
                ? "SIP RTP PCM connected directly to this modem's USB Audio endpoints."
                : "PC microphone/speaker connected to this modem's USB Audio endpoints."));
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AddLog("ERROR", "CellularAudio", $"{media.AtPort}: USB audio startup failed: {ex.Message}");
            return false;
        }
    }

    private async Task PrepareQdc507VoiceRuntimeAsync()
    {
        foreach (var slot in Kernel.Pool.Slots.Values.Where(slot => !slot.IsPcscReader && slot.Modem?.IsOpen == true &&
            slot.Modem.FirmwareRevision?.Contains("QDC507", StringComparison.OrdinalIgnoreCase) == true))
        {
            try
            {
                var media = GetCellularMedia(slot.Id);
                UsbAudioModemBinding.RequireQdc507Runtime(WindowsUsbTopology.EnumeratePresent(), media.AtPort, media.ContainerId, media.Imei);
                await media.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref _serviceDisposed) != 0 || media.IsDisposed) return;
                    await media.Runtime.PrepareAsync().ConfigureAwait(false);
                }
                finally { media.Gate.Release(); }
                AddLog("INFO", "CellularAudio", $"{media.AtPort}: QDC507 voice drivers and calibration are ready.");
            }
            catch (Exception ex) { AddLog("INFO", "CellularAudio", $"{slot.PortName}: QDC507 runtime is not available: {ex.Message}"); }
        }
    }

    private async Task<bool> StartCellularCallAudioAsync(bool standardUacReady, string? messagePath,
        CancellationToken cancellationToken, string slotId, string ownerId, bool externalPcm = false)
    {
        var media = GetCellularMedia(slotId);
        var token = media.Begin(ownerId, cancellationToken);
        var acquired = false;
        var keepAudio = false;
        try
        {
            await media.Gate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            token.ThrowIfCancellationRequested();
            if (!externalPcm) PostToUi(StopMicrophone);
            if (!standardUacReady)
            {
                UsbAudioModemBinding.RequireQdc507Runtime(WindowsUsbTopology.EnumeratePresent(), media.AtPort, media.ContainerId, media.Imei);
                AddLog("INFO", "CellularAudio", $"{media.AtPort}: Starting QDC507 D4/UAC route...");
                media.RouteActive = true;
                await media.Runtime.StartRouteAsync(token).ConfigureAwait(false);
                // The proven QDC507 firmware requires settling after audio_enable.
                // This delay is firmware-specific, not ADB PCM transport latency.
                await Task.Delay(TimeSpan.FromSeconds(7), token).ConfigureAwait(false);
            }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (await OpenCellularAudioBridgeAsync(media, messagePath, token, externalPcm).ConfigureAwait(false))
                {
                    keepAudio = true;
                    AddLog("INFO", "CellularAudio", $"{media.AtPort}: Audio devices: {media.Bridge.DeviceSummary}");
                    if (!externalPcm) CallMediaStatusChanged?.Invoke(standardUacReady ? "Cellular/UAC 8 kHz" : "Cellular/QDC507 UAC 8 kHz");
                    _ = MonitorCellularAudioLevelsAsync(media, token, externalPcm);
                    return true;
                }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
            if (!externalPcm) CallMediaStatusChanged?.Invoke("Cellular/Windows audio unavailable");
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        finally
        {
            if (!keepAudio)
            {
                if (acquired) await StopCellularMediaCoreAsync(media).ConfigureAwait(false);
                media.End(ownerId);
            }
            if (acquired) media.Gate.Release();
        }
    }

    private async Task MonitorCellularAudioLevelsAsync(CellularModuleMediaContext media, CancellationToken token, bool externalPcm)
    {
        try
        {
            for (var sample = 0; sample < 4; sample++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                var levels = media.Bridge.ReadAndResetPeaks();
                AddLog("INFO", "CellularAudio", $"{media.AtPort}: PCM peaks uplink={levels.HostMicrophonePeak}, downlink={levels.ModemDownlinkPeak}.");
                if (sample == 0 && levels.ModemDownlinkPeak <= 1)
                {
                    await media.Gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        AddLog("WARN", "CellularAudio", $"{media.AtPort}: Reopening this modem's zero-stream endpoints after enumeration settled.");
                        await OpenCellularAudioBridgeAsync(media, null, token, externalPcm).ConfigureAwait(false);
                    }
                    finally { media.Gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("WARN", "CellularAudio", $"{media.AtPort}: Audio monitor failed: {ex.Message}"); }
    }

    private async Task StopCellularMediaCoreAsync(CellularModuleMediaContext media)
    {
        try { await media.Bridge.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { AddLog("WARN", "CellularAudio", $"{media.AtPort}: Audio stop failed: {ex.Message}"); }
        if (!media.RouteActive) return;
        media.RouteActive = false;
        try { await media.Runtime.StopRouteAsync().ConfigureAwait(false); }
        catch (Exception ex) { AddLog("WARN", "CellularAudio", $"{media.AtPort}: QDC507 route stop failed: {ex.Message}"); }
    }

    private async Task StopCellularModuleAudioAsync(string? slotId, string? ownerId = null)
    {
        if (slotId == null || !_cellularMedia.TryGetValue(slotId, out var media)) return;
        var canceledOwner = media.Cancel(ownerId);
        if (canceledOwner == null) return;
        await media.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (media.IsDisposed || !media.IsOwner(canceledOwner)) return;
            await StopCellularMediaCoreAsync(media).ConfigureAwait(false);
            media.End(canceledOwner);
        }
        finally { media.Gate.Release(); }
    }

    private async Task DisposeRemovedCellularMediaAsync(string slotId)
    {
        if (!_cellularMedia.TryRemove(slotId, out var media)) return;
        try { await media.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { AddLog("WARN", "CellularAudio", $"{media.AtPort}: Removed modem audio cleanup failed: {ex.Message}"); }
    }

    public void Reserve(string slotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        var selected = Kernel.Pool.Slots.GetValueOrDefault(slotId) ?? throw new InvalidOperationException("所选模组已断开。");
        var container = UsbAudioModemBinding.RequireSelected(WindowsUsbTopology.EnumeratePresent(), selected.PortName, false);
        ReserveCellularMedia(slotId, container);
    }

    private void ReserveCellularMedia(string slotId, Guid container)
    {
        lock (_cellularSipReservationGate)
        {
            if (_cellularSipReservations.ContainsKey(slotId) || _cellularSipReservations.Values.Any(r => r.ContainerId == container))
                throw new InvalidOperationException("所选模组的 USB 音频已被另一 SIP 呼叫占用或正在回收。");
            if (_desktopAudioKind == DesktopAudioKind.CellularUsb &&
                (string.Equals(_desktopAudioSlotId, slotId, StringComparison.OrdinalIgnoreCase) ||
                 _desktopAudioSlotId != null && _cellularMedia.TryGetValue(_desktopAudioSlotId, out var desktop) && desktop.ContainerId == container))
                throw new InvalidOperationException("所选模组的 USB 音频已被本地呼叫占用。");
            _cellularSipReservations[slotId] = new(container);
        }
    }

    public bool IsReserved(string? slotId) => !string.IsNullOrWhiteSpace(slotId) && _cellularSipReservations.ContainsKey(slotId);

    public async Task<ICallPcmMedia> OpenAsync(string slotId, CancellationToken ct)
    {
        var slot = Kernel.Pool.Slots.GetValueOrDefault(slotId) ?? throw new InvalidOperationException("SIP 语音所属模组已断开。");
        if (!_cellularSipReservations.TryGetValue(slotId, out var reservation) || reservation.ReleaseTask != null)
            throw new InvalidOperationException("所选模组的 USB 音频没有预留给本 SIP 呼叫。");
        var media = GetCellularMedia(slotId);
        if (media.ContainerId != reservation.ContainerId) throw new InvalidOperationException("USB 模组身份在预留音频后已变化。");
        Task<bool> startup;
        lock (_cellularSipReservationGate)
        {
            if (!_cellularSipReservations.TryGetValue(slotId, out var current) || !ReferenceEquals(current, reservation) || reservation.ReleaseTask != null)
                throw new OperationCanceledException("SIP 音频预留已释放。", ct);
            // Begin assigns the owner synchronously before the first await.
            // Release cannot miss a startup that has not created its context yet.
            startup = StartCellularCallAudioAsync(slot.CellularUsbAudioAvailable, null, ct, slotId, reservation.OwnerId, true);
        }
        if (!await startup.ConfigureAwait(false))
            throw new InvalidOperationException("所选模组的 USB PCM 未就绪。");
        lock (_cellularSipReservationGate)
            if (!_cellularSipReservations.TryGetValue(slotId, out var current) || !ReferenceEquals(current, reservation) || reservation.ReleaseTask != null)
                throw new OperationCanceledException("SIP 音频在启动期间已释放。", ct);
        return media.Bridge;
    }

    public Task ReleaseAsync(string? slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId)) return Task.CompletedTask;
        lock (_cellularSipReservationGate)
        {
            if (!_cellularSipReservations.TryGetValue(slotId, out var reservation)) return Task.CompletedTask;
            return reservation.ReleaseTask ??= ReleaseCellularReservationAsync(slotId, reservation);
        }
    }
    private async Task ReleaseCellularReservationAsync(string slotId, CellularSipReservation reservation)
    {
        try { await StopCellularModuleAudioAsync(slotId, reservation.OwnerId).ConfigureAwait(false); }
        finally
        {
            lock (_cellularSipReservationGate)
                ((ICollection<KeyValuePair<string, CellularSipReservation>>)_cellularSipReservations).Remove(new(slotId, reservation));
        }
    }
}
