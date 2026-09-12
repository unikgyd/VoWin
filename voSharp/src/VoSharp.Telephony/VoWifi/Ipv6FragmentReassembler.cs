using System.Buffers.Binary;

namespace VoSharp.Telephony.VoWifi;

/// <summary>Reassembles RFC 8200 IPv6 fragments carried inside the ePDG CHILD_SA.</summary>
public sealed class Ipv6FragmentReassembler
{
    private readonly object _gate = new();
    private readonly Dictionary<FragmentKey, PendingDatagram> _pending = new();
    private readonly TimeSpan _fragmentLifetime;
    private readonly int _maximumPendingDatagrams;
    private readonly TimeProvider _timeProvider;

    public Ipv6FragmentReassembler(
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

    /// <summary>Returns the original packet, a reassembled packet, or null while awaiting fragments.</summary>
    public byte[]? Process(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var parsed = Parse(packet);
        if (parsed is null)
            return packet;

        var fragment = parsed.Value;
        if (!fragment.MoreFragments && fragment.Offset == 0)
            return Rebuild(fragment.Header, fragment.PreviousNextHeaderOffset,
                fragment.UpperLayerProtocol, fragment.Payload);

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            RemoveExpired(now);
            if (!_pending.TryGetValue(fragment.Key, out var datagram))
            {
                if (_pending.Count >= _maximumPendingDatagrams)
                    _pending.Remove(_pending.MinBy(entry => entry.Value.LastUpdated).Key);
                datagram = new PendingDatagram(now, fragment.Header,
                    fragment.PreviousNextHeaderOffset, fragment.UpperLayerProtocol);
                _pending.Add(fragment.Key, datagram);
            }

            try
            {
                datagram.LastUpdated = now;
                if (!HeadersEquivalent(datagram.Header, fragment.Header) ||
                    datagram.PreviousNextHeaderOffset != fragment.PreviousNextHeaderOffset ||
                    datagram.UpperLayerProtocol != fragment.UpperLayerProtocol)
                    throw new FormatException("Conflicting IPv6 fragment headers were received.");

                int end = checked(fragment.Offset + fragment.Payload.Length);
                if (end > ushort.MaxValue)
                    throw new FormatException("IPv6 fragment range exceeds the non-jumbo maximum.");
                if (datagram.PayloadLength is { } known && end > known)
                    throw new FormatException("IPv6 fragment extends beyond the final fragment.");
                if (!fragment.MoreFragments)
                {
                    if (datagram.PayloadLength is { } previous && previous != end)
                        throw new FormatException("Conflicting final IPv6 fragments were received.");
                    if (datagram.Fragments.Any(existing => existing.Offset + existing.Payload.Length > end))
                        throw new FormatException("Stored IPv6 fragment extends beyond the final fragment.");
                    datagram.PayloadLength = end;
                }

                foreach (var existing in datagram.Fragments)
                    ValidateOverlap(existing, fragment.Offset, fragment.Payload);
                if (!datagram.Fragments.Any(existing => existing.Offset == fragment.Offset &&
                        existing.Payload.AsSpan().SequenceEqual(fragment.Payload)))
                    datagram.Fragments.Add(new FragmentSegment(fragment.Offset, fragment.Payload));

                if (datagram.PayloadLength is not { } payloadLength ||
                    !IsComplete(datagram.Fragments, payloadLength))
                    return null;
                if (datagram.Header.Length - 40 + payloadLength > ushort.MaxValue)
                    throw new FormatException("Reassembled IPv6 payload exceeds the non-jumbo maximum.");

                var payload = new byte[payloadLength];
                foreach (var segment in datagram.Fragments)
                    segment.Payload.CopyTo(payload, segment.Offset);
                var result = Rebuild(datagram.Header, datagram.PreviousNextHeaderOffset,
                    datagram.UpperLayerProtocol, payload);
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

    private static ParsedFragment? Parse(byte[] packet)
    {
        int totalLength = GetTotalLength(packet);
        int position = 40;
        int previousNextHeaderOffset = 6;
        byte next = packet[6];
        for (var hop = 0; hop < 8; hop++)
        {
            if (next == 44)
            {
                if (position + 8 > totalLength)
                    throw new FormatException("Truncated IPv6 Fragment header.");
                ushort offsetAndFlags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(position + 2, 2));
                if ((offsetAndFlags & 0x0006) != 0)
                    throw new FormatException("Invalid reserved IPv6 fragment bits.");
                int offset = offsetAndFlags & 0xfff8;
                bool more = (offsetAndFlags & 1) != 0;
                var payload = packet.AsSpan(position + 8, totalLength - position - 8).ToArray();
                if (payload.Length == 0 && (more || offset != 0))
                    throw new FormatException("Empty IPv6 fragment.");
                if (more && payload.Length % 8 != 0)
                    throw new FormatException("Non-final IPv6 fragment length must be a multiple of 8 bytes.");
                byte upperProtocol = packet[position];
                uint identification = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(position + 4, 4));
                var header = packet.AsSpan(0, position).ToArray();
                var key = new FragmentKey(
                    Convert.ToHexString(packet.AsSpan(8, 16)),
                    Convert.ToHexString(packet.AsSpan(24, 16)),
                    identification,
                    upperProtocol);
                return new ParsedFragment(key, header, previousNextHeaderOffset,
                    upperProtocol, offset, more, payload);
            }

            if (next is not (0 or 43 or 60))
                return null;
            if (position + 8 > totalLength)
                throw new FormatException("Truncated IPv6 extension header.");
            previousNextHeaderOffset = position;
            next = packet[position];
            position += (packet[position + 1] + 1) * 8;
            if (position > totalLength)
                throw new FormatException("Invalid IPv6 extension header length.");
        }
        throw new FormatException("IPv6 extension-header chain is too deep.");
    }

    private static int GetTotalLength(byte[] packet)
    {
        if (packet.Length < 40 || packet[0] >> 4 != 6)
            throw new FormatException("Expected an inner IPv6 packet.");
        int totalLength = 40 + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        if (totalLength > packet.Length)
            throw new FormatException("Invalid IPv6 length.");
        return totalLength;
    }

    private static byte[] Rebuild(byte[] header, int previousNextHeaderOffset, byte upperProtocol, byte[] payload)
    {
        var result = new byte[header.Length + payload.Length];
        header.CopyTo(result, 0);
        payload.CopyTo(result, header.Length);
        result[previousNextHeaderOffset] = upperProtocol;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2),
            checked((ushort)(result.Length - 40)));
        return result;
    }

    private static bool HeadersEquivalent(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) return false;
        for (var i = 0; i < first.Length; i++)
        {
            if (i is 4 or 5) continue; // Per-fragment payload length differs.
            if (first[i] != second[i]) return false;
        }
        return true;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _pending.Where(entry => now - entry.Value.LastUpdated >= _fragmentLifetime)
                     .Select(entry => entry.Key).ToArray())
            _pending.Remove(key);
    }

    private static void ValidateOverlap(FragmentSegment existing, int offset, byte[] payload)
    {
        int start = Math.Max(existing.Offset, offset);
        int end = Math.Min(existing.Offset + existing.Payload.Length, offset + payload.Length);
        for (var position = start; position < end; position++)
            if (existing.Payload[position - existing.Offset] != payload[position - offset])
                throw new FormatException("Conflicting overlapping IPv6 fragments were received.");
    }

    private static bool IsComplete(List<FragmentSegment> fragments, int payloadLength)
    {
        int coveredThrough = 0;
        foreach (var fragment in fragments.OrderBy(fragment => fragment.Offset))
        {
            if (fragment.Offset > coveredThrough) return false;
            coveredThrough = Math.Max(coveredThrough, fragment.Offset + fragment.Payload.Length);
            if (coveredThrough >= payloadLength) return true;
        }
        return false;
    }

    private readonly record struct FragmentKey(string Source, string Destination, uint Identification, byte Protocol);
    private sealed class PendingDatagram(DateTimeOffset lastUpdated, byte[] header,
        int previousNextHeaderOffset, byte upperLayerProtocol)
    {
        public DateTimeOffset LastUpdated { get; set; } = lastUpdated;
        public byte[] Header { get; } = header;
        public int PreviousNextHeaderOffset { get; } = previousNextHeaderOffset;
        public byte UpperLayerProtocol { get; } = upperLayerProtocol;
        public int? PayloadLength { get; set; }
        public List<FragmentSegment> Fragments { get; } = new();
    }
    private sealed record FragmentSegment(int Offset, byte[] Payload);
    private readonly record struct ParsedFragment(
        FragmentKey Key,
        byte[] Header,
        int PreviousNextHeaderOffset,
        byte UpperLayerProtocol,
        int Offset,
        bool MoreFragments,
        byte[] Payload);
}
