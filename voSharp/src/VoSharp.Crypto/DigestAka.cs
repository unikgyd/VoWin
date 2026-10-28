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
        // RES is binary, not UTF-8 or hex. Keep HA1 and all secret-bearing
        // temporary buffers mutable so they can be cleared on every exit.
        var prefix = Encoding.UTF8.GetBytes($"{username}:{realm}:");
        var ha1Input = new byte[prefix.Length + akaResponse.Length];
        byte[]? ha1Hash = null;
        byte[]? responseInput = null;
        byte[]? responseHash = null;
        try
        {
            Buffer.BlockCopy(prefix, 0, ha1Input, 0, prefix.Length);
            Buffer.BlockCopy(akaResponse, 0, ha1Input, prefix.Length, akaResponse.Length);
            ha1Hash = MD5.HashData(ha1Input);
            var ha2 = ComputeMd5($"{method}:{uri}");
            var suffix = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(qop)
                ? $":{nonce}:{ha2}"
                : $":{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
            responseInput = new byte[32 + suffix.Length];
            for (var i = 0; i < ha1Hash.Length; i++)
            {
                const string hex = "0123456789abcdef";
                responseInput[i * 2] = (byte)hex[ha1Hash[i] >> 4];
                responseInput[i * 2 + 1] = (byte)hex[ha1Hash[i] & 15];
            }
            Buffer.BlockCopy(suffix, 0, responseInput, 32, suffix.Length);
            responseHash = MD5.HashData(responseInput);
            return Convert.ToHexString(responseHash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
            CryptographicOperations.ZeroMemory(ha1Input);
            if (ha1Hash is not null) CryptographicOperations.ZeroMemory(ha1Hash);
            if (responseInput is not null) CryptographicOperations.ZeroMemory(responseInput);
            if (responseHash is not null) CryptographicOperations.ZeroMemory(responseHash);
        }
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
