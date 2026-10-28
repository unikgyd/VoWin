using System.Buffers.Binary;
using System.Net;

namespace VoSharp.Ike;

public sealed record IkeTrafficSelector(IPAddress Start, IPAddress End, byte Protocol, ushort StartPort, ushort EndPort)
{
    internal UInt128 First => Number(Start);
    internal UInt128 Last => Number(End);
    internal bool Ipv6 => Start.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    internal bool Opaque => StartPort == 65535 && EndPort == 0;
    internal bool AnyPort => StartPort == 0 && EndPort == 65535;
    internal static UInt128 Number(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }
    internal void Validate()
    {
        if (Start.AddressFamily != End.AddressFamily || First > Last || StartPort > EndPort && !Opaque ||
            Protocol == 0 && !AnyPort) throw new IkeFormatException("Invalid traffic selector range.");
    }
    public bool Supports(IPAddress ip, byte protocol) => Start.AddressFamily == ip.AddressFamily &&
        First <= Number(ip) && Number(ip) <= Last && (Protocol == 0 || Protocol == protocol) && !Opaque;
    public bool Allows(IPAddress ip, byte protocol, ushort port) => Supports(ip, protocol) && StartPort <= port && port <= EndPort;
    public static IkeTrafficSelector Any(bool ipv6) => new(ipv6 ? IPAddress.IPv6Any : IPAddress.Any,
        ipv6 ? new IPAddress(Enumerable.Repeat((byte)255, 16).ToArray()) : IPAddress.Broadcast, 0, 0, 65535);
}

public static class IkeTrafficSelectors
{
    public static IReadOnlyList<IkeTrafficSelector> Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length is < 4 or > 4096 || body[0] is 0 or > 64) throw new IkeFormatException("Invalid TS payload size or count.");
        var result = new List<IkeTrafficSelector>(); var at = 4;
        for (var i = 0; i < body[0]; i++)
        {
            if (body.Length - at < 8) throw new IkeFormatException("Truncated selector.");
            var size = body[at] switch { 7 => 16, 8 => 40, _ => throw new IkeFormatException("Unsupported selected TS type.") };
            if (BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at + 2, 2)) != size || body.Length - at < size)
                throw new IkeFormatException("Invalid selector length.");
            var width = (size - 8) / 2;
            var selector = new IkeTrafficSelector(new(body.Slice(at + 8, width)), new(body.Slice(at + 8 + width, width)), body[at + 1],
                BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at + 4, 2)), BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at + 6, 2)));
            selector.Validate(); result.Add(selector); at += size;
        }
        if (at != body.Length) throw new IkeFormatException("Trailing TS data."); return result;
    }
    public static byte[] Encode(IReadOnlyList<IkeTrafficSelector> selectors)
    {
        if (selectors.Count is 0 or > 64) throw new IkeFormatException("Invalid TS count.");
        var bytes = new List<byte> { (byte)selectors.Count, 0, 0, 0 };
        foreach (var s in selectors)
        {
            s.Validate(); bytes.Add(s.Ipv6 ? (byte)8 : (byte)7); bytes.Add(s.Protocol); bytes.Add(0); bytes.Add(s.Ipv6 ? (byte)40 : (byte)16);
            bytes.Add((byte)(s.StartPort >> 8)); bytes.Add((byte)s.StartPort); bytes.Add((byte)(s.EndPort >> 8)); bytes.Add((byte)s.EndPort);
            bytes.AddRange(s.Start.GetAddressBytes()); bytes.AddRange(s.End.GetAddressBytes());
        }
        return bytes.ToArray();
    }
    public static void ValidateNarrowing(IReadOnlyList<IkeTrafficSelector> offered, IReadOnlyList<IkeTrafficSelector> selected)
    {
        if (offered.Count is 0 or > 64 || selected.Count is 0 or > 64) throw new IkeFormatException("Invalid TS policy count.");
        foreach (var s in offered.Concat(selected)) s.Validate();
        foreach (var candidate in selected)
        {
            if (offered.Any(o => o.Ipv6 == candidate.Ipv6 && o.First <= candidate.First && o.Last >= candidate.Last &&
                (o.Protocol == 0 || o.Protocol == candidate.Protocol) && (o.AnyPort || (candidate.Opaque ? o.Opaque : !o.Opaque && o.StartPort <= candidate.StartPort && o.EndPort >= candidate.EndPort)))) continue;
            var protocols = candidate.Protocol == 0 ? Enumerable.Range(0, 256) : new[] { (int)candidate.Protocol };
            foreach (var protocol in protocols)
            {
                Cover(candidate, offered, (byte)protocol, false);
                if (candidate.AnyPort || candidate.Opaque) Cover(candidate, offered, (byte)protocol, true);
            }
        }
    }
    private static void Cover(IkeTrafficSelector candidate, IReadOnlyList<IkeTrafficSelector> offered, byte protocol, bool opaque)
    {
        if (candidate.Opaque && !opaque) return;
        var relevant = offered.Where(o => o.Ipv6 == candidate.Ipv6 && (o.Protocol == 0 || o.Protocol == protocol) && o.First <= candidate.Last && o.Last >= candidate.First &&
            (opaque ? o.AnyPort || o.Opaque : !o.Opaque && o.StartPort <= candidate.EndPort && o.EndPort >= candidate.StartPort)).ToArray();
        var boundaries = new List<UInt128> { candidate.First };
        foreach (var o in relevant)
        {
            boundaries.Add(UInt128.Max(o.First, candidate.First)); var last = UInt128.Min(o.Last, candidate.Last);
            if (last < candidate.Last) boundaries.Add(last + 1);
        }
        foreach (var start in boundaries.Distinct().Order())
        {
            var ports = relevant.Where(o => o.First <= start && o.Last >= start).Select(o => opaque ? (Lo: 0, Hi: 0) :
                (Lo: Math.Max(o.StartPort, candidate.StartPort), Hi: Math.Min(o.EndPort, candidate.EndPort))).OrderBy(p => p.Lo);
            int next = opaque ? 0 : candidate.StartPort; int last = opaque ? 0 : candidate.EndPort;
            foreach (var (lo, hi) in ports) { if (lo > next) break; if (hi >= next) next = hi + 1; if (next > last) break; }
            if (next <= last) throw new IkeFormatException("Responder traffic selectors widen the original offer.");
        }
    }
}

/// <summary>Negotiated selector lists plus the one assigned address this IMS flow owns.</summary>
public sealed class IkeChildPolicy
{
    public IReadOnlyList<IkeTrafficSelector> Local { get; }
    public IReadOnlyList<IkeTrafficSelector> Remote { get; }
    public IPAddress AssignedAddress { get; }
    public IkeChildPolicy(IReadOnlyList<IkeTrafficSelector> local, IReadOnlyList<IkeTrafficSelector> remote, IPAddress assigned)
    {
        _ = IkeTrafficSelectors.Encode(local); _ = IkeTrafficSelectors.Encode(remote);
        Local = local.ToArray(); Remote = remote.ToArray(); AssignedAddress = assigned;
    }
    public bool Allows(IPAddress source, IPAddress destination, byte protocol, ushort sourcePort, ushort destinationPort, bool outgoing, bool opaque = false)
    {
        var local = outgoing ? source : destination;
        if (!local.Equals(AssignedAddress)) return false;
        bool Match(IkeTrafficSelector s, IPAddress ip, ushort port) => opaque
            ? s.Start.AddressFamily == ip.AddressFamily && s.First <= IkeTrafficSelector.Number(ip) && IkeTrafficSelector.Number(ip) <= s.Last &&
                (s.Protocol == 0 || s.Protocol == protocol) && (s.AnyPort || s.Opaque)
            : s.Allows(ip, protocol, port);
        return (outgoing ? Local : Remote).Any(s => Match(s, source, sourcePort)) &&
            (outgoing ? Remote : Local).Any(s => Match(s, destination, destinationPort));
    }
    /// <summary>Checks the actual inner IP datagram, including IPv6 extension
    /// chains and opaque non-initial fragments, before committing replay state.</summary>
    public bool AllowsPacket(ReadOnlySpan<byte> bytes, bool outgoing, byte nextHeader)
    {
        if (bytes.Length < 20) return false;
        IPAddress source, destination; byte protocol; int offset; bool opaque = false, fragmented = false;
        if (bytes[0] >> 4 == 4 && nextHeader == 4)
        {
            offset = (bytes[0] & 15) * 4;
            if (offset < 20 || offset > bytes.Length || BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]) != bytes.Length) return false;
            uint sum = 0; for (var i = 0; i < offset; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i, 2));
            while (sum > 65535) sum = (sum & 65535) + (sum >> 16); if (sum != 65535) return false;
            var fragment = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..8]); opaque = (fragment & 0x1fff) != 0; fragmented = (fragment & 0x3fff) != 0;
            if ((fragment & 0x8000) != 0) return false;
            protocol = bytes[9]; source = new(bytes[12..16]); destination = new(bytes[16..20]);
        }
        else if (bytes[0] >> 4 == 6 && nextHeader == 41)
        {
            if (bytes.Length < 40 || 40 + BinaryPrimitives.ReadUInt16BigEndian(bytes[4..6]) != bytes.Length) return false;
            source = new(bytes[8..24]); destination = new(bytes[24..40]); offset = 40; protocol = bytes[6];
            var seenFragment = false;
            for (var count = 0; protocol is 0 or 43 or 44 or 51 or 60; count++)
            {
                if (count >= 16 || bytes.Length - offset < 8) return false;
                var next = bytes[offset]; var size = protocol == 44 ? 8 : protocol == 51 ? (bytes[offset + 1] + 2) * 4 : (bytes[offset + 1] + 1) * 8;
                if (size > bytes.Length - offset) return false;
                if (protocol == 44)
                {
                    var fragment = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2));
                    if (seenFragment || bytes[offset + 1] != 0 || (fragment & 6) != 0) return false;
                    seenFragment = true; opaque = (fragment & 0xfff8) != 0; fragmented = (fragment & 0xfff9) != 0;
                }
                offset += size; protocol = next; if (opaque) break;
            }
        }
        else return false;
        ushort sourcePort = 0, destinationPort = 0;
        if (!opaque)
        {
            var minimum = protocol switch { 6 => 20, 17 or 136 => 8, 33 or 132 => 12, 1 or 58 => 2, 135 => 8, _ => 0 };
            if (bytes.Length - offset < minimum) return false;
            if (protocol is 6 or 17 or 33 or 132 or 136)
            {
                sourcePort = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2)); destinationPort = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2));
                if (protocol == 17 && !fragmented && !ValidUdp(source, destination, bytes[offset..])) return false;
            }
            else if (protocol is 1 or 58) sourcePort = destinationPort = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            else if (protocol == 135) sourcePort = destinationPort = (ushort)(bytes[offset + 2] << 8);
        }
        return Allows(source, destination, protocol, sourcePort, destinationPort, outgoing, opaque);
    }
    private static bool ValidUdp(IPAddress source, IPAddress destination, ReadOnlySpan<byte> udp)
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(udp[4..6]) != udp.Length) return false;
        var supplied = BinaryPrimitives.ReadUInt16BigEndian(udp[6..8]);
        if (supplied == 0) return source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        uint sum = (uint)udp.Length + 17;
        static uint Words(ReadOnlySpan<byte> bytes)
        {
            uint result = 0;
            for (var i = 0; i < bytes.Length; i += 2) result += (uint)(bytes[i] << 8) + (i + 1 < bytes.Length ? bytes[i + 1] : 0u);
            return result;
        }
        sum += Words(source.GetAddressBytes()) + Words(destination.GetAddressBytes()) + Words(udp);
        while (sum > 65535) sum = (sum & 65535) + (sum >> 16);
        return sum == 65535;
    }
}
