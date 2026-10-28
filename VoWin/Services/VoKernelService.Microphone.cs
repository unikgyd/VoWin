using VoSharp.Telephony.Calls;
using VoWin.Models;

namespace VoWin.Services;

public partial class VoKernelService
{
    private void StartAutomaticAnswerMessage(string path)
    {
        string? callId, slotId;
        lock (_cellularSipReservationGate)
        {
            if (_desktopAudioKind != DesktopAudioKind.Microphone) return;
            callId = _desktopAudioCallId; slotId = _desktopAudioSlotId;
        }
        if (callId == null) return;
        StopMicrophone();
        var session = new DesktopMicrophoneSession(callId, slotId, "自动应答录音 · 直接发送到对端",
            pcm => Kernel is VoSharp.Kernel.VoKernel kernel && kernel.TrySendPresentedImsPcm(callId, slotId, pcm),
            isMessagePlayback: true);
        var calls = slotId == null ? null : Kernel.Pool.Slots.GetValueOrDefault(slotId)?.Calls;
        if (calls?.ActiveCall?.CallId == callId && calls.GetMediaStats() is { } stats)
            session.SetTransportBaseline(stats);
        Volatile.Write(ref _microphoneSession, session);
        var cts = new CancellationTokenSource();
        Volatile.Write(ref _autoAnswerPlaybackCts, cts);
        session.Started();
        LogImsMediaSnapshot(slotId, "Automatic answer recording starting after ACK");
        AddLog("INFO", "AutoAnswerAudio", "Starting call-bound recording uplink: 8000Hz/16bit/mono; no local microphone or speaker opened.");
        _autoAnswerPlaybackTask = Task.Run(async () =>
        {
            try
            {
                var frames = await CallMessagePlayback.SendAsync(path, session.Process, cts.Token).ConfigureAwait(false);
                AddLog("INFO", "AutoAnswerAudio", $"Recording uplink completed: PCM frames={frames}; no audio samples logged.");
                PostToUi(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _microphoneSession), session) ||
                        Kernel.ActiveCall?.CallId != callId || Kernel.ActiveCallSlotId != slotId ||
                        Kernel.ActiveCall?.State != CallState.Active) return;
                    var muted = session.Snapshot().IsMuted;
                    StartMicrophone();
                    SetMicrophoneMuted(muted);
                });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                session.CaptureStopped(ex);
                AddLog("WARN", "AutoAnswerAudio", $"Recording uplink failed ({ex.GetType().Name}); manual microphone takeover is available.");
            }
            finally
            {
                Interlocked.CompareExchange(ref _autoAnswerPlaybackCts, null, cts);
                cts.Dispose();
            }
        });
    }

    public MicrophoneUplinkStatus GetMicrophoneUplinkStatus()
    {
        var session = Volatile.Read(ref _microphoneSession);
        if (session == null) return new();
        var status = session.Snapshot();
        var calls = status.SlotId == null ? null : Kernel.Pool.Slots.GetValueOrDefault(status.SlotId)?.Calls;
        if (calls != null && calls.ActiveCall?.CallId == status.CallId && calls.GetMediaStats() is { } stats)
            session.ObserveTransport(stats);
        return session.Snapshot();
    }

    public bool SetMicrophoneMuted(bool muted)
    {
        var session = Volatile.Read(ref _microphoneSession);
        if (session == null) return false;
        var status = session.Snapshot();
        if (status.CallId != Kernel.ActiveCall?.CallId || status.SlotId != Kernel.ActiveCallSlotId) return false;
        session.SetMuted(muted);
        return true;
    }

    public async Task<bool> RestartMicrophoneAsync()
    {
        var original = Volatile.Read(ref _microphoneSession)?.Snapshot();
        if (original == null || Kernel.ActiveCall?.State != CallState.Active) return false;
        var restarted = false;
        await RunOnUiAsync(() =>
        {
            lock (_cellularSipReservationGate)
            {
                if (_desktopAudioKind != DesktopAudioKind.Microphone || _desktopAudioCallId != original.CallId ||
                    _desktopAudioSlotId != original.SlotId || Kernel.ActiveCall?.CallId != original.CallId ||
                    Kernel.ActiveCall?.State != CallState.Active || Kernel.ActiveCallSlotId != original.SlotId) return;
            }
            StartMicrophone();
            SetMicrophoneMuted(original.IsMuted);
            restarted = GetMicrophoneUplinkStatus().IsCapturing;
        });
        return restarted;
    }
}
