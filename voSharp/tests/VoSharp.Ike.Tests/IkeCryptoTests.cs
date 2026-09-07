using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace VoSharp.Ike.Tests;

public class IkeCryptoTests
{
    private static IkeSuite LegacySuite => new(
        IkeEncryptionId.AesCbc,
        128,
        IkePrfId.HmacSha1,
        IkeIntegrityId.HmacSha1_96,
        IkeDhGroupId.Modp1024
    );

    [Fact]
    public void IkeKeyDerivation_SeparatesDirectionsAndKeyLengths()
    {
        var suite = LegacySuite;
        var sharedSecret = new byte[128];
        Array.Fill(sharedSecret, (byte)0x44);

        var ni = new byte[32];
        Array.Fill(ni, (byte)0x55);

        var nr = new byte[32];
        Array.Fill(nr, (byte)0x66);

        var keys = IkeCrypto.DeriveIkeKeys(
            suite,
            sharedSecret,
            ni,
            nr,
            initiatorSpi: 1,
            responderSpi: 2
        );

        Assert.Equal(20, keys.SkD.Length);
        Assert.Equal(20, keys.SkAi.Length);
        Assert.Equal(20, keys.SkAr.Length);
        Assert.Equal(16, keys.SkEi.Length);
        Assert.Equal(16, keys.SkEr.Length);
        Assert.Equal(20, keys.SkPi.Length);
        Assert.Equal(20, keys.SkPr.Length);

        // Initiator and responder keys must be separated
        Assert.False(keys.SkAi.SequenceEqual(keys.SkAr));
        Assert.False(keys.SkEi.SequenceEqual(keys.SkEr));
        Assert.False(keys.SkPi.SequenceEqual(keys.SkPr));
    }

    [Fact]
    public void EncryptedPayload_RoundTripAndTamperDetection()
    {
        var suite = LegacySuite;
        var encryptionKey = new byte[16];
        Array.Fill(encryptionKey, (byte)0x11);

        var integrityKey = new byte[20];
        Array.Fill(integrityKey, (byte)0x22);

        var header = new IkeWire.IkeMessage
        {
            InitiatorSpi = 0x0102030405060708,
            ResponderSpi = 0x0807060504030201,
            Exchange = IkeExchangeType.IkeAuth,
            Flags = IkeFlags.Initiator,
            MessageId = 7
        };

        var inner = new List<IkePayload>
        {
            new(IkePayloadType.IdentificationInitiator, new byte[] { 3, 0, 0, 0, (byte)'u', (byte)'@', (byte)'r' }),
            new(IkePayloadType.Eap, new byte[] { 1, 9, 0, 5, 1 })
        };

        var packet = IkeCrypto.EncryptPayloads(header, inner, suite, encryptionKey, integrityKey);

        var (decodedHeader, decoded) = IkeCrypto.DecryptPayloads(packet, suite, encryptionKey, integrityKey);

        Assert.Equal(header.MessageId, decodedHeader.MessageId);
        Assert.Equal(header.InitiatorSpi, decodedHeader.InitiatorSpi);
        Assert.Equal(header.ResponderSpi, decodedHeader.ResponderSpi);
        Assert.Equal(inner.Count, decoded.Count);

        for (var i = 0; i < inner.Count; i++)
        {
            Assert.Equal(inner[i].Type, decoded[i].Type);
            Assert.True(inner[i].Body.SequenceEqual(decoded[i].Body));
        }

        // Tampered byte in packet must trigger ICV integrity check failure
        var tampered = (byte[])packet.Clone();
        tampered[^1] ^= 0x80;

        Assert.Throws<CryptographicException>(() =>
            IkeCrypto.DecryptPayloads(tampered, suite, encryptionKey, integrityKey));
    }

    [Fact]
    public void EmptyInformational_EncryptsAndDecryptsForIkeDpd()
    {
        var header = new IkeWire.IkeMessage
        {
            InitiatorSpi = 0x0102030405060708,
            ResponderSpi = 0x0807060504030201,
            Exchange = IkeExchangeType.Informational,
            Flags = IkeFlags.Initiator,
            MessageId = 8
        };
        var encryptionKey = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var integrityKey = Enumerable.Repeat((byte)0x22, 20).ToArray();

        var packet = IkeCrypto.EncryptPayloads(header, Array.Empty<IkePayload>(), LegacySuite, encryptionKey, integrityKey);
        var (decodedHeader, payloads) = IkeCrypto.DecryptPayloads(packet, LegacySuite, encryptionKey, integrityKey);

        Assert.Equal(IkeExchangeType.Informational, decodedHeader.Exchange);
        Assert.Equal(header.MessageId, decodedHeader.MessageId);
        Assert.Empty(payloads);
    }

    [Fact]
    public void ChildSaKeyDerivation_ProducesExpectedLengths()
    {
        var ikeSuite = IkeSuite.Preferred;
        var espSuite = EspSuite.Preferred;

        var skD = new byte[ikeSuite.PrfLength];
        Array.Fill(skD, (byte)0x77);

        var ni = new byte[32];
        Array.Fill(ni, (byte)0x88);

        var nr = new byte[32];
        Array.Fill(nr, (byte)0x99);

        var (outEnc, outAuth, inEnc, inAuth) = IkeCrypto.DeriveChildSaKeys(
            ikeSuite, espSuite, skD, ni, nr);

        Assert.Equal(espSuite.EncryptionKeyLength, outEnc.Length);
        Assert.Equal(espSuite.IntegrityKeyLength, outAuth.Length);
        Assert.Equal(espSuite.EncryptionKeyLength, inEnc.Length);
        Assert.Equal(espSuite.IntegrityKeyLength, inAuth.Length);

        Assert.False(outEnc.SequenceEqual(inEnc));
        Assert.False(outAuth.SequenceEqual(inAuth));
    }

    [Fact]
    public void NatDetectionHash_IsDeterministic()
    {
        var ip = IPAddress.Parse("192.168.1.100");
        var hash1 = IkeCrypto.ComputeNatDetectionHash(12345, 67890, ip, 500);
        var hash2 = IkeCrypto.ComputeNatDetectionHash(12345, 67890, ip, 500);

        Assert.Equal(20, hash1.Length);
        Assert.True(hash1.SequenceEqual(hash2));

        var hashOtherPort = IkeCrypto.ComputeNatDetectionHash(12345, 67890, ip, 4500);
        Assert.False(hash1.SequenceEqual(hashOtherPort));
    }
}
