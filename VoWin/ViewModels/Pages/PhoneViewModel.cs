using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.Wave;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.ViewModels.Pages
{
    public enum CallPhase
    {
        Idle,
        Preparing,
        Dialing,
        Ringing,
        Connected,
        Held,
        Ended,
        Incoming
    }

    public partial class PhoneViewModel : ObservableObject
    {
        private readonly IVoKernelService _kernelService;
        private readonly DispatcherTimer _durationTimer;
        private DateTime? _callConnectedTime;
        private CancellationTokenSource? _statusResetCts;
        private string? _callEndReasonTitle;
        private string? _callEndReasonDetail;
        private bool _isShowingEndedSummary;

        public ObservableCollection<ModemSlot> Slots => _kernelService.Slots;
        public ObservableCollection<CallRecordModel> CallHistory => _kernelService.CallHistory;
        public ObservableCollection<string> AudioDevices { get; } = new();

        [ObservableProperty]
        private ModemSlot? _selectedSlot;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DialCommand))]
        private string _phoneNumber = string.Empty;

        [ObservableProperty]
        private bool _forceCellular;

        [ObservableProperty]
        private bool _isMuted;

        [ObservableProperty]
        private bool _isHeld;

        [ObservableProperty]
        private bool _showInCallKeypad;

        [ObservableProperty]
        private string _callDurationText = "00:00";

        [ObservableProperty]
        private string _codecInfo = "VoWiFi AMR-WB 16kHz";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DialCommand))]
        private bool _isCallOperationPending;

        [ObservableProperty]
        private string? _selectedAudioDevice;

        [ObservableProperty]
        private string _dtmfSentHistory = string.Empty;

        [ObservableProperty]
        private bool _useRotaryDial = true;

        [RelayCommand]
        private void ToggleDialerMode()
        {
            UseRotaryDial = !UseRotaryDial;
        }

        public CallState CurrentCallState => _kernelService.CurrentCallState;
        public string? ActiveCallNumber => _kernelService.CurrentCallNumber;
        public bool HasIncomingCall => _kernelService.HasIncomingCall;
        public string? IncomingCallerNumber => _kernelService.IncomingCallerNumber;

        public CallPhase CurrentPhase
        {
            get
            {
                if (HasIncomingCall || CurrentCallState == CallState.Incoming)
                    return CallPhase.Incoming;
                if (_isShowingEndedSummary)
                    return CallPhase.Ended;
                if (CurrentCallState == CallState.Active)
                    return IsHeld ? CallPhase.Held : CallPhase.Connected;
                if (CurrentCallState == CallState.Ringing)
                    return CallPhase.Ringing;
                if (CurrentCallState == CallState.Dialing)
                    return CallPhase.Dialing;
                if (IsCallOperationPending)
                    return CallPhase.Preparing;
                return CallPhase.Idle;
            }
        }

        public bool IsInCall => CurrentPhase != CallPhase.Idle;
        public bool IsDialerVisible => CurrentPhase == CallPhase.Idle;
        public bool IsCallConnected => CurrentPhase == CallPhase.Connected;
        public bool IsCallTimerVisible => CurrentPhase is CallPhase.Connected or CallPhase.Held;
        public bool IsEndedVisible => CurrentPhase == CallPhase.Ended;
        public bool CanDial => !string.IsNullOrWhiteSpace(PhoneNumber) && !IsCallOperationPending && !IsInCall;

        public string CallStatusTitle => CurrentPhase switch
        {
            CallPhase.Preparing => "正在准备呼叫...",
            CallPhase.Dialing => "正在呼叫...",
            CallPhase.Ringing => "对方正在响铃...",
            CallPhase.Connected => "通话中",
            CallPhase.Held => "通话已保持",
            CallPhase.Incoming => "来电请求",
            CallPhase.Ended => _callEndReasonTitle ?? "通话已结束",
            _ => "准备就绪"
        };

        public string CallStatusSubtitle => CurrentPhase switch
        {
            CallPhase.Preparing => (IsVoWifiRegistered && !ForceCellular) ? "正在建立 VoWiFi 语音通道..." : "正在发起蜂窝移动网络呼叫...",
            CallPhase.Dialing => "正在向核心网建立信令会话...",
            CallPhase.Ringing => "等待受话人接听...",
            CallPhase.Connected => CodecInfo,
            CallPhase.Held => "对方已被置于保持状态，麦克风已暂停发送",
            CallPhase.Incoming => $"SIM 卡槽: {SelectedSlot?.DisplayTitle ?? "默认卡槽"}",
            CallPhase.Ended => _callEndReasonDetail ?? (CallDurationText != "00:00" ? $"通话时长: {CallDurationText}" : "呼叫已释放"),
            _ => VoWifiStateText
        };

        public VoSharp.Telephony.VoWifi.VoWifiState VoWifiState => SelectedSlot?.VoWifi?.State ?? VoSharp.Telephony.VoWifi.VoWifiState.Disconnected;
        public bool IsVoWifiRegistered => VoWifiState == VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered;
        public string VoWifiStateText => VoWifiState switch
        {
            VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered => "VoWiFi 高清语音已就绪",
            VoSharp.Telephony.VoWifi.VoWifiState.ConnectingIkev2 or VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistering or VoSharp.Telephony.VoWifi.VoWifiState.IpsecTunnelEstablished or VoSharp.Telephony.VoWifi.VoWifiState.AuthenticatingEapAka or VoSharp.Telephony.VoWifi.VoWifiState.ResolvingEpdg => "VoWiFi 正在建立通道...",
            _ => "VoWiFi 未连接 (拨号时自动建立)"
        };

        public PhoneViewModel(IVoKernelService kernelService)
        {
            _kernelService = kernelService;
            SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();

            LoadAudioDevices();

            _durationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _durationTimer.Tick += (s, e) =>
            {
                if (_callConnectedTime is { } connTime && CurrentCallState == CallState.Active)
                {
                    var span = DateTime.UtcNow - connTime;
                    CallDurationText = $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2}";
                    NotifyCallStateVisuals();

                    if (Views.Windows.InCallFloatingWindow.IsShowing)
                    {
                        Views.Windows.InCallFloatingWindow.UpdateDurationText(CallDurationText);
                        if (!string.IsNullOrEmpty(ActiveCallNumber))
                        {
                            Views.Windows.InCallFloatingWindow.UpdateNumberText(ActiveCallNumber);
                        }
                    }
                }
            };

            // Listen to state changes
            _kernelService.Kernel.CallStateChanged += (s, e) =>
            {
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (e.NewState == CallState.Active)
                    {
                        _isShowingEndedSummary = false;
                        _callConnectedTime = DateTime.UtcNow;
                        _durationTimer.Start();
                        CodecInfo = !string.IsNullOrEmpty(e.Codec) ? e.Codec : "VoWiFi AMR-WB 16kHz";
                    }
                    else if (e.NewState == CallState.Ringing)
                    {
                        _isShowingEndedSummary = false;
                    }
                    else if (e.NewState == CallState.Dialing)
                    {
                        _isShowingEndedSummary = false;
                    }
                    else if (e.NewState == CallState.Ended || e.NewState == CallState.Idle)
                    {
                        _durationTimer.Stop();
                        _callConnectedTime = null;
                        Views.Windows.InCallFloatingWindow.Dismiss();
                        _isShowingEndedSummary = true;
                        ScheduleStatusReset(TimeSpan.FromSeconds(4.5));
                    }

                    NotifyCallStateVisuals();
                }), DispatcherPriority.DataBind);
            };

            _kernelService.Kernel.IncomingCall += (s, e) =>
            {
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isShowingEndedSummary = false;
                    NotifyCallStateVisuals();
                }), DispatcherPriority.DataBind);
            };

            _kernelService.CallMediaStatusChanged += status =>
            {
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    CodecInfo = status;
                    OnPropertyChanged(nameof(CodecInfo));
                    OnPropertyChanged(nameof(CallStatusSubtitle));
                }), DispatcherPriority.DataBind);
            };

            _kernelService.Kernel.CallEnded += (s, e) =>
            {
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ParseCallEndedReason(e.Reason);
                    _isShowingEndedSummary = true;
                    NotifyCallStateVisuals();
                    ScheduleStatusReset(TimeSpan.FromSeconds(4.5));
                }), DispatcherPriority.DataBind);
            };
        }

        private void LoadAudioDevices()
        {
            AudioDevices.Clear();
            try
            {
                int count = WaveOut.DeviceCount;
                for (int i = 0; i < count; i++)
                {
                    var caps = WaveOut.GetCapabilities(i);
                    if (!string.IsNullOrWhiteSpace(caps.ProductName))
                    {
                        AudioDevices.Add(caps.ProductName);
                    }
                }
            }
            catch
            {
                // Fallback gracefully if WinMM unavailable
            }

            if (AudioDevices.Count == 0)
            {
                AudioDevices.Add("默认系统扬声器 / 耳机");
            }
            SelectedAudioDevice = AudioDevices.FirstOrDefault();
        }

        private void ParseCallEndedReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                _callEndReasonTitle = "通话结束";
                _callEndReasonDetail = CallDurationText != "00:00" ? $"通话已完成 · 时长 {CallDurationText}" : "对方已挂断";
                return;
            }

            if (reason.Contains("486", StringComparison.OrdinalIgnoreCase) || reason.Contains("Busy", StringComparison.OrdinalIgnoreCase))
            {
                _callEndReasonTitle = "对方忙 (486)";
                _callEndReasonDetail = "对方线路正忙或正在通话中，请稍后再拨";
            }
            else if (reason.Contains("603", StringComparison.OrdinalIgnoreCase) || reason.Contains("Decline", StringComparison.OrdinalIgnoreCase))
            {
                _callEndReasonTitle = "呼叫被拒绝 (603)";
                _callEndReasonDetail = "受话方主动拒接了本次呼叫";
            }
            else if (reason.Contains("404", StringComparison.OrdinalIgnoreCase) || reason.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
            {
                _callEndReasonTitle = "号码不存在 (404)";
                _callEndReasonDetail = "所拨打的号码未注册或网络不可达";
            }
            else if (reason.Contains("408", StringComparison.OrdinalIgnoreCase) || reason.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                _callEndReasonTitle = "呼叫超时 (408)";
                _callEndReasonDetail = "网络未在规定时间内收到终端响应";
            }
            else if (reason.Contains("503", StringComparison.OrdinalIgnoreCase))
            {
                _callEndReasonTitle = "服务暂时不可用 (503)";
                _callEndReasonDetail = "运营商核心网拥塞或服务不可用";
            }
            else if (reason.Contains("+CEER: 0,25", StringComparison.OrdinalIgnoreCase))
            {
                _callEndReasonTitle = "路由错误 (CEER 25)";
                _callEndReasonDetail = "运营商网络释放（交换路由错误 25）";
            }
            else if (reason.Contains("+CEER: 0,16", StringComparison.OrdinalIgnoreCase) || reason.Contains("normal", StringComparison.OrdinalIgnoreCase))
            {
                _callEndReasonTitle = "通话结束";
                _callEndReasonDetail = CallDurationText != "00:00" ? $"通话已完成 · 时长 {CallDurationText}" : "正常挂机释放";
            }
            else
            {
                _callEndReasonTitle = "通话结束";
                _callEndReasonDetail = reason;
            }
        }

        private void NotifyCallStateVisuals()
        {
            OnPropertyChanged(nameof(CurrentCallState));
            OnPropertyChanged(nameof(CurrentPhase));
            OnPropertyChanged(nameof(IsInCall));
            OnPropertyChanged(nameof(IsDialerVisible));
            OnPropertyChanged(nameof(IsCallConnected));
            OnPropertyChanged(nameof(IsCallTimerVisible));
            OnPropertyChanged(nameof(IsEndedVisible));
            OnPropertyChanged(nameof(CanDial));
            OnPropertyChanged(nameof(CallStatusTitle));
            OnPropertyChanged(nameof(CallStatusSubtitle));
            OnPropertyChanged(nameof(ActiveCallNumber));
            OnPropertyChanged(nameof(HasIncomingCall));
            OnPropertyChanged(nameof(IncomingCallerNumber));
        }

        private void ScheduleStatusReset(TimeSpan delay)
        {
            _statusResetCts?.Cancel();
            _statusResetCts = new CancellationTokenSource();
            _ = ResetStatusLaterAsync(delay, _statusResetCts.Token);
        }

        private async Task ResetStatusLaterAsync(TimeSpan delay, CancellationToken token)
        {
            try
            {
                await Task.Delay(delay, token);
                if (CurrentCallState is CallState.Ended or CallState.Idle)
                {
                    _isShowingEndedSummary = false;
                    _callEndReasonTitle = null;
                    _callEndReasonDetail = null;
                    CallDurationText = "00:00";
                    DtmfSentHistory = string.Empty;
                    ShowInCallKeypad = false;
                    NotifyCallStateVisuals();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }

        [RelayCommand]
        public void AppendDigit(string digit)
        {
            if (string.IsNullOrEmpty(digit)) return;
            char ch = digit[0];

            PhoneNumber += ch;
            SoundEffectService.Instance.PlayDtmfTone(ch);

            // If in active call, send DTMF tone to network
            if (CurrentCallState == CallState.Active)
            {
                DtmfSentHistory += ch;
                _ = SendDtmfSafeAsync(ch);
            }
        }

        [RelayCommand]
        public void SendDtmf(string digit)
        {
            if (string.IsNullOrEmpty(digit)) return;
            char ch = digit[0];

            DtmfSentHistory += ch;
            SoundEffectService.Instance.PlayDtmfTone(ch);
            _ = SendDtmfSafeAsync(ch);
        }

        private async Task SendDtmfSafeAsync(char digit)
        {
            try
            {
                await _kernelService.SendDtmfAsync(digit, SelectedSlot?.Id);
            }
            catch (Exception ex)
            {
                _callEndReasonDetail = $"DTMF 发送失败: {ex.Message}";
                OnPropertyChanged(nameof(CallStatusSubtitle));
            }
        }

        [RelayCommand]
        private void Backspace()
        {
            if (!string.IsNullOrEmpty(PhoneNumber))
            {
                PhoneNumber = PhoneNumber[..^1];
            }
        }

        [RelayCommand]
        private void ClearNumber()
        {
            PhoneNumber = string.Empty;
        }

        [RelayCommand]
        private async Task DialAsync()
        {
            if (string.IsNullOrWhiteSpace(PhoneNumber) || IsCallOperationPending) return;

            IsCallOperationPending = true;
            _isShowingEndedSummary = false;
            _callEndReasonTitle = null;
            _callEndReasonDetail = null;
            DtmfSentHistory = string.Empty;
            ShowInCallKeypad = false;
            NotifyCallStateVisuals();

            try
            {
                _callConnectedTime = null;
                CallDurationText = "00:00";
                await _kernelService.DialAsync(PhoneNumber.Trim(), SelectedSlot?.Id, ForceCellular);
            }
            catch (Exception ex)
            {
                _callEndReasonTitle = "呼叫发起失败";
                _callEndReasonDetail = ex.Message;
                _isShowingEndedSummary = true;
                ScheduleStatusReset(TimeSpan.FromSeconds(4.5));
            }
            finally
            {
                IsCallOperationPending = false;
                NotifyCallStateVisuals();
            }
        }

        [RelayCommand]
        private async Task ConnectVoWifiAsync()
        {
            if (SelectedSlot == null || IsCallOperationPending) return;
            IsCallOperationPending = true;
            try
            {
                bool ok = await _kernelService.StartVoWifiAsync(SelectedSlot.Id);
                OnPropertyChanged(nameof(VoWifiState));
                OnPropertyChanged(nameof(IsVoWifiRegistered));
                OnPropertyChanged(nameof(VoWifiStateText));
                OnPropertyChanged(nameof(CallStatusSubtitle));
            }
            catch (Exception ex)
            {
                _callEndReasonDetail = $"VoWiFi 启动失败: {ex.Message}";
                OnPropertyChanged(nameof(CallStatusSubtitle));
            }
            finally
            {
                IsCallOperationPending = false;
            }
        }

        partial void OnSelectedSlotChanged(ModemSlot? value)
        {
            OnPropertyChanged(nameof(VoWifiState));
            OnPropertyChanged(nameof(IsVoWifiRegistered));
            OnPropertyChanged(nameof(VoWifiStateText));
            OnPropertyChanged(nameof(CallStatusSubtitle));
        }

        [RelayCommand]
        private async Task HangupAsync()
        {
            if (IsCallOperationPending) return;
            IsCallOperationPending = true;
            try
            {
                await _kernelService.HangupAsync(SelectedSlot?.Id);
            }
            finally
            {
                IsCallOperationPending = false;
                _durationTimer.Stop();
                _callConnectedTime = null;
                Views.Windows.InCallFloatingWindow.Dismiss();
                _isShowingEndedSummary = true;
                _callEndReasonTitle = "通话结束";
                _callEndReasonDetail = CallDurationText != "00:00" ? $"通话已完成 · 时长 {CallDurationText}" : "已主动挂断";
                NotifyCallStateVisuals();
                ScheduleStatusReset(TimeSpan.FromSeconds(4.5));
            }
        }

        [RelayCommand]
        private void ReturnToDialer()
        {
            _statusResetCts?.Cancel();
            _isShowingEndedSummary = false;
            _callEndReasonTitle = null;
            _callEndReasonDetail = null;
            CallDurationText = "00:00";
            DtmfSentHistory = string.Empty;
            ShowInCallKeypad = false;
            NotifyCallStateVisuals();
        }

        [RelayCommand]
        private async Task RedialCurrentAsync()
        {
            _statusResetCts?.Cancel();
            _isShowingEndedSummary = false;
            await DialAsync();
        }

        [RelayCommand]
        private async Task AnswerAsync()
        {
            if (IsCallOperationPending) return;
            IsCallOperationPending = true;
            try
            {
                await _kernelService.AnswerAsync(SelectedSlot?.Id);
                _isShowingEndedSummary = false;
                NotifyCallStateVisuals();
            }
            catch (Exception ex)
            {
                _callEndReasonTitle = "接听失败";
                _callEndReasonDetail = ex.Message;
                _isShowingEndedSummary = true;
                ScheduleStatusReset(TimeSpan.FromSeconds(4.5));
            }
            finally
            {
                IsCallOperationPending = false;
                NotifyCallStateVisuals();
            }
        }

        [RelayCommand]
        private async Task RejectAsync()
        {
            if (IsCallOperationPending) return;
            IsCallOperationPending = true;
            try
            {
                await _kernelService.RejectAsync(SelectedSlot?.Id);
                _isShowingEndedSummary = true;
                _callEndReasonTitle = "已拒接";
                _callEndReasonDetail = "已拒绝该来电";
                NotifyCallStateVisuals();
                ScheduleStatusReset(TimeSpan.FromSeconds(3.5));
            }
            catch (Exception ex)
            {
                _callEndReasonTitle = "拒接异常";
                _callEndReasonDetail = ex.Message;
                _isShowingEndedSummary = true;
                ScheduleStatusReset(TimeSpan.FromSeconds(3.5));
            }
            finally
            {
                IsCallOperationPending = false;
                NotifyCallStateVisuals();
            }
        }

        [RelayCommand]
        private void ToggleMute()
        {
            IsMuted = !IsMuted;
        }

        [RelayCommand]
        private void ToggleHold()
        {
            IsHeld = !IsHeld;
            NotifyCallStateVisuals();
        }

        [RelayCommand]
        private void ToggleKeypad()
        {
            ShowInCallKeypad = !ShowInCallKeypad;
        }

        [RelayCommand]
        private void Redial(CallRecordModel? record)
        {
            if (record == null) return;
            PhoneNumber = record.PhoneNumber;
            _ = DialAsync();
        }

        [RelayCommand]
        private void FillNumber(CallRecordModel? record)
        {
            if (record != null)
            {
                PhoneNumber = record.PhoneNumber;
            }
        }

        [RelayCommand]
        private void CopyNumber(CallRecordModel? record)
        {
            if (record != null && !string.IsNullOrWhiteSpace(record.PhoneNumber))
            {
                try
                {
                    Clipboard.SetText(record.PhoneNumber);
                    AppToast.ShowCopySuccess($"号码 {record.PhoneNumber}");
                }
                catch
                {
                    // Ignore clipboard lock
                }
            }
        }

        [RelayCommand]
        private void ClearHistory()
        {
            var result = MessageBox.Show(
                "确定要清空全部最近通话记录吗？\n此操作将移除本地所有已保存的通话记录，无法撤销。",
                "清空最近通话",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _kernelService.ClearCallHistory();
            }
        }

        [RelayCommand]
        private void DeleteRecord(CallRecordModel? record)
        {
            if (record != null)
            {
                _kernelService.DeleteCallRecord(record.Id);
            }
        }
    }
}
