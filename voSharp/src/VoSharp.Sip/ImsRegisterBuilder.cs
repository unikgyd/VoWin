using System.Text.RegularExpressions;
using VoSharp.Crypto;

namespace VoSharp.Sip;

public record ImsProfile(
    string PrivateIdentity,
    string PublicIdentity,
    string HomeDomain,
    string Imei,
    string LocalIp,
    int LocalPort = 5060,
    int? ContactPort = null
);

public static class ImsRegisterBuilder
{
    // 3GPP TS 24.229: default registration expiry is 3600 s
    private const int DefaultExpiresSeconds = 3600;

    /// <summary>
    /// Builds initial 3GPP IMS SIP REGISTER without auth credentials.
    /// RFC 3261 §10 / 3GPP TS 24.229 §5.1.1.2
    /// </summary>
    /// <param name="profile">IMS subscriber profile.</param>
    /// <param name="callId">SIP Call-ID (must remain constant for the registration dialog).</param>
    /// <param name="cseq">CSeq number.</param>
    /// <param name="fromTag">
    ///   From header tag. If null a new random tag is generated.
    ///   The same tag MUST be reused for the authenticated retry (RFC 3261 §8.1.1.3).
    /// </param>
    public static SipMessage BuildInitialRegister(
        ImsProfile profile,
        string callId,
        uint cseq,
        string? fromTag = null)
    {
        var msg = new SipMessage
        {
            IsRequest = true,
            Method = "REGISTER",
            RequestUri = $"sip:{profile.HomeDomain}",
            SipVersion = "SIP/2.0"
        };

        var tag = fromTag ?? Guid.NewGuid().ToString("N")[..8];
        var branch = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];

        var user = profile.PrivateIdentity.Contains('@') ? profile.PrivateIdentity.Split('@')[0] : profile.PrivateIdentity;
        var publicId = profile.PublicIdentity.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
            ? profile.PublicIdentity
            : $"sip:{profile.PublicIdentity}";

        // urn:gsma:imei instance ID per 3GPP TS 24.229 §5.1.1A.1
        var cleanImei = profile.Imei.Replace("-", "").Trim();
        var imeiFormatted = cleanImei.Length == 15
            ? $"{cleanImei[..8]}-{cleanImei.Substring(8, 6)}-{cleanImei[14]}"
            : cleanImei;
        var imeiInstance = $"\"<urn:gsma:imei:{imeiFormatted}>\"";

        msg.SetHeader("Via", $"SIP/2.0/UDP {profile.LocalIp}:{profile.LocalPort};branch={branch};rport");
        msg.SetHeader("Max-Forwards", "70");
        msg.SetHeader("From", $"<{publicId}>;tag={tag}");
        msg.SetHeader("To", $"<{publicId}>");
        msg.SetHeader("Call-ID", callId);
        msg.SetHeader("CSeq", $"{cseq} REGISTER");
        msg.SetHeader("Contact", $"<sip:{user}@{profile.LocalIp}:{profile.ContactPort ?? profile.LocalPort};transport=udp>;+sip.instance={imeiInstance};+g.3gpp.smsip;audio;+g.3gpp.icsi-ref=\"urn%3Aurn-7%3A3gpp-service.ims.icsi.mmtel\";expires={DefaultExpiresSeconds}");
        msg.SetHeader("P-Access-Network-Info", "IEEE-802.11;i-wlan-node-id=000000000000;network-provided");
        msg.SetHeader("Accept-Contact", "*;+g.3gpp.smsip, *;+g.3gpp.icsi-ref=\"urn%3Aurn-7%3A3gpp-service.ims.icsi.mmtel\"");
        msg.SetHeader("Allow", "INVITE, ACK, CANCEL, BYE, MESSAGE, OPTIONS, NOTIFY, PRACK, UPDATE, INFO");
        msg.SetHeader("Accept", "application/vnd.3gpp.sms, text/plain, multipart/mixed");
        msg.SetHeader("Authorization",
            $"Digest username=\"{profile.PrivateIdentity}\", realm=\"{profile.HomeDomain}\", nonce=\"\", " +
            $"uri=\"sip:{profile.HomeDomain}\", response=\"\", algorithm=AKAv1-MD5, integrity-protected=no");
        msg.SetHeader("Supported", "path, gruu");
        msg.SetHeader("User-Agent", "voSharp/1.0.0 (Windows; C# Telephony Kernel)");
        msg.SetHeader("Content-Length", "0");

        return msg;
    }

    /// <summary>
    /// Parses a WWW-Authenticate header from a 401 response.
    /// </summary>
    /// <remarks>
    /// Fails closed on purpose: an <c>algorithm</c> other than AKAv1-MD5, or a <c>qop</c> without
    /// <c>auth</c>, is rejected rather than silently downgraded to plain RFC 2617 Digest. A P-CSCF
    /// that omits the algorithm is not speaking IMS AKA, and computing an MD5 response anyway
    /// would only produce a credential the network rejects — while leaking a valid RES.
    /// </remarks>
    public static DigestChallenge? ParseWwwAuthenticate(string headerValue)
    {
        if (!headerValue.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            return null;

        var realmMatch = Regex.Match(headerValue, @"realm=""([^""]+)""");
        var nonceMatch = Regex.Match(headerValue, @"nonce=""([^""]+)""");
        var opaqueMatch = Regex.Match(headerValue, @"opaque=""([^""]+)""");
        var algoMatch = Regex.Match(headerValue, @"algorithm=([a-zA-Z0-9\-_]+)");
        var qopMatch = Regex.Match(headerValue, @"qop=""?([a-zA-Z0-9\-_,]+)""?");

        if (!realmMatch.Success || !nonceMatch.Success)
            return null;

        // algorithm is mandatory for IMS AKA — never default it.
        if (!algoMatch.Success ||
            !algoMatch.Groups[1].Value.Equals("AKAv1-MD5", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? qop = null;
        if (qopMatch.Success)
        {
            var offered = qopMatch.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (!offered.Any(q => q.Equals("auth", StringComparison.OrdinalIgnoreCase)))
                return null;

            qop = "auth";
        }

        return new DigestChallenge(
            realmMatch.Groups[1].Value,
            nonceMatch.Groups[1].Value,
            opaqueMatch.Success ? opaqueMatch.Groups[1].Value : null,
            algoMatch.Groups[1].Value,
            qop
        );
    }

    /// <summary>
    /// Builds second 3GPP IMS SIP REGISTER with calculated Digest-AKA response.
    /// BUG-12 FIX: fromTag is passed in so it matches the initial REGISTER (RFC 3261 §8.1.1.3).
    /// </summary>
    public static SipMessage BuildAuthenticatedRegister(
        ImsProfile profile,
        string callId,
        uint cseq,
        DigestChallenge challenge,
        byte[] akaResponse,
        string fromTag,
        bool integrityProtected = false)
    {
        // BUG-12 FIX: pass the stable fromTag so From header is identical across the dialog
        var msg = BuildInitialRegister(profile, callId, cseq, fromTag);

        var creds = new DigestCredentials(
            Username: profile.PrivateIdentity,
            AkaResponse: akaResponse,
            Uri: $"sip:{profile.HomeDomain}",
            Method: "REGISTER",
            CNonce: Guid.NewGuid().ToString("N")[..16],
            Nc: 1,
            IntegrityProtected: integrityProtected
        );

        var authHeader = DigestAka.BuildAuthorizationHeader(challenge, creds);
        msg.SetHeader("Authorization", authHeader);

        return msg;
    }
}
