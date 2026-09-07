using System.Security.Cryptography;
using System.Text;

namespace VoSharp.Crypto;

public record DigestChallenge(
    string Realm,
    string Nonce,
    string? Opaque,
    string Algorithm,
    string? Qop,
    bool Stale = false
);

public record DigestCredentials(
    string Username,
    byte[] AkaResponse,
    string Uri,
    string Method,
    string CNonce,
    uint Nc,
    string? Auts = null,
    bool? IntegrityProtected = null
);

public static class DigestAka
{
    /// <summary>
    /// Computes RFC 3310 / 3GPP TS 33.203 Digest-AKAv1-MD5 response.
    /// </summary>
    public static string ComputeResponse(
        string username,
        string realm,
        byte[] akaResponse,
        string method,
        string uri,
        string nonce,
        string nc,
        string cnonce,
        string? qop)
    {
        // HA1 = MD5(username:realm:RES)
        using var md5 = MD5.Create();
        var prefix = Encoding.UTF8.GetBytes($"{username}:{realm}:");
        var ha1Input = new byte[prefix.Length + akaResponse.Length];
        Buffer.BlockCopy(prefix, 0, ha1Input, 0, prefix.Length);
        Buffer.BlockCopy(akaResponse, 0, ha1Input, prefix.Length, akaResponse.Length);
        var ha1 = Convert.ToHexString(md5.ComputeHash(ha1Input)).ToLowerInvariant();

        // HA2 = MD5(method:uri)
        var ha2 = ComputeMd5($"{method}:{uri}");

        if (string.IsNullOrEmpty(qop))
        {
            return ComputeMd5($"{ha1}:{nonce}:{ha2}");
        }

        return ComputeMd5($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
    }

    /// <summary>
    /// Builds the full Authorization header value for SIP REGISTER.
    /// </summary>
    public static string BuildAuthorizationHeader(DigestChallenge challenge, DigestCredentials credentials)
    {
        var ncStr = $"{credentials.Nc:x8}";
        var response = ComputeResponse(
            credentials.Username,
            challenge.Realm,
            credentials.AkaResponse,
            credentials.Method,
            credentials.Uri,
            challenge.Nonce,
            ncStr,
            credentials.CNonce,
            challenge.Qop
        );

        var parts = new List<string>
        {
            $"username=\"{Quote(credentials.Username)}\"",
            $"realm=\"{Quote(challenge.Realm)}\"",
            $"nonce=\"{Quote(challenge.Nonce)}\"",
            $"uri=\"{Quote(credentials.Uri)}\"",
            $"response=\"{response}\"",
            "algorithm=AKAv1-MD5"
        };

        if (!string.IsNullOrEmpty(challenge.Opaque))
        {
            parts.Add($"opaque=\"{Quote(challenge.Opaque)}\"");
        }

        if (!string.IsNullOrEmpty(challenge.Qop))
        {
            parts.Add($"qop={challenge.Qop}");
            parts.Add($"nc={ncStr}");
            parts.Add($"cnonce=\"{Quote(credentials.CNonce)}\"");
        }

        if (!string.IsNullOrEmpty(credentials.Auts))
        {
            parts.Add($"auts=\"{Quote(credentials.Auts)}\"");
        }

        // 3GPP TS 33.203: sent only when sec-agree is in play, and must flip to "yes" once the
        // REGISTER travels over a protected IPsec SA. P-CSCFs reject a stale value.
        if (credentials.IntegrityProtected.HasValue)
        {
            parts.Add($"integrity-protected={(credentials.IntegrityProtected.Value ? "yes" : "no")}");
        }

        return "Digest " + string.Join(", ", parts);
    }

    private static string ComputeMd5(string text)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Quote(string val)
    {
        return val.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
