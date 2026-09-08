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
    private Task? _readTask;
    private TaskCompletionSource<AtResponse>? _pendingCommand;
    private string? _pendingCommandText;
    private TaskCompletionSource<bool>? _pendingPrompt;
    private readonly List<string> _currentResponseLines = new();
    private string? _pendingMultilineUrcHeader;

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
        if (!_port.IsOpen)
        {
            _port.Open();
            _readTask = Task.Run(ReadLoopAsync);
        }
    }

    /// <summary>
    /// Releases the serial handle without disposing the session.  It can be
    /// opened again later to resume URC and command processing.
    /// </summary>
    public bool Close()
    {
        try
        {
            if (_port.IsOpen)
                _port.Close();
            return !_port.IsOpen;
        }
        catch { return false; }
    }

    /// <summary>
    /// Closes the CDC handle and waits for the serial reader to release its
    /// outstanding native read. This is needed before QDC507 rebinds USB UAC.
    /// </summary>
    public async Task<bool> CloseAsync(CancellationToken ct = default)
    {
        var closed = Close();
        var readTask = _readTask;
        if (readTask != null)
        {
            try
            {
                await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(1), ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
        return closed && !_port.IsOpen;
    }

    public async Task<AtResponse> ExecuteCommandAsync(string command, int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (!IsOpen)
            return new AtResponse(false, Array.Empty<string>(), "Port is not open");

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, _cts.Token);

        bool lockTaken = false;
        try
        {
            lockTaken = await _lock.WaitAsync(timeoutMs, linkedCts.Token).ConfigureAwait(false);
            if (!lockTaken)
                return new AtResponse(false, Array.Empty<string>(), "TIMEOUT");

            _currentResponseLines.Clear();
            var tcs = new TaskCompletionSource<AtResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCommand = tcs;
            _pendingCommandText = command;

            _port.Write(command + "\r\n");

            using var reg = linkedCts.Token.Register(() =>
            {
                tcs.TrySetResult(new AtResponse(false, _currentResponseLines.ToArray(), "TIMEOUT", string.Join("\n", _currentResponseLines)));
            });

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AtResponse(false, _currentResponseLines.ToArray(), "TIMEOUT", string.Join("\n", _currentResponseLines));
        }
        catch (Exception ex)
        {
            return new AtResponse(false, _currentResponseLines.ToArray(), ex.Message, string.Join("\n", _currentResponseLines));
        }
        finally
        {
            _pendingCommand = null;
            _pendingCommandText = null;
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
        try
        {
            lockTaken = await _lock.WaitAsync(promptTimeoutMs + completionTimeoutMs, linkedCts.Token).ConfigureAwait(false);
            if (!lockTaken)
                return new AtResponse(false, Array.Empty<string>(), "TIMEOUT");

            _currentResponseLines.Clear();
            var promptTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPrompt = promptTcs;

            _port.Write(initialCommand + "\r\n");

            // Wait for '>' prompt
            using var promptCts = new CancellationTokenSource(promptTimeoutMs);
            using var promptLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, promptCts.Token);
            using var promptReg = promptLinkedCts.Token.Register(() => promptTcs.TrySetCanceled());

            try
            {
                await promptTcs.Task.ConfigureAwait(false);
            }
            catch
            {
                try { _port.Write("\x1B\r\n"); } catch { }
                return new AtResponse(false, _currentResponseLines.ToArray(), "PROMPT_TIMEOUT", string.Join("\n", _currentResponseLines));
            }
            finally
            {
                _pendingPrompt = null;
            }

            var cmdTcs = new TaskCompletionSource<AtResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCommand = cmdTcs;
            _pendingCommandText = initialCommand;

            _port.Write(payload + "\x1A");

            using var cmdReg = linkedCts.Token.Register(() =>
            {
                cmdTcs.TrySetResult(new AtResponse(false, _currentResponseLines.ToArray(), "TIMEOUT", string.Join("\n", _currentResponseLines)));
            });

            return await cmdTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { _port.Write("\x1B\r\n"); } catch { }
            return new AtResponse(false, _currentResponseLines.ToArray(), "TIMEOUT", string.Join("\n", _currentResponseLines));
        }
        catch (Exception ex)
        {
            try { _port.Write("\x1B\r\n"); } catch { }
            return new AtResponse(false, _currentResponseLines.ToArray(), ex.Message, string.Join("\n", _currentResponseLines));
        }
        finally
        {
            _pendingPrompt = null;
            _pendingCommand = null;
            _pendingCommandText = null;
            if (lockTaken)
            {
                try { _lock.Release(); } catch { }
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[2048];
        var lineBuilder = new StringBuilder();

        while (!_cts.IsCancellationRequested && _port.IsOpen)
        {
            try
            {
                int toRead = _port.BytesToRead;
                if (toRead > 0)
                {
                    int bytesRead = _port.Read(buffer, 0, Math.Min(buffer.Length, toRead));
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
                                    HandleLine(line);
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
                    await Task.Delay(10, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested && _port.IsOpen)
                {
                    _eventBus?.Publish(EventTopics.SystemError, "AtSession", $"ReadLoop error: {ex.Message}");
                    try { await Task.Delay(50, _cts.Token).ConfigureAwait(false); } catch { break; }
                }
                else
                {
                    break;
                }
            }
        }
    }

    private void HandleLine(string line)
    {
        // +CMT and +CDS carry their PDU/text body on the next line. Keep both
        // lines together so the SMS layer can decode the notification without
        // losing the body while another AT command is in flight.
        if (_pendingMultilineUrcHeader != null)
        {
            var completeUrc = $"{_pendingMultilineUrcHeader}\n{line}";
            _pendingMultilineUrcHeader = null;
            EmitUrc(completeUrc);
            return;
        }

        if (line.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("+CDS:", StringComparison.OrdinalIgnoreCase))
        {
            _pendingMultilineUrcHeader = line;
            return;
        }

        var isUrc = UrcParser.IsUrc(line);
        var pending = _pendingCommand;
        var belongsToPendingCommand = pending != null &&
            (!isUrc || UrcParser.IsExpectedCommandResponse(_pendingCommandText, line));
        if (belongsToPendingCommand)
        {
            _currentResponseLines.Add(line);

            if (line.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                pending!.TrySetResult(new AtResponse(true, _currentResponseLines.ToArray(), null, string.Join("\n", _currentResponseLines)));
            }
            else if (line.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("+CME ERROR:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("+CMS ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                pending!.TrySetResult(new AtResponse(false, _currentResponseLines.ToArray(), line, string.Join("\n", _currentResponseLines)));
            }
        }

        if (isUrc)
        {
            EmitUrc(line);
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
        try
        {
            if (_port.IsOpen)
            {
                _port.Close();
            }
        }
        catch { }

        if (_readTask != null)
        {
            try
            {
                await Task.WhenAny(_readTask, Task.Delay(100)).ConfigureAwait(false);
            }
            catch { }
        }

        try { _port.Dispose(); } catch { }
        try { _lock.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
