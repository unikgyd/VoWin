using System.Buffers.Binary;

namespace VoSharp.Telephony.VoWifi;

/// <summary>
/// Reassembles IPv4 packets carried inside the ePDG ESP tunnel. Carrier IMS
/// messages (especially an INVITE with many headers) can exceed the tunnel MTU
/// and arrive as IPv4 fragments.
/// </summary>
public sealed class Ipv4FragmentReassembler
{
    private const int MinimumIpv4HeaderLength = 20;
    private const int MaximumIpv4PacketLength = ushort.MaxValue;
    private readonly object _gate = new();
    private readonly Dictionary<FragmentKey, PendingDatagram> _pending = new();
    private readonly TimeSpan _fragmentLifetime;
    private readonly int _maximumPendingDatagrams;
    private readonly TimeProvider _timeProvider;

    public Ipv4FragmentReassembler(
        TimeSpan? fragmentLifetime = null,
        int maximumPendingDatagrams = 128,
        TimeProvider? timeProvider = null)
    {
        _fragmentLifetime = fragmentLifetime ?? TimeSpan.FromSeconds(30);
        if (_fragmentLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(fragmentLifetime));
        if (maximumPendingDatagrams <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPendingDatagrams));
        _maximumPendingDatagrams = maximumPendingDatagrams;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns a complete, non-fragmented IPv4 packet, or <see langword="null"/>
    /// while more fragments are required.
    /// </summary>
    public byte[]? Process(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var fragment = ParseFragment(packet);

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            RemoveExpired(now);

            if (!fragment.MoreFragments && fragment.Offset == 0)
            {
                _pending.Remove(fragment.Key);
                return packet.AsSpan(0, fragment.TotalLength).ToArray();
            }

            if (!_pending.TryGetValue(fragment.Key, out var datagram))
            {
                if (_pending.Count >= _maximumPendingDatagrams)
                {
                    var oldest = _pending.MinBy(entry => entry.Value.LastUpdated).Key;
                    _pending.Remove(oldest);
                }

                datagram = new PendingDatagram(now);
                _pending.Add(fragment.Key, datagram);
            }

            try
            {
                datagram.LastUpdated = now;
                if (fragment.Offset == 0)
                {
                    var header = packet.AsSpan(0, fragment.HeaderLength).ToArray();
                    if (datagram.Header is not null && !datagram.Header.AsSpan().SequenceEqual(header))
                        throw new FormatException("Conflicting first IPv4 fragments were received.");
                    datagram.Header = header;
                }

                int end = checked(fragment.Offset + fragment.Payload.Length);
                if (datagram.PayloadLength is { } knownLength && end > knownLength)
                    throw new FormatException("IPv4 fragment extends beyond the final fragment.");
                if (!fragment.MoreFragments)
                {
                    if (datagram.PayloadLength is { } previousLength && previousLength != end)
                        throw new FormatException("Conflicting final IPv4 fragments were received.");
                    if (datagram.Fragments.Any(existing => existing.Offset + existing.Payload.Length > end))
                        throw new FormatException("Stored IPv4 fragment extends beyond the final fragment.");
                    datagram.PayloadLength = end;
                }

                foreach (var existing in datagram.Fragments)
                    ValidateOverlap(existing, fragment.Offset, fragment.Payload);

                if (!datagram.Fragments.Any(existing =>
                        existing.Offset == fragment.Offset && existing.Payload.AsSpan().SequenceEqual(fragment.Payload)))
                    datagram.Fragments.Add(new FragmentSegment(fragment.Offset, fragment.Payload));

                if (datagram.Header is null || datagram.PayloadLength is not { } payloadLength ||
                    !IsComplete(datagram.Fragments, payloadLength))
                    return null;

                if (datagram.Header.Length + payloadLength > MaximumIpv4PacketLength)
                    throw new FormatException("Reassembled IPv4 packet exceeds the maximum length.");

                var result = new byte[datagram.Header.Length + payloadLength];
                datagram.Header.CopyTo(result, 0);
                foreach (var segment in datagram.Fragments)
                    segment.Payload.CopyTo(result, datagram.Header.Length + segment.Offset);

                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2, 2), (ushort)result.Length);
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6, 2), 0);
                WriteHeaderChecksum(result, datagram.Header.Length);
                _pending.Remove(fragment.Key);
                return result;
            }
            catch
            {
                _pending.Remove(fragment.Key);
                throw;
            }
        }
    }

    public void Clear()
    {
        lock (_gate) _pending.Clear();
    }

    private ParsedFragment ParseFragment(byte[] packet)
    {
        if (packet.Length < MinimumIpv4HeaderLength || packet[0] >> 4 != 4)
            throw new FormatException("Expected an inner IPv4 packet.");

        int headerLength = (packet[0] & 0x0f) * 4;
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        if (headerLength < MinimumIpv4HeaderLength || totalLength < headerLength || totalLength > packet.Length)
            throw new FormatException("Invalid IPv4 length.");

        ushort flagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        if ((flagsAndOffset & 0x8000) != 0)
            throw new FormatException("Invalid reserved IPv4 fragment flag.");

        bool moreFragments = (flagsAndOffset & 0x2000) != 0;
        int offset = (flagsAndOffset & 0x1fff) * 8;
        int payloadLength = totalLength - headerLength;
        if (payloadLength == 0 && (moreFragments || offset != 0))
            throw new FormatException("Empty IPv4 fragment.");
        if (moreFragments && payloadLength % 8 != 0)
            throw new FormatException("Non-final IPv4 fragment length must be a multiple of 8 bytes.");
        if (offset + payloadLength > MaximumIpv4PacketLength - MinimumIpv4HeaderLength)
            throw new FormatException("IPv4 fragment range exceeds the maximum packet length.");

        var key = new FragmentKey(
            BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2)),
            packet[9]);
        return new ParsedFragment(key, headerLength, totalLength, offset, moreFragments,
            packet.AsSpan(headerLength, payloadLength).ToArray());
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _pending
                     .Where(entry => now - entry.Value.LastUpdated >= _fragmentLifetime)
                     .Select(entry => entry.Key)
                     .ToArray())
            _pending.Remove(key);
    }

    private static void ValidateOverlap(FragmentSegment existing, int offset, byte[] payload)
    {
        int overlapStart = Math.Max(existing.Offset, offset);
        int overlapEnd = Math.Min(existing.Offset + existing.Payload.Length, offset + payload.Length);
        for (int position = overlapStart; position < overlapEnd; position++)
        {
            if (existing.Payload[position - existing.Offset] != payload[position - offset])
                throw new FormatException("Conflicting overlapping IPv4 fragments were received.");
        }
    }

    private static bool IsComplete(List<FragmentSegment> fragments, int payloadLength)
    {
        int coveredThrough = 0;
        foreach (var fragment in fragments.OrderBy(fragment => fragment.Offset))
        {
            if (fragment.Offset > coveredThrough)
                return false;
            coveredThrough = Math.Max(coveredThrough, fragment.Offset + fragment.Payload.Length);
            if (coveredThrough >= payloadLength)
                return true;
        }
        return false;
    }

    private static void WriteHeaderChecksum(byte[] packet, int headerLength)
    {
        packet[10] = packet[11] = 0;
        uint sum = 0;
        for (int i = 0; i < headerLength; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i, 2));
        while (sum > ushort.MaxValue)
            sum = (sum & ushort.MaxValue) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), (ushort)~sum);
    }

    private readonly record struct FragmentKey(uint Source, uint Destination, ushort Identification, byte Protocol);
    private sealed class PendingDatagram(DateTimeOffset lastUpdated)
    {
        public DateTimeOffset LastUpdated { get; set; } = lastUpdated;
        public byte[]? Header { get; set; }
        public int? PayloadLength { get; set; }
        public List<FragmentSegment> Fragments { get; } = new();
    }
    private sealed record FragmentSegment(int Offset, byte[] Payload);
    private sealed record ParsedFragment(
        FragmentKey Key,
        int HeaderLength,
        int TotalLength,
        int Offset,
        bool MoreFragments,
        byte[] Payload);
}
