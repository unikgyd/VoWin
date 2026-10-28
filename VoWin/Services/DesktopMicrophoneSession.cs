using System.Buffers.Binary;
using VoWin.Models;
using VoSharp.Telephony.Calls;

namespace VoWin.Services;

/// <summary>A call-scoped PCM sink: late callbacks can never feed a newer call.</summary>
internal sealed class DesktopMicrophoneSession(string callId, string? slotId, string deviceName,
    Func<short[], bool> forward, Func<DateTimeOffset>? clock = null, bool isMessagePlayback = false)
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly DateTimeOffset _startedAt = (clock ?? (() => DateTimeOffset.UtcNow))();
    private DateTimeOffset? _lastData;
    private DateTimeOffset? _peakAt;
    private bool _closed, _running, _muted;
    private long _captured, _forwarded, _rejected, _errors;
    private int _peak;
    private string? _error;
    private ulong _encoded, _packets, _encodeErrors, _sendErrors;
    private ulong _encodedBaseline, _packetsBaseline, _encodeErrorsBaseline, _sendErrorsBaseline;

    public void SetTransportBaseline(RtpMediaStats stats)
    {
        lock (_gate)
        {
            _encodedBaseline = stats.AudioFramesEncoded; _packetsBaseline = stats.AudioPacketsSent;
            _encodeErrorsBaseline = stats.AudioEncodeErrors; _sendErrorsBaseline = stats.SendErrors;
        }
    }

    public void ObserveTransport(RtpMediaStats stats)
    {
        lock (_gate)
        {
            _encoded = stats.AudioFramesEncoded - Math.Min(stats.AudioFramesEncoded, _encodedBaseline);
            _packets = stats.AudioPacketsSent - Math.Min(stats.AudioPacketsSent, _packetsBaseline);
            _encodeErrors = stats.AudioEncodeErrors - Math.Min(stats.AudioEncodeErrors, _encodeErrorsBaseline);
            _sendErrors = stats.SendErrors - Math.Min(stats.SendErrors, _sendErrorsBaseline);
        }
    }

    public void Started() { lock (_gate) { if (!_closed && _error == null) _running = true; } }
    public void SetMuted(bool muted) { lock (_gate) _muted = muted; }
    public void CaptureStopped(Exception? error)
    {
        lock (_gate)
        {
            if (_closed) return;
            _running = false;
            _error ??= error == null ? "采集意外停止" : error.GetType().Name;
        }
    }
    public void Close() { lock (_gate) { _closed = true; _running = false; } }

    public void Process(byte[] buffer, int count)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (count <= 0) return;
            if (count > buffer.Length || count % 2 != 0)
            {
                _errors++;
                return;
            }
            var now = _clock();
            _lastData = now;
            _captured++;
            var samples = new short[count / 2];
            var peak = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2, 2));
                peak = Math.Max(peak, Math.Abs((int)sample));
                samples[i] = _muted ? (short)0 : sample;
            }
            if (_peakAt == null || now - _peakAt >= TimeSpan.FromMilliseconds(250) || peak >= _peak)
            {
                _peak = peak;
                _peakAt = now;
            }
            try
            {
                if (forward(samples)) _forwarded++;
                else _rejected++;
            }
            catch
            {
                // NAudio stops capture if DataAvailable throws. Keep the
                // callback alive and surface the error instead of silent death.
                _errors++;
            }
        }
    }

    public MicrophoneUplinkStatus Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            return new()
            {
                CallId = callId, SlotId = slotId, DeviceName = deviceName,
                IsCapturing = _running, IsMuted = _muted, IsMessagePlayback = isMessagePlayback, CapturedBuffers = _captured,
                ForwardedBuffers = _forwarded, RejectedBuffers = _rejected, ProcessingErrors = _errors,
                InputPeak = _peak, SessionAge = now - _startedAt, DataAge = now - (_lastData ?? _startedAt), Error = _error,
                EncodedFrames = _encoded, AudioPacketsSent = _packets, EncodeErrors = _encodeErrors, SendErrors = _sendErrors
            };
        }
    }
}
