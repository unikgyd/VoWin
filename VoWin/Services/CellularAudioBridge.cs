using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;
using VoSharp.Telephony.Calls;

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
    private CancellationTokenSource? _externalCts;
    private Channel<byte[]>? _downlinkFrames;
    private Task? _uplinkPump;
    private Task? _downlinkPump;

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

    /// <summary>
    /// Opens only the modem UAC endpoints. Uplink PCM is read from the parent
    /// SIP gateway and captured modem PCM is returned over the second pipe.
    /// No Windows microphone or speaker is opened in this mode.
    /// </summary>
    public void StartExternal(Stream uplinkSource, Stream downlinkDestination, string? messagePath = null)
    {
        ArgumentNullException.ThrowIfNull(uplinkSource);
        ArgumentNullException.ThrowIfNull(downlinkDestination);
        Stop();

        var modemInput = FindInputDevice(isModem: true);
        var modemOutput = FindOutputDevice(isModem: true);
        if (modemInput < 0 || modemOutput < 0)
            throw new InvalidOperationException("Windows did not expose the modem AC Interface input/output endpoints.");

        DeviceSummary = $"external SIP PCM, modem input={WaveIn.GetCapabilities(modemInput).ProductName}, " +
                        $"modem output={WaveOut.GetCapabilities(modemOutput).ProductName}";
        Interlocked.Exchange(ref _hostMicrophonePeak, 0);
        Interlocked.Exchange(ref _modemDownlinkPeak, 0);

        var format = new WaveFormat(8000, 16, 1);
        _modemUplink = CreateBuffer(format);
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

        _downlinkFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _modemCapture = CreateCapture(modemInput, format, (_, args) =>
        {
            UpdatePeak(ref _modemDownlinkPeak, args.Buffer, args.BytesRecorded);
            var copy = new byte[args.BytesRecorded];
            Buffer.BlockCopy(args.Buffer, 0, copy, 0, args.BytesRecorded);
            _downlinkFrames?.Writer.TryWrite(copy);
        });

        _externalCts = new CancellationTokenSource();
        var token = _externalCts.Token;
        _uplinkPump = Task.Run(() => PumpExternalUplinkAsync(uplinkSource, token), token);
        _downlinkPump = Task.Run(() => PumpExternalDownlinkAsync(downlinkDestination, token), token);
        _modemPlayback.Play();
        _modemCapture.StartRecording();
        IsRunning = true;
    }

    private async Task PumpExternalUplinkAsync(Stream source, CancellationToken ct)
    {
        var buffer = new byte[320];
        while (!ct.IsCancellationRequested)
        {
            var count = await ReadPcmFrameAsync(source, buffer, ct).ConfigureAwait(false);
            if (count < buffer.Length) break;
            UpdatePeak(ref _hostMicrophonePeak, buffer, count);
            _modemUplink?.AddSamples(buffer, 0, count);
        }
    }

    private static async Task<int> ReadPcmFrameAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await source.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
        }
        return total;
    }

    private async Task PumpExternalDownlinkAsync(Stream destination, CancellationToken ct)
    {
        if (_downlinkFrames == null) return;
        await foreach (var frame in _downlinkFrames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await destination.WriteAsync(frame, ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        IsRunning = false;
        var externalCts = Interlocked.Exchange(ref _externalCts, null);
        try { externalCts?.Cancel(); } catch { }
        _downlinkFrames?.Writer.TryComplete();
        StopCapture(ref _hostMicrophone);
        StopCapture(ref _modemCapture);
        StopPlayback(ref _modemPlayback);
        StopPlayback(ref _hostPlayback);
        _modemUplink = null;
        _hostDownlink = null;
        _messageReader?.Dispose();
        _messageReader = null;
        try { Task.WaitAll([_uplinkPump ?? Task.CompletedTask, _downlinkPump ?? Task.CompletedTask], 1000); } catch { }
        _uplinkPump = null;
        _downlinkPump = null;
        _downlinkFrames = null;
        externalCts?.Dispose();
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
internal sealed class CellularAudioBridge : IAsyncDisposable, IDisposable, ICallPcmMedia
{
    private Process? _process;
    private EventWaitHandle? _stopEvent;
    private TaskCompletionSource<string>? _ready;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _hostMicrophonePeak;
    private int _modemDownlinkPeak;
    private NamedPipeServerStream? _uplinkPipe;
    private NamedPipeServerStream? _downlinkPipe;
    private CancellationTokenSource? _pcmCts;
    private Channel<byte[]>? _uplinkFrames;
    private Task? _uplinkPump;
    private Task? _downlinkPump;

    public bool IsRunning => _process is { HasExited: false } && _ready?.Task.IsCompletedSuccessfully == true;
    public string DeviceSummary { get; private set; } = string.Empty;
    public event Action<short[]>? RemotePcmReceived;

    public async Task StartAsync(
        string? messagePath = null,
        CancellationToken cancellationToken = default,
        bool externalPcm = false)
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
            Task? pipeConnect = null;
            if (externalPcm)
            {
                var pipeBase = $"VoWinCellularPcm_{Guid.NewGuid():N}";
                _uplinkPipe = new NamedPipeServerStream(pipeBase + "_up", PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _downlinkPipe = new NamedPipeServerStream(pipeBase + "_down", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                startInfo.ArgumentList.Add("--external-pcm");
                startInfo.ArgumentList.Add("--uplink-pipe");
                startInfo.ArgumentList.Add(pipeBase + "_up");
                startInfo.ArgumentList.Add("--downlink-pipe");
                startInfo.ArgumentList.Add(pipeBase + "_down");
                pipeConnect = Task.WhenAll(
                    _uplinkPipe.WaitForConnectionAsync(cancellationToken),
                    _downlinkPipe.WaitForConnectionAsync(cancellationToken));
            }
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
                if (pipeConnect != null)
                {
                    await pipeConnect.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
                    StartPcmPumps();
                }
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
        var pcmCts = Interlocked.Exchange(ref _pcmCts, null);
        try { pcmCts?.Cancel(); } catch { }
        _uplinkFrames?.Writer.TryComplete();
        try { _uplinkPipe?.Dispose(); } catch { }
        try { _downlinkPipe?.Dispose(); } catch { }
        _uplinkPipe = null;
        _downlinkPipe = null;
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
        try { await Task.WhenAll(_uplinkPump ?? Task.CompletedTask, _downlinkPump ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        _uplinkPump = null;
        _downlinkPump = null;
        _uplinkFrames = null;
        pcmCts?.Dispose();
        Interlocked.Exchange(ref _hostMicrophonePeak, 0);
        Interlocked.Exchange(ref _modemDownlinkPeak, 0);
    }

    public (int HostMicrophonePeak, int ModemDownlinkPeak) ReadAndResetPeaks() =>
        (Interlocked.Exchange(ref _hostMicrophonePeak, 0),
         Interlocked.Exchange(ref _modemDownlinkPeak, 0));

    public void SendExternalPcm(short[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (_uplinkFrames == null || samples.Length == 0) return;
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        _uplinkFrames.Writer.TryWrite(bytes);
    }

    private void StartPcmPumps()
    {
        _pcmCts = new CancellationTokenSource();
        _uplinkFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        var token = _pcmCts.Token;
        _uplinkPump = Task.Run(async () =>
        {
            await foreach (var frame in _uplinkFrames.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await _uplinkPipe!.WriteAsync(frame, token).ConfigureAwait(false);
                await _uplinkPipe.FlushAsync(token).ConfigureAwait(false);
            }
        }, token);
        _downlinkPump = Task.Run(async () =>
        {
            var bytes = new byte[320];
            while (!token.IsCancellationRequested)
            {
                var count = await ReadPcmFrameAsync(_downlinkPipe!, bytes, token).ConfigureAwait(false);
                if (count < bytes.Length) break;
                var samples = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                try { RemotePcmReceived?.Invoke(samples); } catch { }
            }
        }, token);
    }

    private static async Task<int> ReadPcmFrameAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await source.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
        }
        return total;
    }

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
            NamedPipeClientStream? uplinkPipe = null;
            NamedPipeClientStream? downlinkPipe = null;
            if (args.Contains("--external-pcm", StringComparer.Ordinal))
            {
                uplinkPipe = new NamedPipeClientStream(".", ReadArgument(args, "--uplink-pipe"), PipeDirection.In, PipeOptions.Asynchronous);
                downlinkPipe = new NamedPipeClientStream(".", ReadArgument(args, "--downlink-pipe"), PipeDirection.Out, PipeOptions.Asynchronous);
                await Task.WhenAll(
                    uplinkPipe.ConnectAsync(10000),
                    downlinkPipe.ConnectAsync(10000)).ConfigureAwait(false);
                worker.StartExternal(uplinkPipe, downlinkPipe, messagePath);
            }
            else
            {
                worker.Start(messagePath);
            }
            Console.WriteLine("READY|" + worker.DeviceSummary);
            Console.Out.Flush();

            while (!stopEvent.WaitOne(1000) && !parent.HasExited)
            {
                var levels = worker.ReadAndResetPeaks();
                Console.WriteLine($"LEVEL|{levels.HostMicrophonePeak}|{levels.ModemDownlinkPeak}");
                Console.Out.Flush();
                await Task.Yield();
            }
            uplinkPipe?.Dispose();
            downlinkPipe?.Dispose();
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
