using VoSharp.Telephony.Calls;
using VoWin.Helpers;

namespace VoWin.Models
{
    public enum CallDirection
    {
        Outgoing,
        Incoming,
        Missed
    }

    public class CallRecordModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string PhoneNumber { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public CallDirection Direction { get; set; }
        public CallState FinalState { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
        public string? Codec { get; set; }
        public string? SlotId { get; set; }
        public string? WavRecordingPath { get; set; }

        public string FormattedDuration => Duration.TotalSeconds > 0
            ? $"{(int)Duration.TotalMinutes:D2}:{Duration.Seconds:D2}"
            : (Direction == CallDirection.Missed ? "未接通" : "已挂断");

        private DateTime LocalTimestamp => TimestampDisplayHelper.ToLocalDisplayTime(Timestamp);

        public string FormattedTime => LocalTimestamp.Date == DateTime.Today
            ? LocalTimestamp.ToString("HH:mm")
            : LocalTimestamp.ToString("MM/dd HH:mm");
    }
}
