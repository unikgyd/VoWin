using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace VoSharp.Native.Vici;

/// <summary>
/// VICI element types (RFC-like binary protocol).
/// Reference: https://github.com/strongswan/strongswan/blob/master/src/libcharon/plugins/vici/README.md
/// </summary>
internal enum ViciElementType : byte
{
    SectionStart = 1,
    SectionEnd   = 2,
    KeyValue     = 3,
    ListStart    = 4,
    ListItem     = 5,
    ListEnd      = 6
}

/// <summary>
/// VICI packet types (first byte of each packet after the 4-byte length header).
/// </summary>
public enum ViciPacketType : byte
{
    CmdRequest     = 0,
    CmdResponse    = 1,
    CmdUnknown     = 2,
    EventRegister  = 3,
    EventUnregister = 4,
    EventConfirm   = 5,
    EventUnknown   = 6,
    Event          = 7
}

/// <summary>
/// A VICI protocol message — key-value container that supports nested sections and lists.
/// Used for both building requests and parsing responses from charon-svc.
/// </summary>
public class ViciMessage
{
    private readonly Dictionary<string, object> _data = new(StringComparer.OrdinalIgnoreCase);

    public ViciMessage() { }

    // ── Builder API ─────────────────────────────────────────────────────────

    public ViciMessage Set(string key, string value)
    {
        _data[key] = value;
        return this;
    }

    public ViciMessage Set(string key, byte[] value)
    {
        _data[key] = value;
        return this;
    }

    public ViciMessage SetSection(string key, ViciMessage section)
    {
        _data[key] = section;
        return this;
    }

    public ViciMessage SetList(string key, IEnumerable<string> items)
    {
        _data[key] = items.ToList();
        return this;
    }

    // ── Query API ───────────────────────────────────────────────────────────

    public string? GetString(string key) =>
        _data.TryGetValue(key, out var v) && v is string s ? s :
        _data.TryGetValue(key, out v) && v is byte[] b ? Encoding.UTF8.GetString(b) : null;

    public ViciMessage? GetSection(string key) =>
        _data.TryGetValue(key, out var v) && v is ViciMessage m ? m : null;

    public List<string>? GetList(string key) =>
        _data.TryGetValue(key, out var v) && v is List<string> l ? l : null;

    public IReadOnlyDictionary<string, object> Data => _data;

    public bool ContainsKey(string key) => _data.ContainsKey(key);

    // ── Serialization ───────────────────────────────────────────────────────

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        SerializeTo(ms, _data);
        return ms.ToArray();
    }

    private static void SerializeTo(MemoryStream ms, Dictionary<string, object> data)
    {
        foreach (var (key, value) in data)
        {
            switch (value)
            {
                case string s:
                    ms.WriteByte((byte)ViciElementType.KeyValue);
                    WriteString8(ms, key);
                    WriteString16(ms, s);
                    break;

                case byte[] b:
                    ms.WriteByte((byte)ViciElementType.KeyValue);
                    WriteString8(ms, key);
                    WriteBinary16(ms, b);
                    break;

                case ViciMessage sub:
                    ms.WriteByte((byte)ViciElementType.SectionStart);
                    WriteString8(ms, key);
                    SerializeTo(ms, sub._data);
                    ms.WriteByte((byte)ViciElementType.SectionEnd);
                    break;

                case List<string> list:
                    ms.WriteByte((byte)ViciElementType.ListStart);
                    WriteString8(ms, key);
                    foreach (var item in list)
                    {
                        ms.WriteByte((byte)ViciElementType.ListItem);
                        WriteString16(ms, item);
                    }
                    ms.WriteByte((byte)ViciElementType.ListEnd);
                    break;
            }
        }
    }

    private static void WriteString8(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        ms.WriteByte((byte)bytes.Length);
        ms.Write(bytes);
    }

    private static void WriteString16(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Span<byte> lenBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBuf, (ushort)bytes.Length);
        ms.Write(lenBuf);
        ms.Write(bytes);
    }

    private static void WriteBinary16(MemoryStream ms, byte[] data)
    {
        Span<byte> lenBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBuf, (ushort)data.Length);
        ms.Write(lenBuf);
        ms.Write(data);
    }

    // ── Deserialization ─────────────────────────────────────────────────────

    public static ViciMessage Deserialize(byte[] data)
    {
        var msg = new ViciMessage();
        int offset = 0;
        DeserializeInto(msg._data, data, ref offset);
        return msg;
    }

    private static void DeserializeInto(Dictionary<string, object> target, byte[] data, ref int offset)
    {
        while (offset < data.Length)
        {
            var elemType = (ViciElementType)data[offset++];

            switch (elemType)
            {
                case ViciElementType.KeyValue:
                {
                    var key = ReadString8(data, ref offset);
                    var val = ReadString16(data, ref offset);
                    target[key] = val;
                    break;
                }

                case ViciElementType.SectionStart:
                {
                    var key = ReadString8(data, ref offset);
                    var sub = new ViciMessage();
                    DeserializeInto(sub._data, data, ref offset);
                    target[key] = sub;
                    break;
                }

                case ViciElementType.SectionEnd:
                    return;

                case ViciElementType.ListStart:
                {
                    var key = ReadString8(data, ref offset);
                    var list = new List<string>();
                    while (offset < data.Length && data[offset] == (byte)ViciElementType.ListItem)
                    {
                        offset++; // skip ListItem type byte
                        list.Add(ReadString16(data, ref offset));
                    }
                    if (offset < data.Length && data[offset] == (byte)ViciElementType.ListEnd)
                        offset++;
                    target[key] = list;
                    break;
                }
            }
        }
    }

    private static string ReadString8(byte[] data, ref int offset)
    {
        int len = data[offset++];
        var s = Encoding.UTF8.GetString(data, offset, len);
        offset += len;
        return s;
    }

    private static string ReadString16(byte[] data, ref int offset)
    {
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
        offset += 2;
        var s = Encoding.UTF8.GetString(data, offset, len);
        offset += len;
        return s;
    }
}
