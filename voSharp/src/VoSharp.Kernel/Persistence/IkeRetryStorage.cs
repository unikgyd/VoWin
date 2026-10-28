using Microsoft.Data.Sqlite;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel.Persistence;

/// <summary>Shared per-user journal for the desktop and standalone CLI owners.</summary>
public static class IkeRetryStorage
{
    public static string DefaultPreferencesDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "preferences.db");

    public static IkeRetryGate CreateDefaultGate() => CreateGate(DefaultPreferencesDatabasePath);

    // Explicit paths support embedded owners and isolated tests. The CLI does
    // not expose a path/reset switch that silently creates a fresh journal.
    public static IkeRetryGate CreateGate(string preferencesPath, IIkeRetryClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencesPath);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = preferencesPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        return new(new SqliteIkeRetryJournalStore(connection.ToString()), clock ?? new WindowsIkeRetryClock());
    }
}
