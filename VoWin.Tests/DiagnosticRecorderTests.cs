using System.IO;
using System.IO.Compression;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class DiagnosticRecorderTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoWin-diagnostic-tests-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(_directory); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(_directory, recursive: true); return Task.CompletedTask; }

    private static async Task<Dictionary<string, string>> ReadBundle(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var result = new Dictionary<string, string>();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = await reader.ReadToEndAsync();
        }
        return result;
    }

    [Fact]
    public async Task DisabledStillExportsRecentEventsWithoutPersistingThem()
    {
        var logs = Path.Combine(_directory, "logs");
        await using var recorder = new DiagnosticRecorder(logs);
        recorder.Record("ERROR", "VoWiFi", "ePDG DNS failed; host=epdg.epc.mnc033.mcc234.pub.3gppnetwork.org");
        var path = Path.Combine(_directory, "disabled.zip");
        await recorder.ExportAsync(path, "No SIM connected");
        Assert.False(Directory.Exists(logs));
        var contents = await ReadBundle(path);
        Assert.Equal(new[] { "README.txt", "state.txt", "timeline.log" }, contents.Keys.Order().ToArray());
        Assert.Contains("No SIM", contents["state.txt"]);
        Assert.Contains("ePDG DNS failed", contents["timeline.log"]);
        Assert.Contains("False", contents["README.txt"]);
    }

    [Fact]
    public async Task ExportFlushesNewestQueuedEventAndRedactsBeforeDiskAndZip()
    {
        await using var recorder = new DiagnosticRecorder(Path.Combine(_directory, "logs"));
        recorder.SetEnabled(true);
        recorder.Record("INFO", "IMS", "imsi=234330123456789 sip:+8617387799413@ims.example.org CK=\"secret-ck\" proxy=socks5://alice:secret-pass@proxy.example:1080");
        recorder.Record("TRACE", "AT", "AT+CMGS=5\r\nDEADBEEF raw SMS body should not escape");
        recorder.Record("TRACE", "SIP", "Authorization: Digest username=alice, nonce=private\r\nstatus=401");
        recorder.Record("INFO", "IKE", "Last queued event: stage=IKE_AUTH; status=timeout; endpoint=203.0.113.9:4500");
        var path = Path.Combine(_directory, "private.zip");
        await recorder.ExportAsync(path, "phone=17387799413; PLMN=234-033; folder=C:\\Users\\Alice\\VoWin");
        var files = await ReadBundle(path);
        var all = string.Join('\n', files.Values.Concat(Directory.GetFiles(Path.Combine(_directory, "logs")).Select(File.ReadAllText)));
        foreach (var secret in new[] { "234330123456789", "17387799413", "secret-ck", "secret-pass", "alice", "Alice", "DEADBEEF", "raw SMS body", "username=", "nonce=private" })
            Assert.DoesNotContain(secret, all);
        Assert.Contains("stage=IKE_AUTH", files["timeline.log"]);
        Assert.Contains("status=401", files["timeline.log"]);
        Assert.Contains("203.0.113.9:4500", all);
        Assert.Contains("PLMN=234-033", files["state.txt"]);
    }

    [Theory]
    [InlineData("nonce=\"abc\" CK=def IK='ghi' RAND=jkl AUTN=mno", new[] { "abc", "def", "ghi", "jkl", "mno" })]
    [InlineData("password: abc sk_d=def MSK=ghi AUTS=jkl token=mno", new[] { "abc", "def", "ghi", "jkl", "mno" })]
    [InlineData("http://alice:abc@proxy.example:8080 socks5h://bob:def@proxy.example", new[] { "alice", "abc", "bob", "def" })]
    [InlineData("IMEI=12345678-123456-7 eid=89012345678901234567890123456789", new[] { "12345678-123456-7", "89012345678901234567890123456789" })]
    public void RedactsCommonCredentialAndIdentityForms(string input, string[] secrets)
    {
        var result = DiagnosticLogRedactor.Redact(input);
        foreach (var secret in secrets) Assert.DoesNotContain(secret, result);
    }

    [Fact]
    public void RepeatedRedactionPreservesProtocolContextAndStableSlotCorrelation()
    {
        const string input = "Slot=abcdef0123456789abcdef0123456789; sip:+15551234567@ims.example; status=401; IP=2001:db8::1; PLMN=234-033; LPA:1$server$private-code";
        var redacted = DiagnosticLogRedactor.Redact(input);
        Assert.Equal(redacted, DiagnosticLogRedactor.Redact(redacted));
        Assert.Contains("Slot=slot-abcdef01", redacted);
        Assert.Contains("status=401", redacted);
        Assert.Contains("2001:db8::1", redacted);
        Assert.Contains("PLMN=234-033", redacted);
        Assert.DoesNotContain("private-code", redacted);
        Assert.DoesNotContain("15551234567", redacted);
    }

    [Fact]
    public void RegistrationStatusSurvivesRedactionWhileDigestResponseStillDoesNot()
    {
        var redacted = DiagnosticLogRedactor.Redact("REGISTER step=5/5 status=200; response=\"private-digest\"; removal status=401");
        Assert.Contains("status=200", redacted);
        Assert.Contains("status=401", redacted);
        Assert.DoesNotContain("private-digest", redacted);
    }

    [Fact]
    public async Task RollingHistoryIsBoundedAndSurvivesRestart()
    {
        var logs = Path.Combine(_directory, "logs");
        await using (var first = new DiagnosticRecorder(logs, 1024))
        {
            first.SetEnabled(true);
            for (var i = 0; i < 60; i++) first.Record("TRACE", "Test", $"event-{i:D2} " + new string('x', 180));
            await first.SnapshotAsync();
        }
        var stored = Directory.GetFiles(logs);
        Assert.Equal(4, stored.Length);
        Assert.All(stored, path => Assert.InRange(new FileInfo(path).Length, 1, 1024));
        await using var second = new DiagnosticRecorder(logs, 1024);
        var path = Path.Combine(_directory, "restart.zip");
        await second.ExportAsync(path, "Restart");
        var timeline = (await ReadBundle(path))["timeline.log"];
        Assert.Contains("event-59", timeline);
        Assert.DoesNotContain("event-00", timeline);
    }

    [Fact]
    public async Task TurningOffStopsNewDiskEventsButRetainsPriorHistory()
    {
        var logs = Path.Combine(_directory, "logs");
        await using var recorder = new DiagnosticRecorder(logs);
        recorder.SetEnabled(true);
        recorder.Record("INFO", "Test", "enabled event");
        recorder.SetEnabled(false);
        recorder.Record("INFO", "Test", "disabled event");
        await recorder.SnapshotAsync();
        var disk = string.Join('\n', Directory.GetFiles(logs).Select(File.ReadAllText));
        Assert.Contains("enabled event", disk);
        Assert.DoesNotContain("disabled event", disk);
    }

    [Fact]
    public async Task DiskFailureDoesNotLoseMemoryExportOrCrash()
    {
        var blocked = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "existing user file");
        await using var recorder = new DiagnosticRecorder(blocked);
        recorder.SetEnabled(true);
        recorder.Record("ERROR", "Test", "network failed");
        var path = Path.Combine(_directory, "disk-error.zip");
        await recorder.ExportAsync(path, "State");
        Assert.NotNull(recorder.LastError);
        var contents = await ReadBundle(path);
        Assert.Contains("network failed", contents["timeline.log"]);
        Assert.DoesNotContain("Recording error: none", contents["README.txt"]);
        Assert.Equal("existing user file", await File.ReadAllTextAsync(blocked));
    }

    [Fact]
    public async Task CancelledExportDoesNotReplaceExistingFileOrLeaveTemporaryFiles()
    {
        await using var recorder = new DiagnosticRecorder(Path.Combine(_directory, "logs"));
        var destination = Path.Combine(_directory, "keep.zip");
        await File.WriteAllTextAsync(destination, "original");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.ExportAsync(destination, "State", cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task RecentMemoryIsBoundedAndMultilineEventsCannotForgeLogEntries()
    {
        await using var recorder = new DiagnosticRecorder(Path.Combine(_directory, "logs"));
        for (var i = 0; i < 1001; i++) recorder.Record("INFO", "Test", "memory-event-" + i);
        recorder.Record("ERROR", "Test", "failure\n[INFO] forged event");
        var timeline = string.Join('\n', await recorder.SnapshotAsync());
        Assert.DoesNotContain("memory-event-0]", timeline);
        Assert.DoesNotContain("memory-event-0\n", timeline);
        Assert.Contains("memory-event-1000", timeline);
        Assert.Contains("failure \\n [INFO] forged", timeline);
    }
}
