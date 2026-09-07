using System.Buffers.Binary;
using System.Net;

namespace VoSharp.Telephony.VoWifi;

public static class IpPacketUtils
{
    public static SipDatagram ParseIpv4UdpPacket(byte[] packet)
    {
        var (ihl, total, protocol) = ReadIpv4Header(packet);
        if (protocol != 17) throw new FormatException($"Unsupported inner IP protocol {protocol}; expected UDP.");
        if (total - ihl < 8) throw new FormatException("Truncated UDP header.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(ihl + 4, 2));
        if (length < 8 || length > total - ihl) throw new FormatException("Invalid UDP length.");
        return new SipDatagram(packet.AsSpan(ihl + 8, length - 8).ToArray(),
            new IPEndPoint(new IPAddress(packet.AsSpan(16, 4)), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(ihl + 2, 2))),
            new IPEndPoint(new IPAddress(packet.AsSpan(12, 4)), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(ihl, 2))));
    }

    public static (int HeaderLength, int TotalLength, byte Protocol) ReadIpv4Header(byte[] packet)
    {
        if (packet.Length < 20 || packet[0] >> 4 != 4) throw new FormatException("Expected an inner IPv4 packet.");
        int ihl = (packet[0] & 15) * 4;
        int total = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        if (ihl < 20 || total < ihl || total > packet.Length) throw new FormatException("Invalid IPv4 length.");
        if ((BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2)) & 0x3fff) != 0)
            throw new FormatException("Fragmented inner IPv4 packet requires reassembly.");
        return (ihl, total, packet[9]);
    }

    public static byte[] ReplaceIpv4Payload(byte[] packet, byte protocol, byte[] payload)
    {
        var (ihl, _, _) = ReadIpv4Header(packet);
        if (ihl + payload.Length > ushort.MaxValue) throw new ArgumentException("IPv4 packet exceeds maximum length.");
        var result = new byte[ihl + payload.Length];
        packet.AsSpan(0, ihl).CopyTo(result);
        payload.CopyTo(result, ihl);
        result[9] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2, 2), (ushort)result.Length);
        result[10] = result[11] = 0;
        uint sum = 0;
        for (int i = 0; i < ihl; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(i, 2));
        while (sum > 65535) sum = (sum & 65535) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10, 2), (ushort)~sum);
        return result;
    }
    /// <summary>
    /// Constructs a standard IPv4 UDP datagram ready for RFC 4303 ESP encapsulation (nextHeader: 4).
    /// </summary>
    public static byte[] BuildIpv4UdpPacket(
        IPAddress srcIp,
        IPAddress dstIp,
        ushort srcPort,
        ushort dstPort,
        byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(srcIp);
        ArgumentNullException.ThrowIfNull(dstIp);
        ArgumentNullException.ThrowIfNull(payload);
        if (srcIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            dstIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4 addresses are required.");
        if (payload.Length > 65507) throw new ArgumentException("UDP payload exceeds maximum IPv4 size.");

        ushort ipTotalLen = (ushort)(20 + 8 + payload.Length);
        ushort udpLen = (ushort)(8 + payload.Length);
        var packet = new byte[ipTotalLen];

        // IPv4 Header (20 bytes, Version 4, IHL 5)
        packet[0] = 0x45;
        packet[1] = 0x00; // DSCP / ECN
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)Random.Shared.Next(0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0x4000); // Don't Fragment
        packet[8] = 64;   // TTL
        packet[9] = 17;   // Protocol 17 = UDP
        // Checksum at 10-11 filled below
        Buffer.BlockCopy(srcIp.GetAddressBytes(), 0, packet, 12, 4);
        Buffer.BlockCopy(dstIp.GetAddressBytes(), 0, packet, 16, 4);

        // Compute IPv4 Header Checksum
        uint sum = 0;
        for (int i = 0; i < 20; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i, 2));
        while (sum > 0xFFFF)
            sum = (sum & 0xFFFF) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), (ushort)~sum);

        // UDP Header (8 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), udpLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), 0); // Checksum optional for IPv4 UDP

        // UDP Payload
        Buffer.BlockCopy(payload, 0, packet, 28, payload.Length);
        return packet;
    }
}
