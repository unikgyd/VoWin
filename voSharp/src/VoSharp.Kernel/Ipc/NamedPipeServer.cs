using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace VoSharp.Kernel.Ipc;

public class NamedPipeServer : IAsyncDisposable
{
    private readonly VoKernel _kernel;
    public string PipeName { get; }
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _listenTask;
    private const int MaxCommandLength = 65536;
    private readonly ConcurrentBag<NamedPipeServerStream> _activePipes = new();
    private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
    private int _nextClientTaskId;

    public NamedPipeServer(VoKernel kernel, string pipeName = "voSharp_kernel")
    {
        _kernel = kernel;
        PipeName = pipeName;
    }

    public Task WaitForReadyAsync() => _readyTcs.Task;

    public void Start()
    {
        _listenTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );
                _activePipes.Add(server);

                _readyTcs.TrySetResult();

                using (_cts.Token.Register(() =>
                {
                    try { server?.Close(); } catch { }
                    try { server?.Dispose(); } catch { }
                }))
                {
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                }

                var taskId = Interlocked.Increment(ref _nextClientTaskId);
                var clientTask = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClientAsync(server, _cts.Token).ConfigureAwait(false);
                    }
                    catch { }
                });
                _clientTasks[taskId] = clientTask;
                _ = clientTask.ContinueWith(
                    completedTask => _clientTasks.TryRemove(taskId, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break;
                _kernel.EventBus.Publish(Common.Events.EventTopics.SystemError, "NamedPipeServer", ex.Message);
                try { await Task.Delay(100, _cts.Token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
        using (ct.Register(() =>
        {
            try { pipe.Close(); } catch { }
            try { pipe.Dispose(); } catch { }
        }))
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await ReadCommandAsync(reader, ct).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    var rejected = new KernelCommandResult(false, ex.Message);
                    await writer.WriteLineAsync(rejected.ToJson().AsMemory(), ct).ConfigureAwait(false);
                    break;
                }
                catch { break; }

                if (line == null) break;

                KernelCommandResult result;
                if (!IsCommandAllowed(line))
                {
                    result = new KernelCommandResult(false, "This sensitive command is not available through IPC.");
                }
                else
                {
                    result = await _kernel.ExecuteCommandAsync(line, ct).ConfigureAwait(false);
                }
                var json = result.ToJson().Replace("\r\n", " ").Replace("\n", " ");
                try
                {
                    await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                    await pipe.FlushAsync(ct).ConfigureAwait(false);
                }
                catch { break; }
            }
        }
    }

    internal static bool IsCommandAllowed(string commandLine)
    {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return true;

        var command = parts[0].ToLowerInvariant();
        if (command is "aka" or "at" or "raw") return false;
        return !(command == "euicc" && parts.Length > 1 && parts[1].Equals("delete", StringComparison.OrdinalIgnoreCase));
    }

    internal static async Task<string?> ReadCommandAsync(TextReader reader, CancellationToken ct)
    {
        var command = new StringBuilder();
        var buffer = new char[1];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                return command.Length == 0 ? null : command.ToString();

            var value = buffer[0];
            if (value == '\n')
                return command.ToString().TrimEnd('\r');
            if (value == '\0')
                throw new InvalidDataException("IPC command contains a NUL character.");
            if (command.Length >= MaxCommandLength)
                throw new InvalidDataException($"IPC command exceeds the {MaxCommandLength}-character limit.");

            command.Append(value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var pipe in _activePipes)
        {
            try { pipe.Close(); } catch { }
            try { pipe.Dispose(); } catch { }
        }
        if (_listenTask != null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { }
        }
        var clients = _clientTasks.Values.ToArray();
        if (clients.Length > 0)
        {
            try { await Task.WhenAll(clients).ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}

public class NamedPipeClient : IAsyncDisposable
{
    public string PipeName { get; }
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public NamedPipeClient(string pipeName = "voSharp_kernel")
    {
        PipeName = pipeName;
    }

    public async Task<bool> ConnectAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        return true;
    }

    public async Task<string?> SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (_writer == null || _reader == null || _pipe == null)
            throw new InvalidOperationException("Not connected to named pipe");

        await _writer.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
        await _pipe.FlushAsync(ct).ConfigureAwait(false);
        return await _reader.ReadLineAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Close(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
