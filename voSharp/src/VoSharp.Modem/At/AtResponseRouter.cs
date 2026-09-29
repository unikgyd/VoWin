namespace VoSharp.Modem.At;

/// <summary>
/// Serial AT has no transaction identifier. This router makes completion,
/// timeout and late-terminal drain one atomic state transition so an old OK
/// cannot either complete the next command or leave the channel stuck forever.
/// </summary>
internal sealed class AtResponseRouter
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private TaskCompletionSource<AtResponse>? _pending;
    private string? _pendingText;
    private TaskCompletionSource<bool>? _lateDrain;
    private string? _multilineUrcHeader;

    public event Action<string>? UrcReceived;

    public void BeginCommand(string command, TaskCompletionSource<AtResponse> completion)
    {
        lock (_gate)
        {
            if (_pending != null) throw new InvalidOperationException("An AT command is already pending.");
            if (_lateDrain != null) throw new InvalidOperationException("The previous AT response has not terminated.");
            _lines.Clear();
            _pending = completion;
            _pendingText = command;
        }
    }

    public void EndCommand(TaskCompletionSource<AtResponse>? completion)
    {
        if (completion == null) return;
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, completion)) return;
            _pending = null;
            _pendingText = null;
        }
    }

    public void Timeout(TaskCompletionSource<AtResponse> completion)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, completion) || completion.Task.IsCompleted) return;
            var drain = _lateDrain ??= NewDrain();
            if (!completion.TrySetResult(SnapshotNoLock(false, "TIMEOUT")) && ReferenceEquals(_lateDrain, drain))
            {
                _lateDrain = null;
                drain.TrySetResult(true);
            }
        }
    }

    public void MarkLateResponseDrain()
    {
        lock (_gate) _lateDrain ??= NewDrain();
    }

    public async Task<bool> AwaitPreviousTerminalAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool>? drain;
        lock (_gate) drain = _lateDrain;
        if (drain == null) return true;
        try { return await drain.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    public AtResponse SnapshotResponse(bool success, string? error)
    {
        lock (_gate) return SnapshotNoLock(success, error);
    }

    public void ResetOnClose()
    {
        lock (_gate)
        {
            _pending?.TrySetResult(SnapshotNoLock(false, "PORT_CLOSED"));
            _pending = null;
            _pendingText = null;
            _multilineUrcHeader = null;
            _lines.Clear();
            _lateDrain?.TrySetResult(false);
            _lateDrain = null;
        }
    }

    public void HandleLine(string line)
    {
        string? urc = null;
        lock (_gate)
        {
            if (_multilineUrcHeader != null)
            {
                urc = $"{_multilineUrcHeader}\n{line}";
                _multilineUrcHeader = null;
            }
            else if (line.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("+CDS:", StringComparison.OrdinalIgnoreCase))
            {
                _multilineUrcHeader = line;
            }
            else
            {
                var isUrc = UrcParser.IsUrc(line);
                if (_lateDrain is { } drain)
                {
                    if (IsTerminalResponse(line))
                    {
                        _lateDrain = null;
                        drain.TrySetResult(true);
                    }
                    if (isUrc) urc = line;
                }
                else
                {
                    if (_pending != null && (!isUrc || UrcParser.IsExpectedCommandResponse(_pendingText, line)))
                    {
                        _lines.Add(line);
                        if (line.Equals("OK", StringComparison.OrdinalIgnoreCase))
                            _pending.TrySetResult(SnapshotNoLock(true, null));
                        else if (IsTerminalResponse(line))
                            _pending.TrySetResult(SnapshotNoLock(false, line));
                    }
                    if (isUrc) urc = line;
                }
            }
        }
        if (urc != null) UrcReceived?.Invoke(urc);
    }

    private AtResponse SnapshotNoLock(bool success, string? error)
    {
        var lines = _lines.ToArray();
        return new AtResponse(success, lines, error, string.Join("\n", lines));
    }

    private static TaskCompletionSource<bool> NewDrain() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsTerminalResponse(string line) =>
        line.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("+CME ERROR:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("+CMS ERROR:", StringComparison.OrdinalIgnoreCase);
}
