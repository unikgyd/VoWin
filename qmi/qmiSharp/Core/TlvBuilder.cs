using System.Buffers.Binary;
using System.Text;

namespace qmiSharp.Core;

/// <summary>
/// Fluent builder for constructing a collection of QMI TLVs.
/// </summary>
public sealed class TlvBuilder
{
    private readonly List<QmiTlv> _tlvs = new();

    public TlvBuilder Add(QmiTlv tlv)
    {
        _tlvs.Add(tlv);
        return this;
    }

    public TlvBuilder AddByte(byte type, byte value)
    {
        _tlvs.Add(QmiTlv.FromByte(type, value));
        return this;
    }

    public TlvBuilder AddSByte(byte type, sbyte value)
    {
        _tlvs.Add(new QmiTlv(type, new[] { (byte)value }));
        return this;
    }

    public TlvBuilder AddUInt16(byte type, ushort value)
    {
        _tlvs.Add(QmiTlv.FromUInt16(type, value));
        return this;
    }

    public TlvBuilder AddInt16(byte type, short value)
    {
        byte[] buf = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buf, value);
        _tlvs.Add(new QmiTlv(type, buf));
        return this;
    }

    public TlvBuilder AddUInt32(byte type, uint value)
    {
        _tlvs.Add(QmiTlv.FromUInt32(type, value));
        return this;
    }

    public TlvBuilder AddInt32(byte type, int value)
    {
        byte[] buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        _tlvs.Add(new QmiTlv(type, buf));
        return this;
    }

    public TlvBuilder AddUInt64(byte type, ulong value)
    {
        byte[] buf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        _tlvs.Add(new QmiTlv(type, buf));
        return this;
    }

    public TlvBuilder AddString(byte type, string value, Encoding? encoding = null)
    {
        _tlvs.Add(QmiTlv.FromString(type, value, encoding));
        return this;
    }

    public TlvBuilder AddBytes(byte type, ReadOnlySpan<byte> bytes)
    {
        _tlvs.Add(new QmiTlv(type, bytes.ToArray()));
        return this;
    }

    public List<QmiTlv> Build() => _tlvs;

    public byte[] BuildBytes()
    {
        int total = _tlvs.Sum(t => t.TotalSize);
        byte[] buffer = new byte[total];
        int offset = 0;
        foreach (var tlv in _tlvs)
        {
            tlv.WriteTo(buffer.AsSpan(offset));
            offset += tlv.TotalSize;
        }
        return buffer;
    }
}
