using System.Security.Cryptography;
using System.Text;
using VoSharp.Sip;

namespace VoSharp.Kernel.SipGateway;

/// <summary>Pure, bounded UAC calculation for explicit outbound peer credentials.</summary>
internal static class SipPeerDigest
{
    internal sealed record Challenge(string Realm, string Nonce, string Algorithm, bool UseQop, string? Opaque);

    public static string? CreatePreferred(SipMessage response, SipMessage request, SipGatewayPeer peer)
    {
        if (response.StatusCode is not (401 or 407)) return null;
        var name = response.StatusCode == 401 ? "WWW-Authenticate" : "Proxy-Authenticate";
        var count = (response.Headers.GetValueOrDefault("WWW-Authenticate")?.Count ?? 0) +
            (response.Headers.GetValueOrDefault("Proxy-Authenticate")?.Count ?? 0);
        if (count != 1 || !response.Headers.TryGetValue(name, out var challenges) || challenges.Count != 1) return null;
        return Create(challenges[0], request, peer);
    }

    public static string? Create(string challenge, SipMessage request, SipGatewayPeer peer)
    {
        if (peer.OutboundAuth is not { } credentials || !request.IsRequest || string.IsNullOrEmpty(request.Method) ||
            request.Method.Any(c => !char.IsAsciiLetter(c)) || string.IsNullOrEmpty(request.RequestUri) ||
            Encoding.UTF8.GetByteCount(request.RequestUri) > 2048 || request.RequestUri.Any(char.IsControl) ||
            request.Headers.ContainsKey("Authorization") || request.Headers.ContainsKey("Proxy-Authorization") ||
            !TryParseChallenge(challenge, out var parsed)) return null;
        try { credentials.Validate(); }
        catch (ArgumentException) { return null; }
        if (!string.Equals(parsed.Realm, credentials.ExpectedRealm, StringComparison.Ordinal) ||
            !credentials.AllowedAlgorithms.Contains(parsed.Algorithm, StringComparer.Ordinal)) return null;

        string Hash(string input) => Convert.ToHexString(parsed.Algorithm == "MD5"
            ? MD5.HashData(Encoding.UTF8.GetBytes(input)) : SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var ha1 = Hash($"{credentials.Username}:{parsed.Realm}:{credentials.Password}");
        var ha2 = Hash($"{request.Method}:{request.RequestUri}");
        var cnonce = parsed.UseQop ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() : string.Empty;
        var proof = parsed.UseQop ? Hash($"{ha1}:{parsed.Nonce}:00000001:{cnonce}:auth:{ha2}") : Hash($"{ha1}:{parsed.Nonce}:{ha2}");
        var header = $"Digest username={Quote(credentials.Username)}, realm={Quote(parsed.Realm)}, nonce={Quote(parsed.Nonce)}, uri={Quote(request.RequestUri)}, response={Quote(proof)}, algorithm={parsed.Algorithm}";
        if (parsed.UseQop) header += $", qop=auth, nc=00000001, cnonce={Quote(cnonce)}";
        if (parsed.Opaque is { } opaque) header += $", opaque={Quote(opaque)}";
        return header;
    }

    // Syntax/capability recognition is separate from credential policy:
    // a parsed realm never pins itself or authorizes an implicit MD5 downgrade.
    internal static bool TryParseChallenge(string challenge, out Challenge parsed)
    {
        parsed = null!;
        if (string.IsNullOrEmpty(challenge) || !challenge.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase) ||
            !TryParseParameters(challenge[7..], out var values) || !values.TryGetValue("realm", out var realm) ||
            !values.TryGetValue("nonce", out var nonce) || nonce.Length == 0) return false;
        var algorithm = values.GetValueOrDefault("algorithm", "MD5").ToUpperInvariant();
        if (algorithm is not ("MD5" or "SHA-256") || values.ContainsKey("userhash") ||
            values.TryGetValue("charset", out var charset) && !charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)) return false;
        var qop = values.GetValueOrDefault("qop");
        if (qop != null && !qop.Split(',').Any(item => item.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase))) return false;
        parsed = new(realm, nonce, algorithm, qop != null, values.GetValueOrDefault("opaque"));
        return true;
    }

    /// <summary>Strict bounded Digest comma parameters, without the scheme prefix.</summary>
    internal static bool TryParseParameters(string input, out Dictionary<string, string> values)
    {
        values = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(input) || Encoding.UTF8.GetByteCount(input) > 4096 || input.Any(char.IsControl)) return false;
        var remaining = input.AsSpan().Trim();
        while (!remaining.IsEmpty)
        {
            var equals = remaining.IndexOf('=');
            if (equals < 1) return false;
            var name = remaining[..equals].Trim().ToString();
            if (!IsToken(name)) return false;
            remaining = remaining[(equals + 1)..].TrimStart();
            string value;
            if (remaining.StartsWith("\""))
            {
                var output = new StringBuilder();
                var end = -1;
                for (var index = 1; index < remaining.Length; index++)
                {
                    var ch = remaining[index];
                    if (ch == '"') { end = index; break; }
                    if (ch == '\\')
                    {
                        if (++index == remaining.Length) return false;
                        ch = remaining[index];
                    }
                    output.Append(ch);
                }
                if (end < 0) return false;
                value = output.ToString();
                remaining = remaining[(end + 1)..].TrimStart();
            }
            else
            {
                var comma = remaining.IndexOf(',');
                value = (comma < 0 ? remaining : remaining[..comma]).Trim().ToString();
                if (!IsToken(value)) return false;
                remaining = comma < 0 ? [] : remaining[comma..];
            }
            if (values.Count >= 32 || !values.TryAdd(name, value)) return false;
            if (remaining.IsEmpty) return true;
            if (remaining[0] != ',') return false;
            remaining = remaining[1..].TrimStart();
            if (remaining.IsEmpty) return false;
        }
        return values.Count > 0;
    }

    private static bool IsToken(string value) => value.Length > 0 && value.All(c =>
        char.IsAsciiLetterOrDigit(c) || c == (char)96 || c is '-' or '.' or '!' or '%' or '*' or '_' or '+' or '\'' or '~');
}
