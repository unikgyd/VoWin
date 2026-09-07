using System.Text;
using VoSharp.Common.Utils;

namespace VoSharp.Euicc.Asn1;

/// <summary>
/// Represents an ASN.1 BER-TLV structure used in GSMA SGP.22 / eUICC communication.
/// </summary>
public class Tlv
{
    public uint Tag { get; set; }
    public byte[] Value { get; set; } = [];
    public List<Tlv> Children { get; } = [];

    public Tlv() { }

    public Tlv(uint tag, byte[]? value = null)
    {
        Tag = tag;
        if (value != null) Value = value;
    }

    public Tlv(uint tag, string textValue)
    {
        Tag = tag;
        Value = Encoding.UTF8.GetBytes(textValue);
    }

    public bool IsConstructed => GetFirstTagByte(Tag) is var b && (b & 0x20) != 0;

    public string HexValue() => HexUtils.ToHexString(Value);

    public string TextValue()
    {
        try { return Encoding.UTF8.GetString(Value); }
        catch { return HexValue(); }
    }

    public int IntValue()
    {
        if (Value.Length == 0) return 0;
        int result = 0;
        foreach (var b in Value)
        {
            result = (result << 8) | b;
        }
        return result;
    }

    public Tlv? FindFirstChild(uint tag)
    {
        return Children.FirstOrDefault(c => c.Tag == tag);
    }

    public List<Tlv> FindAllRecursive(uint tag)
    {
        var list = new List<Tlv>();
        if (Tag == tag) list.Add(this);
        foreach (var child in Children)
        {
            list.AddRange(child.FindAllRecursive(tag));
        }
        return list;
    }

    public Tlv? FindFirstRecursive(uint tag)
    {
        if (Tag == tag) return this;
        foreach (var child in Children)
        {
            var found = child.FindFirstRecursive(tag);
            if (found != null) return found;
        }
        return null;
    }

    public byte[] Encode()
    {
        using var ms = new MemoryStream();

        // 1. Encode Tag
        var tagBytes = EncodeTag(Tag);
        ms.Write(tagBytes);

        // 2. Encode Value / Children
        byte[] payload;
        if (Children.Count > 0)
        {
            using var childMs = new MemoryStream();
            foreach (var child in Children)
            {
                var encodedChild = child.Encode();
                childMs.Write(encodedChild);
            }
            payload = childMs.ToArray();
        }
        else
        {
            payload = Value;
        }

        // 3. Encode Length
        var lenBytes = EncodeLength(payload.Length);
        ms.Write(lenBytes);

        // 4. Write Payload
        ms.Write(payload);

        return ms.ToArray();
    }

    public const int MaxTlvDepth = 32;
    public const int MaxTlvNodes = 5000;
    public const int MaxTlvLength = 10 * 1024 * 1024; // 10 MB

    public static (Tlv tlv, int bytesConsumed) Parse(ReadOnlySpan<byte> data)
    {
        int nodeCount = 0;
        return ParseInternal(data, depth: 0, ref nodeCount);
    }

    private static (Tlv tlv, int bytesConsumed) ParseInternal(ReadOnlySpan<byte> data, int depth, ref int nodeCount)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Cannot parse TLV from empty data", nameof(data));

        if (depth > MaxTlvDepth)
            throw new InvalidOperationException($"TLV depth limit exceeded (max {MaxTlvDepth})");

        if (++nodeCount > MaxTlvNodes)
            throw new InvalidOperationException($"TLV node count limit exceeded (max {MaxTlvNodes})");

        int offset = 0;

        // 1. Parse Tag
        byte tagFirstByte = data[offset++];
        uint tag = tagFirstByte;

        if ((tagFirstByte & 0x1F) == 0x1F)
        {
            while (offset < data.Length)
            {
                byte b = data[offset++];
                tag = (tag << 8) | b;
                if ((b & 0x80) == 0) break;
            }
        }

        if (offset >= data.Length)
            throw new InvalidOperationException("Truncated TLV: missing length");

        // 2. Parse Length
        byte lenByte = data[offset++];
        int length = 0;

        if ((lenByte & 0x80) == 0)
        {
            length = lenByte;
        }
        else
        {
            int numBytes = lenByte & 0x7F;
            if (numBytes == 0 || numBytes > 4 || offset + numBytes > data.Length)
                throw new InvalidOperationException($"Invalid BER-TLV length encoding (numBytes={numBytes})");

            for (int i = 0; i < numBytes; i++)
            {
                length = (length << 8) | data[offset++];
            }
        }

        if (length < 0 || length > MaxTlvLength)
            throw new InvalidOperationException($"TLV length {length} exceeds maximum allowed {MaxTlvLength} bytes");

        if (offset + length > data.Length)
            throw new InvalidOperationException($"TLV value truncated: expected {length} bytes, got {data.Length - offset}");

        var value = data.Slice(offset, length).ToArray();
        offset += length;

        var tlv = new Tlv(tag, value);

        // If constructed, parse children recursively
        if (tlv.IsConstructed && value.Length > 0)
        {
            int childOffset = 0;
            while (childOffset < value.Length)
            {
                var (child, childConsumed) = ParseInternal(value.AsSpan(childOffset), depth + 1, ref nodeCount);
                if (childConsumed <= 0) break;
                tlv.Children.Add(child);
                childOffset += childConsumed;
            }
        }

        return (tlv, offset);
    }

    public static List<Tlv> ParseAll(ReadOnlySpan<byte> data)
    {
        var list = new List<Tlv>();
        int offset = 0;
        int nodeCount = 0;
        while (offset < data.Length)
        {
            var (tlv, consumed) = ParseInternal(data[offset..], depth: 0, ref nodeCount);
            list.Add(tlv);
            offset += consumed;
        }
        return list;
    }

    public static string BcdToIccid(byte[] bcd)
    {
        var sb = new StringBuilder();
        foreach (var b in bcd)
        {
            int low = b & 0x0F;
            int high = (b >> 4) & 0x0F;

            if (low <= 9) sb.Append(low);
            if (high <= 9) sb.Append(high);
        }
        return sb.ToString();
    }

    public static byte[] IccidToBcd(string iccid)
    {
        if (iccid.Length % 2 != 0)
            iccid += "F";

        var bytes = new byte[iccid.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            char c1 = iccid[i * 2];
            char c2 = iccid[i * 2 + 1];

            int d1 = c1 >= '0' && c1 <= '9' ? c1 - '0' : 0x0F;
            int d2 = c2 >= '0' && c2 <= '9' ? c2 - '0' : 0x0F;

            bytes[i] = (byte)(d1 | (d2 << 4));
        }
        return bytes;
    }

    private static byte GetFirstTagByte(uint tag)
    {
        if (tag > 0xFFFFFF) return (byte)(tag >> 24);
        if (tag > 0xFFFF) return (byte)(tag >> 16);
        if (tag > 0xFF) return (byte)(tag >> 8);
        return (byte)tag;
    }

    private static byte[] EncodeTag(uint tag)
    {
        if (tag <= 0xFF) return [(byte)tag];
        if (tag <= 0xFFFF) return [(byte)(tag >> 8), (byte)tag];
        if (tag <= 0xFFFFFF) return [(byte)(tag >> 16), (byte)(tag >> 8), (byte)tag];
        return [(byte)(tag >> 24), (byte)(tag >> 16), (byte)(tag >> 8), (byte)tag];
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80)
            return [(byte)length];

        if (length <= 0xFF)
            return [0x81, (byte)length];

        if (length <= 0xFFFF)
            return [0x82, (byte)(length >> 8), (byte)length];

        if (length <= 0xFFFFFF)
            return [0x83, (byte)(length >> 16), (byte)(length >> 8), (byte)length];

        return [0x84, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length];
    }
}
