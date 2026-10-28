using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Sip;

namespace VoSharp.Kernel.SipGateway;

internal sealed class SipDigestAuthenticator
{
    private sealed record NonceState(DateTimeOffset ExpiresAt, ConcurrentDictionary<string, uint> NonceCounts);

    private readonly string _realm;
    private readonly IReadOnlyDictionary<string, string> _accounts;
    private readonly string _algorithm;
    private readonly bool _strictHeaders;
    private readonly ConcurrentDictionary<string, NonceState> _nonces = new(StringComparer.Ordinal);

    public SipDigestAuthenticator(string realm, IReadOnlyDictionary<string, string> accounts,
        string algorithm = "MD5", bool strictHeaders = false)
    {
        if (!algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase) &&
            !algorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unsupported SIP digest algorithm.", nameof(algorithm));
        _realm = realm;
        _accounts = accounts;
        _algorithm = algorithm.ToUpperInvariant();
        _strictHeaders = strictHeaders;
    }

    public string CreateChallenge()
    {
        Cleanup();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _nonces[nonce] = new NonceState(DateTimeOffset.UtcNow.AddMinutes(5), new(StringComparer.OrdinalIgnoreCase));
        return $"Digest realm=\"{Escape(_realm)}\", nonce=\"{nonce}\", algorithm={_algorithm}, qop=\"auth\"";
    }

    public bool Validate(SipMessage request, out string username)
    {
        username = string.Empty;
        var preferred = request.Method.Equals("REGISTER", StringComparison.OrdinalIgnoreCase)
            ? "Authorization" : "Proxy-Authorization";
        var alternate = preferred == "Authorization" ? "Proxy-Authorization" : "Authorization";
        var preferredHeaders = request.Headers.GetValueOrDefault(preferred)?.ToArray() ?? [];
        var alternateHeaders = request.Headers.GetValueOrDefault(alternate)?.ToArray() ?? [];
        // Local clients may keep a stale registrar Authorization next to their
        // proxy proof. The fixed peer is stricter: exactly one proof, one role.
        if (preferredHeaders.Length > 1 || alternateHeaders.Length > 1 ||
            _strictHeaders && preferredHeaders.Length + alternateHeaders.Length != 1) return false;
        var header = preferredHeaders.FirstOrDefault() ?? alternateHeaders.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase)) return false;
        if (!SipPeerDigest.TryParseParameters(header[7..], out var values)) return false;
        if (!values.TryGetValue("username", out var parsedUsername) || string.IsNullOrWhiteSpace(parsedUsername) ||
            !_accounts.TryGetValue(parsedUsername, out var password)) return false;
        if (!values.TryGetValue("realm", out var realm) || !FixedEquals(realm, _realm)) return false;
        var algorithm = values.GetValueOrDefault("algorithm", "MD5");
        if (!string.Equals(algorithm, _algorithm, StringComparison.OrdinalIgnoreCase)) return false;
        if (!values.TryGetValue("nonce", out var nonce) || !_nonces.TryGetValue(nonce, out var nonceState) || nonceState.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        if (!values.TryGetValue("uri", out var uri) || !FixedEquals(uri, request.RequestUri)) return false;
        if (!values.TryGetValue("response", out var supplied)) return false;

        var qop = values.GetValueOrDefault("qop");
        var ncText = values.GetValueOrDefault("nc");
        var cnonce = values.GetValueOrDefault("cnonce");
        if (!string.Equals(qop, "auth", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(ncText) || string.IsNullOrWhiteSpace(cnonce) ||
            !Regex.IsMatch(ncText, "^[0-9A-Fa-f]{8}$") ||
            !uint.TryParse(ncText, System.Globalization.NumberStyles.HexNumber, null, out var nc) || nc == 0) return false;

        var ha1 = HashHex($"{parsedUsername}:{_realm}:{password}");
        var ha2 = HashHex($"{request.Method}:{uri}");
        var expected = HashHex($"{ha1}:{nonce}:{ncText}:{cnonce}:auth:{ha2}");
        if (!FixedEquals(expected, supplied)) return false;

        // AddOrUpdate may invoke its update delegate more than once. A side
        // effect in that delegate can report success even when a competing
        // request has already committed an equal or higher nonce count.
        while (true)
        {
            if (!nonceState.NonceCounts.TryGetValue(parsedUsername, out var previous))
            {
                if (nonceState.NonceCounts.TryAdd(parsedUsername, nc)) { username = parsedUsername; return true; }
                continue;
            }
            if (nc <= previous) return false;
            if (nonceState.NonceCounts.TryUpdate(parsedUsername, nc, previous)) { username = parsedUsername; return true; }
        }
    }

    internal static Dictionary<string, string> ParseParameters(string input)
    {
        return SipPeerDigest.TryParseParameters(input, out var values) ? values : new(StringComparer.OrdinalIgnoreCase);
    }

    private string HashHex(string value) => _algorithm == "SHA-256"
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant() : Md5Hex(value);

    internal static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _nonces)
            if (entry.Value.ExpiresAt <= now) _nonces.TryRemove(entry.Key, out _);
    }
}
