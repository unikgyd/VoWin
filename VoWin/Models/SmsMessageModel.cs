using CommunityToolkit.Mvvm.ComponentModel;
using VoWin.Helpers;
using System.Text.Json.Serialization;

namespace VoWin.Models
{
    public enum SmsDeliveryState
    {
        Received,
        Sending,
        Sent,
        Delivered,
        Failed,
        PartiallySent
    }

    public class SmsMessageModel : ObservableObject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Index { get; set; }
        public string SenderOrRecipient { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public bool IsOutgoing { get; set; }
        private SmsDeliveryState _deliveryState = SmsDeliveryState.Received;
        public SmsDeliveryState DeliveryState
        {
            get => _deliveryState;
            set
            {
                if (!SetProperty(ref _deliveryState, value)) return;
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(DeliveryStatusText));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
        private string? _deliveryStatus;
        public string? DeliveryStatus
        {
            get => _deliveryStatus;
            set => SetProperty(ref _deliveryStatus, value);
        }
        public int? MessageReference { get; set; }
        public string? SlotId { get; set; }
        public string? RawPdu { get; set; }
        /// <summary>Receive-session snapshot, never inferred later from the currently inserted card.</summary>
        [JsonIgnore] public string? SimBinding { get; set; }
        /// <summary>The originating SMS identity, distinct from this persisted row's UI ID.</summary>
        [JsonIgnore] public string? SourceMessageId { get; set; }
        /// <summary>Receive-time canonical reply: null is legacy; empty is intentionally read-only. Never backfill it.</summary>
        [JsonIgnore] public string? SipReplyNumberSnapshot { get; set; }

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
            SmsDeliveryState.PartiallySent => "部分提交，请勿整条重发",
            _ => string.Empty
        };

        public string DeliveryStatusText => StatusDisplay;

        public bool IsFailed => DeliveryState == SmsDeliveryState.Failed;

        public string? ExtractedOtpCode => SmsVerificationCodeExtractor.Extract(Text);

        public bool HasOtpCode => !string.IsNullOrEmpty(ExtractedOtpCode);
    }
}
