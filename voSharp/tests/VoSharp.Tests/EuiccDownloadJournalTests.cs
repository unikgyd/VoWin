using VoSharp.Euicc.Download;

namespace VoSharp.Tests;

public sealed class EuiccDownloadJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vosharp-euicc-journal-" + Guid.NewGuid().ToString("N"));
    private readonly string _journalPath;

    public EuiccDownloadJournalTests()
    {
        _journalPath = Path.Combine(_directory, "journal.json");
        EuiccDownloadJournal.PathOverride = _journalPath;
    }

    [Fact]
    public void JournalPreventsDuplicateActivationAndPersistsOnlyFingerprint()
    {
        const string activationCode = "LPA:1$smdp.example.com$ONE-TIME-MATCHING-ID";
        var fingerprint = EuiccDownloadJournal.Fingerprint(activationCode);

        EuiccDownloadJournal.Begin(fingerprint, ["8901000000000000001"]);

        Assert.Throws<InvalidOperationException>(() =>
            EuiccDownloadJournal.Begin(fingerprint, ["8901000000000000001"]));
        var stored = EuiccDownloadJournal.Get(fingerprint);
        Assert.NotNull(stored);
        Assert.Equal("in_progress", stored.State);
        Assert.DoesNotContain("ONE-TIME-MATCHING-ID", File.ReadAllText(_journalPath));

        EuiccDownloadJournal.MarkUncertain(fingerprint);
        Assert.Equal("uncertain", EuiccDownloadJournal.Get(fingerprint)!.State);

        EuiccDownloadJournal.Complete(fingerprint, "8901000000000000002");
        stored = EuiccDownloadJournal.Get(fingerprint);
        Assert.Equal("completed", stored!.State);
        Assert.Equal("8901000000000000002", stored.InstalledIccid);
    }

    [Fact]
    public void ProviderAuthorizedRetryRemovesOnlyMatchingTransaction()
    {
        var authorized = EuiccDownloadJournal.Fingerprint("LPA:1$smdp.example.com$AUTHORIZED");
        var unrelated = EuiccDownloadJournal.Fingerprint("LPA:1$smdp.example.com$KEEP");
        EuiccDownloadJournal.Begin(authorized, []);
        EuiccDownloadJournal.MarkUncertain(authorized);
        EuiccDownloadJournal.Begin(unrelated, []);

        Assert.True(EuiccDownloadJournal.ResetForAuthorizedRetry(authorized));
        Assert.Null(EuiccDownloadJournal.Get(authorized));
        Assert.NotNull(EuiccDownloadJournal.Get(unrelated));
        Assert.False(EuiccDownloadJournal.ResetForAuthorizedRetry(authorized));
    }

    public void Dispose()
    {
        EuiccDownloadJournal.PathOverride = null;
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }
}
