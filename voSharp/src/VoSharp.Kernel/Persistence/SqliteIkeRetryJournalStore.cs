using System.Text;
using Microsoft.Data.Sqlite;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel.Persistence;

/// <summary>
/// File-backed, exact-byte CAS journal shared by all Windows application owners.
/// Construction performs no IO; failed reads or commits are never an empty store.
/// </summary>
public sealed class SqliteIkeRetryJournalStore : IIkeRetryJournalStore
{
    private readonly string _connectionString;
    private readonly string _directory;

    public SqliteIkeRetryJournalStore(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode is SqliteOpenMode.Memory or SqliteOpenMode.ReadOnly ||
            string.IsNullOrWhiteSpace(builder.DataSource) ||
            builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("IKE retry admission requires a writable file-backed journal.", nameof(connectionString));
        builder.DataSource = Path.GetFullPath(builder.DataSource);
        _directory = Path.GetDirectoryName(builder.DataSource)!;
        builder.Cache = SqliteCacheMode.Private;
        builder.Pooling = false;
        builder.DefaultTimeout = 2;
        _connectionString = builder.ToString();
    }

    public async Task<string?> LoadAsync(CancellationToken ct)
    {
        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureTableAsync(connection, transaction, ct).ConfigureAwait(false);
        var value = await ReadAsync(connection, transaction, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        return value;
    }

    public async Task<bool> CompareExchangeAsync(string? expected, string replacement, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(replacement) || Encoding.UTF8.GetByteCount(replacement) > IkeRetryGate.MaximumBytes)
            throw new ArgumentException("Invalid IKE private journal length.", nameof(replacement));
        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureTableAsync(connection, transaction, ct).ConfigureAwait(false);
        var current = await ReadAsync(connection, transaction, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(expected, current, StringComparison.Ordinal))
        {
            transaction.Commit();
            return false;
        }
        using var command = new SqliteCommand(
            "INSERT INTO IkeRetryPrivateJournal(Id,Body) VALUES(1,$body) ON CONFLICT(Id) DO UPDATE SET Body=excluded.Body", connection, transaction);
        command.Parameters.AddWithValue("$body", replacement);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("IKE private journal write was not confirmed.");
        ct.ThrowIfCancellationRequested();
        // Once Commit succeeds, cancellation must not invent an unknown result.
        // The caller rechecks its lifecycle before sending any network packet.
        transaction.Commit();
        return true;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var set = new SqliteCommand("PRAGMA synchronous=FULL", connection);
            await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            using var read = new SqliteCommand("PRAGMA synchronous", connection);
            if (Convert.ToInt64(await read.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 2)
                throw new InvalidOperationException("IKE private journal requires confirmed FULL synchronous mode.");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task EnsureTableAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS IkeRetryPrivateJournal (Id INTEGER PRIMARY KEY CHECK(Id=1),Body TEXT NOT NULL CHECK(typeof(Body)='text' AND length(CAST(Body AS BLOB)) BETWEEN 1 AND 1048576))", connection, transaction);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        // Do not materialize oversized text or hide a damaged row with WHERE.
        using var command = new SqliteCommand(
            "SELECT Id,typeof(Body),length(CAST(Body AS BLOB)),CASE WHEN length(CAST(Body AS BLOB)) BETWEEN 1 AND 1048576 THEN Body END FROM IkeRetryPrivateJournal", connection, transaction);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        if (reader.GetInt64(0) != 1 || reader.GetString(1) != "text" || reader.IsDBNull(2) ||
            reader.GetInt64(2) is < 1 or > IkeRetryGate.MaximumBytes || reader.IsDBNull(3))
            throw new InvalidDataException("IKE private journal contains an invalid row.");
        var value = reader.GetString(3);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidDataException("IKE private journal contains multiple rows.");
        return value;
    }
}
