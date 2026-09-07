using VoSharp.Telephony.Sms;
using VoWin.Helpers;

namespace VoWin.Models
{
    public enum SmsDeliveryState
    {
        Received,
        Sending,
        Sent,
        Delivered,
        Failed
    }

    public class SmsMessageModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Index { get; set; }
        public string SenderOrRecipient { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool IsOutgoing { get; set; }
        public SmsDeliveryState DeliveryState { get; set; } = SmsDeliveryState.Received;
        public string? DeliveryStatus { get; set; }
        public int? MessageReference { get; set; }
        public string? SlotId { get; set; }
        public string? RawPdu { get; set; }

        private DateTime LocalTimestamp => TimestampDisplayHelper.ToLocalDisplayTime(Timestamp);

        public string FormattedTime => LocalTimestamp.Date == DateTime.Today
            ? LocalTimestamp.ToString("HH:mm")
            : LocalTimestamp.ToString("MM/dd HH:mm");

        public string FormattedTimestamp => FormattedTime;

        public bool Direction => IsOutgoing;

        public string StatusDisplay => DeliveryState switch
        {
            SmsDeliveryState.Sending => "发送中...",
            SmsDeliveryState.Sent => "已发送",
            SmsDeliveryState.Delivered => "已送达",
            SmsDeliveryState.Failed => "发送失败",
            _ => string.Empty
        };

        public string DeliveryStatusText => StatusDisplay;

        public bool IsFailed => DeliveryState == SmsDeliveryState.Failed;

        public string? ExtractedOtpCode
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Text)) return null;
                var match = System.Text.RegularExpressions.Regex.Match(Text, @"(?<!\d)(\d{4,8})(?!\d)");
                if (match.Success)
                {
                    string lower = Text.ToLowerInvariant();
                    if (lower.Contains("验证码") || lower.Contains("校验码") || lower.Contains("动态码") ||
                        lower.Contains("code") || lower.Contains("otp") || lower.Contains("pin") ||
                        (match.Length == 6 && Text.Length < 120))
                    {
                        return match.Groups[1].Value;
                    }
                }
                return null;
            }
        }

        public bool HasOtpCode => !string.IsNullOrEmpty(ExtractedOtpCode);
    }
}
