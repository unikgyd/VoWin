using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoSharp.Euicc.Download;

internal sealed record EuiccDownloadJournalEntry(
    string Fingerprint,
    string State,
    DateTimeOffset UpdatedAt,
    List<string> ProfilesBefore,
    string? InstalledIccid = null);

internal static class EuiccDownloadJournal
{
    private const string InProgress = "in_progress";
    private const string Uncertain = "uncertain";
    private const string Completed = "completed";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static string? PathOverride { get; set; }
    private static string JournalPath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoSharp",
        "euicc-download-journal.json");

    public static string Fingerprint(string canonicalActivationCode)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalActivationCode)));

    public static EuiccDownloadJournalEntry? Get(string fingerprint)
    {
        lock (Sync)
            return Load().FirstOrDefault(entry => entry.Fingerprint.Equals(fingerprint, StringComparison.Ordinal));
    }

    public static void Begin(string fingerprint, IEnumerable<string> profilesBefore)
    {
        lock (Sync)
        {
            var entries = Load();
            if (entries.Any(entry => entry.Fingerprint.Equals(fingerprint, StringComparison.Ordinal)))
                throw new InvalidOperationException("该激活码已有下载事务记录，不能再次发起下载。");
            entries.Add(new EuiccDownloadJournalEntry(
                fingerprint,
                InProgress,
                DateTimeOffset.UtcNow,
                profilesBefore.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            Save(entries);
        }
    }

    public static void Complete(string fingerprint, string iccid)
        => Update(fingerprint, entry => entry with
        {
            State = Completed,
            InstalledIccid = iccid,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    public static void MarkUncertain(string fingerprint)
        => Update(fingerprint, entry => entry with { State = Uncertain, UpdatedAt = DateTimeOffset.UtcNow });

    public static void RemoveIfRetrySafe(string fingerprint)
    {
        lock (Sync)
        {
            var entries = Load();
            if (entries.RemoveAll(entry => entry.Fingerprint.Equals(fingerprint, StringComparison.Ordinal)) > 0)
                Save(entries);
        }
    }

    private static void Update(string fingerprint, Func<EuiccDownloadJournalEntry, EuiccDownloadJournalEntry> update)
    {
        lock (Sync)
        {
            var entries = Load();
            var index = entries.FindIndex(entry => entry.Fingerprint.Equals(fingerprint, StringComparison.Ordinal));
            if (index < 0) return;
            entries[index] = update(entries[index]);
            Save(entries);
        }
    }

    private static List<EuiccDownloadJournalEntry> Load()
    {
        if (!File.Exists(JournalPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<EuiccDownloadJournalEntry>>(File.ReadAllText(JournalPath), JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("eUICC 下载事务日志损坏；为防止重复消耗激活码，已禁止继续写卡。", ex);
        }
    }

    private static void Save(List<EuiccDownloadJournalEntry> entries)
    {
        var directory = Path.GetDirectoryName(JournalPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, JournalPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }
}
