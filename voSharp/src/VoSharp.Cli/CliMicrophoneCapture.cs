using NAudio.Wave;
using VoSharp.Kernel;
using VoSharp.Telephony.Calls;

internal sealed class CliMicrophoneCapture(VoKernel kernel) : IDisposable
{
    private readonly object _gate = new();
    private WaveInEvent? _capture;
    private string? _callId;
    private string? _slotId;
    private long _buffersCaptured;
    private long _buffersForwarded;

    public static string ListDevices()
    {
        if (!OperatingSystem.IsWindows()) return "Windows microphone capture is unavailable on this platform.";
        var devices = Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(index => $"{index}: {WaveInEvent.GetCapabilities(index).ProductName}")
            .ToArray();
        return devices.Length == 0 ? "No recording devices found." : string.Join(Environment.NewLine, devices);
    }

    public string Status()
    {
        lock (_gate)
            return _capture is null ? "Microphone capture is off." :
                $"Microphone capture is active for call {_callId} on slot {_slotId}; " +
                $"buffers captured={Interlocked.Read(ref _buffersCaptured)}, " +
                $"forwarded={Interlocked.Read(ref _buffersForwarded)}.";
    }

    public string Start(int? deviceNumber = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CLI microphone capture requires Windows.");
        lock (_gate)
        {
            if (_capture is not null) throw new InvalidOperationException("Stop the current microphone capture first.");
            var call = kernel.ActiveCall;
            var slotId = kernel.ActiveCallSlotId;
            if (call?.State != CallState.Active || slotId is null ||
                !kernel.Pool.Slots.TryGetValue(slotId, out var slot) || !slot.Calls.IsHostImsCall ||
                slot.Calls.ExternalMediaBridgeEnabled ||
                !string.Equals(slot.Calls.ActiveCall?.CallId, call.CallId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An acknowledged, active Host IMS call is required before starting the CLI microphone.");

            var selected = deviceNumber ?? Enumerable.Range(0, WaveInEvent.DeviceCount)
                .FirstOrDefault(index => !IsModemEndpoint(WaveInEvent.GetCapabilities(index).ProductName), -1);
            if (selected < 0 || selected >= WaveInEvent.DeviceCount)
                throw new InvalidOperationException("No host microphone is available; use 'mic list' to inspect devices.");
            var name = WaveInEvent.GetCapabilities(selected).ProductName;
            if (IsModemEndpoint(name))
                throw new InvalidOperationException("The modem AC Interface is not a host microphone.");

            var capture = new WaveInEvent
            {
                DeviceNumber = selected,
                WaveFormat = new WaveFormat(8000, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers = 4
            };
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += (_, _) => _ = Task.Run(Stop);
            try
            {
                _callId = call.CallId;
                _slotId = slotId;
                Interlocked.Exchange(ref _buffersCaptured, 0);
                Interlocked.Exchange(ref _buffersForwarded, 0);
                _capture = capture;
                capture.StartRecording();
                return $"Capturing host microphone {selected} ({name}) for the current Host IMS call.";
            }
            catch
            {
                _capture = null;
                _callId = null;
                _slotId = null;
                capture.Dispose();
                throw;
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded < 2) return;
        string? callId;
        string? slotId;
        lock (_gate)
        {
            if (!ReferenceEquals(_capture, sender)) return;
            callId = _callId;
            slotId = _slotId;
        }
        if (callId is null) return;
        var samples = new short[args.BytesRecorded / 2];
        Buffer.BlockCopy(args.Buffer, 0, samples, 0, samples.Length * sizeof(short));
        Interlocked.Increment(ref _buffersCaptured);
        if (kernel.TrySendPresentedImsPcm(callId, slotId, samples))
            Interlocked.Increment(ref _buffersForwarded);
    }

    public void StopForCall(string callId)
    {
        lock (_gate)
            if (!string.Equals(_callId, callId, StringComparison.OrdinalIgnoreCase)) return;
        _ = Task.Run(Stop);
    }

    public void Stop()
    {
        WaveInEvent? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _callId = null;
            _slotId = null;
        }
        if (capture is null) return;
        try { capture.StopRecording(); } catch { }
        capture.Dispose();
    }

    private static bool IsModemEndpoint(string name) =>
        name.Contains("AC Interface", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Quectel", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => Stop();
}
