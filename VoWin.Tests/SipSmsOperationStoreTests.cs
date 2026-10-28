using Microsoft.Data.Sqlite;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class SipSmsOperationStoreTests
{
    private static readonly string Id = new('a', 64), Fingerprint = new('b', 64);
    private static string DatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-sip-sms-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return Path.Combine(directory, "preferences.db");
    }
    private static async Task Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync(); using var command = new SqliteCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(SipSmsOperationOutcome.Unknown)]
    [InlineData(SipSmsOperationOutcome.Submitted)]
    [InlineData(SipSmsOperationOutcome.PartiallySubmitted)]
    [InlineData(SipSmsOperationOutcome.Failed)]
    public async Task UnknownIsDurableBeforeTransportAndOutcomeSurvivesNewStore(SipSmsOperationOutcome outcome)
    {
        var path = DatabasePath(); var first = new PreferenceDatabaseService(path);
        var reservation = await first.ReserveAsync(Id, Fingerprint, default);
        Assert.True(reservation.IsNew && reservation.ContentMatches); Assert.Equal(32, reservation.ReservationToken!.Length);
        var reopened = new PreferenceDatabaseService(path);
        Assert.Equal(new(false, true, SipSmsOperationOutcome.Unknown), await reopened.ReserveAsync(Id, Fingerprint, default));
        await reopened.CompleteAsync(Id, Fingerprint, reservation.ReservationToken!, outcome, default);
        Assert.Equal(new(false, true, outcome), await new PreferenceDatabaseService(path).ReserveAsync(Id, Fingerprint, default));
        Assert.Equal(new(false, false, outcome), await first.ReserveAsync(Id, new string('c', 64), default));
    }

    [Fact]
    public async Task IndependentDatabaseInstancesHaveOnlyOneAtomicAdmission()
    {
        var path = DatabasePath();
        var stores = Enumerable.Range(0, 8).Select(_ => new PreferenceDatabaseService(path)).ToArray();
        foreach (var store in stores) await store.InitializeAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = stores.Select(store => Task.Run(async () => { await start.Task; return await store.ReserveAsync(Id, Fingerprint, default); })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(results, result => result.IsNew);
        Assert.All(results, result => { Assert.True(result.ContentMatches); Assert.Equal(SipSmsOperationOutcome.Unknown, result.Outcome); });
    }

    [Fact]
    public async Task CompletionCannotInventOrOverwriteAReservedDestinationOrKnownOutcome()
    {
        var store = new PreferenceDatabaseService(DatabasePath());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(Id, Fingerprint, new string('a', 32), SipSmsOperationOutcome.Submitted, default));
        var token = (await store.ReserveAsync(Id, Fingerprint, default)).ReservationToken!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(Id, new string('c', 64), token, SipSmsOperationOutcome.Submitted, default));
        await store.CompleteAsync(Id, Fingerprint, token, SipSmsOperationOutcome.Submitted, default);
        await store.CompleteAsync(Id, Fingerprint, token, SipSmsOperationOutcome.Submitted, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(Id, Fingerprint, token, SipSmsOperationOutcome.Failed, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CompleteAsync(Id, Fingerprint, token, (SipSmsOperationOutcome)40, default));
        Assert.Equal(SipSmsOperationOutcome.Submitted, (await store.ReserveAsync(Id, Fingerprint, default)).Outcome);
    }

    [Theory]
    [InlineData("Fingerprint='private text'")]
    [InlineData("Outcome=77")]
    [InlineData("ExpiresAtTicks=0")]
    [InlineData("ExpiresAtTicks=9223372036854775807")]
    [InlineData("ReservationToken='bad-token'")]
    public async Task CorruptUnrelatedRecordFailsClosedRatherThanStartingAnEmptyJournal(string change)
    {
        var path = DatabasePath(); var store = new PreferenceDatabaseService(path);
        await store.ReserveAsync(Id, Fingerprint, default);
        await Execute(path, "UPDATE SipSmsOperations SET " + change);
        await Assert.ThrowsAnyAsync<Exception>(() => new PreferenceDatabaseService(path).ReserveAsync(new string('c', 64), Fingerprint, default));
        using var connection = new SqliteConnection("Data Source=" + path); await connection.OpenAsync();
        using var command = new SqliteCommand("SELECT COUNT(*) FROM SipSmsOperations", connection);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task CapacityFailsClosedAndOnlyExpiredEntriesMayBeReclaimed()
    {
        var path = DatabasePath(); var store = new PreferenceDatabaseService(path);
        await store.ReserveAsync(Id, Fingerprint, default);
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync(); using var transaction = connection.BeginTransaction();
            for (var i = 0; i < 2047; i++)
            {
                using var insert = new SqliteCommand("INSERT INTO SipSmsOperations VALUES($id,$fingerprint,0,$until,$token)", connection, transaction);
                insert.Parameters.AddWithValue("$id", i.ToString("x64")); insert.Parameters.AddWithValue("$fingerprint", Fingerprint);
                insert.Parameters.AddWithValue("$until", DateTime.UtcNow.AddHours(24).Ticks);
                insert.Parameters.AddWithValue("$token", Guid.NewGuid().ToString("N"));
                await insert.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        var next = new string('c', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAsync(next, Fingerprint, default));
        Assert.False((await store.ReserveAsync(Id, Fingerprint, default)).IsNew);
        await Execute(path, "UPDATE SipSmsOperations SET ExpiresAtTicks=1 WHERE OperationId='" + Id + "'");
        Assert.True((await store.ReserveAsync(next, Fingerprint, default)).IsNew);
    }

    [Fact]
    public async Task CancelledOrInvalidAdmissionCannotCreateAReservation()
    {
        var path = DatabasePath(); var store = new PreferenceDatabaseService(path);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReserveAsync(Id, Fingerprint, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReserveAsync("raw-private-phone", Fingerprint, default));
        Assert.True((await store.ReserveAsync(Id, Fingerprint, default)).IsNew);
    }

    [Fact]
    public async Task ExpiredReservationCannotCompleteTheSuccessorWithIdenticalContent()
    {
        var path = DatabasePath(); var store = new PreferenceDatabaseService(path);
        var old = await store.ReserveAsync(Id, Fingerprint, default);
        await Execute(path, "UPDATE SipSmsOperations SET ExpiresAtTicks=1");
        var next = await new PreferenceDatabaseService(path).ReserveAsync(Id, Fingerprint, default);
        Assert.True(next.IsNew); Assert.NotEqual(old.ReservationToken, next.ReservationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(Id, Fingerprint, old.ReservationToken!, SipSmsOperationOutcome.Submitted, default));
        Assert.Equal(SipSmsOperationOutcome.Unknown, (await store.ReserveAsync(Id, Fingerprint, default)).Outcome);
        await store.CompleteAsync(Id, Fingerprint, next.ReservationToken!, SipSmsOperationOutcome.Submitted, default);
    }

    [Fact]
    public async Task RealDatabaseWriteLockCannotGrantAdmissionOrEraseUnknownCompletion()
    {
        var path = DatabasePath(); var store = new PreferenceDatabaseService(path);
        var reserved = await store.ReserveAsync(Id, Fingerprint, default);
        using (var locker = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Cache = SqliteCacheMode.Private }.ToString()))
        {
            await locker.OpenAsync(); using var held = locker.BeginTransaction(deferred: false);
            var reserveError = await Assert.ThrowsAsync<SqliteException>(() => Task.Run(() => store.ReserveAsync(new string('c', 64), Fingerprint, default)));
            Assert.Contains(reserveError.SqliteErrorCode, new[] { 5, 6 });
            var completeError = await Assert.ThrowsAsync<SqliteException>(() => Task.Run(() => store.CompleteAsync(Id, Fingerprint, reserved.ReservationToken!, SipSmsOperationOutcome.Submitted, default)));
            Assert.Contains(completeError.SqliteErrorCode, new[] { 5, 6 });
        }
        Assert.Equal(SipSmsOperationOutcome.Unknown, (await store.ReserveAsync(Id, Fingerprint, default)).Outcome);
        Assert.True((await store.ReserveAsync(new string('c', 64), Fingerprint, default)).IsNew);
    }

    [Fact]
    public async Task InMemoryDatabaseCannotPretendToProvideDurableAdmission()
    {
        var store = new PreferenceDatabaseService(":memory:");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAsync(Id, Fingerprint, default));
    }

    [Fact]
    public async Task DesktopSIPStartupInjectsThePersistentDatabaseStoreWithoutChangingSavedConfiguration()
    {
        var path = DatabasePath(); var preferences = new PreferenceDatabaseService(path);
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port; reservation.Dispose();
        var options = new SipGatewayOptions
        {
            BindAddress = IPAddress.Loopback, SipPort = port,
            Accounts = new() { ["lab"] = "synthetic-only-password" },
            MessagesEnabled = true, AllowOutgoingSms = true,
            RequireConfiguredLines = true
        };
        await using var service = new VoKernelService(preferences, new(Path.Combine(Path.GetDirectoryName(path)!, "telephony.json")),
            Path.Combine(Path.GetDirectoryName(path)!, "diagnostic.log"));
        await service.StartSipGatewayAsync(options);
        var active = service.Kernel.LocalSipGateway!;
        var runtime = (SipGatewayOptions)typeof(SipGateway).GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(active)!;
        Assert.Same(preferences, runtime.SmsOperationStore); Assert.Null(options.SmsOperationStore);
        Assert.Empty(service.Kernel.Pool.Slots);
        await service.StopSipGatewayAsync();
    }

    [Fact]
    public async Task LedgerStoresOnlyHashesAndOutcomeAndDoesNotExtendExpiryOnReplayOrCompletion()
    {
        var path = DatabasePath(); var store = new PreferenceDatabaseService(path);
        var started = DateTime.UtcNow;
        var token = (await store.ReserveAsync(Id, Fingerprint, default)).ReservationToken!;
        async Task<long> Expiry()
        {
            using var connection = new SqliteConnection("Data Source=" + path); await connection.OpenAsync();
            using var command = new SqliteCommand("SELECT ExpiresAtTicks FROM SipSmsOperations", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }
        var expiry = await Expiry(); Assert.InRange(expiry, started.AddHours(24).Ticks, DateTime.UtcNow.AddHours(24).Ticks);
        await store.CompleteAsync(Id, Fingerprint, token, SipSmsOperationOutcome.Submitted, default);
        await new PreferenceDatabaseService(path).ReserveAsync(Id, Fingerprint, default);
        Assert.Equal(expiry, await Expiry());
        using var connection = new SqliteConnection("Data Source=" + path); await connection.OpenAsync();
        using var columns = new SqliteCommand("PRAGMA table_info(SipSmsOperations)", connection);
        using var reader = await columns.ExecuteReaderAsync(); var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        Assert.Equal(new[] { "OperationId", "Fingerprint", "Outcome", "ExpiresAtTicks", "ReservationToken" }, names);
    }
}
