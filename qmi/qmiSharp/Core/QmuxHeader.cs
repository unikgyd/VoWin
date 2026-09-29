using System.Buffers.Binary;

namespace qmiSharp.Core;

/// <summary>
/// 6-byte QMUX Wire Header (matches Qualcomm QCQMUX.h and qmi-go)
/// Offset 0:   IFType (always 0x01 for QMUX)
/// Offset 1-2: Length (little-endian, total length after IFType)
/// Offset 3:   ControlFlags (0x00 for normal, 0x80 for service)
/// Offset 4:   ServiceType (uint8)
/// Offset 5:   ClientID (uint8)
/// </summary>
public readonly record struct QmuxHeader(
    byte IFType,
    ushort Length,
    byte ControlFlags,
    byte ServiceType,
    byte ClientId)
{
    public const int Size = 6;
    public const byte ExpectedIFType = 0x01;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException("Destination span too short for QMUX header", nameof(destination));

        destination[0] = IFType;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..3], Length);
        destination[3] = ControlFlags;
        destination[4] = ServiceType;
        destination[5] = ClientId;
    }

    public static QmuxHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException("Source span too short for QMUX header", nameof(source));

        if (source[0] != ExpectedIFType)
            throw new FormatException($"Invalid QMUX IFType: 0x{source[0]:X2}, expected 0x01");

        return new QmuxHeader(
            IFType: source[0],
            Length: BinaryPrimitives.ReadUInt16LittleEndian(source[1..3]),
            ControlFlags: source[3],
            ServiceType: source[4],
            ClientId: source[5]
        );
    }
}

/// <summary>
/// 6-byte libqmi in-memory QRTR header (marker + 5-byte header) used for
/// services with IDs greater than 0xFF. This header is not sent on an AF_QIPCRTR
/// socket; a QRTR transport strips it before sending the QMI payload.
/// </summary>
public readonly record struct QrtrVirtualHeader(
    byte IFType,
    ushort Length,
    ushort ServiceType,
    byte ClientId)
{
    public const int Size = 6;
    public const byte ExpectedIFType = 0x02;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException("Destination span too short for QRTR header", nameof(destination));

        destination[0] = IFType;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..3], Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[3..5], ServiceType);
        destination[5] = ClientId;
    }

    public static QrtrVirtualHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException("Source span too short for QRTR header", nameof(source));

        if (source[0] != ExpectedIFType)
            throw new FormatException($"Invalid QRTR IFType: 0x{source[0]:X2}, expected 0x02");

        return new QrtrVirtualHeader(
            IFType: source[0],
            Length: BinaryPrimitives.ReadUInt16LittleEndian(source[1..3]),
            ServiceType: BinaryPrimitives.ReadUInt16LittleEndian(source[3..5]),
            ClientId: source[5]
        );
    }
}
