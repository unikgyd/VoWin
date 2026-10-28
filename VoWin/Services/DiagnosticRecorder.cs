using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Channels;

namespace VoWin.Services;

/// <summary>Bounded, redacted support timeline. No packet bodies or credential stores are collected.</summary>
internal sealed class DiagnosticRecorder : IAsyncDisposable
{
    internal const int DefaultFileBytes = 2 * 1024 * 1024;
    internal const int FileCount = 4;
    private const int QueueLimit = 4096;
    private readonly string _directory;
    private readonly int _fileBytes;
    private readonly string _session = Guid.NewGuid().ToString("N")[..12];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly Task _worker;
    private readonly Queue<string> _recent = new();
    private readonly object _gate = new();
    private int _pending;
    private long _dropped;
    private volatile bool _enabled;
    private string? _lastError;
    private sealed record Work(string? Line = null, TaskCompletionSource<string[]>? Snapshot = null);

    public DiagnosticRecorder(string directory, int fileBytes = DefaultFileBytes)
    {
        _directory = directory;
        _fileBytes = Math.Max(512, fileBytes);
        _worker = RunAsync();
    }

    public bool Enabled => _enabled;
    public string? LastError => _lastError;
    public long Dropped => Interlocked.Read(ref _dropped);

    public void SetEnabled(bool enabled)
    {
        var wasEnabled = _enabled;
        _enabled = enabled;
        Record("INFO", "Diagnostics", enabled
            ? "Detailed recording enabled; session metadata and protocol milestones only."
            : "Detailed recording stopped; previous recording retained.", forcePersist: enabled || wasEnabled);
    }

    public void Record(string level, string source, string message, bool forcePersist = false)
    {
        // Redact BEFORE entering either memory or the disk queue. Prevent multiline log injection.
        var line = DiagnosticLogRedactor.Redact(
            $"[{DateTimeOffset.Now:O}] [session={_session}] [+{_elapsed.ElapsedMilliseconds}ms] [{level}] [{source}] {message}")
            .Replace("\r", " \\r ").Replace("\n", " \\n ");
        if (line.Length > 8192) line = line[..8192] + " <truncated>";
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > 1000) _recent.Dequeue();
        }
        if (!_enabled && !forcePersist) return;
        if (Interlocked.Increment(ref _pending) > QueueLimit)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            return;
        }
        if (!_queue.Writer.TryWrite(new(line))) Interlocked.Decrement(ref _pending);
    }

    public async Task<string[]> SnapshotAsync(CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Barriers cannot be dropped when the log queue is full.
        if (!_queue.Writer.TryWrite(new(Snapshot: completion)))
            throw new ObjectDisposedException(nameof(DiagnosticRecorder));
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private string LogPath(int index) => Path.Combine(_directory, $"detailed-{index}.log");

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (work.Line != null)
            {
                Interlocked.Decrement(ref _pending);
                try
                {
                    Directory.CreateDirectory(_directory);
                    var bytes = Encoding.UTF8.GetBytes(work.Line + Environment.NewLine);
                    if (File.Exists(LogPath(0)) && new FileInfo(LogPath(0)).Length + bytes.Length > _fileBytes)
                    {
                        File.Delete(LogPath(FileCount - 1));
                        for (var i = FileCount - 2; i >= 0; i--)
                            if (File.Exists(LogPath(i))) File.Move(LogPath(i), LogPath(i + 1), overwrite: true);
                    }
                    // A single event cannot exceed a file's size limit.
                    if (bytes.Length > _fileBytes) bytes = Encoding.UTF8.GetBytes("<oversize event omitted>\n");
                    await using var stream = new FileStream(LogPath(0), FileMode.Append, FileAccess.Write,
                        FileShare.Read, 16384, useAsync: true);
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    _lastError = null;
                }
                catch (Exception ex) { _lastError = $"{ex.GetType().Name}: {DiagnosticLogRedactor.Redact(ex.Message)}"; }
            }
            if (work.Snapshot != null)
            {
                try
                {
                    var lines = new List<string>();
                    for (var i = FileCount - 1; i >= 0; i--)
                    {
                        var path = LogPath(i);
                        if (File.Exists(path))
                        {
                            // Read only our fixed allowlist, and cap even files changed by external programs.
                            try
                            {
                                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                if (stream.Length > _fileBytes) stream.Seek(-_fileBytes, SeekOrigin.End);
                                using var reader = new StreamReader(stream);
                                var buffer = new char[_fileBytes];
                                var length = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                                lines.Add(DiagnosticLogRedactor.Redact(new string(buffer, 0, length)));
                            }
                            catch (Exception ex)
                            {
                                _lastError = $"{ex.GetType().Name}: could not read a retained diagnostic file";
                                lines.Add("<retained file unavailable; recent memory events still included>");
                            }
                        }
                    }
                    lock (_gate) lines.Add("=== Recent events (available even with detailed recording off) ===\n" + string.Join('\n', _recent));
                    work.Snapshot.TrySetResult(lines.ToArray());
                }
                catch (Exception ex) { work.Snapshot.TrySetException(ex); }
            }
        }
    }

    public async Task ExportAsync(string destination, string state, CancellationToken ct = default)
    {
        var timeline = await SnapshotAsync(ct).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, ".vowin-diagnostics-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                async Task Write(string name, string value)
                {
                    await using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    await writer.WriteAsync(DiagnosticLogRedactor.Redact(value).AsMemory(), ct).ConfigureAwait(false);
                }
                await Write("README.txt", "VoWin support diagnostics v1\n" +
                    "Generated: " + DateTimeOffset.Now.ToString("O") + "\nSession: " + _session + "\n" +
                    $"Detailed recording at export: {Enabled}; queue overflow events: {Dropped}\n" +
                    $"Recording error: {LastError ?? "none"}\n" +
                    "timeline.log: chronological rolling files (oldest first), followed by recent memory events; overlap is possible.\n" +
                    "Detailed history survives restart. When off, only recent events in this running process are available.\n" +
                    "Storage: 4 x 2 MiB maximum, oldest files rotate. UI clear does not erase these files.\n" +
                    "No SIM/APDU keys, raw SIP/AT packets, SMS body, audio, database, config or memory dump is included.\n" +
                    "Subscriber identifiers and credentials are redacted. Endpoint IPs, domains, PLMN and software versions remain for troubleshooting. Review before sharing.\n" +
                    "Export uses cached state only: no modem commands, registrations, calls or messages.\n");
                await Write("state.txt", state);
                await Write("timeline.log", string.Join(Environment.NewLine, timeline));
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }
}
