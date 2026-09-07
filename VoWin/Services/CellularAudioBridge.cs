using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.IO;

namespace VoWin.Services;

/// <summary>
/// Bridges the host microphone/speaker with a modem USB Audio Class endpoint.
/// Quectel UAC voice PCM is 8 kHz, signed 16-bit, mono.
/// </summary>
internal sealed class CellularAudioWorker : IDisposable
{
    private WaveIn? _hostMicrophone;
    private WaveOut? _modemPlayback;
    private BufferedWaveProvider? _modemUplink;
    private WaveIn? _modemCapture;
    private WaveOut? _hostPlayback;
    private BufferedWaveProvider? _hostDownlink;
    private AudioFileReader? _messageReader;
    private int _hostMicrophonePeak;
    private int _modemDownlinkPeak;

    public bool IsRunning { get; private set; }
    public string DeviceSummary { get; private set; } = string.Empty;

    public void Start(string? messagePath = null)
    {
        Stop();

        var modemInput = FindInputDevice(isModem: true);
        var hostInput = FindInputDevice(isModem: false);
        var modemOutput = FindOutputDevice(isModem: true);
        var hostOutput = FindOutputDevice(isModem: false);
        if (modemInput < 0 || modemOutput < 0)
            throw new InvalidOperationException("Windows did not expose the modem AC Interface input/output endpoints.");
        if (hostInput < 0 || hostOutput < 0)
            throw new InvalidOperationException("No host microphone or speaker is available for cellular audio.");

        DeviceSummary = $"host mic={WaveIn.GetCapabilities(hostInput).ProductName}, " +
                        $"host speaker={WaveOut.GetCapabilities(hostOutput).ProductName}, " +
                        $"modem input={WaveIn.GetCapabilities(modemInput).ProductName}, " +
                        $"modem output={WaveOut.GetCapabilities(modemOutput).ProductName}";
        Interlocked.Exchange(ref _hostMicrophonePeak, 0);
        Interlocked.Exchange(ref _modemDownlinkPeak, 0);

        var format = new WaveFormat(8000, 16, 1);
        _modemUplink = CreateBuffer(format);
        _hostDownlink = CreateBuffer(format);

        _modemPlayback = new WaveOut { DeviceNumber = modemOutput, Volume = 1.0f };
        var uplinkMix = new MixingSampleProvider(new[] { _modemUplink.ToSampleProvider() }) { ReadFully = true };
        if (!string.IsNullOrWhiteSpace(messagePath) && File.Exists(messagePath))
        {
            _messageReader = new AudioFileReader(messagePath);
            ISampleProvider message = _messageReader;
            if (message.WaveFormat.Channels == 2) message = new StereoToMonoSampleProvider(message);
            if (message.WaveFormat.SampleRate != 8000) message = new WdlResamplingSampleProvider(message, 8000);
            uplinkMix.AddMixerInput(message);
        }
        _modemPlayback.Init(uplinkMix.ToWaveProvider16());
        _hostPlayback = new WaveOut { DeviceNumber = hostOutput, Volume = 1.0f };
        _hostPlayback.Init(_hostDownlink);

        _hostMicrophone = CreateCapture(hostInput, format, (_, args) =>
        {
            UpdatePeak(ref _hostMicrophonePeak, args.Buffer, args.BytesRecorded);
            _modemUplink?.AddSamples(args.Buffer, 0, args.BytesRecorded);
        });
        _modemCapture = CreateCapture(modemInput, format, (_, args) =>
        {
            UpdatePeak(ref _modemDownlinkPeak, args.Buffer, args.BytesRecorded);
            _hostDownlink?.AddSamples(args.Buffer, 0, args.BytesRecorded);
        });

        _modemPlayback.Play();
        _hostPlayback.Play();
        _hostMicrophone.StartRecording();
        _modemCapture.StartRecording();
        IsRunning = true;
    }

    public void Stop()
    {
        IsRunning = false;
        StopCapture(ref _hostMicrophone);
        StopCapture(ref _modemCapture);
        StopPlayback(ref _modemPlayback);
        StopPlayback(ref _hostPlayback);
        _modemUplink = null;
        _hostDownlink = null;
        _messageReader?.Dispose();
        _messageReader = null;
    }

    public (int HostMicrophonePeak, int ModemDownlinkPeak) ReadAndResetPeaks() =>
        (Interlocked.Exchange(ref _hostMicrophonePeak, 0),
         Interlocked.Exchange(ref _modemDownlinkPeak, 0));

    private static void UpdatePeak(ref int destination, byte[] buffer, int count)
    {
        var peak = 0;
        for (var offset = 0; offset + 1 < count; offset += 2)
        {
            var value = Math.Abs((int)BitConverter.ToInt16(buffer, offset));
            if (value > peak) peak = value;
        }

        var observed = Volatile.Read(ref destination);
        while (peak > observed)
        {
            var previous = Interlocked.CompareExchange(ref destination, peak, observed);
            if (previous == observed) break;
            observed = previous;
        }
    }

    private static BufferedWaveProvider CreateBuffer(WaveFormat format) => new(format)
    {
        DiscardOnBufferOverflow = true,
        ReadFully = true
    };

    private static WaveIn CreateCapture(int deviceNumber, WaveFormat format, EventHandler<WaveInEventArgs> handler)
    {
        var capture = new WaveIn
        {
            DeviceNumber = deviceNumber,
            WaveFormat = format,
            BufferMilliseconds = 20,
            NumberOfBuffers = 4
        };
        capture.DataAvailable += handler;
        return capture;
    }

    private static int FindInputDevice(bool isModem)
    {
        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            var modem = IsModemEndpoint(WaveIn.GetCapabilities(i).ProductName);
            if (modem == isModem) return i;
        }
        return -1;
    }

    private static int FindOutputDevice(bool isModem)
    {
        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            var modem = IsModemEndpoint(WaveOut.GetCapabilities(i).ProductName);
            if (modem == isModem) return i;
        }
        return -1;
    }

    private static bool IsModemEndpoint(string name) =>
        name.Contains("AC Interface", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Quectel", StringComparison.OrdinalIgnoreCase);

    private static void StopCapture(ref WaveIn? capture)
    {
        if (capture == null) return;
        try { capture.StopRecording(); } catch { }
        capture.Dispose();
        capture = null;
    }

    private static void StopPlayback(ref WaveOut? playback)
    {
        if (playback == null) return;
        try { playback.Stop(); } catch { }
        playback.Dispose();
        playback = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Runs the MME audio worker in a fresh VoWin process created after QDC507
/// republishes UAC. A long-lived WPF process otherwise keeps the pre-route
/// WinMM device mapping and receives a successful but permanent zero stream.
/// </summary>
internal sealed class CellularAudioBridge : IAsyncDisposable, IDisposable
{
    private Process? _process;
    private EventWaitHandle? _stopEvent;
    private TaskCompletionSource<string>? _ready;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _hostMicrophonePeak;
    private int _modemDownlinkPeak;

    public bool IsRunning => _process is { HasExited: false } && _ready?.Task.IsCompletedSuccessfully == true;
    public string DeviceSummary { get; private set; } = string.Empty;

    public async Task StartAsync(string? messagePath = null, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            var eventName = $@"Local\VoWinCellularAudio_{Guid.NewGuid():N}";
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            var ready = _ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("VoWin executable path is unavailable for the cellular-audio helper.", executable);
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--cellular-audio-helper");
            startInfo.ArgumentList.Add("--stop-event");
            startInfo.ArgumentList.Add(eventName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        if (!string.IsNullOrWhiteSpace(messagePath) && File.Exists(messagePath))
        {
            startInfo.ArgumentList.Add("--message-file");
            startInfo.ArgumentList.Add(messagePath);
        }

            var process = _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start VoWin cellular audio helper.");
            _ = PumpOutputAsync(process, ready);
            try
            {
                DeviceSummary = await ready.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
            }
            catch
            {
                await StopCoreAsync();
                throw new InvalidOperationException("Cellular audio helper did not become ready within 12 seconds.");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        var stopEvent = Interlocked.Exchange(ref _stopEvent, null);
        try { stopEvent?.Set(); } catch { }
        if (process != null)
        {
            try
            {
                var exitTask = process.WaitForExitAsync();
                var exited = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(3))) == exitTask;
                if (!exited && !process.HasExited)
                    process.Kill(true);
            }
            catch { }
            process.Dispose();
        }
        stopEvent?.Dispose();
        _ready = null;
        Interlocked.Exchange(ref _hostMicrophonePeak, 0);
        Interlocked.Exchange(ref _modemDownlinkPeak, 0);
    }

    public (int HostMicrophonePeak, int ModemDownlinkPeak) ReadAndResetPeaks() =>
        (Interlocked.Exchange(ref _hostMicrophonePeak, 0),
         Interlocked.Exchange(ref _modemDownlinkPeak, 0));

    private async Task PumpOutputAsync(Process process, TaskCompletionSource<string> ready)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("READY|", StringComparison.Ordinal))
                {
                    ready.TrySetResult(line[6..]);
                }
                else if (line.StartsWith("LEVEL|", StringComparison.Ordinal))
                {
                    var fields = line.Split('|');
                    if (fields.Length == 3 && int.TryParse(fields[1], out var uplink) && int.TryParse(fields[2], out var downlink))
                    {
                        Interlocked.Exchange(ref _hostMicrophonePeak, uplink);
                        Interlocked.Exchange(ref _modemDownlinkPeak, downlink);
                    }
                }
                else if (line.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    ready.TrySetException(new InvalidOperationException(line[6..]));
                }
            }
            if (!process.HasExited || process.ExitCode != 0)
                ready.TrySetException(new InvalidOperationException("Cellular audio helper exited before it was ready."));
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }
}

internal static class CellularAudioWorkerHost
{
    public static bool IsRequested(string[] args) => args.Contains("--cellular-audio-helper", StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            // This executable is normally a WPF app. Do not let WinMM capture
            // callbacks bind to its STA dispatcher: the helper is intentionally
            // headless and must consume PCM on NAudio's worker thread, like the
            // proven CLI bridge does.
            SynchronizationContext.SetSynchronizationContext(null);
            var eventName = ReadArgument(args, "--stop-event");
            var parentId = int.Parse(ReadArgument(args, "--parent-pid"));
            using var stopEvent = EventWaitHandle.OpenExisting(eventName);
            using var parent = Process.GetProcessById(parentId);
            var messagePath = ReadOptionalArgument(args, "--message-file");
            using var worker = new CellularAudioWorker();
            worker.Start(messagePath);
            Console.WriteLine("READY|" + worker.DeviceSummary);
            Console.Out.Flush();

            while (!stopEvent.WaitOne(1000) && !parent.HasExited)
            {
                var levels = worker.ReadAndResetPeaks();
                Console.WriteLine($"LEVEL|{levels.HostMicrophonePeak}|{levels.ModemDownlinkPeak}");
                Console.Out.Flush();
                await Task.Yield();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR|" + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
            Console.Out.Flush();
            return 1;
        }
    }

    private static string ReadArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException($"Missing {name}.");
        return args[index + 1];
    }

    private static string? ReadOptionalArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
