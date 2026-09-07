namespace VoWin.Helpers;

/// <summary>
/// SMS PDUs are decoded to UTC so their network-supplied SCTS offsets remain
/// unambiguous.  The UI, however, must consistently present those instants in
/// the Windows local time zone.
/// </summary>
public static class TimestampDisplayHelper
{
    public static DateTime ToLocalDisplayTime(DateTime timestamp) =>
        timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
}
