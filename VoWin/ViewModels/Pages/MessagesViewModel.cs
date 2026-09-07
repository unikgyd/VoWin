using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoSharp.Kernel.Pool;
using VoWin.Models;
using VoWin.Services;
using Wpf.Ui.Controls;

namespace VoWin.ViewModels.Pages
{
    public partial class MessagesViewModel : ObservableObject
    {
        private readonly IVoKernelService _kernelService;

        public MessagesViewModel ViewModel => this;
        public ObservableCollection<SmsConversationModel> Conversations => _kernelService.Conversations;
        public ICollectionView ConversationsView { get; }
        public ObservableCollection<ModemSlot> Slots => _kernelService.Slots;

        [ObservableProperty]
        private SmsConversationModel? _selectedConversation;

        [ObservableProperty]
        private ModemSlot? _selectedSlot;

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private string _messageText = string.Empty;

        [ObservableProperty]
        private bool _isCreatingNewChat;

        [ObservableProperty]
        private string _newRecipientInput = string.Empty;

        [ObservableProperty]
        private bool _requestDeliveryReport = true;

        [ObservableProperty]
        private bool _forceCellular;

        [ObservableProperty]
        private bool _isSending;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isCalling;

        public bool IsBusy => IsSending || IsCalling;

        public string CharCountInfo
        {
            get
            {
                int len = MessageText?.Length ?? 0;
                bool isUcs2 = MessageText != null && MessageText.Any(c => c > 127);
                int segmentSize = isUcs2 ? 70 : 160;
                if (len == 0)
                {
                    return $"0 / {segmentSize} (1条, {(isUcs2 ? "UCS-2" : "GSM-7")})";
                }

                int segments = (int)Math.Ceiling((double)len / segmentSize);
                if (segments == 0) segments = 1;
                string enc = isUcs2 ? "UCS-2" : "GSM-7";
                return $"{len} / {segmentSize * segments} ({segments}条, {enc})";
            }
        }

        public string MessageInput
        {
            get => MessageText;
            set => MessageText = value;
        }

        public string MessageTextInput
        {
            get => MessageText;
            set => MessageText = value;
        }

        public string CharacterCountInfo => CharCountInfo;

        public MessagesViewModel(IVoKernelService kernelService)
        {
            _kernelService = kernelService;
            SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();
            ConversationsView = CollectionViewSource.GetDefaultView(Conversations);
            ConversationsView.Filter = FilterConversation;

            Slots.CollectionChanged += (s, e) =>
            {
                if (SelectedSlot == null && Slots.Count > 0)
                {
                    SelectedSlot = Slots.FirstOrDefault();
                }
            };

            if (Conversations.Count > 0)
            {
                SelectedConversation = Conversations.First();
                SelectedConversation.UnreadCount = 0;
            }
        }

        partial void OnMessageTextChanged(string value)
        {
            OnPropertyChanged(nameof(CharCountInfo));
            OnPropertyChanged(nameof(CharacterCountInfo));
            OnPropertyChanged(nameof(MessageInput));
            OnPropertyChanged(nameof(MessageTextInput));
            SendMessageCommand.NotifyCanExecuteChanged();
        }

        partial void OnSearchQueryChanged(string value) => ConversationsView.Refresh();

        partial void OnIsSendingChanged(bool value)
        {
            OnPropertyChanged(nameof(IsBusy));
            SendMessageCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsCallingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

        private bool FilterConversation(object item)
        {
            if (item is not SmsConversationModel conversation || string.IsNullOrWhiteSpace(SearchQuery))
            {
                return true;
            }

            var query = SearchQuery.Trim();
            return conversation.ContactNumber.Contains(query, StringComparison.OrdinalIgnoreCase)
                || conversation.Messages.Any(message => message.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        [RelayCommand]
        private async Task CallContactAsync()
        {
            if (SelectedConversation == null || IsCalling) return;
            IsCalling = true;
            StatusMessage = $"正在呼叫 {SelectedConversation.ContactNumber}...";
            try
            {
                var targetSlot = SelectedSlot ?? Slots.FirstOrDefault();
                await _kernelService.DialAsync(SelectedConversation.ContactNumber, targetSlot?.Id);
                StatusMessage = "呼叫已发起。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"呼叫失败: {ex.Message}";
            }
            finally
            {
                IsCalling = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanSendMessage))]
        private Task SendMessageAsync() => SendSmsAsync();

        private bool CanSendMessage() =>
            !IsSending && SelectedConversation != null && !string.IsNullOrWhiteSpace(MessageText);

        partial void OnSelectedConversationChanged(SmsConversationModel? value)
        {
            if (value != null)
            {
                value.UnreadCount = 0;
            }
            SendMessageCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void StartNewChat()
        {
            IsCreatingNewChat = true;
            NewRecipientInput = string.Empty;
        }

        [RelayCommand]
        private void CancelNewChat()
        {
            IsCreatingNewChat = false;
            NewRecipientInput = string.Empty;
        }

        [RelayCommand]
        private void ConfirmNewChat()
        {
            if (string.IsNullOrWhiteSpace(NewRecipientInput)) return;
            var num = NewRecipientInput.Trim();

            var existing = Conversations.FirstOrDefault(c => c.ContactNumber == num);
            if (existing == null)
            {
                string[] colors = { "#0078D4", "#107C10", "#D13438", "#8764B8", "#FF8C00", "#008272" };
                int hash = Math.Abs(num.GetHashCode());
                var letter = num.Length > 0 ? (char.IsLetter(num[0]) ? num[0].ToString().ToUpper() : num[^1].ToString()) : "#";

                existing = new SmsConversationModel
                {
                    ContactNumber = num,
                    AvatarLetter = letter,
                    AvatarColor = colors[hash % colors.Length]
                };
                Conversations.Insert(0, existing);
            }

            SelectedConversation = existing;
            SelectedConversation.UnreadCount = 0;
            IsCreatingNewChat = false;
            NewRecipientInput = string.Empty;
        }

        [RelayCommand]
        private async Task DeleteConversationAsync(SmsConversationModel? conv)
        {
            var target = conv ?? SelectedConversation;
            if (target != null)
            {
                var uiMessageBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "确认删除会话",
                    Content = $"确定要删除与 {target.ContactNumber} 的全部会话记录吗？\n此操作不可恢复。",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消"
                };

                var result = await uiMessageBox.ShowDialogAsync();
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    _kernelService.DeleteConversation(target.ContactNumber);
                    if (SelectedConversation == target)
                    {
                        SelectedConversation = Conversations.FirstOrDefault();
                    }
                    StatusMessage = $"已删除与 {target.ContactNumber} 的会话。";
                }
            }
        }

        [RelayCommand]
        private async Task ClearConversationAsync()
        {
            if (SelectedConversation != null)
            {
                var uiMessageBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "确认清空会话",
                    Content = $"确定要清空与 {SelectedConversation.ContactNumber} 的所有短信记录吗？\n此操作不可恢复。",
                    PrimaryButtonText = "清空",
                    CloseButtonText = "取消"
                };

                var result = await uiMessageBox.ShowDialogAsync();
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    _kernelService.ClearConversation(SelectedConversation.ContactNumber);
                    StatusMessage = $"已清空与 {SelectedConversation.ContactNumber} 的短信记录。";
                }
            }
        }

        [RelayCommand]
        private void CopyOtpCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            try
            {
                System.Windows.Clipboard.SetText(code.Trim());
                StatusMessage = $"验证码 {code.Trim()} 已复制到剪贴板。";
                AppToast.ShowCopySuccess($"短信验证码 {code.Trim()}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CopyContactNumber()
        {
            if (SelectedConversation == null || string.IsNullOrWhiteSpace(SelectedConversation.ContactNumber)) return;
            try
            {
                System.Windows.Clipboard.SetText(SelectedConversation.ContactNumber.Trim());
                StatusMessage = $"号码 {SelectedConversation.ContactNumber.Trim()} 已复制到剪贴板。";
                AppToast.ShowCopySuccess($"联系人号码 {SelectedConversation.ContactNumber.Trim()}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task RetryMessageAsync(SmsMessageModel? msg)
        {
            if (msg == null || SelectedConversation == null) return;
            MessageText = msg.Text;
            await SendSmsAsync();
        }

        [RelayCommand]
        private void TestOtpNotification()
        {
            var testSms = new SmsMessageModel
            {
                SenderOrRecipient = "10690007890",
                Text = "【阿里云】您正在登录控制台，验证码为 839201，请在 5 分钟内完成验证。切勿告知他人！",
                Timestamp = DateTime.Now,
                IsOutgoing = false,
                DeliveryState = SmsDeliveryState.Received
            };
            SoundEffectService.Instance.PlaySmsChime();
            Views.Windows.IncomingSmsFloatingWindow.ShowNotification(testSms, SelectedSlot?.Name ?? "SIM 1");
            StatusMessage = "已触发验证码短信悬浮窗测试。";
        }

        [RelayCommand]
        private void CopyMessage(object? parameter)
        {
            string? text = null;
            if (parameter is SmsMessageModel model)
            {
                text = model.Text;
            }
            else if (parameter is string s)
            {
                text = s;
            }
            else if (parameter == null && SelectedConversation?.Messages.LastOrDefault() is SmsMessageModel last)
            {
                text = last.Text;
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                StatusMessage = "短信内容已成功复制到剪贴板。";
                AppToast.ShowCopySuccess("短信内容");
            }
            catch
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    StatusMessage = "短信内容已成功复制到剪贴板。";
                    AppToast.ShowCopySuccess("短信内容");
                }
                catch (Exception ex)
                {
                    StatusMessage = $"复制失败: {ex.Message}";
                }
            }
        }

        [RelayCommand]
        private void DeleteMessage(SmsMessageModel? msg)
        {
            if (SelectedConversation != null && msg != null)
            {
                _kernelService.DeleteMessage(SelectedConversation.ContactNumber, msg.Id);
            }
        }

        private async Task SendSmsAsync()
        {
            if (SelectedConversation == null || string.IsNullOrWhiteSpace(MessageText)) return;
            var text = MessageText.Trim();
            var recipient = SelectedConversation.ContactNumber;

            IsSending = true;
            StatusMessage = "正在发送短信...";

            try
            {
                MessageText = string.Empty;
                var targetSlot = SelectedSlot ?? Slots.FirstOrDefault();
                var res = await _kernelService.SendSmsAsync(
                    recipient,
                    text,
                    RequestDeliveryReport,
                    targetSlot?.Id,
                    forceVowifi: !ForceCellular,
                    forceCellular: ForceCellular);

                StatusMessage = res.AllPartsAccepted ? "短信已成功提交发送。" : $"部分发送: {res.SubmissionStatus}";
            }
            catch (Exception ex)
            {
                if (string.IsNullOrWhiteSpace(MessageText))
                {
                    MessageText = text;
                }
                StatusMessage = $"发送失败: {ex.Message}";
            }
            finally
            {
                IsSending = false;
            }
        }
    }
}
