using VoSharp.Kernel.Persistence;
using VoSharp.Telephony.VoWifi;

namespace VoWin.Services;

public partial class PreferenceDatabaseService : IIkeRetryJournalStore
{
    async Task<string?> IIkeRetryJournalStore.LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await InitializeAsync().ConfigureAwait(false);
        return await new SqliteIkeRetryJournalStore(_connectionString).LoadAsync(ct).ConfigureAwait(false);
    }

    async Task<bool> IIkeRetryJournalStore.CompareExchangeAsync(string? expected, string replacement, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await InitializeAsync().ConfigureAwait(false);
        return await new SqliteIkeRetryJournalStore(_connectionString).CompareExchangeAsync(expected, replacement, ct).ConfigureAwait(false);
    }
}
