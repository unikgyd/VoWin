using System.IO.Ports;
using System.Text;
using VoSharp.Common.Events;

namespace VoSharp.Modem.At;

public class AtSession : IAtSession, IAsyncDisposable
{
    private readonly SerialPort _port;
    private readonly AsyncEventBus? _eventBus;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _portLifecycleGate = new();
    private readonly AtResponseRouter _router = new();
    private Task? _readTask;
    private CancellationTokenSource? _readLoopCts;
    private TaskCompletionSource<bool>? _pendingPrompt;

    public AtSession(string portName, int baudRate = 115200, AsyncEventBus? eventBus = null)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 300,
            WriteTimeout = 500,
            NewLine = "\r\n",
            DtrEnable = true,
            RtsEnable = true
        };
        _eventBus = eventBus;
        _router.UrcReceived += EmitUrc;
    }

    public bool IsOpen => _port.IsOpen;
    public string PortName => _port.PortName;

    /// <summary>
    /// Controls the DTR line without closing the AT channel.  QDC507 keeps its
    /// in-call USB PCM path alive only while the host releases DTR after CLCC
    /// reaches Active.
    /// </summary>
    public void SetDataTerminalReady(bool asserted)
    {
        try { _port.DtrEnable = asserted; }
        catch { }
    }

    public event EventHandler<string>? UrcReceived;

    /// <summary>
    /// Optional gate for publishing parsed URCs to the shared event bus. The raw
    /// URC event is still raised so modem-specific consumers can handle it.
    /// </summary>
    public Func<string, bool>? UrcPublicationFilter { get; set; }

    public void Open()
    {
        lock (_portLifecycleGate)
        {
            if (_port.IsOpen) return;
            var previous = _readTask;
            if (previous is { IsCompleted: false } &&
                Task.WhenAny(previous, Task.Delay(TimeSpan.FromSeconds(1))).GetAwaiter().GetResult() != previous)
                throw new IOException("The previous AT reader has not stopped; refusing to open a second reader.");
            _port.Open();
            var readerCts = new CancellationTokenSource();
            _readLoopCts = readerCts;
            _readTask = Task.Run(async () =>
            {
                try { await ReadLoopAsync(readerCts.Token).ConfigureAwait(false); }
                finally { readerCts.Dispose(); }
            });
        }
    }

    /// <summary>
    /// Releases the serial handle without disposing the session.  It can be
    /// opened again later to resume URC and command processing.
    /// </summary>
    public bool Close() => CloseCore(out _);

    private bool CloseCore(out Task? reader)
    {
        lock (_portLifecycleGate)
        {
            var readerCts = _readLoopCts;
            _readLoopCts = null;
            try { readerCts?.Cancel(); } catch (ObjectDisposedException) { }
            var closed = true;
            try { if (_port.IsOpen) _port.Close(); }
            catch { closed = false; }
            _pendingPrompt?.TrySetCanceled();
            _pendingPrompt = null;
            _router.ResetOnClose();
            reader = _readTask;
            return closed && !_port.IsOpen;
        }
    }

    /// <summary>
    /// Closes the CDC handle and waits for the serial reader to release its
    /// outstanding native read. This is needed before QDC507 rebinds USB UAC.
    /// </summary>
    public async Task<bool> CloseAsync(CancellationToken ct = default)
    {
        var closed = CloseCore(out var readTask);
        if (readTask != null)
        {
            try
            {
                await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(1), ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
        return closed && (readTask == null || readTask.IsCompleted) && !_port.IsOpen;
    }

    public async Task<AtResponse> ExecuteCommandAsync(string command, int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (!IsOpen)
            return new AtResponse(false, Array.Empty<string>(), "Port is not open");

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, _cts.Token);

        bool lockTaken = false;
        bool writeAttempted = false;
        TaskCompletionSource<AtResponse>? pending = null;
        try
        {
            lockTaken = await _lock.WaitAsync(timeoutMs, linkedCts.Token).ConfigureAwait(false);
            if (!lockTaken)
                return new AtResponse(false, Array.Empty<string>(), "TIMEOUT");

            if (!await _router.AwaitPreviousTerminalAsync(linkedCts.Token).ConfigureAwait(false))
                return new AtResponse(false, Array.Empty<string>(), "AT_DESYNCHRONIZED");
            if (!IsOpen)
                return new AtResponse(false, Array.Empty<string>(), "Port is not open");

            pending = new TaskCompletionSource<AtResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _router.BeginCommand(command, pending);

            writeAttempted = true;
            _port.Write(command + "\r\n");

            using var reg = linkedCts.Token.Register(() => _router.Timeout(pending));

            return await pending.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (writeAttempted && pending != null) _router.Timeout(pending);
            return pending != null
                ? _router.SnapshotResponse(false, "TIMEOUT")
                : new AtResponse(false, Array.Empty<string>(), "TIMEOUT");
        }
        catch (Exception ex)
        {
            if (writeAttempted && IsOpen) _router.MarkLateResponseDrain();
            return pending != null
                ? _router.SnapshotResponse(false, ex.Message)
                : new AtResponse(false, Array.Empty<string>(), ex.Message);
        }
        finally
        {
            _router.EndCommand(pending);
            if (lockTaken)
            {
                try { _lock.Release(); } catch { }
            }
        }
    }

    public async Task<AtResponse> ExecutePromptCommandAsync(
        string initialCommand,
        string payload,
        int promptTimeoutMs = 3000,
        int completionTimeoutMs = 15000,
        CancellationToken ct = default)
    {
        if (!IsOpen)
            return new AtResponse(false, Array.Empty<string>(), "Port is not open");

        using var totalTimeoutCts = new CancellationTokenSource(promptTimeoutMs + completionTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, totalTimeoutCts.Token, _cts.Token);

        bool lockTaken = false;
        bool writeAttempted = false;
        TaskCompletionSource<AtResponse>? pending = null;
        try
        {
            lockTaken = await _lock.WaitAsync(promptTimeoutMs + completionTimeoutMs, linkedCts.Token).ConfigureAwait(false);
            if (!lockTaken)
                return new AtResponse(false, Array.Empty<string>(), "TIMEOUT");

            if (!await _router.AwaitPreviousTerminalAsync(linkedCts.Token).ConfigureAwait(false))
                return new AtResponse(false, Array.Empty<string>(), "AT_DESYNCHRONIZED");
            if (!IsOpen)
                return new AtResponse(false, Array.Empty<string>(), "Port is not open");

            var promptTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPrompt = promptTcs;
            pending = new TaskCompletionSource<AtResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _router.BeginCommand(initialCommand, pending);

            writeAttempted = true;
            _port.Write(initialCommand + "\r\n");

            // Wait for '>' prompt
            using var promptCts = new CancellationTokenSource(promptTimeoutMs);
            using var promptLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, promptCts.Token);
            using var promptReg = promptLinkedCts.Token.Register(() => promptTcs.TrySetCanceled());

            try
            {
                var first = await Task.WhenAny(promptTcs.Task, pending.Task).ConfigureAwait(false);
                if (first == pending.Task || pending.Task.IsCompleted)
                    return await pending.Task.ConfigureAwait(false);
                await promptTcs.Task.ConfigureAwait(false);
            }
            catch
            {
                _router.Timeout(pending);
                var response = await pending.Task.ConfigureAwait(false);
                if (response.ErrorCode != "TIMEOUT") return response;
                try { _port.Write("\x1B\r\n"); } catch { }
                return _router.SnapshotResponse(false, "PROMPT_TIMEOUT");
            }
            finally
            {
                _pendingPrompt = null;
            }

            _port.Write(payload + "\x1A");

            using var cmdReg = linkedCts.Token.Register(() => _router.Timeout(pending));

            return await pending.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (pending != null) _router.Timeout(pending);
            else if (writeAttempted && IsOpen) _router.MarkLateResponseDrain();
            if (writeAttempted && IsOpen) try { _port.Write("\x1B\r\n"); } catch { }
            return pending != null
                ? _router.SnapshotResponse(false, "TIMEOUT")
                : new AtResponse(false, Array.Empty<string>(), "TIMEOUT");
        }
        catch (Exception ex)
        {
            if (writeAttempted && IsOpen) _router.MarkLateResponseDrain();
            if (writeAttempted && IsOpen) try { _port.Write("\x1B\r\n"); } catch { }
            return pending != null
                ? _router.SnapshotResponse(false, ex.Message)
                : new AtResponse(false, Array.Empty<string>(), ex.Message);
        }
        finally
        {
            _pendingPrompt = null;
            _router.EndCommand(pending);
            if (lockTaken)
            {
                try { _lock.Release(); } catch { }
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken readerCt)
    {
        var buffer = new byte[2048];
        var lineBuilder = new StringBuilder();

        while (!_cts.IsCancellationRequested && !readerCt.IsCancellationRequested && _port.IsOpen)
        {
            try
            {
                int toRead = _port.BytesToRead;
                if (toRead > 0)
                {
                    int bytesRead = _port.Read(buffer, 0, Math.Min(buffer.Length, toRead));
                    if (readerCt.IsCancellationRequested) break;
                    if (bytesRead > 0)
                    {
                        var text = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        for (int i = 0; i < text.Length; i++)
                        {
                            char c = text[i];
                            if (c == '>' && _pendingPrompt != null)
                            {
                                _pendingPrompt.TrySetResult(true);
                            }

                            if (c == '\r') continue;
                            if (c == '\n')
                            {
                                var line = lineBuilder.ToString().Trim();
                                lineBuilder.Clear();
                                if (!string.IsNullOrEmpty(line))
                                {
                                    _router.HandleLine(line);
                                }
                            }
                            else
                            {
                                lineBuilder.Append(c);
                                if (_pendingPrompt != null && lineBuilder.ToString().Trim().EndsWith('>'))
                                {
                                    _pendingPrompt.TrySetResult(true);
                                }
                            }
                        }
                    }
                }
                else
                {
                    await Task.Delay(10, readerCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested && !readerCt.IsCancellationRequested && _port.IsOpen)
                {
                    _eventBus?.Publish(EventTopics.SystemError, "AtSession", $"ReadLoop error: {ex.Message}");
                    try { await Task.Delay(50, readerCt).ConfigureAwait(false); } catch { break; }
                }
                else
                {
                    break;
                }
            }
        }
    }

    private void EmitUrc(string line)
    {
        try { UrcReceived?.Invoke(this, line); } catch { }
        if (UrcPublicationFilter?.Invoke(line) != false)
        {
            UrcParser.Parse(line, _eventBus);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        CloseCore(out var reader);
        if (reader != null)
        {
            try
            {
                await Task.WhenAny(reader, Task.Delay(100)).ConfigureAwait(false);
            }
            catch { }
        }

        try { _port.Dispose(); } catch { }
        try { _lock.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
