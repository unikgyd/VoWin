using Microsoft.Data.Sqlite;
using VoSharp.Kernel.SipGateway;

namespace VoWin.Services;

public partial class PreferenceDatabaseService : ISipSmsOperationStore
{
    private const int SipSmsOperationLimit = 2048;
    private sealed record StoredSipSmsOperation(string Fingerprint, SipSmsOperationOutcome Outcome, long ExpiresAtTicks, string Token);

    public async Task<SipSmsOperationReservation> ReserveAsync(string operationId, string fingerprint, CancellationToken ct)
    {
        ValidateSipSmsHash(operationId); ValidateSipSmsHash(fingerprint);
        await InitializeAsync().ConfigureAwait(false);
        using var connection = NewSipSmsConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ConfigureSipSmsDurabilityAsync(connection, ct).ConfigureAwait(false);
        // The SQLite write transaction also serializes two application/store
        // instances. A per-object semaphore alone is not durable admission.
        using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureSipSmsTableAsync(connection, transaction, ct).ConfigureAwait(false);
        var entries = await ReadSipSmsOperationsAsync(connection, transaction, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow.Ticks;
        using (var prune = new SqliteCommand("DELETE FROM SipSmsOperations WHERE ExpiresAtTicks <= $now", connection, transaction))
        {
            prune.Parameters.AddWithValue("$now", now);
            await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (entries.TryGetValue(operationId, out var previous) && previous.ExpiresAtTicks > now)
        {
            transaction.Commit();
            return new(false, previous.Fingerprint == fingerprint, previous.Outcome);
        }
        if (entries.Count(entry => entry.Value.ExpiresAtTicks > now) >= SipSmsOperationLimit)
            throw new InvalidOperationException("SIP SMS operation store capacity reached.");
        var reservationToken = Guid.NewGuid().ToString("N");
        using (var insert = new SqliteCommand(
            "INSERT INTO SipSmsOperations(OperationId,Fingerprint,Outcome,ExpiresAtTicks,ReservationToken) VALUES($id,$fingerprint,$outcome,$until,$token)", connection, transaction))
        {
            insert.Parameters.AddWithValue("$id", operationId);
            insert.Parameters.AddWithValue("$fingerprint", fingerprint);
            insert.Parameters.AddWithValue("$outcome", (int)SipSmsOperationOutcome.Unknown);
            insert.Parameters.AddWithValue("$until", new DateTime(now, DateTimeKind.Utc).AddHours(24).Ticks);
            insert.Parameters.AddWithValue("$token", reservationToken);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        // Flush the Unknown reservation before allowing any transport call.
        transaction.Commit();
        return new(true, true, SipSmsOperationOutcome.Unknown, reservationToken);
    }

    public async Task CompleteAsync(string operationId, string fingerprint, string reservationToken, SipSmsOperationOutcome outcome, CancellationToken ct)
    {
        ValidateSipSmsHash(operationId); ValidateSipSmsHash(fingerprint);
        ValidateSipSmsToken(reservationToken);
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        await InitializeAsync().ConfigureAwait(false);
        using var connection = NewSipSmsConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ConfigureSipSmsDurabilityAsync(connection, ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureSipSmsTableAsync(connection, transaction, ct).ConfigureAwait(false);
        var entries = await ReadSipSmsOperationsAsync(connection, transaction, ct).ConfigureAwait(false);
        if (!entries.TryGetValue(operationId, out var previous) || previous.Fingerprint != fingerprint || previous.Token != reservationToken ||
            previous.Outcome != SipSmsOperationOutcome.Unknown && previous.Outcome != outcome)
            throw new InvalidOperationException("SIP SMS completion has no matching reservation.");
        using var update = new SqliteCommand(
            "UPDATE SipSmsOperations SET Outcome=$outcome WHERE OperationId=$id AND Fingerprint=$fingerprint AND ReservationToken=$token", connection, transaction);
        update.Parameters.AddWithValue("$outcome", (int)outcome);
        update.Parameters.AddWithValue("$id", operationId);
        update.Parameters.AddWithValue("$fingerprint", fingerprint);
        update.Parameters.AddWithValue("$token", reservationToken);
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("SIP SMS reservation changed.");
        transaction.Commit();
    }

    private static async Task EnsureSipSmsTableAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS SipSmsOperations (
                OperationId TEXT NOT NULL PRIMARY KEY,
                Fingerprint TEXT NOT NULL,
                Outcome INTEGER NOT NULL,
                ExpiresAtTicks INTEGER NOT NULL,
                ReservationToken TEXT NOT NULL
            );", connection, transaction);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private SqliteConnection NewSipSmsConnection()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || string.IsNullOrWhiteSpace(builder.DataSource) ||
            builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Paid SIP SMS requires a file-backed operation store.");
        // Shared-cache schema locks can interfere with preparing another
        // connection's statements. Keep this bounded journal connection private;
        // the database's actual write transaction still serializes all writers.
        builder.Cache = SqliteCacheMode.Private;
        builder.DefaultTimeout = 2;
        return new(builder.ToString());
    }

    private static async Task ConfigureSipSmsDurabilityAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = new SqliteCommand("PRAGMA synchronous=FULL", connection);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, StoredSipSmsOperation>> ReadSipSmsOperationsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = new SqliteCommand("SELECT OperationId,Fingerprint,Outcome,ExpiresAtTicks,ReservationToken FROM SipSmsOperations", connection, transaction);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var entries = new Dictionary<string, StoredSipSmsOperation>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (entries.Count >= SipSmsOperationLimit)
                throw new InvalidOperationException("SIP SMS operation store exceeds its limit.");
            var id = reader.GetString(0); var fingerprint = reader.GetString(1);
            ValidateSipSmsHash(id); ValidateSipSmsHash(fingerprint);
            var rawOutcome = reader.GetInt64(2); var until = reader.GetInt64(3);
            var token = reader.GetString(4); ValidateSipSmsToken(token);
            if (rawOutcome is < 0 or > 3 || until <= 0 || until > DateTime.MaxValue.Ticks)
                throw new InvalidOperationException("SIP SMS operation store contains invalid entries.");
            entries.Add(id, new(fingerprint, (SipSmsOperationOutcome)rawOutcome, until, token));
        }
        return entries;
    }

    private static void ValidateSipSmsHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("SIP SMS identifiers must be SHA-256 hex digests.");
    }

    private static void ValidateSipSmsToken(string value)
    {
        if (value is null || value.Length != 32 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("SIP SMS reservation token is invalid.");
    }
}
