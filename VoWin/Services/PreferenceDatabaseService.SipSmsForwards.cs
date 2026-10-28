using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel.SipGateway;
using VoSharp.Telephony.Sms;
using VoWin.Models;

namespace VoWin.Services;

public partial class PreferenceDatabaseService : ISipSmsForwardStore
{
    private const int SipSmsForwardLimit = 2048;
    private const int SipSmsReceiptLimit = 32768;
    private sealed record StoredSmsForward(string Id, string InboxId, SipSmsForwardRecipient Recipient,
        string Fingerprint, string Sender, string? ReplyNumber, long Expires, SipSmsForwardState State, string? Token);

    private static async Task EnsureSipSmsForwardTableAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        // Table creation and historical receipt seeding share the caller's
        // immediate writer transaction. A second initializer must never see a
        // newly created but empty ledger and admit old content for a new user.
        bool receiptsExisted;
        using (var previous = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SipSmsForwardReceipts' COLLATE NOCASE", connection, transaction))
            receiptsExisted = Convert.ToInt64(await previous.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0;
        // No cascading foreign key: deletion must not erase an uncertain attempt
        // and let the same original source acquire another transmission later.
        using var command = new SqliteCommand("""
            CREATE TABLE IF NOT EXISTS SipSmsForwards (
                Id TEXT NOT NULL PRIMARY KEY,
                InboxId TEXT NOT NULL,
                RecipientKind INTEGER NOT NULL CHECK(RecipientKind IN (0,1)),
                RecipientUser TEXT NOT NULL,
                Authority TEXT NOT NULL,
                PayloadFingerprint TEXT NOT NULL,
                SenderSnapshot TEXT NOT NULL,
                ExpiresAtTicks INTEGER NOT NULL,
                State INTEGER NOT NULL CHECK(State IN (0,1,2,3)),
                AttemptToken TEXT,
                ReplyNumberSnapshot TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_SipSmsForwards_Pending ON SipSmsForwards(State,ExpiresAtTicks);
            CREATE TABLE IF NOT EXISTS SipSmsForwardReceipts (
                SourceKey TEXT NOT NULL PRIMARY KEY,
                ExpiresAtTicks INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS SipSmsForwardReceiptGuard (
                Id INTEGER NOT NULL PRIMARY KEY CHECK(Id=1),
                UntilTicks INTEGER NOT NULL
            );
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // Same writer transaction as schema discovery: independent initializers
        // cannot race ALTER. NULL is legacy, never a country-policy backfill.
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = new SqliteCommand("PRAGMA table_info(SipSmsForwards)", connection, transaction))
        using (var reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) columns.Add(reader.GetString(1));
        if (!columns.Contains("ReplyNumberSnapshot"))
        {
            using var alter = new SqliteCommand("ALTER TABLE SipSmsForwards ADD COLUMN ReplyNumberSnapshot TEXT", connection, transaction);
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (!receiptsExisted)
            await SeedHistoricalSmsForwardReceiptsAsync(connection, transaction, ct).ConfigureAwait(false);
    }

    private static async Task SeedHistoricalSmsForwardReceiptsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        // This is a one-time empty-plan migration, not historical forwarding.
        // Read only recorded source metadata: never read bodies, infer a SIM,
        // create an intent, or use the (possibly very old) SMS timestamp as TTL.
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var overflow = false;
        using (var select = new SqliteCommand(
            "SELECT Id,SlotId,SimBinding,SourceMessageId FROM SmsMessages WHERE IsOutgoing=0 ORDER BY Id", connection, transaction))
        using (var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var source = SmsForwardSourceKey(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
                if (sources.Contains(source)) continue;
                if (sources.Count == SipSmsReceiptLimit) { overflow = true; break; }
                sources.Add(source);
            }
        var until = DateTimeOffset.UtcNow.AddHours(24).UtcTicks;
        using (var insert = new SqliteCommand(
            "INSERT INTO SipSmsForwardReceipts(SourceKey,ExpiresAtTicks) VALUES($source,$until)", connection, transaction))
        {
            var sourceParameter = insert.Parameters.Add("$source", SqliteType.Text);
            insert.Parameters.AddWithValue("$until", until);
            foreach (var source in sources)
            {
                sourceParameter.Value = source;
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        if (overflow)
        {
            // Not all historical sources fit. Freeing a ledger slot must not
            // authorize one we could not remember during this upgrade.
            using var guard = new SqliteCommand("""
                INSERT INTO SipSmsForwardReceiptGuard(Id,UntilTicks) VALUES(1,$until)
                ON CONFLICT(Id) DO UPDATE SET UntilTicks=MAX(UntilTicks,excluded.UntilTicks)
                """, connection, transaction);
            guard.Parameters.AddWithValue("$until", until);
            await guard.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static string SmsForwardSourceKey(SmsMessageModel message) =>
        SmsForwardSourceKey(message.Id, message.SlotId, message.SimBinding, message.SourceMessageId);

    private static string SmsForwardSourceKey(string id, string? slot, string? binding, string? source)
    {
        var fields = new[] { "sip-sms-receipt-v1", slot?.ToLowerInvariant() ?? "", binding ?? "",
            string.IsNullOrEmpty(source) ? "row:" + id : "source:" + source };
        var framed = string.Concat(fields.Select(value => Encoding.UTF8.GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(framed))).ToLowerInvariant();
    }

    private static string SmsForwardIntentId(string source, SipSmsForwardRecipient recipient) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sip-sms-forward-v1\0" + source + "\0" +
            recipient.Kind + "\0" + recipient.User.ToLowerInvariant() + "\0" + recipient.Authority))).ToLowerInvariant();

    private static async Task<(bool First, bool CapacityHold)> RememberSmsForwardReceiptAsync(SqliteConnection connection,
        SqliteTransaction transaction, SmsMessageModel message, CancellationToken ct)
    {
        // Receipt history is independent from Inbox and per-recipient intents.
        // Even an empty/unsupported original plan cannot be backfilled after a
        // user deletes the Inbox row and the network repeats the same source.
        var now = DateTimeOffset.UtcNow;
        var source = SmsForwardSourceKey(message);
        await ValidateSmsForwardReceiptHistoryAsync(connection, transaction, ct).ConfigureAwait(false);
        using (var prune = new SqliteCommand("DELETE FROM SipSmsForwardReceipts WHERE ExpiresAtTicks<=$now", connection, transaction))
        {
            prune.Parameters.AddWithValue("$now", now.UtcTicks); await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        using (var previous = new SqliteCommand("SELECT ExpiresAtTicks FROM SipSmsForwardReceipts WHERE SourceKey=$source", connection, transaction))
        {
            previous.Parameters.AddWithValue("$source", source);
            if (await previous.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return (false, false);
        }
        long hold = 0;
        using (var guard = new SqliteCommand("SELECT UntilTicks FROM SipSmsForwardReceiptGuard WHERE Id=1", connection, transaction))
        {
            var value = await guard.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value != null) hold = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            if (hold < 0 || hold > DateTime.MaxValue.Ticks) throw new InvalidOperationException("Invalid SMS receipt quarantine.");
        }
        long count;
        using (var capacity = new SqliteCommand("SELECT COUNT(*) FROM SipSmsForwardReceipts", connection, transaction))
            count = Convert.ToInt64(await capacity.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (count > SipSmsReceiptLimit) throw new InvalidOperationException("SMS source receipt store exceeds its capacity.");
        if (count == SipSmsReceiptLimit)
        {
            // A bounded ledger must not forget an unrecorded source and later
            // leak it to a new account. Persist a conservative quarantine for
            // the whole 24h uncertainty window; local inbox storage still works.
            using var guard = new SqliteCommand("""
                INSERT INTO SipSmsForwardReceiptGuard(Id,UntilTicks) VALUES(1,$until)
                ON CONFLICT(Id) DO UPDATE SET UntilTicks=MAX(UntilTicks,excluded.UntilTicks)
                """, connection, transaction);
            guard.Parameters.AddWithValue("$until", now.AddHours(24).UtcTicks);
            await guard.ExecuteNonQueryAsync(ct).ConfigureAwait(false); return (false, true);
        }
        using (var insert = new SqliteCommand("INSERT INTO SipSmsForwardReceipts(SourceKey,ExpiresAtTicks) VALUES($source,$until)", connection, transaction))
        {
            insert.Parameters.AddWithValue("$source", source); insert.Parameters.AddWithValue("$until", now.AddHours(24).UtcTicks);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return (hold <= now.UtcTicks, hold > now.UtcTicks);
    }

    private static async Task ValidateSmsForwardReceiptHistoryAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        // Validate before pruning: a corrupt expiry must not be interpreted as
        // forgotten history and authorize old content for a newer recipient.
        using var command = new SqliteCommand("""
            SELECT
              (SELECT COUNT(*) FROM SipSmsForwardReceipts),
              (SELECT COUNT(*) FROM SipSmsForwardReceipts WHERE typeof(SourceKey)<>'text' OR length(SourceKey)<>64
                OR SourceKey GLOB '*[^a-f0-9]*' OR typeof(ExpiresAtTicks)<>'integer' OR ExpiresAtTicks<=0 OR ExpiresAtTicks>$max),
              (SELECT COUNT(*) FROM SipSmsForwardReceiptGuard),
              (SELECT COUNT(*) FROM SipSmsForwardReceiptGuard WHERE Id<>1 OR typeof(UntilTicks)<>'integer' OR UntilTicks<0 OR UntilTicks>$max)
            """, connection, transaction);
        command.Parameters.AddWithValue("$max", DateTime.MaxValue.Ticks);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(0) > SipSmsReceiptLimit || reader.GetInt64(1) != 0 ||
            reader.GetInt64(2) > 1 || reader.GetInt64(3) != 0)
            throw new InvalidOperationException("SMS source receipt history is invalid; forwarding was not authorized.");
    }

    private static void ValidateForwardRecipients(SmsMessageModel message, IReadOnlyList<SipSmsForwardRecipient> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count > 65) throw new ArgumentException("Too many incoming SMS recipients.");
        if (recipients.Count == 0) return;
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in recipients)
        {
            ValidateForwardRecipient(recipient);
            if (!unique.Add(recipient.Kind + ":" + recipient.User)) throw new ArgumentException("Duplicate incoming SMS recipient.");
        }
    }

    private static bool CanForwardPayload(SmsMessageModel message)
    {
        if (message.Id.Length is 0 or > 128 || message.SlotId is not { Length: > 0 and <= 128 } ||
            message.SlotId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or ':' or '-')) ||
            message.SenderOrRecipient.Length is 0 or > 128 || message.Text.Length is 0 or > 16384 || message.Text.Contains('\0')) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(message.Text); _ = new UTF8Encoding(false, true).GetByteCount(message.SenderOrRecipient); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private static void ValidateForwardRecipient(SipSmsForwardRecipient recipient)
    {
        if (recipient is null || !Enum.IsDefined(recipient.Kind) || recipient.User is not { Length: > 0 and <= 64 } ||
            recipient.User.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '+' or '*' or '-')))
            throw new ArgumentException("Invalid incoming SMS recipient identity.");
        ValidateSipSmsHash(recipient.Authority);
    }

    private static async Task<(int, bool, bool)> EnqueueSmsForwardsAsync(SqliteConnection connection, SqliteTransaction transaction,
        SmsMessageModel message, IReadOnlyList<SipSmsForwardRecipient> recipients, CancellationToken ct)
    {
        if (recipients.Count == 0) return (0, false, false);
        if (!CanForwardPayload(message)) return (0, false, true); // Unsupported SIP payload is still a valid local inbox item.
        var now = DateTimeOffset.UtcNow;
        await EnsureSipSmsForwardTableAsync(connection, transaction, ct).ConfigureAwait(false);
        var rows = await ReadSmsForwardRowsAsync(connection, transaction, ct).ConfigureAwait(false);
        await PruneSmsForwardsAsync(connection, transaction, now.UtcTicks, ct).ConfigureAwait(false);
        var existing = rows.Where(row => row.Expires > now.UtcTicks).ToDictionary(row => row.Id);
        var payload = new SipSmsForwardPayload(message.SlotId!, message.SimBinding, message.SenderOrRecipient, message.Text)
            { ReplyNumber = message.SipReplyNumberSnapshot };
        var fingerprint = payload.Fingerprint();
        // A fresh UI row after deleting a message must not replay the same
        // original-source delivery. Unknown tombstones survive inbox deletion.
        var source = SmsForwardSourceKey(message);
        var pending = recipients.Select(recipient => (Recipient: recipient, Id: SmsForwardIntentId(source, recipient)))
            .Where(value => !existing.ContainsKey(value.Id)).ToArray();
        if (existing.Count + pending.Length > SipSmsForwardLimit) return (0, true, false); // Save inbox, never a partial recipient plan.
        foreach (var entry in pending)
        {
            using var insert = new SqliteCommand("""
                INSERT INTO SipSmsForwards(Id,InboxId,RecipientKind,RecipientUser,Authority,PayloadFingerprint,SenderSnapshot,ExpiresAtTicks,State,ReplyNumberSnapshot)
                VALUES($id,$inbox,$kind,$user,$authority,$fingerprint,$sender,$until,0,$reply)
                """, connection, transaction);
            insert.Parameters.AddWithValue("$id", entry.Id); insert.Parameters.AddWithValue("$inbox", message.Id);
            insert.Parameters.AddWithValue("$kind", (int)entry.Recipient.Kind); insert.Parameters.AddWithValue("$user", entry.Recipient.User);
            insert.Parameters.AddWithValue("$authority", entry.Recipient.Authority); insert.Parameters.AddWithValue("$fingerprint", fingerprint);
            insert.Parameters.AddWithValue("$sender", message.SenderOrRecipient); insert.Parameters.AddWithValue("$until", now.AddHours(24).UtcTicks);
            insert.Parameters.AddWithValue("$reply", (object?)message.SipReplyNumberSnapshot ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return (pending.Length, false, false);
    }

    public async Task<IReadOnlyList<SipSmsForwardItem>> GetPendingAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > SipSmsForwardLimit) throw new ArgumentOutOfRangeException(nameof(limit));
        await InitializeAsync().WaitAsync(ct).ConfigureAwait(false);
        using var connection = NewSipSmsConnection(); await connection.OpenAsync(ct).ConfigureAwait(false);
        await ConfigureSipSmsDurabilityAsync(connection, ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureSipSmsForwardTableAsync(connection, transaction, ct).ConfigureAwait(false);
        var rows = await ReadSmsForwardRowsAsync(connection, transaction, ct).ConfigureAwait(false);
        await PruneSmsForwardsAsync(connection, transaction, now.UtcTicks, ct).ConfigureAwait(false);
        var items = new List<SipSmsForwardItem>();
        foreach (var row in rows.Where(row => row.State == SipSmsForwardState.Queued && row.Expires > now.UtcTicks).OrderBy(row => row.Expires))
        {
            if (items.Count == limit) break;
            var payload = await ReadSmsForwardPayloadAsync(connection, transaction, row, ct).ConfigureAwait(false);
            if (payload == null) { await CancelSmsForwardRowAsync(connection, transaction, row.Id, ct).ConfigureAwait(false); continue; }
            items.Add(new(row.Id, row.Recipient, payload, row.Fingerprint, new DateTimeOffset(row.Expires, TimeSpan.Zero)));
        }
        transaction.Commit();
        return items;
    }

    public async Task<SipSmsForwardClaim?> TryClaimAsync(string id, string expectedFingerprint, DateTimeOffset now, CancellationToken ct)
    {
        ValidateSipSmsHash(id); ValidateSipSmsHash(expectedFingerprint);
        await InitializeAsync().WaitAsync(ct).ConfigureAwait(false);
        using var connection = NewSipSmsConnection(); await connection.OpenAsync(ct).ConfigureAwait(false);
        await ConfigureSipSmsDurabilityAsync(connection, ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureSipSmsForwardTableAsync(connection, transaction, ct).ConfigureAwait(false);
        var row = (await ReadSmsForwardRowsAsync(connection, transaction, ct).ConfigureAwait(false)).FirstOrDefault(value => value.Id == id);
        if (row == null || row.State != SipSmsForwardState.Queued) return null;
        var payload = await ReadSmsForwardPayloadAsync(connection, transaction, row, ct).ConfigureAwait(false);
        if (row.Fingerprint != expectedFingerprint) return null; // A stale caller cannot cancel replacement work.
        if (row.Expires <= now.UtcTicks || payload == null)
        {
            await CancelSmsForwardRowAsync(connection, transaction, id, ct).ConfigureAwait(false);
            transaction.Commit(); return null;
        }
        var token = Guid.NewGuid().ToString("N");
        using var update = new SqliteCommand("""
            UPDATE SipSmsForwards SET State=1,AttemptToken=$token
            WHERE Id=$id AND State=0 AND PayloadFingerprint=$fingerprint AND ExpiresAtTicks>$now
            """, connection, transaction);
        update.Parameters.AddWithValue("$token", token); update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$fingerprint", expectedFingerprint); update.Parameters.AddWithValue("$now", now.UtcTicks);
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return null;
        transaction.Commit(); // FULL commit is the admission boundary, before any SIP bytes.
        return new(id, token, row.Recipient, payload, row.Fingerprint) { InboxId = row.InboxId };
    }

    public async Task<bool> IsClaimSourceCurrentAsync(SipSmsForwardClaim claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ValidateSipSmsHash(claim.Id); ValidateSipSmsHash(claim.PayloadFingerprint); ValidateSipSmsToken(claim.AttemptToken);
        ValidateForwardRecipient(claim.Recipient);
        if (claim.InboxId is not { Length: > 0 and <= 128 } || claim.Payload == null ||
            claim.Payload.Fingerprint() != claim.PayloadFingerprint) return false;
        await InitializeAsync().WaitAsync(ct).ConfigureAwait(false);
        using var connection = NewSipSmsConnection(); await connection.OpenAsync(ct).ConfigureAwait(false);
        // No schema creation, pruning, lease renewal or state write belongs in
        // a wire guard. One read snapshot binds the intent and its actual Inbox.
        using var transaction = connection.BeginTransaction(deferred: true);
        var row = (await ReadSmsForwardRowsAsync(connection, transaction, ct).ConfigureAwait(false))
            .FirstOrDefault(value => value.Id == claim.Id);
        if (row == null || row.State != SipSmsForwardState.Unknown || row.Token != claim.AttemptToken ||
            row.InboxId != claim.InboxId || row.Recipient != claim.Recipient || row.Fingerprint != claim.PayloadFingerprint) return false;
        var payload = await ReadSmsForwardPayloadAsync(connection, transaction, row, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return row.Expires > DateTimeOffset.UtcNow.UtcTicks && payload != null && payload.Fingerprint() == claim.PayloadFingerprint;
    }

    public async Task CompleteAsync(string id, string attemptToken, SipSmsForwardState state, CancellationToken ct)
    {
        ValidateSipSmsHash(id); ValidateSipSmsToken(attemptToken);
        if (state is not (SipSmsForwardState.Unknown or SipSmsForwardState.Accepted)) throw new ArgumentOutOfRangeException(nameof(state));
        await InitializeAsync().WaitAsync(ct).ConfigureAwait(false);
        using var connection = NewSipSmsConnection(); await connection.OpenAsync(ct).ConfigureAwait(false);
        await ConfigureSipSmsDurabilityAsync(connection, ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureSipSmsForwardTableAsync(connection, transaction, ct).ConfigureAwait(false);
        using var update = new SqliteCommand("""
            UPDATE SipSmsForwards SET State=$state
            WHERE Id=$id AND AttemptToken=$token AND (State=1 OR State=$state)
            """, connection, transaction);
        update.Parameters.AddWithValue("$state", (int)state); update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$token", attemptToken);
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("SMS forward completion has no matching attempt.");
        transaction.Commit();
    }

    public async Task CancelQueuedAsync(string id, CancellationToken ct)
    {
        ValidateSipSmsHash(id);
        await InitializeAsync().WaitAsync(ct).ConfigureAwait(false);
        using var connection = NewSipSmsConnection(); await connection.OpenAsync(ct).ConfigureAwait(false);
        await ConfigureSipSmsDurabilityAsync(connection, ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureSipSmsForwardTableAsync(connection, transaction, ct).ConfigureAwait(false);
        await CancelSmsForwardRowAsync(connection, transaction, id, ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task CancelSmsForwardRowAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken ct)
    {
        using var update = new SqliteCommand("UPDATE SipSmsForwards SET State=3 WHERE Id=$id AND State=0", connection, transaction);
        update.Parameters.AddWithValue("$id", id); await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task PruneSmsForwardsAsync(SqliteConnection connection, SqliteTransaction transaction, long now, CancellationToken ct)
    {
        using var prune = new SqliteCommand("DELETE FROM SipSmsForwards WHERE ExpiresAtTicks<=$now", connection, transaction);
        prune.Parameters.AddWithValue("$now", now); await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<StoredSmsForward>> ReadSmsForwardRowsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = new SqliteCommand("SELECT Id,InboxId,RecipientKind,RecipientUser,Authority,PayloadFingerprint,SenderSnapshot,ExpiresAtTicks,State,AttemptToken,ReplyNumberSnapshot FROM SipSmsForwards", connection, transaction);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<StoredSmsForward>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count >= SipSmsForwardLimit) throw new InvalidOperationException("SMS forwarding store exceeds its capacity.");
            var id = reader.GetString(0); ValidateSipSmsHash(id);
            var inbox = reader.GetString(1); var kind = reader.GetInt64(2);
            if (kind is < 0 or > 1 || inbox.Length is 0 or > 128) throw new InvalidOperationException("Invalid SMS forward source.");
            var recipient = new SipSmsForwardRecipient((SipSmsForwardRecipientKind)kind, reader.GetString(3), reader.GetString(4));
            ValidateForwardRecipient(recipient);
            var fingerprint = reader.GetString(5); ValidateSipSmsHash(fingerprint);
            var sender = reader.GetString(6); var expires = reader.GetInt64(7); var state = reader.GetInt64(8);
            var token = reader.IsDBNull(9) ? null : reader.GetString(9);
            if (!reader.IsDBNull(10) && reader.GetValue(10) is not string)
                throw new InvalidOperationException("Invalid SMS reply snapshot type.");
            var reply = reader.IsDBNull(10) ? null : reader.GetString(10);
            if (!IsSmsReplySnapshotValue(reply)) throw new InvalidOperationException("Invalid SMS reply snapshot.");
            if (sender.Length is 0 or > 128 || expires <= 0 || expires > DateTime.MaxValue.Ticks || state is < 0 or > 3)
                throw new InvalidOperationException("Invalid SMS forward record.");
            if (state is 1 or 2) ValidateSipSmsToken(token!);
            else if (token != null) throw new InvalidOperationException("Unexpected SMS forwarding attempt token.");
            rows.Add(new(id, inbox, recipient, fingerprint, sender, reply, expires, (SipSmsForwardState)state, token));
        }
        return rows;
    }

    private static async Task<SipSmsForwardPayload?> ReadSmsForwardPayloadAsync(SqliteConnection connection, SqliteTransaction transaction, StoredSmsForward row, CancellationToken ct)
    {
        using var command = new SqliteCommand("SELECT SlotId,SimBinding,RemoteNumber,Text,IsOutgoing,SourceMessageId,SipReplyNumberSnapshot FROM SmsMessages WHERE Id=$id", connection, transaction);
        command.Parameters.AddWithValue("$id", row.InboxId);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.GetInt64(4) != 0 || reader.GetString(2) != row.Sender.Trim()) return null;
        var binding = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (binding != null && !SmsSubscriptionIdentity.IsValid(binding)) return null;
        if (!reader.IsDBNull(6) && reader.GetValue(6) is not string) return null;
        var reply = reader.IsDBNull(6) ? null : reader.GetString(6);
        if (reply != row.ReplyNumber || !IsSmsReplySnapshotValue(reply)) return null;
        var source = SmsForwardSourceKey(row.InboxId, reader.GetString(0), binding, reader.IsDBNull(5) ? null : reader.GetString(5));
        if (SmsForwardIntentId(source, row.Recipient) != row.Id) return null;
        var payload = new SipSmsForwardPayload(reader.GetString(0), binding, row.Sender, reader.GetString(3))
            { ReplyNumber = reply };
        return payload.Fingerprint() == row.Fingerprint ? payload : null;
    }
}
