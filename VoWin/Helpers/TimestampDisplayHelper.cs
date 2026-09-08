using System.Globalization;

namespace VoWin.Helpers;

/// <summary>
/// SMS PDUs are decoded to UTC so their network-supplied SCTS offsets remain
/// unambiguous.  The UI, however, must consistently present those instants in
/// the Windows local time zone.
/// </summary>
public static class TimestampDisplayHelper
{
    private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromMinutes(5);

    public static DateTime ToLocalDisplayTime(DateTime timestamp) =>
        ToUtcStorageTime(timestamp).ToLocalTime();

    /// <summary>
    /// Normalizes app timestamps to UTC before persistence. Legacy values with
    /// no Kind were historically created as local wall-clock times.
    /// </summary>
    public static DateTime ToUtcStorageTime(DateTime timestamp)
    {
        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
                TimeZoneInfo.Local)
        };
    }

    public static bool TryParseStoredUtc(string? value, out DateTime utc)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed))
        {
            utc = ToUtcStorageTime(parsed);
            return true;
        }

        utc = default;
        return false;
    }

    /// <summary>
    /// Some networks encode local wall-clock SCTS values while incorrectly
    /// declaring GMT+0. If that produces a timestamp in the future, reinterpret
    /// the same wall clock in the computer's local zone when it is more plausible.
    /// Standards-compliant and legitimately delayed timestamps are left intact.
    /// </summary>
    public static DateTime NormalizeIncomingNetworkTime(DateTime timestamp, DateTime? receivedAtUtc = null)
    {
        var received = ToUtcStorageTime(receivedAtUtc ?? DateTime.UtcNow);
        var candidate = ToUtcStorageTime(timestamp);
        if (candidate <= received + FutureClockTolerance)
        {
            return candidate;
        }

        var wallClock = DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified);
        DateTime localInterpretation;
        try
        {
            localInterpretation = TimeZoneInfo.ConvertTimeToUtc(wallClock, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return candidate;
        }

        return localInterpretation <= received + FutureClockTolerance &&
               Math.Abs((localInterpretation - received).Ticks) < Math.Abs((candidate - received).Ticks)
            ? localInterpretation
            : candidate;
    }
}
