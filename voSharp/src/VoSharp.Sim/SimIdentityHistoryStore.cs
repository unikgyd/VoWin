using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoSharp.Sim;

/// <summary>
/// Learns IMSIs observed for each physical profile. Multi-IMSI eSIMs may expose
/// a roaming IMSI after attachment, while their ePDG still expects a previously
/// observed home identity. Entries are learned at runtime and keyed by a hash of
/// ICCID; this store contains no bundled carrier knowledge.
/// </summary>
public sealed class SimIdentityHistoryStore
{
    private const int FormatVersion = 2;
    private static readonly object FileGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    private sealed record Entry(
        string Imsi,
        int MncLength,
        string[] HomePlmns,
        DateTime LastSeenUtc,
        DateTime? LastSucceededUtc,
        bool HomePlmnVerified = false);

    private sealed record Snapshot(int Version, Dictionary<string, List<Entry>> Cards);

    public SimIdentityHistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoWin",
            "vowifi-identities.json");
    }

    public void Observe(SimIdentity identity) => Update(identity, succeeded: false);

    public void MarkSuccessful(SimIdentity identity) => Update(identity, succeeded: true);

    public void ObserveProfileIdentities(SimIdentity identity)
    {
        Observe(identity);
        // Both reads belong to the verified current ICCID. Never transplant
        // the selected EF_AD MNC length onto a different reported IMSI.
        foreach (var imsi in new[] { identity.ReportedImsi, identity.PermanentImsi }.Distinct())
            if (IsValidImsi(imsi) && imsi != identity.Imsi)
                Observe(SimIdentity.FromImsiAndIccid(imsi!, identity.Iccid,
                    identity.OperatorName, identity.PhoneNumber, homePlmns: identity.HomePlmns,
                    imsiSource: "same-profile alternate read"));
    }

    public IReadOnlyList<SimIdentity> GetCandidates(SimIdentity liveIdentity, bool successfulOnly = false)
    {
        ArgumentNullException.ThrowIfNull(liveIdentity);
        lock (FileGate)
        {
            var snapshot = Load();
            if (!snapshot.Cards.TryGetValue(CardKey(liveIdentity.Iccid), out var entries))
                return Array.Empty<SimIdentity>();

            return entries
                .Where(entry => !successfulOnly || entry.LastSucceededUtc.HasValue)
                .OrderByDescending(entry => entry.LastSucceededUtc.HasValue)
                .ThenByDescending(entry => entry.LastSucceededUtc)
                .ThenByDescending(entry => entry.LastSeenUtc)
                .Where(entry => IsValidImsi(entry.Imsi) && entry.MncLength is 2 or 3)
                .Select(entry => entry.Imsi == liveIdentity.Imsi && liveIdentity.IsHomePlmnAuthoritative
                    ? liveIdentity // Current EF_AD outranks stale history for the same IMSI.
                    : SimIdentity.FromImsiAndIccid(
                        entry.Imsi,
                        liveIdentity.Iccid,
                        liveIdentity.OperatorName,
                        liveIdentity.PhoneNumber,
                        entry.MncLength,
                        entry.HomePlmns.Concat(liveIdentity.HomePlmns ?? Array.Empty<string>()).Distinct().ToArray())
                        with { IsHomePlmnAuthoritative = entry.HomePlmnVerified, ImsiSource = "same-profile history" })
                .ToArray();
        }
    }

    private void Update(SimIdentity identity, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!IsValidImsi(identity.Imsi) || string.IsNullOrWhiteSpace(identity.Iccid) || identity.Mnc.Length is not (2 or 3))
            return;

        lock (FileGate)
        {
            var snapshot = Load();
            var key = CardKey(identity.Iccid);
            if (!snapshot.Cards.TryGetValue(key, out var entries))
            {
                entries = new List<Entry>();
                snapshot.Cards[key] = entries;
            }

            var existing = entries.FirstOrDefault(entry => entry.Imsi == identity.Imsi);
            var now = DateTime.UtcNow;
            var keepSuccessfulHint = !succeeded && !identity.IsHomePlmnAuthoritative &&
                (existing?.LastSucceededUtc != null || existing?.HomePlmnVerified == true);
            var updated = new Entry(
                identity.Imsi,
                keepSuccessfulHint ? existing!.MncLength : identity.Mnc.Length,
                (identity.HomePlmns ?? Array.Empty<string>()).Distinct().ToArray(),
                now,
                succeeded ? now : existing?.LastSucceededUtc,
                keepSuccessfulHint ? existing!.HomePlmnVerified : identity.IsHomePlmnAuthoritative);
            if (existing != null)
                entries.Remove(existing);
            entries.Add(updated);

            // A profile can rotate identities, but an unbounded history is neither
            // useful nor desirable. Keep the most relevant recent candidates.
            snapshot.Cards[key] = entries
                .OrderByDescending(entry => entry.LastSucceededUtc.HasValue)
                .ThenByDescending(entry => entry.LastSucceededUtc)
                .ThenByDescending(entry => entry.LastSeenUtc)
                .Take(8)
                .ToList();
            Save(snapshot);
        }
    }

    private Snapshot Load()
    {
        try
        {
            if (!File.Exists(_path))
                return Empty();
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), JsonOptions);
            // V1 did not distinguish EF_AD from inferred lengths. Preserve IMSI
            // history (important for multi-IMSI cards), but migrate it as hints.
            return snapshot is { Version: 1 or FormatVersion, Cards: not null }
                ? snapshot with { Version = FormatVersion } : Empty();
        }
        catch (IOException) { return Empty(); }
        catch (UnauthorizedAccessException) { return Empty(); }
        catch (JsonException) { return Empty(); }
    }

    private void Save(Snapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Snapshot Empty() => new(FormatVersion, new Dictionary<string, List<Entry>>());

    private static string CardKey(string iccid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(iccid.Trim()))).ToLowerInvariant();

    private static bool IsValidImsi(string? imsi) =>
        imsi is { Length: >= 5 and <= 16 } && imsi.All(char.IsAsciiDigit);
}
