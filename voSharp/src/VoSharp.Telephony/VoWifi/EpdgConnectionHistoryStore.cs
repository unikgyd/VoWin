using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoSharp.Sim;

namespace VoSharp.Telephony.VoWifi;

public sealed record EpdgSuccessfulSelection(string Host, string HomeMcc, string HomeMnc,
    string? DiscoveryPlmn, DateTime SucceededAtUtc);

/// <summary>Remembers registered gateways, not cached DNS answers or authentication secrets.</summary>
public sealed class EpdgConnectionHistoryStore
{
    private static readonly object Gate = new();
    private readonly string _path;
    public EpdgConnectionHistoryStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "epdg-success.json");
    private static string Key(SimIdentity sim) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(sim.Iccid.Trim() + "\n" + sim.Imsi)));

    public EpdgSuccessfulSelection? Get(SimIdentity sim)
    {
        if (string.IsNullOrWhiteSpace(sim.Iccid)) return null;
        lock (Gate)
        {
            var entry = Load().GetValueOrDefault(Key(sim));
            if (entry == null || !EpdgResolver.BuildHomePlmnCandidates(sim).Contains((entry.HomeMcc, entry.HomeMnc))) return null;
            try { _ = new EpdgUserConfiguration(entry.Host).Normalize(); }
            catch (ArgumentException) { return null; }
            return entry;
        }
    }

    public void MarkRegistered(SimIdentity sim, EpdgResolutionResult discovery)
    {
        if (string.IsNullOrWhiteSpace(sim.Iccid) || discovery.IpAddresses.Length == 0 ||
            !EpdgResolver.BuildHomePlmnCandidates(sim).Contains((discovery.HomeMcc, discovery.HomeMnc))) return;
        var endpoint = discovery.Endpoints?.FirstOrDefault(endpoint => endpoint.Candidate.Host == discovery.Fqdn &&
            endpoint.Address.Equals(discovery.IpAddresses[0]) && endpoint.HomeMcc == discovery.HomeMcc && endpoint.HomeMnc == discovery.HomeMnc);
        if (endpoint == null) return;
        lock (Gate)
        {
            var entries = Load();
            entries[Key(sim)] = new(discovery.Fqdn, discovery.HomeMcc, discovery.HomeMnc,
                endpoint.Candidate.Plmn, DateTime.UtcNow);
            entries = entries.OrderByDescending(pair => pair.Value.SucceededAtUtc).Take(128).ToDictionary(pair => pair.Key, pair => pair.Value);
            var temporary = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private Dictionary<string, EpdgSuccessfulSelection> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 262144) return new();
            var entries = JsonSerializer.Deserialize<Dictionary<string, EpdgSuccessfulSelection?>>(File.ReadAllText(_path));
            return (entries ?? []).Where(pair => pair.Key.Length == 64 && pair.Key.All(Uri.IsHexDigit) && Valid(pair.Value))
                .OrderByDescending(pair => pair.Value!.SucceededAtUtc).Take(128)
                .ToDictionary(pair => pair.Key, pair => pair.Value!);
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
        catch (JsonException) { return new(); }
    }

    private static bool Valid(EpdgSuccessfulSelection? entry)
    {
        if (entry == null || entry.HomeMcc?.Length != 3 || entry.HomeMnc?.Length is not (2 or 3) ||
            !entry.HomeMcc.All(char.IsAsciiDigit) || !entry.HomeMnc.All(char.IsAsciiDigit) ||
            string.IsNullOrWhiteSpace(entry.Host) || entry.Host.IndexOfAny([' ', ',', ';', '\r', '\n', '\t']) >= 0) return false;
        try { return new EpdgUserConfiguration(entry.Host).Normalize().PreferredAddresses == entry.Host; }
        catch (ArgumentException) { return false; }
    }
}
