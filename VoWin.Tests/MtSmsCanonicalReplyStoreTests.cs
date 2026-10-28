using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel.SipGateway;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

/// <summary>
/// Real disposable-file SQLite coverage for immutable MT canonical replies.
/// Reflection admits compilation against the old API, but missing contracts
/// fail explicitly before fixture setup. No listener, modem or real SMS exists.
/// </summary>
public sealed class MtSmsCanonicalReplyStoreTests
{
    private static readonly string Binding = new('a', 64), Authority = new('b', 64);
    private const string OriginalReply = "+8613812345678";
    private const string DifferentReply = "+4413812345678";

    private static PropertyInfo SnapshotProperty()
    {
        var property = typeof(SmsMessageModel).GetProperty("SipReplyNumberSnapshot", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(property is not null, "Incoming SMS must expose the private immutable canonical reply snapshot API.");
        Assert.Equal(typeof(string), property!.PropertyType);
        Assert.NotNull(property.SetMethod);
        return property;
    }

    private static PropertyInfo ReplyProperty()
    {
        var property = typeof(SipSmsForwardPayload).GetProperty("ReplyNumber", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(property is not null, "Durable forwarding payload must carry its original canonical reply snapshot.");
        Assert.Equal(typeof(string), property!.PropertyType);
        Assert.NotNull(property.SetMethod);
        Assert.Contains(typeof(IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        return property;
    }

    private static void RequireContract() { _ = SnapshotProperty(); _ = ReplyProperty(); }
    private static string? Snapshot(SmsMessageModel message) => (string?)SnapshotProperty().GetValue(message);
    private static string? Reply(SipSmsForwardPayload payload) => (string?)ReplyProperty().GetValue(payload);
    private static void SetSnapshot(SmsMessageModel message, string? value) => SnapshotProperty().SetValue(message, value);
    private static void SetReply(SipSmsForwardPayload payload, string? value) => ReplyProperty().SetValue(payload, value);

    private static SmsMessageModel Message(string source, string? reply = OriginalReply, string sender = "13812345678")
    {
        var message = new SmsMessageModel
        {
            SlotId = "slot-a", SourceMessageId = source, SimBinding = Binding,
            SenderOrRecipient = sender, Text = "synthetic immutable canonical reply body",
            Timestamp = DateTime.UtcNow, IsOutgoing = false, DeliveryState = SmsDeliveryState.Received
        };
        SetSnapshot(message, reply);
        return message;
    }

    private static SipSmsForwardRecipient Recipient(string user = "1001") =>
        new(SipSmsForwardRecipientKind.Local, user, Authority);

    private static Task<IReadOnlyList<SipSmsForwardItem>> Pending(PreferenceDatabaseService database) =>
        ((ISipSmsForwardStore)database).GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None);

    [Fact]
    public async Task FirstConcurrentReceiveFreezesCanonicalReplyAndLosingPlanCannotRewriteItAfterReopen()
    {
        RequireContract();
        await using var fixture = await Fixture.CreateAsync();
        var other = new PreferenceDatabaseService(fixture.Path);
        await other.InitializeAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputs = new[] { (Database: fixture.Database, Recipient: Recipient()), (Database: other, Recipient: Recipient("1002")) };
        var jobs = inputs.Select(input => Task.Run(async () =>
        {
            await start.Task;
            return await input.Database.SaveIncomingSmsWithForwardsAsync(Message("same-network-source"), [input.Recipient]);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(results, result => result.IsNew);
        Assert.Equal(1, results.Sum(result => result.QueuedRecipients));

        var reopened = new PreferenceDatabaseService(fixture.Path);
        var inbox = Assert.Single(await reopened.GetAllSmsMessagesAsync());
        var item = Assert.Single(await Pending(reopened));
        Assert.Equal(OriginalReply, Snapshot(inbox));
        Assert.Equal(OriginalReply, Reply(item.Payload));
        Assert.Equal("13812345678", inbox.SenderOrRecipient);
        Assert.Equal(inbox.SenderOrRecipient, item.Payload.Sender);
        Assert.Equal(item.Payload.Fingerprint(), item.PayloadFingerprint);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SipSmsForwardReceipts"));

        var previous = await fixture.IntentSnapshotAsync();
        var duplicate = await reopened.SaveIncomingSmsWithForwardsAsync(
            Message("same-network-source", DifferentReply), [Recipient("1003")]);
        Assert.False(duplicate.IsNew); Assert.Equal(0, duplicate.QueuedRecipients);
        Assert.Equal(previous, await fixture.IntentSnapshotAsync());
        Assert.Equal(OriginalReply, Snapshot(Assert.Single(await reopened.GetAllSmsMessagesAsync())));
        Assert.Equal(OriginalReply, Reply(Assert.Single(await Pending(reopened)).Payload));
        var claim = Assert.IsType<SipSmsForwardClaim>(await ((ISipSmsForwardStore)reopened).TryClaimAsync(
            item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(OriginalReply, Reply(claim.Payload));
        Assert.True(await ((ISipSmsForwardStore)reopened).IsClaimSourceCurrentAsync(claim, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(OriginalReply)]
    public async Task OrdinaryHistorySaveCannotPromoteLegacyOrReadOnlyOrReplaceFirstCanonicalSnapshot(string? original)
    {
        RequireContract();
        await using var fixture = await Fixture.CreateAsync();
        var message = Message("immutable-history", original);
        Assert.True((await fixture.Database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()])).IsNew);
        var item = Assert.Single(await Pending(fixture.Database));
        var ledger = await fixture.IntentSnapshotAsync();
        var receipt = await fixture.ReceiptSnapshotAsync();
        SetSnapshot(message, original == OriginalReply ? DifferentReply : OriginalReply);
        await fixture.Database.SaveSmsMessageAsync(message);

        var reopened = new PreferenceDatabaseService(fixture.Path);
        var stored = Assert.Single(await reopened.GetAllSmsMessagesAsync());
        Assert.Equal(original, Snapshot(stored));
        Assert.Equal(original, Reply(Assert.Single(await Pending(reopened)).Payload));
        Assert.Equal(item.PayloadFingerprint, Assert.Single(await Pending(reopened)).PayloadFingerprint);
        Assert.Equal(ledger, await fixture.IntentSnapshotAsync());
        Assert.Equal(receipt, await fixture.ReceiptSnapshotAsync());
        Assert.DoesNotContain("SipReplyNumberSnapshot", JsonSerializer.Serialize(stored));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingReadCancelsWhenActualInboxAndQueueCanonicalSnapshotsNoLongerMatch(bool queue)
    {
        RequireContract();
        await using var fixture = await Fixture.CreateAsync();
        var message = Message("pending-tamper");
        await fixture.Database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()]);
        var item = Assert.Single(await Pending(fixture.Database));
        await fixture.TamperAsync(queue, message.Id, item.Id, DifferentReply);

        Assert.Empty(await Pending(new PreferenceDatabaseService(fixture.Path)));
        Assert.Equal((long)SipSmsForwardState.Cancelled, await fixture.StateAsync(item.Id));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SmsMessages"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SipSmsForwardReceipts"));
        await fixture.RequireFreshPositiveAsync("fresh-after-pending-tamper");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ClaimAndLastWireGuardRejectCanonicalTamperingWithoutRevivingAnUnknownAttempt(bool queue, bool alreadyClaimed)
    {
        RequireContract();
        await using var fixture = await Fixture.CreateAsync();
        var message = Message("claim-tamper");
        await fixture.Database.SaveIncomingSmsWithForwardsAsync(message, [Recipient()]);
        var item = Assert.Single(await Pending(fixture.Database));
        var store = (ISipSmsForwardStore)fixture.Database;
        SipSmsForwardClaim? claim = null;
        if (alreadyClaimed)
            claim = Assert.IsType<SipSmsForwardClaim>(await store.TryClaimAsync(
                item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
        var before = await fixture.IntentSnapshotAsync();
        await fixture.TamperAsync(queue, message.Id, item.Id, DifferentReply);

        var independent = (ISipSmsForwardStore)new PreferenceDatabaseService(fixture.Path);
        if (alreadyClaimed)
        {
            Assert.False(await independent.IsClaimSourceCurrentAsync(claim!, CancellationToken.None));
            Assert.Equal((long)SipSmsForwardState.Unknown, await fixture.StateAsync(item.Id));
            Assert.Equal(claim!.AttemptToken, await fixture.TextAsync("SELECT AttemptToken FROM SipSmsForwards WHERE Id=$id", ("$id", item.Id)));
            if (!queue) Assert.Equal(before, await fixture.IntentSnapshotAsync());
            Assert.Null(await independent.TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
        }
        else
        {
            Assert.Null(await independent.TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Equal((long)SipSmsForwardState.Cancelled, await fixture.StateAsync(item.Id));
        }
        Assert.Empty(await Pending(new PreferenceDatabaseService(fixture.Path)));
        await fixture.RequireFreshPositiveAsync("fresh-after-claim-tamper");
    }

    [Fact]
    public async Task LegacyNullAndExplicitReadOnlyRemainDifferentDurablePayloadVersions()
    {
        RequireContract();
        await using var fixture = await Fixture.CreateAsync();
        foreach (var (source, snapshot) in new (string, string?)[]
                 { ("legacy", null), ("read-only", ""), ("canonical", OriginalReply) })
            await fixture.Database.SaveIncomingSmsWithForwardsAsync(Message(source, snapshot), [Recipient()]);
        var inbox = (await fixture.Database.GetAllSmsMessagesAsync()).ToDictionary(item => item.SourceMessageId!);
        var pending = await Pending(fixture.Database);
        Assert.Equal(3, pending.Count);
        Assert.Null(Snapshot(inbox["legacy"]));
        Assert.Equal("", Snapshot(inbox["read-only"]));
        Assert.Equal(OriginalReply, Snapshot(inbox["canonical"]));
        var legacy = Assert.Single(pending, item => Reply(item.Payload) == null);
        var readOnly = Assert.Single(pending, item => Reply(item.Payload) == "");
        var canonical = Assert.Single(pending, item => Reply(item.Payload) == OriginalReply);
        Assert.Equal(LegacyFingerprint(legacy.Payload), legacy.PayloadFingerprint);
        Assert.NotEqual(legacy.PayloadFingerprint, readOnly.PayloadFingerprint);
        Assert.NotEqual(readOnly.PayloadFingerprint, canonical.PayloadFingerprint);
        var changed = new SipSmsForwardPayload(canonical.Payload.LineId, canonical.Payload.SimBinding,
            canonical.Payload.Sender, canonical.Payload.Text);
        SetReply(changed, DifferentReply);
        Assert.NotEqual(canonical.PayloadFingerprint, changed.Fingerprint());
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SmsMessages WHERE SipReplyNumberSnapshot IS NULL"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SipSmsForwards WHERE ReplyNumberSnapshot IS NULL"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SmsMessages WHERE SipReplyNumberSnapshot=''"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SipSmsForwards WHERE ReplyNumberSnapshot=''"));
    }

    [Theory]
    [InlineData("13812345678")]
    [InlineData("+0123456789")]
    [InlineData("+123456")]
    [InlineData("+1234567890123456")]
    [InlineData(" +8613812345678")]
    [InlineData("+8613812345678 ")]
    [InlineData("+8613812345678\n")]
    [InlineData("+８６１３８１２３４５６７８")]
    public async Task InvalidNonNullCanonicalReplyCannotPersistInboxReceiptOrAnyRecipient(string invalid)
    {
        RequireContract();
        await using var fixture = await Fixture.CreateAsync();
        var message = Message("invalid-canonical", invalid);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Database.SaveIncomingSmsWithForwardsAsync(
            message, [Recipient(), Recipient("1002")]));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SmsMessages"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SipSmsForwardReceipts"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SipSmsForwards"));
        Assert.Empty(await Pending(fixture.Database));
        var valid = await fixture.Database.SaveIncomingSmsWithForwardsAsync(
            Message("invalid-canonical", OriginalReply), [Recipient()]);
        Assert.True(valid.IsNew); Assert.Equal(1, valid.QueuedRecipients);
        await fixture.RequireFreshPositiveAsync("fresh-after-invalid-canonical");
    }

    [Fact]
    public async Task FormerSchemaMigrationPreservesNullFingerprintExpiryReceiptAndUnknownWithoutBackfillingCanonicalOrRecipients()
    {
        RequireContract();
        await using var fixture = await Fixture.CreateAsync(initialize: false);
        await fixture.ExecuteAsync("""
            CREATE TABLE SmsMessages (
                Id TEXT PRIMARY KEY, RemoteNumber TEXT NOT NULL, Text TEXT NOT NULL, Timestamp TEXT NOT NULL,
                IsOutgoing INTEGER NOT NULL, DeliveryState INTEGER NOT NULL, DeliveryStatus TEXT,
                MessageReference INTEGER, SlotId TEXT, RawPdu TEXT, SimBinding TEXT, SourceMessageId TEXT);
            CREATE TABLE SipSmsForwards (
                Id TEXT NOT NULL PRIMARY KEY, InboxId TEXT NOT NULL, RecipientKind INTEGER NOT NULL CHECK(RecipientKind IN (0,1)),
                RecipientUser TEXT NOT NULL, Authority TEXT NOT NULL, PayloadFingerprint TEXT NOT NULL,
                SenderSnapshot TEXT NOT NULL, ExpiresAtTicks INTEGER NOT NULL,
                State INTEGER NOT NULL CHECK(State IN (0,1,2,3)), AttemptToken TEXT);
            CREATE TABLE SipSmsForwardReceipts (SourceKey TEXT NOT NULL PRIMARY KEY, ExpiresAtTicks INTEGER NOT NULL);
            CREATE TABLE SipSmsForwardReceiptGuard (Id INTEGER NOT NULL PRIMARY KEY CHECK(Id=1), UntilTicks INTEGER NOT NULL);
            """);
        var expiry = DateTimeOffset.UtcNow.AddHours(12).UtcTicks;
        for (var index = 0; index < 4; index++)
        {
            var source = "legacy-source-" + index;
            var sender = index % 2 == 0 ? "13812345678" : OriginalReply;
            var payload = new SipSmsForwardPayload("slot-a", Binding, sender, "synthetic legacy body " + index);
            var key = SourceKey(source);
            var id = IntentId(key, Recipient());
            await fixture.ExecuteAsync("""
                INSERT INTO SmsMessages(Id,RemoteNumber,Text,Timestamp,IsOutgoing,DeliveryState,SlotId,SimBinding,SourceMessageId)
                VALUES($inbox,$sender,$body,$time,0,0,'slot-a',$binding,$source);
                INSERT INTO SipSmsForwards(Id,InboxId,RecipientKind,RecipientUser,Authority,PayloadFingerprint,SenderSnapshot,ExpiresAtTicks,State,AttemptToken)
                VALUES($id,$inbox,0,'1001',$authority,$fingerprint,$sender,$expiry,$state,$token);
                INSERT INTO SipSmsForwardReceipts(SourceKey,ExpiresAtTicks) VALUES($key,$expiry);
                """, ("$inbox", "legacy-inbox-" + index), ("$sender", sender), ("$body", payload.Text),
                ("$time", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$binding", Binding),
                ("$source", source), ("$id", id), ("$authority", Authority), ("$fingerprint", LegacyFingerprint(payload)),
                ("$expiry", expiry), ("$state", index < 2 ? 0 : 1), ("$token", index < 2 ? null : new string('d', 32)), ("$key", key));
        }
        var oldQueue = await fixture.IntentSnapshotAsync(includeReply: false);
        var oldReceipts = await fixture.ReceiptSnapshotAsync();
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('SmsMessages') WHERE name='SipReplyNumberSnapshot'"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('SipSmsForwards') WHERE name='ReplyNumberSnapshot'"));

        await fixture.Database.InitializeAsync();
        var reopened = new PreferenceDatabaseService(fixture.Path);
        await reopened.InitializeAsync();
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('SmsMessages') WHERE name='SipReplyNumberSnapshot' AND type='TEXT'"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('SipSmsForwards') WHERE name='ReplyNumberSnapshot' AND type='TEXT'"));
        Assert.Equal(4L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SmsMessages WHERE SipReplyNumberSnapshot IS NULL"));
        Assert.Equal(4L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SipSmsForwards WHERE ReplyNumberSnapshot IS NULL"));
        Assert.Equal(oldQueue, await fixture.IntentSnapshotAsync(includeReply: false));
        Assert.Equal(oldReceipts, await fixture.ReceiptSnapshotAsync());
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM SipSmsForwards WHERE State=1 AND AttemptToken=$token", ("$token", new string('d', 32))));
        var pending = await Pending(reopened);
        Assert.Equal(2, pending.Count);
        Assert.All(pending, item => { Assert.Null(Reply(item.Payload)); Assert.Equal(LegacyFingerprint(item.Payload), item.PayloadFingerprint); });
        Assert.Contains(pending, item => item.Payload.Sender == "13812345678");
        Assert.Contains(pending, item => item.Payload.Sender == OriginalReply);
        Assert.All(await reopened.GetAllSmsMessagesAsync(), item => Assert.Null(Snapshot(item)));
        Assert.Equal(oldQueue, await fixture.IntentSnapshotAsync(includeReply: false));
        Assert.Equal(oldReceipts, await fixture.ReceiptSnapshotAsync());
        await fixture.RequireFreshPositiveAsync("new-after-legacy-migration");
    }

    private static string LegacyFingerprint(SipSmsForwardPayload payload) => FramedHash(
        "sip-sms-forward-payload-v1", payload.LineId, payload.SimBinding ?? "", payload.Sender, payload.Text);
    private static string SourceKey(string source) => FramedHash("sip-sms-receipt-v1", "slot-a", Binding, "source:" + source);
    private static string FramedHash(params string[] fields) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Concat(fields.Select(value => Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value))))).ToLowerInvariant();
    private static string IntentId(string source, SipSmsForwardRecipient recipient) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        "sip-sms-forward-v1\0" + source + "\0" + recipient.Kind + "\0" + recipient.User.ToLowerInvariant() + "\0" + recipient.Authority))).ToLowerInvariant();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        public string Path { get; }
        public PreferenceDatabaseService Database { get; }
        private Fixture(string directory, string path) { _directory = directory; Path = path; Database = new(path); }

        public static async Task<Fixture> CreateAsync(bool initialize = true)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vowin-mt-canonical-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var result = new Fixture(directory, System.IO.Path.Combine(directory, "preferences.db"));
            try { if (initialize) await result.Database.InitializeAsync(); return result; }
            catch { await result.DisposeAsync(); throw; }
        }

        private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
            { DataSource = Path, Cache = SqliteCacheMode.Private }.ToString());

        public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        public async Task<string> TextAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return Assert.IsType<string>(await command.ExecuteScalarAsync());
        }

        public Task<long> StateAsync(string id) => ScalarAsync("SELECT State FROM SipSmsForwards WHERE Id=$id", ("$id", id));
        public Task TamperAsync(bool queue, string inbox, string id, string? value) => queue
            ? ExecuteAsync("UPDATE SipSmsForwards SET ReplyNumberSnapshot=$reply WHERE Id=$id", ("$reply", value), ("$id", id))
            : ExecuteAsync("UPDATE SmsMessages SET SipReplyNumberSnapshot=$reply WHERE Id=$id", ("$reply", value), ("$id", inbox));

        public async Task<string> IntentSnapshotAsync(bool includeReply = true)
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand("""
                SELECT hex(Id)||'/'||hex(InboxId)||'/'||RecipientKind||'/'||hex(RecipientUser)||'/'||hex(Authority)
                    ||'/'||hex(PayloadFingerprint)||'/'||hex(SenderSnapshot)||'/'||ExpiresAtTicks||'/'||State||'/'||hex(AttemptToken)
                """ + (includeReply ? "||'/'||CASE WHEN ReplyNumberSnapshot IS NULL THEN 'NULL' ELSE 'TEXT:'||hex(ReplyNumberSnapshot) END" : "") +
                " FROM SipSmsForwards ORDER BY Id", connection);
            using var reader = await command.ExecuteReaderAsync();
            var values = new List<string>();
            while (await reader.ReadAsync()) values.Add(reader.GetString(0));
            return string.Join("\n", values);
        }

        public async Task<string> ReceiptSnapshotAsync()
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand("SELECT SourceKey||'/'||ExpiresAtTicks FROM SipSmsForwardReceipts ORDER BY SourceKey", connection);
            using var reader = await command.ExecuteReaderAsync();
            var values = new List<string>();
            while (await reader.ReadAsync()) values.Add(reader.GetString(0));
            return string.Join("\n", values);
        }

        public async Task RequireFreshPositiveAsync(string source)
        {
            var result = await Database.SaveIncomingSmsWithForwardsAsync(Message(source), [Recipient("1002")]);
            Assert.True(result.IsNew); Assert.Equal(1, result.QueuedRecipients);
            var item = Assert.Single((await Pending(Database)).Where(item => item.Recipient.User == "1002"));
            var store = (ISipSmsForwardStore)Database;
            var claim = Assert.IsType<SipSmsForwardClaim>(await store.TryClaimAsync(item.Id, item.PayloadFingerprint, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Equal(OriginalReply, Reply(claim.Payload));
            Assert.True(await store.IsClaimSourceCurrentAsync(claim, CancellationToken.None));
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools(); Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
