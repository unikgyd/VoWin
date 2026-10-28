using System.IO;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel.SipGateway;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

/// <summary>Independent disposable-file SQLite tests. No listener, modem, carrier or production settings.</summary>
public sealed class MtSmsForwardStoreTests
{
    private static readonly string Binding = new('a', 64), AuthorityA = new('b', 64), AuthorityB = new('c', 64);
    private static SmsMessageModel Message(string source = "original-source") => new()
    {
        SlotId = "slot-a", SourceMessageId = source, SimBinding = Binding,
        SenderOrRecipient = "+15551234567", Text = "synthetic private MT body",
        Timestamp = DateTime.UtcNow, IsOutgoing = false, DeliveryState = SmsDeliveryState.Received
    };
    private static SipSmsForwardRecipient Recipient(string user = "1001", string? authority = null) =>
        new(SipSmsForwardRecipientKind.Local, user, authority ?? AuthorityA);
    private static Task<IReadOnlyList<SipSmsForwardItem>> Pending(PreferenceDatabaseService database) =>
        ((ISipSmsForwardStore)database).GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None);

    [Fact]
    public async Task NewInboxAndCapturedRecipientsSurviveReopenAndKeepOriginalPayload()
    {
        await WithDatabase(async (database, path) =>
        {
            var message = Message();
            var result = await database.SaveIncomingSmsWithForwardsAsync(message, [Recipient(), Recipient("1002", AuthorityB)]);
            Assert.True(result.IsNew); Assert.Equal(2, result.QueuedRecipients); Assert.False(result.ForwardingCapacityExceeded);
            var reopened = new PreferenceDatabaseService(path);
            var stored = Assert.Single(await reopened.GetAllSmsMessagesAsync());
            var queued = await Pending(reopened);
            Assert.Equal(2, queued.Count);
            Assert.All(queued, item =>
            {
                Assert.Equal(message.Id, stored.Id);
                Assert.Equal(message.SlotId, item.Payload.LineId); Assert.Equal(Binding, item.Payload.SimBinding);
                Assert.Equal(message.SenderOrRecipient, item.Payload.Sender); Assert.Equal(message.Text, item.Payload.Text);
                Assert.Equal(item.Payload.Fingerprint(), item.PayloadFingerprint);
                Assert.InRange(item.ExpiresAtUtc, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
                Assert.DoesNotContain(message.Text, item.ToString()); Assert.DoesNotContain(AuthorityA, item.ToString());
            });
            Assert.Contains(queued, item => item.Recipient.User == "1001" && item.Recipient.Authority == AuthorityA);
            Assert.Contains(queued, item => item.Recipient.User == "1002" && item.Recipient.Authority == AuthorityB);
        });
    }

    [Fact]
    public async Task DuplicateReceiveCannotAddAnAccountCreatedAfterTheOriginalCommit()
    {
        await WithDatabase(async (database, _) =>
        {
            var original = Message();
            Assert.True((await database.SaveIncomingSmsWithForwardsAsync(original, [Recipient()])).IsNew);
            var duplicate = Message();
            var later = await database.SaveIncomingSmsWithForwardsAsync(duplicate, [Recipient("1002", AuthorityB)]);
            Assert.False(later.IsNew); Assert.Equal(0, later.QueuedRecipients);
            var intent = Assert.Single(await Pending(database));
            Assert.Equal("1001", intent.Recipient.User); Assert.Equal(AuthorityA, intent.Recipient.Authority);
            Assert.Single(await database.GetAllSmsMessagesAsync());
        });
    }

    [Fact]
    public async Task OldInboxWithNoCapturedPlanIsNeverBackfilled()
    {
        await WithDatabase(async (database, _) =>
        {
            var message = Message(); Assert.True(await database.SaveIncomingSmsIfNewAsync(message));
            Assert.Empty(await Pending(database));
            var duplicate = await database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient()]);
            Assert.False(duplicate.IsNew); Assert.Empty(await Pending(database));
        });
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("queued")]
    [InlineData("unknown")]
    public async Task DeletedInboxThenDuplicateSourceCannotAddNewRecipientOrResetEpoch(string firstPlan)
    {
        await WithDatabase(async (database, _) =>
        {
            var original = Message();
            await database.SaveIncomingSmsWithForwardsAsync(original, firstPlan == "empty" ? [] : [Recipient()]);
            if (firstPlan == "unknown")
            {
                var old = Assert.Single(await Pending(database));
                Assert.NotNull(await ((ISipSmsForwardStore)database).TryClaimAsync(old.Id, old.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            }
            await database.DeleteSmsMessageAsync(original.Id);
            var replay = await database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient("1002", AuthorityB), Recipient("1001", new string('d', 64))]);
            Assert.True(replay.IsNew); Assert.Equal(0, replay.QueuedRecipients);
            Assert.Empty(await Pending(database));
            Assert.Single(await database.GetAllSmsMessagesAsync());
        });
    }

    [Theory]
    [InlineData("oversize-text")]
    [InlineData("oversize-sender")]
    [InlineData("nul-text")]
    [InlineData("no-line")]
    [InlineData("invalid-text-unicode")]
    [InlineData("invalid-sender-unicode")]
    public async Task UnsupportedSipPayloadIsStillSavedLocallyWithoutALaterBackfill(string unsupported)
    {
        await WithDatabase(async (database, _) =>
        {
            var message = Message(); message.RawPdu = "synthetic-original-raw-source";
            switch (unsupported)
            {
                case "oversize-text": message.Text = new string('X', 16385); break;
                case "oversize-sender": message.SenderOrRecipient = new string('A', 129); break;
                case "nul-text": message.Text = "before\0after"; break;
                case "no-line": message.SlotId = null; break;
                case "invalid-text-unicode": message.Text = "before\uD800after"; break;
                case "invalid-sender-unicode": message.SenderOrRecipient = "before\uD800after"; break;
            }
            var committed = await database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()]);
            Assert.True(committed.IsNew); Assert.Equal(0, committed.QueuedRecipients);
            Assert.True(committed.ForwardingUnsupported); Assert.False(committed.ForwardingCapacityExceeded);
            var saved = Assert.Single(await database.GetAllSmsMessagesAsync());
            Assert.Equal(message.RawPdu, saved.RawPdu); Assert.Empty(await Pending(database));
            if (unsupported == "oversize-text") Assert.Equal(16385, saved.Text.Length);
            message.Text = "a later duplicate must not create a newly routable old SMS";
            Assert.False((await database.SaveIncomingSmsWithForwardsAsync(message, [Recipient("1002")])).IsNew);
            Assert.Empty(await Pending(database));
        });
    }

    [Fact]
    public async Task QueueInsertFailureRollsBackTheInboxAndEveryRecipientTogether()
    {
        await WithDatabase(async (database, path) =>
        {
            await Pending(database); // Ensure the queue schema exists, without importing old inbox rows.
            await Execute(path, """
                CREATE TRIGGER deny_synthetic_forward BEFORE INSERT ON SipSmsForwards
                BEGIN SELECT RAISE(ABORT, 'synthetic forward insertion failure'); END;
                """);
            await Assert.ThrowsAnyAsync<Exception>(() => database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient(), Recipient("1002")]));
            Assert.Empty(await database.GetAllSmsMessagesAsync()); Assert.Empty(await Pending(database));
        });
    }

    [Fact]
    public async Task IndependentConcurrentReceiversCannotAppendTheirLosingRecipientSnapshot()
    {
        await WithDatabase(async (database, path) =>
        {
            var other = new PreferenceDatabaseService(path); await other.InitializeAsync();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobs = new[] { (database, Recipient()), (other, Recipient("1002", AuthorityB)) }.Select(async pair =>
            {
                await release.Task;
                return await pair.Item1.SaveIncomingSmsWithForwardsAsync(Message(), [pair.Item2]);
            }).ToArray();
            release.SetResult(); var results = await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Single(results, result => result.IsNew); Assert.Equal(1, results.Sum(result => result.QueuedRecipients));
            Assert.Single(await database.GetAllSmsMessagesAsync()); Assert.Single(await Pending(database));
        });
    }

    [Fact]
    public async Task IndependentStoresClaimOnlyOnceAndUnknownIsDurableBeforeTheCallerCanSend()
    {
        await WithDatabase(async (database, path) =>
        {
            await database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient()]);
            var item = Assert.Single(await Pending(database));
            var stores = Enumerable.Range(0, 8).Select(_ => new PreferenceDatabaseService(path)).ToArray();
            foreach (var store in stores) await store.InitializeAsync();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claims = stores.Select(async store =>
            {
                await release.Task;
                return await ((ISipSmsForwardStore)store).TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None);
            }).ToArray();
            release.SetResult();
            var claim = Assert.Single((await Task.WhenAll(claims).WaitAsync(TimeSpan.FromSeconds(15))).OfType<SipSmsForwardClaim>());
            Assert.Matches("\\A[0-9a-f]{32}\\z", claim.AttemptToken);
            Assert.Equal(SipSmsForwardState.Unknown, await State(path, claim.Id));
            Assert.Empty(await Pending(new PreferenceDatabaseService(path)));
            Assert.Null(await ((ISipSmsForwardStore)database).TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
        });
    }

    [Fact]
    public async Task CompletionRequiresTheOriginalTokenAndCannotDowngradeAcceptedWork()
    {
        await WithDatabase(async (database, path) =>
        {
            await database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient()]);
            var item = Assert.Single(await Pending(database)); var store = (ISipSmsForwardStore)database;
            var claim = Assert.IsType<SipSmsForwardClaim>(await store.TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            await IgnoreRejectedCompletion(() => store.CompleteAsync(item.Id, Guid.NewGuid().ToString("N"), SipSmsForwardState.Accepted, CancellationToken.None));
            Assert.Equal(SipSmsForwardState.Unknown, await State(path, item.Id));
            await store.CompleteAsync(item.Id, claim.AttemptToken, SipSmsForwardState.Accepted, CancellationToken.None);
            Assert.Equal(SipSmsForwardState.Accepted, await State(path, item.Id));
            await IgnoreRejectedCompletion(() => store.CompleteAsync(item.Id, claim.AttemptToken, SipSmsForwardState.Unknown, CancellationToken.None));
            Assert.Equal(SipSmsForwardState.Accepted, await State(path, item.Id));
            Assert.Empty(await Pending(new PreferenceDatabaseService(path)));
        });
    }

    [Fact]
    public async Task LateCompletionCannotCompleteAnExpiredAndRecreatedIntentWithTheSameId()
    {
        await WithDatabase(async (database, path) =>
        {
            var first = Message(); await database.SaveIncomingSmsWithForwardsAsync(first, [Recipient()]);
            var item = Assert.Single(await Pending(database)); var store = (ISipSmsForwardStore)database;
            var old = Assert.IsType<SipSmsForwardClaim>(await store.TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            await Execute(path, "UPDATE SipSmsForwards SET ExpiresAtTicks=1; UPDATE SipSmsForwardReceipts SET ExpiresAtTicks=1;");
            await database.DeleteSmsMessageAsync(first.Id);
            Assert.Equal(1, (await database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient()])).QueuedRecipients);
            var replacement = Assert.Single(await Pending(database));
            Assert.Equal(item.Id, replacement.Id);
            var current = Assert.IsType<SipSmsForwardClaim>(await store.TryClaimAsync(replacement.Id, replacement.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.NotEqual(old.AttemptToken, current.AttemptToken);
            await IgnoreRejectedCompletion(() => store.CompleteAsync(item.Id, old.AttemptToken, SipSmsForwardState.Accepted, CancellationToken.None));
            Assert.Equal(SipSmsForwardState.Unknown, await State(path, item.Id));
            await store.CompleteAsync(item.Id, current.AttemptToken, SipSmsForwardState.Accepted, CancellationToken.None);
            Assert.Equal(SipSmsForwardState.Accepted, await State(path, item.Id));
        });
    }

    [Fact]
    public async Task OriginalWhitespaceSenderIsNotSilentlyPromotedToAReplyableNumber()
    {
        await WithDatabase(async (database, _) =>
        {
            var message = Message(); message.SenderOrRecipient = " +15551234567 ";
            await database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()]);
            var item = Assert.Single(await Pending(database));
            Assert.Equal(message.SenderOrRecipient, item.Payload.Sender);
            Assert.Equal(item.Payload.Fingerprint(), item.PayloadFingerprint);
            var claim = Assert.IsType<SipSmsForwardClaim>(await ((ISipSmsForwardStore)database).TryClaimAsync(
                item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Equal(message.SenderOrRecipient, claim.Payload.Sender);
        });
    }

    [Fact]
    public async Task CancelQueuedCannotUndoAnAlreadyClaimedUnknownAttempt()
    {
        await WithDatabase(async (database, path) =>
        {
            await database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient()]);
            var item = Assert.Single(await Pending(database)); var store = (ISipSmsForwardStore)database;
            Assert.NotNull(await store.TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            await store.CancelQueuedAsync(item.Id, CancellationToken.None);
            Assert.Equal(SipSmsForwardState.Unknown, await State(path, item.Id)); Assert.Empty(await Pending(database));
        });
    }

    [Fact]
    public async Task CancelBeforeClaimDoesNotReserveOrLoseQueuedWork()
    {
        await WithDatabase(async (database, _) =>
        {
            await database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient()]); var item = Assert.Single(await Pending(database));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ((ISipSmsForwardStore)database).TryClaimAsync(
                item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, cancel.Token));
            Assert.Single(await Pending(database));
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClaimRechecksDeletedMessageOrConversationAfterCandidateWasRead(bool conversation)
    {
        await WithDatabase(async (database, path) =>
        {
            var message = Message(); await database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()]);
            var item = Assert.Single(await Pending(database));
            if (conversation) await database.DeleteConversationAsync(message.SenderOrRecipient); else await database.DeleteSmsMessageAsync(message.Id);
            Assert.Null(await ((ISipSmsForwardStore)database).TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Empty(await Pending(database)); Assert.Equal(SipSmsForwardState.Cancelled, await State(path, item.Id));
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClaimRechecksEditedSenderOrBodyAfterCandidateWasRead(bool sender)
    {
        await WithDatabase(async (database, path) =>
        {
            var message = Message(); await database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()]);
            var item = Assert.Single(await Pending(database));
            if (sender) message.SenderOrRecipient = "+15550009999"; else message.Text = "changed private text";
            await database.SaveSmsMessageAsync(message);
            Assert.Null(await ((ISipSmsForwardStore)database).TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Empty(await Pending(database)); Assert.Equal(SipSmsForwardState.Cancelled, await State(path, item.Id));
        });
    }

    [Fact]
    public async Task DeletingInboxThenReusingItsRowIdentityCannotReviveUnknownWork()
    {
        await WithDatabase(async (database, path) =>
        {
            var message = Message(); await database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()]);
            var item = Assert.Single(await Pending(database)); var store = (ISipSmsForwardStore)database;
            var claim = Assert.IsType<SipSmsForwardClaim>(await store.TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            await database.DeleteSmsMessageAsync(message.Id);
            var recreated = await database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()]);
            Assert.True(recreated.IsNew); Assert.Equal(0, recreated.QueuedRecipients);
            Assert.Empty(await Pending(database)); Assert.Equal(SipSmsForwardState.Unknown, await State(path, item.Id));
            await store.CompleteAsync(item.Id, claim.AttemptToken, SipSmsForwardState.Accepted, CancellationToken.None);
            Assert.Equal(SipSmsForwardState.Accepted, await State(path, item.Id));
        });
    }

    [Fact]
    public async Task ExpiredWorkIsNotReturnedOrClaimed()
    {
        await WithDatabase(async (database, _) =>
        {
            await database.SaveIncomingSmsWithForwardsAsync(Message(), [Recipient()]); var item = Assert.Single(await Pending(database));
            var later = DateTimeOffset.UtcNow.AddHours(25); var store = (ISipSmsForwardStore)database;
            Assert.Null(await store.TryClaimAsync(item.Id, item.PayloadFingerprint, later, CancellationToken.None));
            Assert.Empty(await store.GetPendingAsync(later, 2048, CancellationToken.None));
        });
    }

    [Fact]
    public async Task CapacityCountsTerminalTombstonesAndKeepsNewInboxWithoutPartialPlans()
    {
        await WithDatabase(async (database, path) =>
        {
            var recipients = Enumerable.Range(0, 64).Select(index => Recipient("account-" + index)).ToArray();
            for (var index = 0; index < 32; index++)
            {
                var committed = await database.SaveIncomingSmsWithForwardsAsync(Message("source-" + index), recipients);
                Assert.True(committed.IsNew); Assert.Equal(64, committed.QueuedRecipients); Assert.False(committed.ForwardingCapacityExceeded);
            }
            var item = (await Pending(database))[0]; var store = (ISipSmsForwardStore)database;
            var claim = Assert.IsType<SipSmsForwardClaim>(await store.TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            await store.CompleteAsync(item.Id, claim.AttemptToken, SipSmsForwardState.Accepted, CancellationToken.None);
            var overflow = await database.SaveIncomingSmsWithForwardsAsync(Message("overflow-source"), [Recipient("new-account"), Recipient("new-account-2")]);
            Assert.True(overflow.IsNew); Assert.Equal(0, overflow.QueuedRecipients); Assert.True(overflow.ForwardingCapacityExceeded);
            Assert.Equal(33, (await database.GetAllSmsMessagesAsync()).Count);
            Assert.Equal(2047, (await Pending(database)).Count);
            Assert.Equal(2048L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwards"));
        });
    }

    [Fact]
    public async Task ReceiptOverflowQuarantineSurvivesReopenAndFreedCapacityCannotBackfillAnUnrecordedSource()
    {
        await WithDatabase(async (database, path) =>
        {
            await database.SaveIncomingSmsWithForwardsAsync(Message("bootstrap-source"), []);
            var until = DateTime.UtcNow.AddHours(24).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Execute(path, """
                WITH RECURSIVE synthetic(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM synthetic WHERE n<32767)
                INSERT INTO SipSmsForwardReceipts(SourceKey,ExpiresAtTicks)
                SELECT printf('%064x',n),
                """ + until + " FROM synthetic;");
            Assert.Equal(32768L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            var original = Message("unrecorded-overflow-source");
            var overflow = await database.SaveIncomingSmsWithForwardsAsync(original, [Recipient()]);
            Assert.True(overflow.IsNew); Assert.Equal(0, overflow.QueuedRecipients); Assert.True(overflow.ForwardingCapacityExceeded);
            Assert.True(await Scalar(path, "SELECT UntilTicks FROM SipSmsForwardReceiptGuard WHERE Id=1") > DateTime.UtcNow.Ticks);
            await database.DeleteSmsMessageAsync(original.Id);
            await Execute(path, "DELETE FROM SipSmsForwardReceipts WHERE SourceKey=printf('%064x',1)");
            var reopened = new PreferenceDatabaseService(path);
            var replay = await reopened.SaveIncomingSmsWithForwardsAsync(Message("unrecorded-overflow-source"), [Recipient("1002", AuthorityB)]);
            Assert.True(replay.IsNew); Assert.Equal(0, replay.QueuedRecipients); Assert.True(replay.ForwardingCapacityExceeded);
            Assert.Empty(await Pending(reopened));
            Assert.Equal(2, (await reopened.GetAllSmsMessagesAsync()).Count);
            Assert.Equal(32768L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            Assert.Equal(0L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts WHERE length(SourceKey)<>64 OR SourceKey GLOB '*[^a-f0-9]*'"));
        });
    }

    [Theory]
    [InlineData("negative-expiry")]
    [InlineData("invalid-source-key")]
    [InlineData("negative-quarantine")]
    public async Task CorruptedReceiptHistoryIsNotPrunedOrIgnoredAsAnEmptyLedger(string corruption)
    {
        await WithDatabase(async (database, path) =>
        {
            await database.SaveIncomingSmsWithForwardsAsync(Message("earlier-source"), []);
            await Execute(path, corruption switch
            {
                "negative-expiry" => "UPDATE SipSmsForwardReceipts SET ExpiresAtTicks=-1",
                "invalid-source-key" => "UPDATE SipSmsForwardReceipts SET SourceKey='not-a-digest'",
                _ => "INSERT INTO SipSmsForwardReceiptGuard(Id,UntilTicks) VALUES(1,-1)"
            });
            await Assert.ThrowsAnyAsync<Exception>(() => database.SaveIncomingSmsWithForwardsAsync(Message("later-source"), [Recipient()]));
            Assert.Single(await database.GetAllSmsMessagesAsync());
            Assert.Empty(await Pending(database));
        });
    }

    private static async Task IgnoreRejectedCompletion(Func<Task> complete)
    {
        try { await complete(); }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
    }
    private static async Task<SipSmsForwardState> State(string path, string id) =>
        (SipSmsForwardState)await Scalar(path, "SELECT State FROM SipSmsForwards WHERE Id=$id", id);
    private static async Task<long> Scalar(string path, string sql, string? id = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Private }.ToString());
        await connection.OpenAsync(); using var command = new SqliteCommand(sql, connection);
        if (id != null) command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private static async Task Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Private }.ToString());
        await connection.OpenAsync(); using var command = new SqliteCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
    private static async Task WithDatabase(Func<PreferenceDatabaseService, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-mt-independent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "preferences.db"); var database = new PreferenceDatabaseService(path);
            await database.InitializeAsync(); await action(database, path);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }
}
