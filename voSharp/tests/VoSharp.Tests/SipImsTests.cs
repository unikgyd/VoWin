using System.Text;
using VoSharp.Crypto;

namespace VoSharp.Tests;

/// <summary>
/// Gate G3 — anchors the IMS authentication layer against published vectors.
/// Vectors are taken from <c>voCore/internal/vowifi/ims/digest_test.go</c>, which in turn
/// uses the RFC 2617 reference values.
/// </summary>
public class SipImsTests
{
    /// <summary>
    /// RFC 2617 §3.5 reference vector. The "password" slot is where IMS puts the raw USIM RES,
    /// so proving this proves the whole HA1 construction.
    /// </summary>
    [Fact]
    public void ComputeResponseMatchesRfc2617Vector()
    {
        var response = DigestAka.ComputeResponse(
            username: "Mufasa",
            realm: "testrealm@host.com",
            akaResponse: Encoding.UTF8.GetBytes("Circle Of Life"),
            method: "GET",
            uri: "/dir/index.html",
            nonce: "dcd98b7102dd2f0e8b11d0f600bfb0c093",
            nc: "00000001",
            cnonce: "0a4f113b",
            qop: "auth");

        Assert.Equal("6629fae49393a05397450978507c4ef1", response);
    }

    /// <summary>RFC 2069 fallback: with no qop the nc/cnonce segments drop out.</summary>
    [Fact]
    public void ComputeResponseFallsBackToRfc2069WhenNoQop()
    {
        var response = DigestAka.ComputeResponse(
            username: "Mufasa",
            realm: "testrealm@host.com",
            akaResponse: Encoding.UTF8.GetBytes("Circle Of Life"),
            method: "GET",
            uri: "/dir/index.html",
            nonce: "dcd98b7102dd2f0e8b11d0f600bfb0c093",
            nc: "00000001",
            cnonce: "0a4f113b",
            qop: null);

        Assert.Equal(Md5(
            Md5("Mufasa:testrealm@host.com:Circle Of Life") + ":" +
            "dcd98b7102dd2f0e8b11d0f600bfb0c093" + ":" +
            Md5("GET:/dir/index.html")), response);
    }

    /// <summary>
    /// RES is binary, not a hex string. Passing the hex text is the single most common
    /// AKAv1-MD5 implementation error, so assert the two forms differ.
    /// </summary>
    [Fact]
    public void ResponseUsesRawResBytesNotHexText()
    {
        var res = new byte[] { 0xd1, 0xe2, 0xa3, 0xc4, 0xb5, 0xa6, 0x97, 0x88 };

        var withBinary = DigestAka.ComputeResponse("u", "r", res, "REGISTER", "sip:r",
            "nonce", "00000001", "cnonce", "auth");

        var hexText = Encoding.UTF8.GetBytes(Convert.ToHexString(res));
        var withHexText = DigestAka.ComputeResponse("u", "r", hexText, "REGISTER", "sip:r",
            "nonce", "00000001", "cnonce", "auth");

        Assert.NotEqual(withBinary, withHexText);
    }

    [Fact]
    public void AuthorizationHeaderLeavesAlgorithmQopAndNcUnquoted()
    {
        var header = DigestAka.BuildAuthorizationHeader(
            new DigestChallenge(
                Realm: "ims.mnc001.mcc001.3gppnetwork.org",
                Nonce: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                Opaque: null,
                Algorithm: "AKAv1-MD5",
                Qop: "auth"),
            new DigestCredentials(
                Username: "001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
                AkaResponse: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                Uri: "sip:ims.mnc001.mcc001.3gppnetwork.org",
                Method: "REGISTER",
                CNonce: "0a4f113b",
                Nc: 1));

        Assert.Contains("algorithm=AKAv1-MD5", header, StringComparison.Ordinal);
        Assert.Contains("qop=auth", header, StringComparison.Ordinal);
        Assert.Contains("nc=00000001", header, StringComparison.Ordinal);
        Assert.Contains("cnonce=\"0a4f113b\"", header, StringComparison.Ordinal);

        // Nothing that must stay a bare token may be quoted.
        Assert.DoesNotContain("algorithm=\"", header, StringComparison.Ordinal);
        Assert.DoesNotContain("qop=\"", header, StringComparison.Ordinal);
        Assert.DoesNotContain("nc=\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void NcIsFormattedAsEightHexDigits()
    {
        var header = DigestAka.BuildAuthorizationHeader(
            new DigestChallenge("r", "n", null, "AKAv1-MD5", "auth"),
            new DigestCredentials("u", new byte[8], "sip:r", "REGISTER", "c", Nc: 0x1234));

        Assert.Contains("nc=00001234", header, StringComparison.Ordinal);
    }

    // ── Challenge parsing must fail closed ───────────────────────────────────

    [Theory]
    [InlineData("Digest realm=\"r\", nonce=\"n\"")]                                   // no algorithm
    [InlineData("Digest realm=\"r\", nonce=\"n\", algorithm=MD5")]                    // plain Digest
    [InlineData("Digest realm=\"r\", nonce=\"n\", algorithm=AKAv1-MD5, qop=\"auth-int\"")]
    [InlineData("Basic realm=\"r\"")]
    public void ChallengeIsRejectedWhenNotImsAka(string header) =>
        Assert.Null(VoSharp.Sip.ImsRegisterBuilder.ParseWwwAuthenticate(header));

    [Fact]
    public void ChallengeIsAcceptedWithImsAkaAndNormalisesQop()
    {
        var challenge = VoSharp.Sip.ImsRegisterBuilder.ParseWwwAuthenticate(
            "Digest realm=\"ims.mnc001.mcc001.3gppnetwork.org\", nonce=\"AAAA\", " +
            "algorithm=AKAv1-MD5, qop=\"auth,auth-int\"");

        Assert.NotNull(challenge);
        Assert.Equal("AKAv1-MD5", challenge!.Algorithm);
        Assert.Equal("auth", challenge.Qop);
    }

    // ── integrity-protected ──────────────────────────────────────────────────

    [Fact]
    public void AuthorizationHeaderCarriesIntegrityProtectedFlag()
    {
        var challenge = new DigestChallenge("r", "n", null, "AKAv1-MD5", "auth");

        var unprotected = DigestAka.BuildAuthorizationHeader(challenge,
            new DigestCredentials("u", new byte[8], "sip:r", "REGISTER", "c", 1, null, IntegrityProtected: false));
        var protectedLeg = DigestAka.BuildAuthorizationHeader(challenge,
            new DigestCredentials("u", new byte[8], "sip:r", "REGISTER", "c", 1, null, IntegrityProtected: true));

        Assert.Contains("integrity-protected=no", unprotected, StringComparison.Ordinal);
        Assert.Contains("integrity-protected=yes", protectedLeg, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationHeaderOmitsIntegrityProtectedWhenUnspecified()
    {
        var header = DigestAka.BuildAuthorizationHeader(
            new DigestChallenge("r", "n", null, "AKAv1-MD5", "auth"),
            new DigestCredentials("u", new byte[8], "sip:r", "REGISTER", "c", 1));

        Assert.DoesNotContain("integrity-protected", header, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialRegisterDeclaresAlgorithmAndUnprotectedIntegrity()
    {
        var profile = new VoSharp.Sip.ImsProfile(
            PrivateIdentity: "001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
            PublicIdentity: "sip:001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
            HomeDomain: "ims.mnc001.mcc001.3gppnetwork.org",
            Imei: "860000000000001",
            LocalIp: "10.0.0.5",
            LocalPort: 5060);

        var message = VoSharp.Sip.ImsRegisterBuilder.BuildInitialRegister(profile, "callid", 1, "fromtag");
        var authorization = message.GetHeader("Authorization");

        Assert.NotNull(authorization);
        Assert.Contains("algorithm=AKAv1-MD5", authorization, StringComparison.Ordinal);
        Assert.Contains("integrity-protected=no", authorization, StringComparison.Ordinal);
        Assert.Contains("nonce=\"\"", authorization, StringComparison.Ordinal);
    }

    private static string Md5(string text)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
