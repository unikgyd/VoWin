using VoSharp.Telephony.Calls;

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
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
        public string? Codec { get; set; }
        public string? SlotId { get; set; }
        public string? WavRecordingPath { get; set; }

        public string FormattedDuration => Duration.TotalSeconds > 0
            ? $"{(int)Duration.TotalMinutes:D2}:{Duration.Seconds:D2}"
            : (Direction == CallDirection.Missed ? "未接通" : "已挂断");

        public string FormattedTime => Timestamp.Date == DateTime.Today
            ? Timestamp.ToString("HH:mm")
            : Timestamp.ToString("MM/dd HH:mm");
    }
}
