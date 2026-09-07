using System.Collections.Concurrent;
using System.Text;

namespace VoSharp.Telephony.Sms;

/// <summary>
/// Thread-safe in-memory buffer that reassembles multi-part concatenated SMS messages.
/// Handles fragments arriving out of order and filters duplicate/retransmitted segments.
/// </summary>
public class SmsReassembler
{
    private class FragmentGroup
    {
        public string Sender { get; init; } = string.Empty;
        public int Reference { get; init; }
        public int Total { get; init; }
        public SmsEncoding Encoding { get; init; }
        public DateTime FirstReceived { get; init; } = DateTime.UtcNow;
        public DateTime? ServiceCenterTimestamp { get; set; }
        public Dictionary<int, string> Segments { get; } = new();
    }

    private const int MaxPendingGroups = 1000;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, FragmentGroup> _groups = new();
    private readonly object _lock = new();

    /// <summary>
    /// Feeds an incoming SMS part into the reassembly engine.
    /// If the SMS is a single-part message or the final missing part of a multi-part message,
    /// returns the complete, reassembled <see cref="IncomingSms"/>.
    /// Otherwise, returns <c>null</c> while awaiting remaining fragments.
    /// </summary>
    public IncomingSms? ProcessIncomingPart(IncomingSms part)
    {
        if (part.Concat == null || part.Concat.Total <= 1)
        {
            // Single-part SMS — immediately ready
            return part;
        }

        var concat = part.Concat;
        // 3GPP TS 23.040 validation: 1 <= Sequence <= Total <= 255
        if (concat.Total > 255 || concat.Sequence < 1 || concat.Sequence > concat.Total)
        {
            return part;
        }

        string key = BuildGroupKey(part.SenderNumber, concat.Reference, concat.Total);

        lock (_lock)
        {
            // Capacity protection
            if (_groups.Count >= MaxPendingGroups)
            {
                CleanupStaleFragments(DefaultTtl);
                if (_groups.Count >= MaxPendingGroups)
                {
                    // Evict oldest group
                    var oldestKey = _groups.OrderBy(kvp => kvp.Value.FirstReceived).FirstOrDefault().Key;
                    if (oldestKey != null)
                    {
                        _groups.TryRemove(oldestKey, out _);
                    }
                }
            }

            if (!_groups.TryGetValue(key, out var group))
            {
                group = new FragmentGroup
                {
                    Sender = part.SenderNumber,
                    Reference = concat.Reference,
                    Total = concat.Total,
                    Encoding = part.Encoding,
                    FirstReceived = DateTime.UtcNow,
                    ServiceCenterTimestamp = part.ServiceCenterTimestamp
                };
                _groups[key] = group;
            }

            // Record segment text in sequence position
            group.Segments[concat.Sequence] = part.Text;
            if (part.ServiceCenterTimestamp.HasValue && !group.ServiceCenterTimestamp.HasValue)
            {
                group.ServiceCenterTimestamp = part.ServiceCenterTimestamp;
            }

            // Check if all parts 1..Total have arrived
            if (group.Segments.Count >= group.Total)
            {
                var sb = new StringBuilder();
                for (int seq = 1; seq <= group.Total; seq++)
                {
                    if (group.Segments.TryGetValue(seq, out var segText))
                    {
                        sb.Append(segText);
                    }
                }

                _groups.TryRemove(key, out _);

                return new IncomingSms(
                    SenderNumber: group.Sender,
                    Text: sb.ToString(),
                    Timestamp: group.FirstReceived,
                    ServiceCenterTimestamp: group.ServiceCenterTimestamp,
                    Encoding: group.Encoding,
                    Concat: concat,
                    RawPdu: part.RawPdu,
                    RawTpdu: part.RawTpdu,
                    ProtocolId: part.ProtocolId,
                    DataCodingScheme: part.DataCodingScheme,
                    IsMachinePayload: part.IsMachinePayload,
                    IsStatusReport: part.IsStatusReport,
                    StatusReport: part.StatusReport
                );
            }
        }

        return null;
    }

    /// <summary>
    /// Cleans up incomplete message fragment groups that have exceeded their TTL.
    /// </summary>
    public void CleanupStaleFragments(TimeSpan ttl)
    {
        var cutoff = DateTime.UtcNow - ttl;
        lock (_lock)
        {
            var staleKeys = _groups
                .Where(kvp => kvp.Value.FirstReceived < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                _groups.TryRemove(key, out _);
            }
        }
    }

    private static string BuildGroupKey(string sender, int reference, int total)
    {
        return $"{sender.Trim()}:{reference}:{total}";
    }
}
