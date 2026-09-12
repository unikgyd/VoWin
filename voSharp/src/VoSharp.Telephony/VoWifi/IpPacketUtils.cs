using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace VoSharp.Telephony.VoWifi;

public static class IpPacketUtils
{
    /// <summary>
    /// Conservative inner-packet MTU used by the userspace ePDG tunnel. With UDP/4500,
    /// AES-CBC and a 12/16-byte ICV, a 1400-byte inner packet stays below a 1500-byte
    /// outer IPv4 or IPv6 link MTU after ESP padding.
    /// </summary>
    public const int DefaultTunnelInnerMtu = 1400;

    public static SipDatagram ParseUdpPacket(byte[] packet) => packet.Length > 0 && packet[0] >> 4 == 6
        ? ParseIpv6UdpPacket(packet)
        : ParseIpv4UdpPacket(packet);

    /// <summary>
    /// Resolves the upper-layer protocol carried by an inner IP packet. IPv6 answers are taken
    /// after the RFC 8200 extension-header chain, so a packet that carries options or a fragment
    /// header is not mistaken for an unsupported protocol.
    /// </summary>
    public static bool TryGetInnerTransport(byte[] packet, out byte protocol)
    {
        if (packet.Length == 0)
        {
            protocol = 0;
            return false;
        }
        if (packet[0] >> 4 == 6) return TryFindIpv6UpperLayer(packet, out protocol, out _);
        if (packet[0] >> 4 != 4 || packet.Length < 20)
        {
            protocol = 0;
            return false;
        }
        protocol = packet[9];
        return true;
    }

    /// <summary>True when the inner packet carries UDP; control traffic (ICMPv6, TCP, ESP) is not.</summary>
    public static bool IsUdpInnerPacket(byte[] packet) => TryGetInnerTransport(packet, out var protocol) && protocol == 17;

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

    public static SipDatagram ParseIpv6UdpPacket(byte[] packet)
    {
        if (packet.Length < 40 || packet[0] >> 4 != 6) throw new FormatException("Expected an inner IPv6 packet.");
        var total = 40 + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        if (total > packet.Length) throw new FormatException("Invalid IPv6 length.");
        if (!TryFindIpv6UpperLayer(packet, out var nextHeader, out var headerLength) || nextHeader != 17)
            throw new FormatException($"Unsupported inner IPv6 next-header {nextHeader}; expected UDP.");
        if (total - headerLength < 8) throw new FormatException("Truncated UDP header.");
        var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(headerLength + 4, 2));
        if (length < 8 || length > total - headerLength) throw new FormatException("Invalid UDP length.");
        return new SipDatagram(packet.AsSpan(headerLength + 8, length - 8).ToArray(),
            new IPEndPoint(new IPAddress(packet.AsSpan(24, 16)), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(headerLength + 2, 2))),
            new IPEndPoint(new IPAddress(packet.AsSpan(8, 16)), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(headerLength, 2))));
    }

    /// <summary>
    /// Walks the RFC 8200 extension-header chain and returns the upper-layer protocol together
    /// with the offset at which its header starts. A real ePDG may carry options or a fragment
    /// header between the fixed IPv6 header and UDP/ICMPv6, and treating the first byte of such a
    /// header as a protocol number is what made valid traffic look unsupported.
    /// </summary>
    public static bool TryFindIpv6UpperLayer(byte[] packet, out byte protocol, out int offset)
    {
        protocol = 0;
        offset = 0;
        if (packet.Length < 40 || packet[0] >> 4 != 6) return false;
        var total = 40 + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        if (total > packet.Length) return false;

        byte next = packet[6];
        int position = 40;
        // A legal packet carries only a handful of extension headers; the bound also stops a
        // malformed one from walking off the end of the buffer.
        for (var hop = 0; hop < 8; hop++)
        {
            switch (next)
            {
                case 0:   // Hop-by-hop options
                case 43:  // Routing
                case 60:  // Destination options
                    if (position + 8 > total) return false;
                    next = packet[position];
                    position += (packet[position + 1] + 1) * 8;
                    continue;
                case 44:  // Fragment
                    if (position + 8 > total) return false;
                    next = packet[position];
                    position += 8;
                    continue;
                case 51:  // Authentication Header (RFC 4302): length in 32-bit words, minus 2
                    if (position + 8 > total) return false;
                    next = packet[position];
                    position += (packet[position + 1] + 2) * 4;
                    continue;
                default:
                    protocol = next;
                    offset = position;
                    return position <= total;
            }
        }
        return false;
    }

    public static (int HeaderLength, int TotalLength, byte NextHeader) ReadIpv6Header(byte[] packet)
    {
        if (packet.Length < 40 || packet[0] >> 4 != 6) throw new FormatException("Expected an inner IPv6 packet.");
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        var total = 40 + payloadLength;
        if (total > packet.Length) throw new FormatException("Invalid IPv6 length.");
        // The IMS packets generated here use no extension headers. Rejecting them
        // explicitly is safer than accidentally interpreting an extension as UDP.
        return (40, total, packet[6]);
    }

    /// <summary>
    /// Answers an ICMPv6 Echo Request carried inside the ePDG CHILD_SA. A normal
    /// OS tunnel would do this in its IPv6 stack; the user-space tunnel must do it itself.
    /// </summary>
    public static bool TryBuildIcmpv6EchoReply(byte[] packet, out byte[] reply)
    {
        reply = Array.Empty<byte>();
        if (packet.Length < 48 || packet[0] >> 4 != 6)
            return false;

        var totalLength = 40 + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        if (totalLength > packet.Length)
            return false;
        if (!TryFindIpv6UpperLayer(packet, out var nextHeader, out var headerLength))
            return false;
        if (nextHeader != 58 || totalLength - headerLength < 8 || packet[headerLength] != 128)
            return false;

        reply = packet.AsSpan(0, totalLength).ToArray();
        var source = reply.AsSpan(8, 16).ToArray();
        reply.AsSpan(24, 16).CopyTo(reply.AsSpan(8, 16));
        source.CopyTo(reply, 24);
        reply[7] = 64;
        reply[headerLength] = 129; // Echo Reply
        reply[headerLength + 2] = 0;
        reply[headerLength + 3] = 0;
        var checksum = ComputeIpv6UpperLayerChecksum(
            reply.AsSpan(8, 16), reply.AsSpan(24, 16),
            nextHeader, reply.AsSpan(headerLength, totalLength - headerLength));
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(headerLength + 2, 2), checksum);
        return true;
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

    public static byte[] ReplaceIpv6Payload(byte[] packet, byte nextHeader, byte[] payload)
    {
        var (headerLength, _, _) = ReadIpv6Header(packet);
        if (payload.Length > ushort.MaxValue) throw new ArgumentException("IPv6 payload exceeds the non-jumbo maximum.");
        var result = new byte[headerLength + payload.Length];
        packet.AsSpan(0, headerLength).CopyTo(result);
        payload.CopyTo(result, headerLength);
        result[6] = nextHeader;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), checked((ushort)payload.Length));
        return result;
    }

    public static byte[] BuildUdpPacket(
        IPAddress srcIp, IPAddress dstIp, ushort srcPort, ushort dstPort, byte[] payload) =>
        srcIp.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork when dstIp.AddressFamily == srcIp.AddressFamily =>
                BuildIpv4UdpPacket(srcIp, dstIp, srcPort, dstPort, payload),
            System.Net.Sockets.AddressFamily.InterNetworkV6 when dstIp.AddressFamily == srcIp.AddressFamily =>
                BuildIpv6UdpPacket(srcIp, dstIp, srcPort, dstPort, payload),
            _ => throw new ArgumentException("Source and destination must use the same IPv4/IPv6 family.")
        };

    /// <summary>
    /// Splits an oversized inner packet before CHILD_SA ESP encapsulation. Every returned
    /// packet is a complete IPv4/IPv6 fragment no larger than <paramref name="maximumLength"/>,
    /// so the outer UDP/ESP datagram does not depend on carrier-side IP fragment delivery.
    /// </summary>
    public static IReadOnlyList<byte[]> FragmentForTunnel(byte[] packet, int maximumLength = DefaultTunnelInnerMtu)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (maximumLength < 128)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (packet.Length == 0)
            throw new FormatException("Cannot fragment an empty IP packet.");

        return (packet[0] >> 4) switch
        {
            4 => FragmentIpv4Packet(packet, maximumLength),
            6 => FragmentIpv6Packet(packet, maximumLength),
            _ => throw new FormatException("Expected an inner IPv4 or IPv6 packet.")
        };
    }

    private static IReadOnlyList<byte[]> FragmentIpv4Packet(byte[] packet, int maximumLength)
    {
        var (headerLength, totalLength, _) = ReadIpv4HeaderAllowFragments(packet);
        if (totalLength <= maximumLength)
            return [packet.AsSpan(0, totalLength).ToArray()];

        ushort originalFlags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        if ((originalFlags & 0x3fff) != 0)
            throw new FormatException("Cannot re-fragment an already fragmented IPv4 packet.");

        int maximumPayload = (maximumLength - headerLength) & ~7;
        if (maximumPayload < 8)
            throw new ArgumentOutOfRangeException(nameof(maximumLength), "IPv4 fragment MTU is too small.");

        var payload = packet.AsSpan(headerLength, totalLength - headerLength);
        var fragments = new List<byte[]>((payload.Length + maximumPayload - 1) / maximumPayload);
        for (var offset = 0; offset < payload.Length; offset += maximumPayload)
        {
            int count = Math.Min(maximumPayload, payload.Length - offset);
            bool more = offset + count < payload.Length;
            var fragment = new byte[headerLength + count];
            packet.AsSpan(0, headerLength).CopyTo(fragment);
            payload.Slice(offset, count).CopyTo(fragment.AsSpan(headerLength));
            BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(2, 2), checked((ushort)fragment.Length));
            // This userspace tunnel is the IPv4 source for these generated packets. Clear DF and
            // preserve only the newly generated MF/offset fields.
            ushort flagsAndOffset = (ushort)((more ? 0x2000 : 0) | ((offset / 8) & 0x1fff));
            BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(6, 2), flagsAndOffset);
            WriteIpv4HeaderChecksum(fragment, headerLength);
            fragments.Add(fragment);
        }
        return fragments;
    }

    private static IReadOnlyList<byte[]> FragmentIpv6Packet(byte[] packet, int maximumLength)
    {
        var (headerLength, totalLength, nextHeader) = ReadIpv6Header(packet);
        if (totalLength <= maximumLength)
            return [packet.AsSpan(0, totalLength).ToArray()];
        if (headerLength != 40 || nextHeader is 0 or 43 or 44 or 60)
            throw new FormatException("Outbound IPv6 fragmentation requires a packet without extension headers.");

        int maximumPayload = (maximumLength - 40 - 8) & ~7;
        if (maximumPayload < 8)
            throw new ArgumentOutOfRangeException(nameof(maximumLength), "IPv6 fragment MTU is too small.");

        var payload = packet.AsSpan(40, totalLength - 40);
        uint identification;
        do
        {
            identification = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        } while (identification == 0);

        var fragments = new List<byte[]>((payload.Length + maximumPayload - 1) / maximumPayload);
        for (var offset = 0; offset < payload.Length; offset += maximumPayload)
        {
            int count = Math.Min(maximumPayload, payload.Length - offset);
            bool more = offset + count < payload.Length;
            var fragment = new byte[40 + 8 + count];
            packet.AsSpan(0, 40).CopyTo(fragment);
            fragment[6] = 44; // Fragment extension header
            BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(4, 2), checked((ushort)(8 + count)));
            fragment[40] = nextHeader;
            fragment[41] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(42, 2),
                checked((ushort)((offset & 0xfff8) | (more ? 1 : 0))));
            BinaryPrimitives.WriteUInt32BigEndian(fragment.AsSpan(44, 4), identification);
            payload.Slice(offset, count).CopyTo(fragment.AsSpan(48));
            fragments.Add(fragment);
        }
        return fragments;
    }

    private static (int HeaderLength, int TotalLength, byte Protocol) ReadIpv4HeaderAllowFragments(byte[] packet)
    {
        if (packet.Length < 20 || packet[0] >> 4 != 4)
            throw new FormatException("Expected an inner IPv4 packet.");
        int headerLength = (packet[0] & 15) * 4;
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        if (headerLength < 20 || totalLength < headerLength || totalLength > packet.Length)
            throw new FormatException("Invalid IPv4 length.");
        return (headerLength, totalLength, packet[9]);
    }

    private static void WriteIpv4HeaderChecksum(byte[] packet, int headerLength)
    {
        packet[10] = packet[11] = 0;
        uint sum = 0;
        for (var i = 0; i < headerLength; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i, 2));
        while (sum > ushort.MaxValue)
            sum = (sum & ushort.MaxValue) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), (ushort)~sum);
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

    public static byte[] BuildIpv6UdpPacket(
        IPAddress srcIp,
        IPAddress dstIp,
        ushort srcPort,
        ushort dstPort,
        byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(srcIp);
        ArgumentNullException.ThrowIfNull(dstIp);
        ArgumentNullException.ThrowIfNull(payload);
        if (srcIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            dstIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            throw new ArgumentException("IPv6 addresses are required.");
        if (payload.Length > 65527) throw new ArgumentException("UDP payload exceeds the non-jumbo IPv6 maximum.");

        var udpLength = checked((ushort)(8 + payload.Length));
        var packet = new byte[40 + udpLength];
        packet[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), udpLength);
        packet[6] = 17;
        packet[7] = 64;
        srcIp.GetAddressBytes().CopyTo(packet, 8);
        dstIp.GetAddressBytes().CopyTo(packet, 24);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(40, 2), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42, 2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(44, 2), udpLength);
        payload.CopyTo(packet, 48);

        var checksum = ComputeIpv6UpperLayerChecksum(
            packet.AsSpan(8, 16), packet.AsSpan(24, 16), 17, packet.AsSpan(40));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(46, 2), checksum == 0 ? (ushort)0xFFFF : checksum);
        return packet;
    }

    private static ushort ComputeIpv6UpperLayerChecksum(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> destination,
        byte nextHeader,
        ReadOnlySpan<byte> payload)
    {
        uint sum = 0;
        AddWords(source, ref sum);
        AddWords(destination, ref sum);
        sum += (uint)payload.Length;
        sum += nextHeader;
        AddWords(payload, ref sum);
        while (sum > 0xFFFF) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    private static void AddWords(ReadOnlySpan<byte> bytes, ref uint sum)
    {
        for (var i = 0; i + 1 < bytes.Length; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i, 2));
        if ((bytes.Length & 1) != 0)
            sum += (uint)bytes[^1] << 8;
    }
}
