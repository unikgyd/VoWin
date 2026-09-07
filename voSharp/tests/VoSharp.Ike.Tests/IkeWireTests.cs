using System.Buffers.Binary;
using VoSharp.Ike;

namespace VoSharp.Ike.Tests;

/// <summary>
/// Offline verification of the IKEv2 codec (gate G5 for the B′ C# stack).
/// </summary>
/// <remarks>
/// The previous implementation (VoSharp.Telephony.Ikev2Protocol) wrote SPIs and lengths in
/// the wrong byte order (BUG-03) and emitted an all-zero SA payload (BUG-04). These tests
/// pin the on-the-wire bytes so neither regression can come back.
/// </remarks>
public class IkeWireTests
{
    // ── Header ──────────────────────────────────────────────────────────────

    [Fact]
    public void HeaderUsesBigEndianForSpiAndLength()
    {
        var message = new IkeWire.IkeMessage
        {
            InitiatorSpi = 0x0102030405060708,
            ResponderSpi = 0,
            Exchange = IkeExchangeType.IkeSaInit,
            Flags = IkeFlags.Initiator,
            MessageId = 0
        };

        var bytes = IkeWire.SerializeMessage(message);

        // RFC 7296 §3.1: SPIi, SPIr, Message-ID and Length are all big-endian.
        Assert.Equal("0102030405060708", Convert.ToHexString(bytes[..8]).ToLowerInvariant());
        Assert.Equal("0000000000000000", Convert.ToHexString(bytes[8..16]).ToLowerInvariant());
        Assert.Equal(28u, BinaryPrimitives.ReadUInt32BigEndian(bytes[24..28]));
    }

    [Fact]
    public void HeaderRoundTrips()
    {
        var message = new IkeWire.IkeMessage
        {
            InitiatorSpi = 0x1122334455667788,
            ResponderSpi = 0x99aabbccddeeff00,
            Exchange = IkeExchangeType.IkeAuth,
            Flags = IkeFlags.Initiator | IkeFlags.Response,
            MessageId = 7
        };

        var parsed = IkeWire.ParseMessage(IkeWire.SerializeMessage(message));

        Assert.Equal(message.InitiatorSpi, parsed.InitiatorSpi);
        Assert.Equal(message.ResponderSpi, parsed.ResponderSpi);
        Assert.Equal(message.Exchange, parsed.Exchange);
        Assert.Equal(message.Flags, parsed.Flags);
        Assert.Equal(message.MessageId, parsed.MessageId);
    }

    [Fact]
    public void ParseRejectsAConflictingLengthField()
    {
        var bytes = IkeWire.SerializeMessage(new IkeWire.IkeMessage
        {
            InitiatorSpi = 1,
            Exchange = IkeExchangeType.IkeSaInit,
            Flags = IkeFlags.Initiator
        });

        // Corrupt the length field: it must match the buffer or the message is malformed.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24, 4), 999);

        Assert.Throws<IkeFormatException>(() => IkeWire.ParseMessage(bytes));
    }

    [Fact]
    public void ParseRejectsNonIkev2Version()
    {
        var bytes = IkeWire.SerializeMessage(new IkeWire.IkeMessage
        {
            Exchange = IkeExchangeType.IkeSaInit,
            Flags = IkeFlags.Initiator
        });
        bytes[17] = 0x10;   // IKEv1

        Assert.Throws<IkeFormatException>(() => IkeWire.ParseMessage(bytes));
    }

    // ── Payload chaining ────────────────────────────────────────────────────

    [Fact]
    public void PayloadsAreChainedThroughNextPayloadBytes()
    {
        var message = new IkeWire.IkeMessage
        {
            Exchange = IkeExchangeType.IkeSaInit,
            Flags = IkeFlags.Initiator
        };
        message.Payloads.Add(new IkePayload(IkePayloadType.SecurityAssociation, new byte[] { 0xAA }));
        message.Payloads.Add(new IkePayload(IkePayloadType.KeyExchange, new byte[] { 0xBB }));
        message.Payloads.Add(new IkePayload(IkePayloadType.Nonce, new byte[] { 0xCC }));

        var bytes = IkeWire.SerializeMessage(message);
        var parsed = IkeWire.ParseMessage(bytes);

        Assert.Equal(3, parsed.Payloads.Count);
        Assert.Equal(IkePayloadType.SecurityAssociation, parsed.Payloads[0].Type);
        Assert.Equal(IkePayloadType.KeyExchange, parsed.Payloads[1].Type);
        Assert.Equal(IkePayloadType.Nonce, parsed.Payloads[2].Type);

        // The message header points at the first payload; each payload points at the next.
        Assert.Equal((byte)IkePayloadType.SecurityAssociation, bytes[16]);
        Assert.Equal((byte)IkePayloadType.KeyExchange, bytes[28]);
        Assert.Equal((byte)IkePayloadType.Nonce, bytes[33]);
        Assert.Equal((byte)IkePayloadType.None, bytes[38]);
    }

    [Fact]
    public void PayloadLengthCoversHeaderPlusBody()
    {
        var message = new IkeWire.IkeMessage { Exchange = IkeExchangeType.IkeSaInit };
        message.Payloads.Add(new IkePayload(IkePayloadType.Nonce, new byte[32]));

        var bytes = IkeWire.SerializeMessage(message);

        // Generic payload header is 4 bytes; the length field includes it.
        Assert.Equal(36, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(30, 2)));
    }

    // ── SA payload ──────────────────────────────────────────────────────────

    [Fact]
    public void KeyLengthAttributeIsEncodedAsTypeValue()
    {
        // On the wire: AF=1, type 14 (Key Length), then the value 128 => 800E 0080.
        var encoded = IkeWire.EncodeProposals(new[]
        {
            new IkeProposal
            {
                ProposalNumber = 1,
                Protocol = IkeProtocolId.Ike,
                Transforms =
                {
                    new IkeTransform(IkeTransformType.Encryption, IkeEncryptionId.AesCbc, 128)
                }
            }
        });

        var attribute = Convert.ToHexString(encoded[^4..]).ToLowerInvariant();
        Assert.Equal("800e0080", attribute);
    }

    [Fact]
    public void ProposalRoundTripsWithSpiAndTransforms()
    {
        var proposal = new IkeProposal
        {
            ProposalNumber = 1,
            Protocol = IkeProtocolId.Esp,
            Spi = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            Transforms =
            {
                new IkeTransform(IkeTransformType.Encryption, IkeEncryptionId.AesCbc, 128),
                new IkeTransform(IkeTransformType.Integrity, IkeIntegrityId.HmacSha1_96),
                new IkeTransform(IkeTransformType.ExtendedSequenceNumbers, 0)
            }
        };

        var decoded = IkeWire.DecodeProposals(IkeWire.EncodeProposals(new[] { proposal }));

        Assert.Single(decoded);
        Assert.Equal(IkeProtocolId.Esp, decoded[0].Protocol);
        Assert.Equal(1, decoded[0].ProposalNumber);
        Assert.Equal("DEADBEEF", Convert.ToHexString(decoded[0].Spi));
        Assert.Equal(3, decoded[0].Transforms.Count);

        Assert.Equal(IkeTransformType.Encryption, decoded[0].Transforms[0].Type);
        Assert.Equal(IkeEncryptionId.AesCbc, decoded[0].Transforms[0].Id);
        Assert.Equal((ushort)128, decoded[0].Transforms[0].KeyLengthBits);

        // Transforms without a key length must not gain one during the round trip.
        Assert.Null(decoded[0].Transforms[1].KeyLengthBits);
    }

    [Fact]
    public void MultipleProposalsMarkOnlyTheLastOneAsLast()
    {
        var proposals = new[]
        {
            new IkeProposal
            {
                ProposalNumber = 1,
                Protocol = IkeProtocolId.Ike,
                Transforms = { new IkeTransform(IkeTransformType.Encryption, IkeEncryptionId.AesCbc, 256) }
            },
            new IkeProposal
            {
                ProposalNumber = 2,
                Protocol = IkeProtocolId.Ike,
                Transforms = { new IkeTransform(IkeTransformType.Encryption, IkeEncryptionId.AesCbc, 128) }
            }
        };

        var encoded = IkeWire.EncodeProposals(proposals);

        // "More proposals follow" is 2; the last proposal carries 0.
        Assert.Equal(2, encoded[0]);

        var secondOffset = BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(2, 2));
        Assert.Equal(0, encoded[secondOffset]);

        Assert.Equal(2, IkeWire.DecodeProposals(encoded).Count);
    }

    [Fact]
    public void IkeProposalCarriesAnEmptySpi()
    {
        // For protocol IKE the SPI lives in the message header, so the in-proposal SPI
        // size must be zero.
        var encoded = IkeWire.EncodeProposals(new[]
        {
            new IkeProposal
            {
                ProposalNumber = 1,
                Protocol = IkeProtocolId.Ike,
                Transforms = { new IkeTransform(IkeTransformType.Encryption, IkeEncryptionId.AesCbc, 128) }
            }
        });

        Assert.Equal(0, encoded[6]);   // SPI size
    }

    [Fact]
    public void TruncatedProposalIsRejected()
    {
        var encoded = IkeWire.EncodeProposals(new[]
        {
            new IkeProposal
            {
                Protocol = IkeProtocolId.Ike,
                Transforms = { new IkeTransform(IkeTransformType.Encryption, IkeEncryptionId.AesCbc, 128) }
            }
        });

        Assert.Throws<IkeFormatException>(() => IkeWire.DecodeProposals(encoded.AsSpan(0, 4)));
    }
}

public class ModpGroupTests
{
    [Theory]
    [InlineData((ushort)IkeDhGroupId.Modp1024, 128)]
    [InlineData((ushort)IkeDhGroupId.Modp2048, 256)]
    public void PublicValueIsPaddedToTheGroupSize(ushort group, int expectedBytes)
    {
        var dh = ModpGroup.Create(group);

        Assert.Equal(expectedBytes, dh.Public.Length);
        Assert.Equal(group, dh.GroupId);
    }

    [Theory]
    [InlineData((ushort)IkeDhGroupId.Modp1024)]
    [InlineData((ushort)IkeDhGroupId.Modp2048)]
    public void BothPartiesDeriveTheSameSecret(ushort group)
    {
        var initiator = ModpGroup.Create(group);
        var responder = ModpGroup.Create(group);

        var initiatorSecret = initiator.ComputeSharedSecret(responder.Public);
        var responderSecret = responder.ComputeSharedSecret(initiator.Public);

        Assert.Equal(initiatorSecret, responderSecret);
    }

    [Fact]
    public void PublicValuesDifferBetweenInstances()
    {
        // Guards against a hardcoded or uninitialised keypair.
        var first = ModpGroup.Create2048();
        var second = ModpGroup.Create2048();

        Assert.NotEqual(first.Public, second.Public);
    }

    [Fact]
    public void SharedSecretIsPaddedLikeThePublicValue()
    {
        var initiator = ModpGroup.Create2048();
        var responder = ModpGroup.Create2048();

        Assert.Equal(256, initiator.ComputeSharedSecret(responder.Public).Length);
    }

    [Fact]
    public void OutOfRangePeerPublicIsRejected()
    {
        var dh = ModpGroup.Create2048();

        Assert.Throws<IkeFormatException>(() => dh.ComputeSharedSecret(new byte[256]));   // zero
        Assert.Throws<IkeFormatException>(() => dh.ComputeSharedSecret(Enumerable.Repeat((byte)0xFF, 256).ToArray()));
    }

    [Fact]
    public void UnsupportedGroupIsRejected()
    {
        Assert.Throws<NotSupportedException>(() => ModpGroup.Create(IkeDhGroupId.Ecp256));
    }
}
