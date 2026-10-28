using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VoSharp.Common.Utils;

public static class HostImsBearerIdentity
{
    public const string LegacyEntryName = "VoWin IMS PPP Probe";
    public static string EntryForImeiHash(string hash) => Regex.IsMatch(hash, "^[0-9a-f]{64}$")
        ? "VoWin IMS " + hash[..16] : throw new ArgumentException("Invalid modem hash.");
    public static string EntryForImei(string imei) => Regex.IsMatch(imei, "^[0-9]{15}$")
        ? EntryForImeiHash(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(imei))))
        : throw new ArgumentException("A verified 15-digit modem identity is required.");
    public static bool IsControlledEntry(string name) => name == LegacyEntryName || Regex.IsMatch(name, "^VoWin IMS [0-9a-f]{16}$");
}
