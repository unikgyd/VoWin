using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;

namespace VoWin.Tests;

/// <summary>Only synthetic SQLite state is exercised. The child re-runs one
/// exact Fact in this test assembly, not a production executable or CLI.</summary>
public sealed class SipSmsOperationProcessTests
{
    private const string Prefix = "vowin-sip-sms-process-";
    private const string ModeVariable = "VOWIN_SIP_SMS_PROCESS_MODE";
    private const string RootVariable = "VOWIN_SIP_SMS_PROCESS_ROOT";
    private const string TokenVariable = "VOWIN_SIP_SMS_PROCESS_TOKEN";
    private static readonly string OperationId = new('a', 64), Fingerprint = new('b', 64);
    private static readonly TimeSpan ChildDeadline = TimeSpan.FromSeconds(60);

    private sealed record Observation(int ProcessId, bool IsNew, bool ContentMatches,
        SipSmsOperationOutcome Outcome, bool HasReservationToken);

    [Fact]
    public async Task CommittedUnknownSurvivesOwnedTestChildTermination()
    {
        if (ChildMode() is { } mode)
        {
            Assert.Equal("crash", mode);
            await RunChildAsync(mode);
            return;
        }
        using var directory = new TestDirectory();
        await new PreferenceDatabaseService(directory.Database).InitializeAsync();
        await using var child = OwnedChild.Start(directory, "crash", nameof(CommittedUnknownSurvivesOwnedTestChildTermination));
        var observation = await ReadObservationAsync(directory, "committed-crash.json", child);
        Assert.NotEqual(Environment.ProcessId, observation.ProcessId);
        Assert.True(observation.IsNew && observation.ContentMatches && observation.HasReservationToken);
        Assert.Equal(SipSmsOperationOutcome.Unknown, observation.Outcome);
        Assert.False(child.HasExited); // Commit is acknowledged while the child is still alive.

        // Only this Process.Start result and its descendants may be killed.
        await child.TerminateAndJoinAsync();
        var replay = await new PreferenceDatabaseService(directory.Database)
            .ReserveAsync(OperationId, Fingerprint, CancellationToken.None);
        Assert.False(replay.IsNew);
        Assert.True(replay.ContentMatches);
        Assert.Equal(SipSmsOperationOutcome.Unknown, replay.Outcome);
        Assert.Null(replay.ReservationToken); // A replay is not a new completion owner.
    }

    [Fact]
    public async Task IndependentTestProcessesHaveExactlyOneAdmissionForTheSameOperation()
    {
        if (ChildMode() is { } mode)
        {
            Assert.True(mode is "left" or "right", "Unexpected child test role.");
            await RunChildAsync(mode);
            return;
        }
        using var directory = new TestDirectory();
        await new PreferenceDatabaseService(directory.Database).InitializeAsync();
        await using var left = OwnedChild.Start(directory, "left", nameof(IndependentTestProcessesHaveExactlyOneAdmissionForTheSameOperation));
        await using var right = OwnedChild.Start(directory, "right", nameof(IndependentTestProcessesHaveExactlyOneAdmissionForTheSameOperation));
        await Task.WhenAll(WaitForFileAsync(directory, "ready-left", left), WaitForFileAsync(directory, "ready-right", right));
        await PublishAsync(directory, "release", "go");
        var results = await Task.WhenAll(
            ReadObservationAsync(directory, "result-left.json", left),
            ReadObservationAsync(directory, "result-right.json", right));
        await Task.WhenAll(left.JoinSuccessfullyAsync(), right.JoinSuccessfullyAsync());

        Assert.NotEqual(results[0].ProcessId, results[1].ProcessId);
        Assert.All(results, result => Assert.NotEqual(Environment.ProcessId, result.ProcessId));
        Assert.Single(results, result => result.IsNew);
        Assert.Single(results, result => result.HasReservationToken);
        Assert.All(results, result =>
        {
            Assert.True(result.ContentMatches);
            Assert.Equal(SipSmsOperationOutcome.Unknown, result.Outcome);
            Assert.Equal(result.IsNew, result.HasReservationToken);
        });
        var replay = await new PreferenceDatabaseService(directory.Database)
            .ReserveAsync(OperationId, Fingerprint, CancellationToken.None);
        Assert.Equal(new(false, true, SipSmsOperationOutcome.Unknown), replay);
    }

    private static string? ChildMode()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (mode is null && (Environment.GetEnvironmentVariable(RootVariable) is not null ||
                             Environment.GetEnvironmentVariable(TokenVariable) is not null))
            throw new InvalidOperationException("Incomplete synthetic child environment.");
        return mode;
    }

    private static async Task RunChildAsync(string mode)
    {
        using var directory = TestDirectory.BorrowValidatedChildRoot();
        var store = new PreferenceDatabaseService(directory.Database);
        await store.InitializeAsync();
        if (mode != "crash")
        {
            await PublishAsync(directory, "ready-" + mode, "ready");
            await WaitForFileAsync(directory, "release", null);
        }
        using var deadline = new CancellationTokenSource(ChildDeadline);
        var reserved = await store.ReserveAsync(OperationId, Fingerprint, deadline.Token);
        var observation = new Observation(Environment.ProcessId, reserved.IsNew, reserved.ContentMatches,
            reserved.Outcome, reserved.ReservationToken is { Length: 32 });
        // ReserveAsync has returned only after committing Unknown. Atomic file
        // publication lets the parent observe that boundary without a sleep.
        await PublishAsync(directory, mode == "crash" ? "committed-crash.json" : "result-" + mode + ".json",
            JsonSerializer.Serialize(observation));
        if (mode == "crash")
        {
            // Parent kills the owned vstest process tree here. The finite
            // watchdog prevents a forever-orphaned test if the parent crashes.
            await Task.Delay(TimeSpan.FromMinutes(2));
            throw new TimeoutException("The synthetic crash child was not terminated by its parent.");
        }
    }

    private static async Task<Observation> ReadObservationAsync(TestDirectory directory, string name, OwnedChild child)
    {
        await WaitForFileAsync(directory, name, child);
        var result = JsonSerializer.Deserialize<Observation>(await File.ReadAllTextAsync(directory.File(name)));
        return result ?? throw new InvalidOperationException("Missing synthetic child result.");
    }

    private static async Task WaitForFileAsync(TestDirectory directory, string name, OwnedChild? child)
    {
        using var deadline = new CancellationTokenSource(ChildDeadline);
        while (!System.IO.File.Exists(directory.File(name)))
        {
            if (child?.HasExited == true)
                throw new InvalidOperationException("Synthetic child exited before publishing its result: " + await child.DiagnosticsAsync());
            await Task.Delay(20, deadline.Token);
        }
        directory.Validate();
        if ((System.IO.File.GetAttributes(directory.File(name)) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Synthetic child result must not be a reparse point.");
    }

    private static async Task PublishAsync(TestDirectory directory, string name, string value)
    {
        directory.Validate();
        var temporary = directory.File(name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        await System.IO.File.WriteAllTextAsync(temporary, value);
        System.IO.File.Move(temporary, directory.File(name));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Root { get; }
        public string Token { get; }
        private readonly bool _ownsDirectory;
        private int _unjoinedChildren;
        public string Database => File("preferences.db");

        public TestDirectory()
        {
            Token = Guid.NewGuid().ToString("N");
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Token));
            Directory.CreateDirectory(Root);
            System.IO.File.WriteAllText(Path.Combine(Root, "owner-token"), Token);
            _ownsDirectory = true;
            Validate();
        }

        private TestDirectory(string root, string token)
        {
            Root = root; Token = token;
            Validate();
        }

        public static TestDirectory BorrowValidatedChildRoot() => new(
            Environment.GetEnvironmentVariable(RootVariable) ?? throw new InvalidOperationException("Missing synthetic root."),
            Environment.GetEnvironmentVariable(TokenVariable) ?? throw new InvalidOperationException("Missing synthetic owner token."));

        public void Validate()
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Token.Length != 32 || Token.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                Root.IndexOfAny([';', '\r', '\n', '\0']) >= 0 ||
                !string.Equals(full, Root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(full), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(full) != Prefix + Token || !Directory.Exists(full) ||
                (System.IO.File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing a non-owned synthetic test directory.");
            var marker = Path.Combine(full, "owner-token");
            if (!System.IO.File.Exists(marker) || (System.IO.File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
                System.IO.File.ReadAllText(marker) != Token)
                throw new InvalidOperationException("Synthetic directory ownership marker is invalid.");
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var database = Path.Combine(full, "preferences.db" + suffix);
                if (System.IO.File.Exists(database) && (System.IO.File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Synthetic database must not be a reparse point.");
            }
        }

        public string File(string name)
        {
            if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name is "." or "..")
                throw new InvalidOperationException("Synthetic file must be directly below its owned root.");
            return Path.Combine(Root, name);
        }

        public void ChildStarted() => Interlocked.Increment(ref _unjoinedChildren);
        public void ChildJoined() => Interlocked.Decrement(ref _unjoinedChildren);

        public void Dispose()
        {
            if (!_ownsDirectory) return;
            if (Volatile.Read(ref _unjoinedChildren) != 0)
                throw new InvalidOperationException("Unjoined owned test child; retaining its synthetic directory: " + Root);
            Validate();
            // Clear only the two connection pools used for this temporary DB,
            // never all SQLite pools belonging to other tests or applications.
            var original = "Data Source=" + Database + ";Cache=Shared";
            foreach (var value in new[] { original, new SqliteConnectionStringBuilder(original)
                { DefaultTimeout = 2, Cache = SqliteCacheMode.Private }.ToString() })
            {
                using var connection = new SqliteConnection(value);
                SqliteConnection.ClearPool(connection);
            }
            ValidateNoReparsePoints(Root);
            Directory.Delete(Root, recursive: true);
        }

        private void ValidateNoReparsePoints(string path)
        {
            if (!Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(path, Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the synthetic root.");
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = System.IO.File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing recursive cleanup through a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) ValidateNoReparsePoints(child);
            }
        }
    }

    private sealed class OwnedChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly TestDirectory _directory;
        private readonly Task<string> _output, _error;
        private int _joined;
        public bool HasExited => _process.HasExited;

        private OwnedChild(Process process, TestDirectory directory)
        {
            _process = process;
            _directory = directory;
            _output = process.StandardOutput.ReadToEndAsync();
            _error = process.StandardError.ReadToEndAsync();
        }

        public static OwnedChild Start(TestDirectory directory, string role, string fact)
        {
            directory.Validate();
            var assembly = typeof(SipSmsOperationProcessTests).Assembly.Location;
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrEmpty(host) || !Path.IsPathFullyQualified(host) || !System.IO.File.Exists(host)) host = "dotnet";
            var info = new ProcessStartInfo(host)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory.Root
            };
            info.ArgumentList.Add("vstest");
            info.ArgumentList.Add(assembly);
            info.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(SipSmsOperationProcessTests).FullName + "." + fact);
            info.ArgumentList.Add("--TestAdapterPath:" + Path.GetDirectoryName(assembly));
            info.ArgumentList.Add("--ResultsDirectory:" + directory.File("results-" + role));
            info.ArgumentList.Add("--logger:console;verbosity=minimal");
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("VOWIN_TEST_", StringComparison.Ordinal)).ToArray())
                info.Environment.Remove(key); // No inherited live-device opt-in.
            info.Environment.Remove("VSTEST_HOST_DEBUG");
            info.Environment.Remove("VSTEST_RUNNER_DEBUG");
            info.Environment[ModeVariable] = role;
            info.Environment[RootVariable] = directory.Root;
            info.Environment[TokenVariable] = directory.Token;
            var process = new Process { StartInfo = info };
            var registered = false;
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Synthetic child did not start.");
                directory.ChildStarted(); registered = true;
                return new OwnedChild(process, directory);
            }
            catch
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    if (process.WaitForExit(15000) && registered) directory.ChildJoined();
                }
                catch { } // An unjoined child keeps the directory cleanup fence.
                process.Dispose();
                throw;
            }
        }

        public async Task JoinSuccessfullyAsync()
        {
            await _process.WaitForExitAsync().WaitAsync(ChildDeadline);
            var diagnostics = await DiagnosticsAsync();
            MarkJoined();
            Assert.True(_process.ExitCode == 0, "Synthetic child test failed: " + diagnostics);
        }

        public async Task<string> DiagnosticsAsync()
        {
            var values = await Task.WhenAll(_output, _error).WaitAsync(TimeSpan.FromSeconds(15));
            var text = string.Join("\n", values);
            return text.Length <= 4096 ? text : text[..4096];
        }

        public async Task TerminateAndJoinAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            _ = await DiagnosticsAsync();
            MarkJoined();
        }

        private void MarkJoined()
        {
            if (Interlocked.Exchange(ref _joined, 1) == 0) _directory.ChildJoined();
        }

        public async ValueTask DisposeAsync()
        {
            try { await TerminateAndJoinAsync(); }
            finally { _process.Dispose(); }
        }
    }
}
