using System.Buffers.Binary;

namespace qmiSharp.Core;

/// <summary>
/// 6-byte Control Service (CTL) Header
/// Offset 0:   ControlFlags (0x00 for request, 0x01 for response, 0x02 for indication)
/// Offset 1:   TransactionID (1 byte for CTL)
/// Offset 2-3: MessageID (little-endian)
/// Offset 4-5: Length (little-endian, length of TLVs)
/// </summary>
public readonly record struct CtlHeader(
    byte ControlFlags,
    byte TransactionId,
    ushort MessageId,
    ushort Length)
{
    public const int Size = 6;

    public const byte FlagRequest = 0x00;
    public const byte FlagResponse = 0x01;
    public const byte FlagIndication = 0x02;

    public bool IsResponse => (ControlFlags & FlagResponse) != 0;
    public bool IsIndication => (ControlFlags & FlagIndication) != 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException("Destination span too short for CTL header", nameof(destination));

        destination[0] = ControlFlags;
        destination[1] = TransactionId;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..4], MessageId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], Length);
    }

    public static CtlHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException("Source span too short for CTL header", nameof(source));

        return new CtlHeader(
            ControlFlags: source[0],
            TransactionId: source[1],
            MessageId: BinaryPrimitives.ReadUInt16LittleEndian(source[2..4]),
            Length: BinaryPrimitives.ReadUInt16LittleEndian(source[4..6])
        );
    }
}

/// <summary>
/// 7-byte Service Header (for services other than Control)
/// Offset 0:   ControlFlags (0x00 for request, 0x02 for response, 0x04 for indication)
/// Offset 1-2: TransactionID (2 bytes, little-endian)
/// Offset 3-4: MessageID (little-endian)
/// Offset 5-6: Length (little-endian, length of TLVs)
/// </summary>
public readonly record struct ServiceHeader(
    byte ControlFlags,
    ushort TransactionId,
    ushort MessageId,
    ushort Length)
{
    public const int Size = 7;

    public const byte FlagRequest = 0x00;
    public const byte FlagResponse = 0x02;
    public const byte FlagIndication = 0x04;

    public bool IsResponse => (ControlFlags & FlagResponse) != 0;
    public bool IsIndication => (ControlFlags & FlagIndication) != 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException("Destination span too short for Service header", nameof(destination));

        destination[0] = ControlFlags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..3], TransactionId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[3..5], MessageId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[5..7], Length);
    }

    public static ServiceHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException("Source span too short for Service header", nameof(source));

        return new ServiceHeader(
            ControlFlags: source[0],
            TransactionId: BinaryPrimitives.ReadUInt16LittleEndian(source[1..3]),
            MessageId: BinaryPrimitives.ReadUInt16LittleEndian(source[3..5]),
            Length: BinaryPrimitives.ReadUInt16LittleEndian(source[5..7])
        );
    }
}
