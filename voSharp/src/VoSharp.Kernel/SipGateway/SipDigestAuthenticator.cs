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
    private readonly ConcurrentDictionary<string, NonceState> _nonces = new(StringComparer.Ordinal);

    public SipDigestAuthenticator(string realm, IReadOnlyDictionary<string, string> accounts)
    {
        _realm = realm;
        _accounts = accounts;
    }

    public string CreateChallenge()
    {
        Cleanup();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _nonces[nonce] = new NonceState(DateTimeOffset.UtcNow.AddMinutes(5), new(StringComparer.OrdinalIgnoreCase));
        return $"Digest realm=\"{Escape(_realm)}\", nonce=\"{nonce}\", algorithm=MD5, qop=\"auth\"";
    }

    public bool Validate(SipMessage request, out string username)
    {
        username = string.Empty;
        var header = request.Method.Equals("REGISTER", StringComparison.OrdinalIgnoreCase)
            ? request.GetHeader("Authorization") ?? request.GetHeader("Proxy-Authorization")
            : request.GetHeader("Proxy-Authorization") ?? request.GetHeader("Authorization");
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase)) return false;
        var values = ParseParameters(header[7..]);
        if (!values.TryGetValue("username", out var parsedUsername) || string.IsNullOrWhiteSpace(parsedUsername) ||
            !_accounts.TryGetValue(parsedUsername, out var password)) return false;
        username = parsedUsername;
        if (!values.TryGetValue("realm", out var realm) || !FixedEquals(realm, _realm)) return false;
        if (values.TryGetValue("algorithm", out var algorithm) &&
            !string.Equals(algorithm, "MD5", StringComparison.OrdinalIgnoreCase)) return false;
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

        var ha1 = Md5Hex($"{username}:{_realm}:{password}");
        var ha2 = Md5Hex($"{request.Method}:{uri}");
        var expected = Md5Hex($"{ha1}:{nonce}:{ncText}:{cnonce}:auth:{ha2}");
        if (!FixedEquals(expected, supplied)) return false;

        // AddOrUpdate may invoke its update delegate more than once. A side
        // effect in that delegate can report success even when a competing
        // request has already committed an equal or higher nonce count.
        while (true)
        {
            if (!nonceState.NonceCounts.TryGetValue(username, out var previous))
            {
                if (nonceState.NonceCounts.TryAdd(username, nc)) return true;
                continue;
            }
            if (nc <= previous) return false;
            if (nonceState.NonceCounts.TryUpdate(username, nc, previous)) return true;
        }
    }

    internal static Dictionary<string, string> ParseParameters(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(input, "(?:^|,)\\s*([A-Za-z][A-Za-z0-9_-]*)\\s*=\\s*(?:\\\"((?:\\\\.|[^\\\"])*)\\\"|([^,\\s]+))"))
        {
            var value = match.Groups[2].Success
                ? UnescapeQuotedPair(match.Groups[2].Value)
                : match.Groups[3].Value;
            result[match.Groups[1].Value] = value;
        }
        return result;
    }

    private static string UnescapeQuotedPair(string input)
    {
        var output = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\\' && i + 1 < input.Length) i++;
            output.Append(input[i]);
        }
        return output.ToString();
    }

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
