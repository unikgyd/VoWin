using System.IO;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel.SipGateway;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

/// <summary>Real disposable SQLite files and independent connections; no SIP listener, modem or user profile.</summary>
public sealed class MtSmsForwardGuardTests
{
    private static readonly string Binding = new('a', 64), Authority = new('b', 64);

    [Fact]
    public async Task CurrentClaimSurvivesIndependentStoreAndRepeatedGuardReadsWithoutRenewal()
    {
        await using var f = await GuardFixture.CreateAsync();
        Assert.Equal(f.Message.Id, f.Claim.InboxId);
        var before = await f.IntentSnapshotAsync();
        for (var index = 0; index < 3; index++)
        {
            Assert.True(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
            Assert.True(await ((ISipSmsForwardStore)f.OtherDatabase).IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
            Assert.Equal(before, await f.IntentSnapshotAsync());
        }
        await f.RequireUnknownAsync();
    }

    [Theory]
    [InlineData("body")]
    [InlineData("sender")]
    [InlineData("delete")]
    [InlineData("direction")]
    [InlineData("slot")]
    [InlineData("binding")]
    [InlineData("source-id")]
    [InlineData("missing-source-id")]
    [InlineData("other-inbox-different-source")]
    [InlineData("other-inbox-same-source")]
    public async Task ActualInboxMutationFromIndependentConnectionInvalidatesOnlyTheOriginalClaim(string mutation)
    {
        await using var f = await GuardFixture.CreateAsync();
        Assert.True(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
        switch (mutation)
        {
            case "delete":
                await f.OtherDatabase.DeleteSmsMessageAsync(f.Message.Id);
                break;
            case "body":
            case "sender":
            case "direction":
                if (mutation == "body") f.Message.Text = "changed synthetic private body";
                if (mutation == "sender") f.Message.SenderOrRecipient = "+15550009999";
                if (mutation == "direction") f.Message.IsOutgoing = true;
                await f.OtherDatabase.SaveSmsMessageAsync(f.Message);
                break;
            case "slot":
                await f.ExecuteAsync("UPDATE SmsMessages SET SlotId=$value WHERE Id=$id", ("$value", "another-slot"), ("$id", f.Message.Id));
                break;
            case "binding":
                await f.ExecuteAsync("UPDATE SmsMessages SET SimBinding=$value WHERE Id=$id", ("$value", new string('c', 64)), ("$id", f.Message.Id));
                break;
            case "source-id":
            case "missing-source-id":
                await f.ExecuteAsync("UPDATE SmsMessages SET SourceMessageId=$value WHERE Id=$id",
                    ("$value", mutation == "source-id" ? "another-network-source" : null), ("$id", f.Message.Id));
                break;
            default:
                var replacement = NewMessage(mutation == "other-inbox-same-source" ? f.Message.SourceMessageId! : "another-network-source");
                Assert.NotEqual(f.Message.Id, replacement.Id);
                await f.OtherDatabase.SaveSmsMessageAsync(replacement);
                await f.ExecuteAsync("UPDATE SipSmsForwards SET InboxId=$inbox WHERE Id=$id",
                    ("$inbox", replacement.Id), ("$id", f.Claim.Id));
                break;
        }
        var before = await f.IntentSnapshotAsync();
        Assert.False(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
        Assert.False(await ((ISipSmsForwardStore)f.OtherDatabase).IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
        Assert.Equal(before, await f.IntentSnapshotAsync());
        await f.RequireUnknownAsync();
    }

    [Theory]
    [InlineData("token")]
    [InlineData("accepted")]
    [InlineData("cancelled")]
    [InlineData("queued")]
    [InlineData("expired")]
    [InlineData("recipient-kind")]
    [InlineData("recipient-user")]
    [InlineData("recipient-authority")]
    [InlineData("fingerprint")]
    [InlineData("sender-snapshot")]
    [InlineData("missing-inbox")]
    [InlineData("intent-id")]
    public async Task ActualIntentMutationCannotBeOverriddenByTheClaimCachedBeforeIt(string mutation)
    {
        await using var f = await GuardFixture.CreateAsync();
        Assert.True(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
        var set = mutation switch
        {
            "token" => "AttemptToken=$value",
            "accepted" or "cancelled" or "queued" => mutation is "queued" or "cancelled" ? "State=$value,AttemptToken=NULL" : "State=$value",
            "expired" => "ExpiresAtTicks=$value",
            "recipient-kind" => "RecipientKind=$value",
            "recipient-user" => "RecipientUser=$value",
            "recipient-authority" => "Authority=$value",
            "fingerprint" => "PayloadFingerprint=$value",
            "sender-snapshot" => "SenderSnapshot=$value",
            "missing-inbox" => "InboxId=$value",
            _ => "Id=$value"
        };
        object value = mutation switch
        {
            "token" => new string('f', 32),
            "accepted" => (int)SipSmsForwardState.Accepted,
            "cancelled" => (int)SipSmsForwardState.Cancelled,
            "queued" => (int)SipSmsForwardState.Queued,
            "expired" => DateTime.UtcNow.AddMinutes(-1).Ticks,
            "recipient-kind" => (int)SipSmsForwardRecipientKind.Peer,
            "recipient-user" => "1002",
            "recipient-authority" or "fingerprint" or "intent-id" => new string('d', 64),
            "sender-snapshot" => "+15550009999",
            _ => "absent-synthetic-inbox"
        };
        await f.ExecuteAsync("UPDATE SipSmsForwards SET " + set + " WHERE Id=$id", ("$value", value), ("$id", f.Claim.Id));
        var before = await f.IntentSnapshotAsync();
        Assert.False(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
        Assert.Equal(before, await f.IntentSnapshotAsync());
        // Do not call GetPending here: it intentionally prunes expired rows.
        // The guard itself must preserve even expired or externally changed evidence.
    }

    [Theory]
    [InlineData("id")]
    [InlineData("token")]
    [InlineData("recipient")]
    [InlineData("fingerprint")]
    [InlineData("payload")]
    [InlineData("inbox-id")]
    [InlineData("missing-inbox-id")]
    public async Task CallerCannotForgeAClaimOrItsInboxBinding(string mutation)
    {
        await using var f = await GuardFixture.CreateAsync();
        var forged = mutation switch
        {
            "id" => f.Claim with { Id = new('d', 64) },
            "token" => f.Claim with { AttemptToken = new('e', 32) },
            "recipient" => f.Claim with { Recipient = f.Claim.Recipient with { User = "1002" } },
            "fingerprint" => f.Claim with { PayloadFingerprint = new('f', 64) },
            "payload" => f.Claim with { Payload = f.Claim.Payload with { Text = "forged synthetic body" } },
            "inbox-id" => f.Claim with { InboxId = "another-inbox-row" },
            _ => f.Claim with { InboxId = null }
        };
        var before = await f.IntentSnapshotAsync();
        Assert.False(await f.Store.IsClaimSourceCurrentAsync(forged, CancellationToken.None));
        Assert.Equal(before, await f.IntentSnapshotAsync());
        Assert.True(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
        await f.RequireUnknownAsync();
    }

    [Theory]
    [InlineData(SmsDeliveryState.Delivered)]
    [InlineData(SmsDeliveryState.Failed)]
    public async Task DisplayTimestampAndDeliveryStatusDoNotChangeTheSourceGuardOrItsOriginalExpiry(SmsDeliveryState state)
    {
        await using var f = await GuardFixture.CreateAsync();
        var before = await f.IntentSnapshotAsync();
        f.Message.Timestamp = DateTime.UtcNow.AddYears(-1);
        f.Message.DeliveryState = state;
        f.Message.DeliveryStatus = "synthetic display-only status";
        await f.OtherDatabase.SaveSmsMessageAsync(f.Message);
        Assert.True(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
        Assert.Equal(before, await f.IntentSnapshotAsync());
        await f.RequireUnknownAsync();
    }

    [Fact]
    public async Task CancelledGuardDoesNotReviveOrRenewUnknownAndLaterHealthyReadStillWorks()
    {
        await using var f = await GuardFixture.CreateAsync();
        var before = await f.IntentSnapshotAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.IsClaimSourceCurrentAsync(f.Claim, cancellation.Token));
        Assert.Equal(before, await f.IntentSnapshotAsync());
        await f.RequireUnknownAsync();
        Assert.True(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
    }

    [Fact]
    public async Task ActualSqlReadFailureDoesNotReviveOrRenewUnknownAndRestoringSchemaRestoresGuard()
    {
        await using var f = await GuardFixture.CreateAsync();
        var before = await f.IntentSnapshotAsync();
        await f.ExecuteAsync("ALTER TABLE SmsMessages RENAME TO SyntheticGuardUnavailableInbox");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
            Assert.Equal(before, await f.IntentSnapshotAsync());
            await f.RequireUnknownAsync();
        }
        finally { await f.ExecuteAsync("ALTER TABLE SyntheticGuardUnavailableInbox RENAME TO SmsMessages"); }
        Assert.True(await f.Store.IsClaimSourceCurrentAsync(f.Claim, CancellationToken.None));
        Assert.Equal(before, await f.IntentSnapshotAsync());
    }

    private static SmsMessageModel NewMessage(string source = "original-network-source") => new()
    {
        SlotId = "original-slot", SourceMessageId = source, SimBinding = Binding,
        SenderOrRecipient = "+15551234567", Text = "synthetic original guard body",
        Timestamp = DateTime.UtcNow, IsOutgoing = false, DeliveryState = SmsDeliveryState.Received
    };

    private sealed class GuardFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _path;
        public PreferenceDatabaseService Database { get; }
        public PreferenceDatabaseService OtherDatabase { get; }
        public ISipSmsForwardStore Store => Database;
        public SmsMessageModel Message { get; }
        public SipSmsForwardClaim Claim { get; }

        private GuardFixture(string directory, string path, PreferenceDatabaseService database,
            PreferenceDatabaseService other, SmsMessageModel message, SipSmsForwardClaim claim) =>
            (_directory, _path, Database, OtherDatabase, Message, Claim) = (directory, path, database, other, message, claim);

        public static async Task<GuardFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "vowin-mt-source-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "preferences.db");
                var database = new PreferenceDatabaseService(path); await database.InitializeAsync();
                var other = new PreferenceDatabaseService(path); await other.InitializeAsync();
                var message = NewMessage();
                var result = await database.SaveIncomingSmsWithForwardsAsync(message,
                    [new(SipSmsForwardRecipientKind.Local, "1001", Authority)]);
                Assert.True(result.IsNew); Assert.Equal(1, result.QueuedRecipients);
                var store = (ISipSmsForwardStore)database;
                var item = Assert.Single(await store.GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None));
                var claim = Assert.IsType<SipSmsForwardClaim>(await store.TryClaimAsync(item.Id, item.PayloadFingerprint,
                    DateTimeOffset.UtcNow, CancellationToken.None));
                return new(directory, path, database, other, message, claim);
            }
            catch { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); throw; }
        }

        public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand(sql, connection);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> IntentSnapshotAsync()
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand("""
                SELECT hex(Id)||'/'||hex(InboxId)||'/'||RecipientKind||'/'||hex(RecipientUser)||'/'||hex(Authority)
                    ||'/'||hex(PayloadFingerprint)||'/'||hex(SenderSnapshot)||'/'||ExpiresAtTicks||'/'||State||'/'||hex(AttemptToken)
                FROM SipSmsForwards
                """, connection);
            return Assert.IsType<string>(await command.ExecuteScalarAsync());
        }

        public async Task RequireUnknownAsync()
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand("SELECT State FROM SipSmsForwards WHERE Id=$id", connection);
            command.Parameters.AddWithValue("$id", Claim.Id);
            Assert.Equal((long)SipSmsForwardState.Unknown, Assert.IsType<long>(await command.ExecuteScalarAsync()));
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM SipSmsForwards WHERE State=0";
            Assert.Equal(0L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
        }

        private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
            { DataSource = _path, Cache = SqliteCacheMode.Private }.ToString());

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools(); Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
