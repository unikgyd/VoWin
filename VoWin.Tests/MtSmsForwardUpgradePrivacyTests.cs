using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel.SipGateway;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

/// <summary>Prior-schema upgrade regression using only a disposable real SQLite file.</summary>
public sealed class MtSmsForwardUpgradePrivacyTests
{
    private static readonly string HistoricalBinding = new('a', 64);

    [Fact]
    public async Task UpgradedHistoricalInboxDeletedBeforeDuplicateCannotForwardToANewRecipient()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-mt-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "preferences.db");
            var binding = new string('a', 64);
            // This is the former schema, not a row written through the new service: there are
            // deliberately no forwarding tables or receipts before first initialization.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = path, Cache = SqliteCacheMode.Private }.ToString()))
            {
                await connection.OpenAsync();
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    CREATE TABLE SmsMessages (
                        Id TEXT PRIMARY KEY,
                        RemoteNumber TEXT NOT NULL,
                        Text TEXT NOT NULL,
                        Timestamp TEXT NOT NULL,
                        IsOutgoing INTEGER NOT NULL,
                        DeliveryState INTEGER NOT NULL,
                        DeliveryStatus TEXT,
                        MessageReference INTEGER,
                        SlotId TEXT,
                        RawPdu TEXT,
                        SimBinding TEXT,
                        SourceMessageId TEXT
                    );
                    INSERT INTO SmsMessages
                        (Id,RemoteNumber,Text,Timestamp,IsOutgoing,DeliveryState,SlotId,SimBinding,SourceMessageId)
                    VALUES
                        ('historical-row','+15551234567','synthetic historical private MT body',
                         '2026-10-20T12:00:00.0000000Z',0,0,'slot-a',$binding,'historical-source');
                    """;
                seed.Parameters.AddWithValue("$binding", binding);
                await seed.ExecuteNonQueryAsync();
                using var verifyPriorSchema = connection.CreateCommand();
                verifyPriorSchema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'SipSmsForward%'";
                Assert.Equal(0L, Convert.ToInt64(await verifyPriorSchema.ExecuteScalarAsync()));
            }

            var database = new PreferenceDatabaseService(path);
            await database.InitializeAsync();
            Assert.Equal("historical-row", Assert.Single(await database.GetAllSmsMessagesAsync()).Id);
            await database.DeleteSmsMessageAsync("historical-row");
            Assert.Empty(await database.GetAllSmsMessagesAsync());

            var replay = new SmsMessageModel
            {
                Id = "new-ui-row", SlotId = "slot-a", SimBinding = binding,
                SourceMessageId = "historical-source", SenderOrRecipient = "+15551234567",
                Text = "synthetic historical private MT body", Timestamp = DateTime.UtcNow,
                IsOutgoing = false, DeliveryState = SmsDeliveryState.Received
            };
            var laterAccount = new SipSmsForwardRecipient(SipSmsForwardRecipientKind.Local, "1002", new string('c', 64));
            var result = await database.SaveIncomingSmsWithForwardsAsync(replay, [laterAccount]);

            Assert.True(result.IsNew); // Local inbox restoration is allowed, historical private forwarding is not.
            Assert.Equal(0, result.QueuedRecipients);
            Assert.Empty(await ((ISipSmsForwardStore)database).GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None));
            Assert.Equal(replay.Id, Assert.Single(await database.GetAllSmsMessagesAsync()).Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentFirstInitializeSeedsOnlyIncomingOnceAndReopenDoesNotExtendReceipts()
    {
        await WithHistoricalDatabase(1, async path =>
        {
            await Execute(path, """
                INSERT INTO SmsMessages
                  (Id,RemoteNumber,Text,Timestamp,IsOutgoing,DeliveryState,SlotId,SimBinding,SourceMessageId)
                VALUES ('historical-outgoing','+15557654321','synthetic outgoing body',
                  '2026-10-20T12:00:00.0000000Z',1,2,'slot-a',$binding,'outgoing-source')
                """, ("$binding", HistoricalBinding));
            var first = new PreferenceDatabaseService(path);
            var second = new PreferenceDatabaseService(path);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bothReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = 0;
            var before = DateTimeOffset.UtcNow;
            Task Run(PreferenceDatabaseService database) => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref ready) == 2) bothReady.TrySetResult();
                await start.Task;
                await database.InitializeAsync();
            });
            var initializations = new[] { Run(first), Run(second) };
            try { await bothReady.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { start.TrySetResult(); }
            await Task.WhenAll(initializations).WaitAsync(TimeSpan.FromSeconds(40));
            var after = DateTimeOffset.UtcNow;

            Assert.Equal(1L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            Assert.Equal(0L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwards"));
            Assert.Equal(2L, await Scalar(path, "SELECT COUNT(*) FROM SmsMessages"));
            Assert.InRange(await Scalar(path, "SELECT ExpiresAtTicks FROM SipSmsForwardReceipts"),
                before.AddHours(24).UtcTicks, after.AddHours(24).UtcTicks);
            Assert.Equal(1L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts WHERE SourceKey=$source",
                ("$source", ReceiptKey("historical-source-1"))));

            // A deterministic shortened expiry reveals re-seeding without sleeping for clock progress.
            var shortenedExpiry = DateTimeOffset.UtcNow.AddHours(12).UtcTicks;
            await Execute(path, "UPDATE SipSmsForwardReceipts SET ExpiresAtTicks=$until", ("$until", shortenedExpiry));
            await new PreferenceDatabaseService(path).InitializeAsync();
            Assert.Equal(1L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            Assert.Equal(shortenedExpiry, await Scalar(path, "SELECT ExpiresAtTicks FROM SipSmsForwardReceipts"));
            Assert.Equal(0L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwards"));
        });
    }

    [Fact]
    public async Task ExistingReceiptTableWithExpiredOrPrunedHistoryIsNotReseededOnInitialize()
    {
        await WithHistoricalDatabase(2, async path =>
        {
            var expired = DateTimeOffset.UtcNow.AddHours(-1).UtcTicks;
            await Execute(path, """
                CREATE TABLE SipSmsForwardReceipts (
                    SourceKey TEXT NOT NULL PRIMARY KEY,
                    ExpiresAtTicks INTEGER NOT NULL
                );
                INSERT INTO SipSmsForwardReceipts(SourceKey,ExpiresAtTicks) VALUES($source,$expired)
                """, ("$source", ReceiptKey("historical-source-1")), ("$expired", expired));
            // The other historical source deliberately has no row: its old receipt was pruned.
            await new PreferenceDatabaseService(path).InitializeAsync();
            await new PreferenceDatabaseService(path).InitializeAsync();
            Assert.Equal(1L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            Assert.Equal(expired, await Scalar(path, "SELECT ExpiresAtTicks FROM SipSmsForwardReceipts WHERE SourceKey=$source",
                ("$source", ReceiptKey("historical-source-1"))));
            Assert.Equal(0L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts WHERE SourceKey=$source",
                ("$source", ReceiptKey("historical-source-2"))));
            Assert.Equal(0L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwards"));
            Assert.Equal(2L, await Scalar(path, "SELECT COUNT(*) FROM SmsMessages"));
        });
    }

    [Fact]
    public async Task HistoricalMigrationCapacityOverflowPersistsQuarantineAfterASlotIsFreed()
    {
        await WithHistoricalDatabase(32769, async path =>
        {
            var before = DateTimeOffset.UtcNow;
            var database = new PreferenceDatabaseService(path);
            await database.InitializeAsync();
            var after = DateTimeOffset.UtcNow;
            Assert.Equal(32768L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            Assert.Equal(32769L, await Scalar(path, "SELECT COUNT(*) FROM SmsMessages"));
            Assert.Equal(0L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwards"));
            var guardUntil = await Scalar(path, "SELECT UntilTicks FROM SipSmsForwardReceiptGuard WHERE Id=1");
            Assert.InRange(guardUntil, before.AddHours(24).UtcTicks, after.AddHours(24).UtcTicks);
            Assert.Equal(0L, await Scalar(path,
                "SELECT COUNT(*) FROM SipSmsForwardReceipts WHERE length(SourceKey)<>64 OR SourceKey GLOB '*[^a-f0-9]*'"));

            await Execute(path, "DELETE FROM SipSmsForwardReceipts WHERE SourceKey=(SELECT MIN(SourceKey) FROM SipSmsForwardReceipts)");
            var reopened = new PreferenceDatabaseService(path);
            await reopened.InitializeAsync();
            Assert.Equal(32767L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            Assert.Equal(guardUntil, await Scalar(path, "SELECT UntilTicks FROM SipSmsForwardReceiptGuard WHERE Id=1"));
            var newSource = new SmsMessageModel
            {
                Id = "new-source-ui-row", SlotId = "slot-a", SimBinding = HistoricalBinding,
                SourceMessageId = "new-after-upgrade", SenderOrRecipient = "+15551234567",
                Text = "synthetic fresh private MT body", Timestamp = DateTime.UtcNow,
                IsOutgoing = false, DeliveryState = SmsDeliveryState.Received
            };
            var result = await reopened.SaveIncomingSmsWithForwardsAsync(newSource,
                [new(SipSmsForwardRecipientKind.Local, "1002", new string('c', 64))]);
            Assert.True(result.IsNew);
            Assert.Equal(0, result.QueuedRecipients);
            Assert.True(result.ForwardingCapacityExceeded);
            Assert.Equal(32770L, await Scalar(path, "SELECT COUNT(*) FROM SmsMessages"));
            Assert.Equal(32768L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            Assert.Empty(await ((ISipSmsForwardStore)reopened).GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None));
        });
    }

    [Fact]
    public async Task FailedHistoricalMigrationRollsBackReceiptMarkerAndCanBeRetriedAfterRepair()
    {
        await WithHistoricalDatabase(1, async path =>
        {
            // SQLite's former TEXT PRIMARY KEY allows NULL in a non-WITHOUT-ROWID table.
            // This malformed legacy ID makes source metadata decoding fail after CREATE TABLE.
            await Execute(path, "UPDATE SmsMessages SET Id=NULL WHERE Id='historical-row-1'");
            await Assert.ThrowsAnyAsync<Exception>(() => new PreferenceDatabaseService(path).InitializeAsync());
            Assert.Equal(0L, await Scalar(path,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SipSmsForwardReceipts'"));
            await Execute(path, "UPDATE SmsMessages SET Id='repaired-historical-row' WHERE Id IS NULL");
            await new PreferenceDatabaseService(path).InitializeAsync();
            Assert.Equal(1L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwardReceipts"));
            Assert.Equal(0L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwards"));
            Assert.Equal(1L, await Scalar(path, "SELECT COUNT(*) FROM SmsMessages"));
        });
    }

    private static async Task WithHistoricalDatabase(int incomingCount, Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-mt-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "preferences.db");
            await Execute(path, """
                CREATE TABLE SmsMessages (
                    Id TEXT PRIMARY KEY,
                    RemoteNumber TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    IsOutgoing INTEGER NOT NULL,
                    DeliveryState INTEGER NOT NULL,
                    DeliveryStatus TEXT,
                    MessageReference INTEGER,
                    SlotId TEXT,
                    RawPdu TEXT,
                    SimBinding TEXT,
                    SourceMessageId TEXT
                );
                WITH RECURSIVE historical(n) AS (
                    SELECT 1 UNION ALL SELECT n+1 FROM historical WHERE n<$count
                )
                INSERT INTO SmsMessages
                    (Id,RemoteNumber,Text,Timestamp,IsOutgoing,DeliveryState,SlotId,SimBinding,SourceMessageId)
                SELECT 'historical-row-' || n,'+15551234567','synthetic historical private MT body',
                    '2026-10-20T12:00:00.0000000Z',0,0,'slot-a',$binding,'historical-source-' || n
                FROM historical
                """, ("$count", incomingCount), ("$binding", HistoricalBinding));
            Assert.Equal(0L, await Scalar(path,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'SipSmsForward%'"));
            await action(path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ReceiptKey(string source)
    {
        var fields = new[] { "sip-sms-receipt-v1", "slot-a", HistoricalBinding, "source:" + source };
        var framed = string.Concat(fields.Select(value =>
            Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(framed))).ToLowerInvariant();
    }

    private static async Task Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Cache = SqliteCacheMode.Private }.ToString());
        await connection.OpenAsync();
        using var command = new SqliteCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Cache = SqliteCacheMode.Private }.ToString());
        await connection.OpenAsync();
        using var command = new SqliteCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
