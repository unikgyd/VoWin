using VoSharp.Sip;

namespace VoSharp.Tests;

/// <summary>
/// Gate G3 — sec-agree (3GPP TS 33.203 / RFC 3329).
/// Vectors taken from <c>voCore/internal/vowifi/ims/security_test.go</c>.
/// </summary>
public class SecurityAgreementTests
{
    private static readonly SecurityProposal Proposal = new(
        IntegrityAlgorithm: "hmac-sha-1-96",
        EncryptionAlgorithm: "aes-cbc",
        SpiClient: 1001,
        SpiServer: 1002,
        PortClient: 40666,
        PortServer: 55610);

    /// <summary>voCore <c>security_test.go:41</c> — the exact O2 DE integrity-only offer.</summary>
    [Fact]
    public void SingleOfferMatchesO2GermanyVector()
    {
        var header = SecurityAgreementBuilder.BuildSecurityClient(
            Proposal,
            new[] { "hmac-sha-1-96" },
            new[] { "null" });

        Assert.Equal(
            "ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;prot=esp;mod=trans;ealg=null;" +
            "spi-c=0000001001;spi-s=0000001002;port-c=40666;port-s=55610",
            header);
    }

    /// <summary>TS 33.203 mandates 10-digit zero-padded decimal SPIs.</summary>
    [Fact]
    public void SpiIsPaddedToTenDigits()
    {
        var header = SecurityAgreementBuilder.BuildSecurityClient(
            Proposal, new[] { "hmac-sha-1-96" }, new[] { "null" });

        Assert.Contains("spi-c=0000001001", header, StringComparison.Ordinal);
        Assert.Contains("spi-s=0000001002", header, StringComparison.Ordinal);
    }

    /// <summary>The default Android-style offer is 2 integrity × 3 encryption = 6 mechanisms.</summary>
    [Fact]
    public void DefaultOfferContainsSixMechanismsInPreferenceOrder()
    {
        var header = SecurityAgreementBuilder.BuildSecurityClient(Proposal);

        var offers = SecurityAgreementBuilder.SplitHeaderValues(header);
        Assert.Equal(6, offers.Count);
        Assert.Contains("q=1.000", header, StringComparison.Ordinal);
        Assert.StartsWith("ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;prot=esp;mod=trans;ealg=aes-cbc", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// voCore <c>security_test.go:13</c> — the P-CSCF may return mechanisms we do not support.
    /// We select the supported one and echo the entire header back in Security-Verify.
    /// </summary>
    [Fact]
    public void SelectsSupportedMechanismAndEchoesWholeHeader()
    {
        const string securityServer =
            "digest;q=0.900, ipsec-3gpp;q=0.100;alg=hmac-sha-1-96;prot=esp;mod=trans;" +
            "ealg=aes-cbc;spi-c=2001;spi-s=2002;port-c=50601;port-s=50600";

        var agreement = SecurityAgreementBuilder.ParseSecurityServer(securityServer, Proposal);

        Assert.NotNull(agreement);
        Assert.Equal("hmac-sha-1-96", agreement!.Selected.IntegrityAlgorithm);
        Assert.Equal("aes-cbc", agreement.Selected.EncryptionAlgorithm);
        Assert.Equal(2001u, agreement.PcscfClientSpi);
        Assert.Equal(2002u, agreement.PcscfServerSpi);
        Assert.Equal(50601, agreement.PcscfClientPort);
        Assert.Equal(50600, agreement.PcscfServerPort);

        // Security-Verify must reproduce the header verbatim, unsupported mechanisms included.
        Assert.Equal(securityServer, agreement.VerifyValue);
    }

    [Fact]
    public void RejectsAMechanismWeNeverOffered()
    {
        const string securityServer =
            "ipsec-3gpp;q=1.000;alg=hmac-sha-2-256;prot=esp;mod=trans;ealg=aes-cbc;" +
            "spi-c=2001;spi-s=2002;port-c=50601;port-s=50600";

        Assert.Null(SecurityAgreementBuilder.ParseSecurityServer(securityServer, Proposal));
    }

    [Fact]
    public void RejectsTunnelModeAndNonEsp()
    {
        const string tunnelMode =
            "ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;prot=esp;mod=tunnel;ealg=aes-cbc;" +
            "spi-c=2001;spi-s=2002;port-c=50601;port-s=50600";
        const string ahProtocol =
            "ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;prot=ah;mod=trans;ealg=aes-cbc;" +
            "spi-c=2001;spi-s=2002;port-c=50601;port-s=50600";

        Assert.Null(SecurityAgreementBuilder.ParseSecurityServer(tunnelMode, Proposal));
        Assert.Null(SecurityAgreementBuilder.ParseSecurityServer(ahProtocol, Proposal));
    }

    [Fact]
    public void RejectsCollidingSpis()
    {
        // spi-s collides with the SPI we offered, which would make the SAs indistinguishable.
        const string securityServer =
            "ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;prot=esp;mod=trans;ealg=aes-cbc;" +
            "spi-c=2001;spi-s=1001;port-c=50601;port-s=50600";

        Assert.Null(SecurityAgreementBuilder.ParseSecurityServer(securityServer, Proposal));
    }

    [Fact]
    public void RejectsReservedOrDuplicatePorts()
    {
        const string reservedPort =
            "ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;prot=esp;mod=trans;ealg=aes-cbc;" +
            "spi-c=2001;spi-s=2002;port-c=5060;port-s=50600";
        const string duplicatePorts =
            "ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;prot=esp;mod=trans;ealg=aes-cbc;" +
            "spi-c=2001;spi-s=2002;port-c=50600;port-s=50600";

        Assert.Null(SecurityAgreementBuilder.ParseSecurityServer(reservedPort, Proposal));
        Assert.Null(SecurityAgreementBuilder.ParseSecurityServer(duplicatePorts, Proposal));
    }

    [Fact]
    public void PicksTheHighestQAmongSeveralSupportedMechanisms()
    {
        const string securityServer =
            "ipsec-3gpp;q=0.100;alg=hmac-md5-96;prot=esp;mod=trans;ealg=aes-cbc;" +
            "spi-c=3001;spi-s=3002;port-c=50701;port-s=50700, " +
            "ipsec-3gpp;q=0.900;alg=hmac-sha-1-96;prot=esp;mod=trans;ealg=aes-cbc;" +
            "spi-c=2001;spi-s=2002;port-c=50601;port-s=50600";

        var agreement = SecurityAgreementBuilder.ParseSecurityServer(securityServer, Proposal);

        Assert.NotNull(agreement);
        Assert.Equal("hmac-sha-1-96", agreement!.Selected.IntegrityAlgorithm);
        Assert.Equal(2001u, agreement.PcscfClientSpi);
    }

    // ── Key expansion (voCore security_test.go:168-189) ──────────────────────

    [Theory]
    [InlineData("hmac-md5-96", 16)]
    [InlineData("hmac-sha-1-96", 20)]
    public void IntegrityKeyLengths(string algorithm, int expectedLength)
    {
        var ik = new byte[16];
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }.CopyTo(ik, 0);
        var ck = new byte[16];

        var (integrityKey, _) = SecurityAgreementBuilder.ExpandKeys(ck, ik, algorithm, "null");

        Assert.Equal(expectedLength, integrityKey.Length);
    }

    [Fact]
    public void Sha1IntegrityKeyIsIkPaddedWithFourZeros()
    {
        var ik = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var ck = new byte[16];

        var (integrityKey, _) = SecurityAgreementBuilder.ExpandKeys(ck, ik, "hmac-sha-1-96", "null");

        Assert.Equal(20, integrityKey.Length);
        Assert.Equal(ik, integrityKey[..16]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, integrityKey[16..]);
    }

    [Theory]
    [InlineData("null", 0)]
    [InlineData("aes-cbc", 16)]
    [InlineData("des-ede3-cbc", 24)]
    public void EncryptionKeyLengths(string algorithm, int expectedLength)
    {
        var ck = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var ik = new byte[16];

        var (_, encryptionKey) = SecurityAgreementBuilder.ExpandKeys(ck, ik, "hmac-md5-96", algorithm);

        Assert.Equal(expectedLength, encryptionKey.Length);
    }

    [Fact]
    public void AesEncryptionKeyIsCkVerbatim()
    {
        var ck = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var ik = new byte[16];

        var (_, encryptionKey) = SecurityAgreementBuilder.ExpandKeys(ck, ik, "hmac-sha-1-96", "aes-cbc");

        Assert.Equal(ck, encryptionKey);
    }

    [Fact]
    public void ThreeDesKeyHasOddParityOnTheAppendedBytes()
    {
        var ck = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var ik = new byte[16];

        var (_, encryptionKey) = SecurityAgreementBuilder.ExpandKeys(ck, ik, "hmac-sha-1-96", "des-ede3-cbc");

        Assert.Equal(24, encryptionKey.Length);
        for (var i = 16; i < 24; i++)
            Assert.Equal(1, System.Numerics.BitOperations.PopCount(encryptionKey[i]) & 1);
    }

    // ── Misc ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SplitHeaderValuesRespectsQuotesAndAngleBrackets()
    {
        var parts = SecurityAgreementBuilder.SplitHeaderValues(
            "a;b=\"x,y\", c;d=<sip:z;lr>, e");

        Assert.Equal(new[] { "a;b=\"x,y\"", "c;d=<sip:z;lr>", "e" }, parts);
    }

    [Theory]
    [InlineData(5060, false)]
    [InlineData(5061, false)]
    [InlineData(1024, false)]
    [InlineData(1025, true)]
    [InlineData(65535, true)]
    public void ProtectedPortValidation(int port, bool expected) =>
        Assert.Equal(expected, SecurityAgreementBuilder.IsValidProtectedPort(port));

    [Fact]
    public void PairRequestIdIsStableAndNonZero()
    {
        var first = SecurityAgreementBuilder.PairRequestId(1001u, 2002u);
        var second = SecurityAgreementBuilder.PairRequestId(1001u, 2002u);

        Assert.Equal(first, second);
        Assert.NotEqual(0u, first);
        Assert.NotEqual(0u, SecurityAgreementBuilder.PairRequestId(7u, 7u));
    }
}
