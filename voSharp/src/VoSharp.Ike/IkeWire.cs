using System.Buffers.Binary;

namespace VoSharp.Ike;

/// <summary>One IKEv2 payload: a 4-byte generic header plus the type-specific body.</summary>
public sealed record IkePayload(IkePayloadType Type, byte[] Body, bool Critical = false)
{
    /// <summary>
    /// Value to write into this payload's "next payload" header byte, overriding the
    /// position-derived chaining.
    /// </summary>
    /// <remarks>
    /// Only the Encrypted (SK) payload needs this: its next-payload byte announces the first
    /// <em>inner</em> payload type, not whatever follows SK in the outer message (RFC 7296 3.14).
    /// Parsing fills it in for every payload, so a decryptor can read where the inner chain starts.
    /// </remarks>
    public IkePayloadType? NextPayloadOverride { get; init; }
}

/// <summary>One transform inside a proposal.</summary>
public sealed record IkeTransform(byte Type, ushort Id, ushort? KeyLengthBits = null);

/// <summary>One proposal inside an SA payload.</summary>
public sealed class IkeProposal
{
    public byte ProposalNumber { get; init; }

    public IkeProtocolId Protocol { get; init; }

    /// <summary>
    /// SPI carried inside the proposal. Empty for <see cref="IkeProtocolId.Ike"/>
    /// (the SPI lives in the message header), 4 bytes for ESP/AH.
    /// </summary>
    public byte[] Spi { get; init; } = Array.Empty<byte>();

    public List<IkeTransform> Transforms { get; init; } = new();
}

/// <summary>
/// IKEv2 message and SA-payload codec (RFC 7296 §3.1–§3.3).
/// </summary>
/// <remarks>
/// All multi-byte fields are big-endian, per RFC 7296 §3.1. The previous implementation
/// in <c>VoSharp.Telephony.Ikev2Protocol</c> mixed little- and big-endian writes
/// (BUG-03) and emitted an all-zero SA payload (BUG-04); this codec replaces that code
/// and is covered by round-trip tests using known on-the-wire byte sequences.
/// </remarks>
public static class IkeWire
{
    // ── Message header ──────────────────────────────────────────────────────

    public sealed class IkeMessage
    {
        public ulong InitiatorSpi { get; set; }
        public ulong ResponderSpi { get; set; }
        public IkeExchangeType Exchange { get; set; }
        public IkeFlags Flags { get; set; }
        public uint MessageId { get; set; }
        public byte Version { get; set; } = IkeDefaults.Version;
        public List<IkePayload> Payloads { get; } = new();
    }

    public static IkeMessage ParseMessage(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < IkeDefaults.HeaderLength)
            throw new IkeFormatException(
                $"IKE header needs {IkeDefaults.HeaderLength} bytes, got {buffer.Length}.");

        var version = buffer[17];
        if ((version >> 4) != 2)
            throw new IkeFormatException($"Unsupported IKE version 0x{version:X2} (expected 2.x).");

        var message = new IkeMessage
        {
            InitiatorSpi = BinaryPrimitives.ReadUInt64BigEndian(buffer[..8]),
            ResponderSpi = BinaryPrimitives.ReadUInt64BigEndian(buffer[8..16]),
            Version = version,
            Exchange = (IkeExchangeType)buffer[18],
            Flags = (IkeFlags)buffer[19],
            MessageId = BinaryPrimitives.ReadUInt32BigEndian(buffer[20..24])
        };

        var declared = BinaryPrimitives.ReadUInt32BigEndian(buffer[24..28]);
        if (declared != buffer.Length)
            throw new IkeFormatException($"IKE length field {declared} != buffer length {buffer.Length}.");

        message.Payloads.AddRange(
            ParsePayloadChain(buffer[IkeDefaults.HeaderLength..], (IkePayloadType)buffer[16]));

        return message;
    }

    /// <summary>
    /// Walks a chain of payloads. The type of the first one comes from outside the chain
    /// (the message header, or the SK payload's next-payload byte); after that each payload's
    /// own header names its successor.
    /// </summary>
    public static List<IkePayload> ParsePayloadChain(ReadOnlySpan<byte> buffer, IkePayloadType first)
    {
        var payloads = new List<IkePayload>();
        var next = first;
        var offset = 0;

        while (next != IkePayloadType.None && offset < buffer.Length)
        {
            var (payload, consumed) = ParsePayload(buffer[offset..], next);
            payloads.Add(payload);

            // The "next payload" byte belongs to the preceding payload's header.
            next = (IkePayloadType)buffer[offset];
            offset += consumed;
        }

        return payloads;
    }

    /// <summary>
    /// Encodes a payload chain on its own, without a message header. Used for the plaintext
    /// inside an SK payload, whose inner payloads chain exactly like outer ones.
    /// </summary>
    public static byte[] SerializePayloadChain(IReadOnlyList<IkePayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        var bodies = new List<byte[]>(payloads.Count);
        var total = 0;

        for (var i = 0; i < payloads.Count; i++)
        {
            var nextType = i + 1 < payloads.Count ? payloads[i + 1].Type : IkePayloadType.None;
            var body = SerializePayload(payloads[i], nextType);
            bodies.Add(body);
            total += body.Length;
        }

        var chain = new byte[total];
        var at = 0;
        foreach (var body in bodies)
        {
            body.CopyTo(chain, at);
            at += body.Length;
        }

        return chain;
    }

    public static byte[] SerializeMessage(IkeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var chain = SerializePayloadChain(message.Payloads);
        var total = IkeDefaults.HeaderLength + chain.Length;

        var buffer = new byte[total];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt64BigEndian(span[..8], message.InitiatorSpi);
        BinaryPrimitives.WriteUInt64BigEndian(span[8..16], message.ResponderSpi);
        span[16] = (byte)(message.Payloads.Count > 0 ? message.Payloads[0].Type : IkePayloadType.None);
        span[17] = message.Version;
        span[18] = (byte)message.Exchange;
        span[19] = (byte)message.Flags;
        BinaryPrimitives.WriteUInt32BigEndian(span[20..24], message.MessageId);
        BinaryPrimitives.WriteUInt32BigEndian(span[24..28], (uint)total);

        chain.CopyTo(span[IkeDefaults.HeaderLength..]);

        return buffer;
    }

    // ── Generic payload ─────────────────────────────────────────────────────

    private static byte[] SerializePayload(IkePayload payload, IkePayloadType nextType)
    {
        var length = IkeDefaults.GenericPayloadHeaderLength + payload.Body.Length;
        var buffer = new byte[length];

        buffer[0] = (byte)(payload.NextPayloadOverride ?? nextType);
        buffer[1] = (byte)(payload.Critical ? 0x80 : 0x00);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)length);
        payload.Body.CopyTo(buffer.AsSpan(4));

        return buffer;
    }

    private static (IkePayload Payload, int Consumed) ParsePayload(
        ReadOnlySpan<byte> buffer, IkePayloadType type)
    {
        if (buffer.Length < IkeDefaults.GenericPayloadHeaderLength)
            throw new IkeFormatException($"Truncated payload header for {type}.");

        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..4]);
        if (length < IkeDefaults.GenericPayloadHeaderLength || length > buffer.Length)
            throw new IkeFormatException($"Payload {type} declares length {length} beyond the buffer.");

        var critical = (buffer[1] & 0x80) != 0;
        var body = buffer[4..length].ToArray();

        return (
            new IkePayload(type, body, critical) { NextPayloadOverride = (IkePayloadType)buffer[0] },
            length);
    }

    // ── SA payload: proposals ───────────────────────────────────────────────

    /// <summary>
    /// Encodes the body of an SA payload: one or more proposal substructures.
    /// </summary>
    public static byte[] EncodeProposals(IReadOnlyList<IkeProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Count == 0)
            throw new IkeFormatException("An SA payload must carry at least one proposal.");

        using var stream = new MemoryStream();

        for (var i = 0; i < proposals.Count; i++)
        {
            var isLastProposal = i + 1 == proposals.Count;
            stream.Write(EncodeProposal(proposals[i], isLastProposal));
        }

        return stream.ToArray();
    }

    /// <summary>Decodes an SA payload body back into proposals.</summary>
    public static List<IkeProposal> DecodeProposals(ReadOnlySpan<byte> body)
    {
        var proposals = new List<IkeProposal>();
        var offset = 0;

        while (offset < body.Length)
        {
            var (proposal, consumed, isLast) = DecodeProposal(body[offset..]);
            proposals.Add(proposal);
            offset += consumed;

            if (isLast) break;
        }

        return proposals;
    }

    private static byte[] EncodeProposal(IkeProposal proposal, bool isLast)
    {
        const int headerLength = 8;   // Last/Resv/Len/Num/ProtoID/SPISize/NumTransforms
        var spiSize = (byte)proposal.Spi.Length;

        var transforms = new List<byte[]>(proposal.Transforms.Count);
        var transformBytes = 0;

        for (var i = 0; i < proposal.Transforms.Count; i++)
        {
            var encoded = EncodeTransform(proposal.Transforms[i], i + 1 == proposal.Transforms.Count);
            transforms.Add(encoded);
            transformBytes += encoded.Length;
        }

        var length = headerLength + spiSize + transformBytes;
        var buffer = new byte[length];

        buffer[0] = (byte)(isLast ? 0 : 2);   // 2 = more proposals follow, 0 = last
        buffer[1] = 0;                        // reserved
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)length);
        buffer[4] = proposal.ProposalNumber;
        buffer[5] = (byte)proposal.Protocol;
        buffer[6] = spiSize;
        buffer[7] = (byte)proposal.Transforms.Count;

        var offset = headerLength;
        proposal.Spi.CopyTo(buffer, offset);
        offset += spiSize;

        foreach (var transform in transforms)
        {
            transform.CopyTo(buffer, offset);
            offset += transform.Length;
        }

        return buffer;
    }

    private static (IkeProposal Proposal, int Consumed, bool IsLast) DecodeProposal(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
            throw new IkeFormatException("Truncated proposal header.");

        var lastFlag = buffer[0];
        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..4]);
        if (length < 8 || length > buffer.Length)
            throw new IkeFormatException($"Proposal declares length {length} beyond the buffer.");

        var spiSize = buffer[6];
        var transformCount = buffer[7];

        var proposal = new IkeProposal
        {
            ProposalNumber = buffer[4],
            Protocol = (IkeProtocolId)buffer[5],
            Spi = buffer.Slice(8, spiSize).ToArray()
        };

        var offset = 8 + spiSize;
        for (var i = 0; i < transformCount; i++)
        {
            if (offset >= length)
                throw new IkeFormatException("Proposal declares more transforms than it carries.");

            var (transform, consumed) = DecodeTransform(buffer[offset..length]);
            proposal.Transforms.Add(transform);
            offset += consumed;
        }

        return (proposal, length, lastFlag == 0);
    }

    private static byte[] EncodeTransform(IkeTransform transform, bool isLast)
    {
        const int headerLength = 8;   // Last/Resv/Len/Type/Resv/ID
        var attributeLength = transform.KeyLengthBits.HasValue ? 4 : 0;
        var length = headerLength + attributeLength;

        var buffer = new byte[length];
        buffer[0] = (byte)(isLast ? 0 : 3);   // 3 = more transforms follow, 0 = last
        buffer[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)length);
        buffer[4] = transform.Type;
        buffer[5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6, 2), transform.Id);

        if (transform.KeyLengthBits is { } keyLength)
        {
            // Type/Value encoding (AF=1): [0x8000 | type][value]. Key length fits in 15 bits.
            BinaryPrimitives.WriteUInt16BigEndian(
                buffer.AsSpan(8, 2), (ushort)(IkeAttributeType.FormatTv | IkeAttributeType.KeyLength));
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(10, 2), keyLength);
        }

        return buffer;
    }

    private static (IkeTransform Transform, int Consumed) DecodeTransform(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
            throw new IkeFormatException("Truncated transform header.");

        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..4]);
        if (length < 8 || length > buffer.Length)
            throw new IkeFormatException($"Transform declares length {length} beyond the buffer.");

        var transform = new IkeTransform(
            Type: buffer[4],
            Id: BinaryPrimitives.ReadUInt16BigEndian(buffer[6..8]));

        ushort? keyLength = null;
        for (var offset = 8; offset + 4 <= length; offset += 4)
        {
            var raw = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..(offset + 2)]);
            var isTv = (raw & IkeAttributeType.FormatTv) != 0;
            var type = (ushort)(raw & ~IkeAttributeType.FormatTv);

            if (type != IkeAttributeType.KeyLength) continue;

            if (isTv)
            {
                keyLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 2)..(offset + 4)]);
            }
            else
            {
                var valueLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 2)..(offset + 4)]);
                if (valueLength == 2 && offset + 8 <= length)
                    keyLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 4)..(offset + 6)]);
            }
        }

        return (transform with { KeyLengthBits = keyLength }, length);
    }
}

public sealed class IkeFormatException : Exception
{
    public IkeFormatException(string message) : base(message) { }
}
