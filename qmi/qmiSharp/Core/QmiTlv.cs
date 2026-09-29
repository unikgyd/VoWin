using System.Buffers.Binary;
using System.Text;

namespace qmiSharp.Core;

/// <summary>
/// Represents a single QMI Type-Length-Value (TLV) structure.
/// Wire format:
/// - Offset 0:   Type (1 byte)
/// - Offset 1-2: Length (2 bytes, little-endian)
/// - Offset 3+:  Value (Length bytes)
/// </summary>
public sealed class QmiTlv
{
    public const int HeaderSize = 3;

    public byte Type { get; }
    public ReadOnlyMemory<byte> Value { get; }

    public int Length => Value.Length;
    public int TotalSize => HeaderSize + Value.Length;
    public ReadOnlySpan<byte> Span => Value.Span;

    public QmiTlv(byte type, ReadOnlyMemory<byte> value)
    {
        if (value.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "A QMI TLV cannot exceed 65535 bytes");
        Type = type;
        Value = value;
    }

    public QmiTlv(byte type, byte[] value) : this(type, (ReadOnlyMemory<byte>)value)
    {
    }

    public static QmiTlv FromByte(byte type, byte value) =>
        new(type, new[] { value });

    public static QmiTlv FromUInt16(byte type, ushort value)
    {
        byte[] buf = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        return new QmiTlv(type, buf);
    }

    public static QmiTlv FromUInt32(byte type, uint value)
    {
        byte[] buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        return new QmiTlv(type, buf);
    }

    public static QmiTlv FromString(byte type, string value, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return new QmiTlv(type, encoding.GetBytes(value));
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < TotalSize)
            throw new ArgumentException("Destination span too short for TLV", nameof(destination));

        destination[0] = Type;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..3], (ushort)Value.Length);
        Value.Span.CopyTo(destination[3..]);
    }

    public byte[] ToByteArray()
    {
        byte[] buf = new byte[TotalSize];
        WriteTo(buf);
        return buf;
    }

    public static QmiTlv Parse(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        if (source.Length < HeaderSize)
            throw new ArgumentException("Source span too short for TLV header", nameof(source));

        byte type = source[0];
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(source[1..3]);

        if (source.Length < HeaderSize + length)
            throw new ArgumentException($"TLV payload truncated. Expected {length} bytes, available {source.Length - HeaderSize}", nameof(source));

        bytesConsumed = HeaderSize + length;
        return new QmiTlv(type, source.Slice(HeaderSize, length).ToArray());
    }

    public static List<QmiTlv> ParseMultiple(ReadOnlySpan<byte> source)
    {
        var tlvs = new List<QmiTlv>();
        int offset = 0;

        while (offset < source.Length)
        {
            if (source.Length - offset < HeaderSize)
                throw new ArgumentException("Trailing bytes do not form a complete TLV header", nameof(source));

            var tlv = Parse(source[offset..], out int consumed);
            tlvs.Add(tlv);
            offset += consumed;
        }

        return tlvs;
    }

    // Typed data accessors
    public byte AsByte(int offset = 0)
    {
        EnsureAvailable(offset, 1);
        return Value.Span[offset];
    }

    public sbyte AsSByte(int offset = 0)
    {
        EnsureAvailable(offset, 1);
        return (sbyte)Value.Span[offset];
    }

    public ushort AsUInt16(int offset = 0)
    {
        EnsureAvailable(offset, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(Value.Span[offset..]);
    }

    public short AsInt16(int offset = 0)
    {
        EnsureAvailable(offset, 2);
        return BinaryPrimitives.ReadInt16LittleEndian(Value.Span[offset..]);
    }

    public uint AsUInt32(int offset = 0)
    {
        EnsureAvailable(offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(Value.Span[offset..]);
    }

    public int AsInt32(int offset = 0)
    {
        EnsureAvailable(offset, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(Value.Span[offset..]);
    }

    public ulong AsUInt64(int offset = 0)
    {
        EnsureAvailable(offset, 8);
        return BinaryPrimitives.ReadUInt64LittleEndian(Value.Span[offset..]);
    }

    private void EnsureAvailable(int offset, int size)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset > Value.Length - size)
            throw new InvalidDataException($"TLV 0x{Type:X2} is truncated: need {size} byte(s) at offset {offset}, length is {Value.Length}");
    }

    public string AsString(Encoding? encoding = null)
    {
        if (Value.IsEmpty) return string.Empty;
        encoding ??= Encoding.UTF8;
        return encoding.GetString(Value.Span).TrimEnd('\0');
    }

    public override string ToString() =>
        $"TLV(Type=0x{Type:X2}, Len={Value.Length}, Data={Convert.ToHexString(Value.Span)})";
}
