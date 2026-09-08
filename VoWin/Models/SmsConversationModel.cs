using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VoWin.Helpers;

namespace VoWin.Models
{
    public partial class SmsConversationModel : ObservableObject
    {
        [ObservableProperty]
        private string _contactNumber = string.Empty;

        [ObservableProperty]
        private string? _contactName;

        [ObservableProperty]
        private string _lastMessageText = string.Empty;

        [ObservableProperty]
        private DateTime _lastMessageTime = DateTime.UtcNow;

        [ObservableProperty]
        private int _unreadCount;

        [ObservableProperty]
        private string _avatarLetter = "#";

        [ObservableProperty]
        private string _avatarColor = "#0078D4";

        public ObservableCollection<SmsMessageModel> Messages { get; } = new();

        public string DisplayTitle => string.IsNullOrWhiteSpace(ContactName) ? ContactNumber : ContactName;

        public string RemoteParty => DisplayTitle;

        public string RemotePartyInitial => !string.IsNullOrEmpty(AvatarLetter) && AvatarLetter != "#"
            ? AvatarLetter
            : (!string.IsNullOrEmpty(DisplayTitle) ? (char.IsLetter(DisplayTitle[0]) ? DisplayTitle[0].ToString().ToUpper() : DisplayTitle[^1].ToString()) : "#");

        public string LatestMessageSummary => LastMessageText;

        public string FormattedLatestTime => FormattedTime;

        private DateTime LocalLastMessageTime => TimestampDisplayHelper.ToLocalDisplayTime(LastMessageTime);

        public string FormattedTime => LocalLastMessageTime.Date == DateTime.Today
            ? LocalLastMessageTime.ToString("HH:mm")
            : LocalLastMessageTime.ToString("MM/dd");

        partial void OnContactNumberChanged(string value)
        {
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(RemoteParty));
            OnPropertyChanged(nameof(RemotePartyInitial));
        }

        partial void OnContactNameChanged(string? value)
        {
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(RemoteParty));
            OnPropertyChanged(nameof(RemotePartyInitial));
        }

        partial void OnAvatarLetterChanged(string value)
        {
            OnPropertyChanged(nameof(RemotePartyInitial));
        }

        partial void OnLastMessageTextChanged(string value)
        {
            OnPropertyChanged(nameof(LatestMessageSummary));
        }

        partial void OnLastMessageTimeChanged(DateTime value)
        {
            OnPropertyChanged(nameof(FormattedTime));
            OnPropertyChanged(nameof(FormattedLatestTime));
        }

        public void UpdateLastMessage()
        {
            var last = Messages.OrderBy(m => m.Timestamp).LastOrDefault();
            if (last != null)
            {
                LastMessageText = last.Text;
                LastMessageTime = last.Timestamp;
            }
            else
            {
                LastMessageText = string.Empty;
            }
            OnPropertyChanged(nameof(FormattedTime));
            OnPropertyChanged(nameof(FormattedLatestTime));
            OnPropertyChanged(nameof(LatestMessageSummary));
            OnPropertyChanged(nameof(LastMessageText));
        }
    }
}
