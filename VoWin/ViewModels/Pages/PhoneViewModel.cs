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
        public IVoKernelService GatewayService => _kernelService;
        private readonly DispatcherTimer _durationTimer;
        private DateTime? _callConnectedTime;
        private CancellationTokenSource? _statusResetCts;
        private string? _callEndReasonTitle;
        private string? _callEndReasonDetail;
        private bool _isShowingEndedSummary;
        private bool _hangupRequested;
        private string? _dialSlotId;
        private VoSharp.Kernel.SipGateway.SipIncomingCallClaimed? _sipPickup;
        [ObservableProperty] private MicrophoneUplinkStatus _microphoneStatus = new();
        [ObservableProperty] private bool _isMicrophoneRestarting;
        public bool ShowMicrophoneStatus => IsCallTimerVisible && MicrophoneStatus.HasSession;
        public bool CanControlMicrophone => IsCallTimerVisible && MicrophoneStatus.HasSession;
        public bool CanRestartMicrophone => IsCallConnected && MicrophoneStatus.CanRestart && !IsMicrophoneRestarting;
        public string MicrophoneRestartText => MicrophoneStatus.IsMessagePlayback ? "切换到麦克风" : "重新打开麦克风";
        partial void OnMicrophoneStatusChanged(MicrophoneUplinkStatus value)
        {
            IsMuted = value.IsMuted;
            OnPropertyChanged(nameof(ShowMicrophoneStatus));
            OnPropertyChanged(nameof(CanControlMicrophone));
            OnPropertyChanged(nameof(CanRestartMicrophone));
            OnPropertyChanged(nameof(MicrophoneRestartText));
        }
        partial void OnIsMicrophoneRestartingChanged(bool value) => OnPropertyChanged(nameof(CanRestartMicrophone));
        internal void RefreshMicrophoneStatus() => MicrophoneStatus = _kernelService.GetMicrophoneUplinkStatus();
        [ObservableProperty] private bool _isHangupOperationPending;
        [ObservableProperty] private string? _callActionError;
        partial void OnCallActionErrorChanged(string? value) => OnPropertyChanged(nameof(CallStatusSubtitle));

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
        private string _codecInfo = "等待通话协商音频";

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
        public bool IsSipRemoteCall => _sipPickup is { IsLocal: false } pickup &&
            string.Equals(pickup.CallId, _kernelService.Kernel.ActiveCall?.CallId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pickup.SlotId, _kernelService.Kernel.ActiveCallSlotId, StringComparison.OrdinalIgnoreCase);
        public bool ShowIncomingControls => CurrentPhase == CallPhase.Incoming && !IsSipRemoteCall;
        public bool ShowHangupControl => !IsEndedVisible && !ShowIncomingControls;
        public bool ShowRegularCallActions => CurrentPhase is CallPhase.Connected or CallPhase.Held;
        public bool CanAcceptIncoming => ShowIncomingControls && !IsCallOperationPending;
        public string? IncomingCallerNumber => _kernelService.IncomingCallerNumber;
        public string CurrentCallLine
        {
            get
            {
                var owner = Slots.FirstOrDefault(slot => slot.Id == _kernelService.Kernel.ActiveCallSlotId);
                if (owner == null) return "当前通话线路：等待确认";
                var mode = owner.HasCellularCall ? "模组语音桥接" : owner.Calls.IsHostImsCall ? "Host IMS"
                    : owner.Calls.ActiveCall != null ? "VoWiFi" : "线路待确认";
                return $"当前通话：{owner.DisplayTitle} · {mode}（不随下方呼出设备切换）";
            }
        }

        public CallPhase CurrentPhase
        {
            get
            {
                if (HasIncomingCall || CurrentCallState == CallState.Incoming)
                    return CallPhase.Incoming;
                if (_isShowingEndedSummary)
                    return CallPhase.Ended;
                if (CurrentCallState is CallState.Active or CallState.Held)
                    return IsHeld || CurrentCallState == CallState.Held ? CallPhase.Held : CallPhase.Connected;
                if (CurrentCallState == CallState.Ringing)
                    return CallPhase.Ringing;
                if (CurrentCallState == CallState.Dialing)
                    return CallPhase.Dialing;
                if (IsCallOperationPending && !_hangupRequested)
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
            CallPhase.Connected => IsSipRemoteCall ? "其他分机通话中" : "通话中",
            CallPhase.Held => "通话已保持",
            CallPhase.Incoming => IsSipRemoteCall ? "其他分机接听中" : "来电请求",
            CallPhase.Ended => _callEndReasonTitle ?? "通话已结束",
            _ => "拨号"
        };

        public string CallStatusSubtitle => CallActionError ?? (CurrentPhase switch
        {
            CallPhase.Preparing => ForceCellular ? "正在建立所选设备的蜂窝呼叫..." :
                _kernelService.GetGatewayStatus(_dialSlotId ?? SelectedSlot?.Id).Route.Value,
            CallPhase.Dialing => "正在向核心网建立信令会话...",
            CallPhase.Ringing => "等待受话人接听...",
            CallPhase.Connected => IsSipRemoteCall ? "SIP 分机已接听 · 本机麦克风未启用" : CodecInfo,
            CallPhase.Held => "对方已被置于保持状态，麦克风已暂停发送",
            CallPhase.Incoming when IsSipRemoteCall => "本机已停止响铃 · 正在等待运营商确认接通",
            CallPhase.Incoming when _kernelService.Kernel.LocalSipGateway?.RoutesIncomingCall(_kernelService.Kernel.ActiveCallSlotId) == true
                => "本机与 SIP 分机同时响铃 · 任选一端接听 · 拒接结束整通",
            CallPhase.Incoming => $"SIM 卡槽: {Slots.FirstOrDefault(slot =>
                string.Equals(slot.Id, _kernelService.Kernel.ActiveCallSlotId, StringComparison.OrdinalIgnoreCase))?.DisplayTitle
                ?? SelectedSlot?.DisplayTitle ?? "默认卡槽"}",
            CallPhase.Ended => _callEndReasonDetail ?? (CallDurationText != "00:00" ? $"通话时长: {CallDurationText}" : "呼叫已释放"),
            _ => GatewayRouteText
        });

        public VoSharp.Telephony.VoWifi.VoWifiState VoWifiState => SelectedSlot?.VoWifi?.State ?? VoSharp.Telephony.VoWifi.VoWifiState.Disconnected;
        public bool IsVoWifiRegistered => VoWifiState == VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered;
        public bool IsHostImsRegistered => SelectedSlot?.GetHostImsRegistrationStatus().IsRegistered == true;
        public string GatewayRouteText => _kernelService.GetGatewayStatus(SelectedSlot?.Id).Route.Value;

        public PhoneViewModel(IVoKernelService kernelService)
        {
            _kernelService = kernelService;
            SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();

            LoadAudioDevices();

            _durationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _durationTimer.Tick += (s, e) =>
            {
                if (_callConnectedTime is { } connTime && CurrentCallState is CallState.Active or CallState.Held)
                {
                    var span = DateTime.UtcNow - connTime;
                    CallDurationText = $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2}";
                    RefreshMicrophoneStatus();
                    NotifyCallStateVisuals();

                }
            };

            // Listen to state changes
            _kernelService.Kernel.SipIncomingCallClaimed += (_, pickup) =>
            {
                void Apply()
                {
                    PresentSipIncomingPickup(pickup);
                }
                if (App.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.BeginInvoke((Action)Apply);
                else Apply();
            };
            _kernelService.Kernel.CallStateChanged += (s, e) =>
            {
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (e.NewState is CallState.Ended or CallState.Idle) return;
                    if (!_kernelService.IsPresentedCallEvent(e.CallId)) return;
                    if (e.NewState == CallState.Active)
                    {
                        _isShowingEndedSummary = false;
                        IsHeld = false;
                        _callConnectedTime = _kernelService.Kernel.ActiveCall?.ConnectedTime ?? DateTime.UtcNow;
                        _durationTimer.Start();
                        CodecInfo = !string.IsNullOrEmpty(e.Codec) ? e.Codec : "等待实际音频格式";
                    }
                    else if (e.NewState == CallState.Ringing)
                    {
                        _isShowingEndedSummary = false;
                    }
                    else if (e.NewState == CallState.Dialing)
                    {
                        _isShowingEndedSummary = false;
                    }
                    else if (e.NewState == CallState.Held)
                    {
                        IsHeld = true;
                    }
                    NotifyCallStateVisuals();
                }), DispatcherPriority.DataBind);
            };

            _kernelService.Kernel.IncomingCall += (s, e) =>
            {
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_kernelService.IsPresentedCallEvent(e.CallId)) return;
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
                    if (!_kernelService.IsPresentedCallEvent(e.CallId))
                    {
                        var promoted = _kernelService.Kernel.ActiveCall;
                        if (promoted == null || !_kernelService.IsPresentedCallEvent(promoted.CallId)) return;
                        ShowPromotedCall(promoted);
                        return;
                    }
                    _durationTimer.Stop();
                    _callConnectedTime = null;
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
            OnPropertyChanged(nameof(IsSipRemoteCall));
            OnPropertyChanged(nameof(ShowIncomingControls));
            OnPropertyChanged(nameof(ShowHangupControl));
            OnPropertyChanged(nameof(ShowRegularCallActions));
            OnPropertyChanged(nameof(CanAcceptIncoming));
            OnPropertyChanged(nameof(IncomingCallerNumber));
            OnPropertyChanged(nameof(CurrentCallLine));
            OnPropertyChanged(nameof(ShowMicrophoneStatus));
            OnPropertyChanged(nameof(CanControlMicrophone));
            OnPropertyChanged(nameof(CanRestartMicrophone));
        }

        internal void PresentSipIncomingPickup(VoSharp.Kernel.SipGateway.SipIncomingCallClaimed pickup)
        {
            if (!_kernelService.IsPresentedCallEvent(pickup.CallId) ||
                !string.Equals(_kernelService.Kernel.ActiveCallSlotId, pickup.SlotId, StringComparison.OrdinalIgnoreCase)) return;
            _sipPickup = pickup;
            NotifyCallStateVisuals();
        }

        private void ScheduleStatusReset(TimeSpan delay)
        {
            _statusResetCts?.Cancel();
            _statusResetCts = new CancellationTokenSource();
            _ = ResetStatusLaterAsync(delay, _statusResetCts.Token);
        }

        private void ShowPromotedCall(CallSession call)
        {
            _statusResetCts?.Cancel();
            _isShowingEndedSummary = false;
            _callEndReasonTitle = null;
            _callEndReasonDetail = null;
            CallActionError = null;
            DtmfSentHistory = string.Empty;
            ShowInCallKeypad = false;
            IsHeld = call.State == CallState.Held;
            _callConnectedTime = call.State is CallState.Active or CallState.Held ? call.ConnectedTime : null;
            var duration = _callConnectedTime.HasValue ? DateTime.UtcNow - _callConnectedTime.Value : TimeSpan.Zero;
            CallDurationText = $"{(int)duration.TotalMinutes:D2}:{duration.Seconds:D2}";
            CodecInfo = !string.IsNullOrWhiteSpace(call.Codec) ? call.Codec : "等待通话协商音频";
            if (_callConnectedTime.HasValue) _durationTimer.Start();
            else _durationTimer.Stop();
            NotifyCallStateVisuals();
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
                await _kernelService.SendDtmfAsync(digit, _kernelService.Kernel.ActiveCallSlotId ?? SelectedSlot?.Id);
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
            _hangupRequested = false;
            CallActionError = null;
            _dialSlotId = SelectedSlot?.Id;
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
                await _kernelService.DialAsync(PhoneNumber.Trim(), _dialSlotId, ForceCellular);
            }
            catch (OperationCanceledException) when (_hangupRequested) { }
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
                _dialSlotId = null;
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
                OnPropertyChanged(nameof(GatewayRouteText));
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
            OnPropertyChanged(nameof(IsHostImsRegistered));
            OnPropertyChanged(nameof(GatewayRouteText));
            OnPropertyChanged(nameof(CallStatusSubtitle));
        }

        [RelayCommand]
        private async Task HangupAsync()
        {
            // INVITE can wait minutes for a final response. Hangup must remain
            // available during that wait, on the original call's slot.
            if (IsHangupOperationPending) return;
            IsHangupOperationPending = true;
            _hangupRequested = true;
            CallActionError = null;
            var originalCall = _kernelService.Kernel.ActiveCall;
            try
            {
                await _kernelService.HangupAsync(_kernelService.Kernel.ActiveCallSlotId ?? _dialSlotId ?? SelectedSlot?.Id);
                // Ending this call can promote another module's call before
                // the command returns. Its UI/media belong to that new call,
                // not to this completed command's delayed continuation.
                if (_kernelService.Kernel.ActiveCall is { } next && !ReferenceEquals(next, originalCall))
                {
                    ShowPromotedCall(next);
                    return;
                }
                _durationTimer.Stop();
                _callConnectedTime = null;
                Views.Windows.InCallFloatingWindow.Dismiss();
                _isShowingEndedSummary = true;
                _callEndReasonTitle = "通话结束";
                _callEndReasonDetail = CallDurationText != "00:00" ? $"通话已完成 · 时长 {CallDurationText}" : "已主动挂断";
                NotifyCallStateVisuals();
                ScheduleStatusReset(TimeSpan.FromSeconds(4.5));
            }
            catch (Exception ex)
            {
                _hangupRequested = false;
                CallActionError = $"挂断失败：{ex.Message}";
                NotifyCallStateVisuals();
            }
            finally { IsHangupOperationPending = false; }
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
            if (IsCallOperationPending || IsSipRemoteCall) return;
            IsCallOperationPending = true;
            CallActionError = null;
            try
            {
                await _kernelService.AnswerAsync(_kernelService.Kernel.ActiveCallSlotId ?? SelectedSlot?.Id);
                _isShowingEndedSummary = false;
                NotifyCallStateVisuals();
            }
            catch (Exception ex)
            {
                CallActionError = $"接听失败：{ex.Message}";
                NotifyCallStateVisuals();
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
            CallActionError = null;
            var originalCall = _kernelService.Kernel.ActiveCall;
            try
            {
                await _kernelService.RejectAsync(_kernelService.Kernel.ActiveCallSlotId ?? SelectedSlot?.Id);
                if (_kernelService.Kernel.ActiveCall is { } next && !ReferenceEquals(next, originalCall))
                {
                    ShowPromotedCall(next);
                    return;
                }
                _isShowingEndedSummary = true;
                _callEndReasonTitle = "已拒接";
                _callEndReasonDetail = "已拒绝该来电";
                NotifyCallStateVisuals();
                ScheduleStatusReset(TimeSpan.FromSeconds(3.5));
            }
            catch (Exception ex)
            {
                CallActionError = $"拒接失败：{ex.Message}";
                NotifyCallStateVisuals();
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
            if (_kernelService.SetMicrophoneMuted(!IsMuted)) RefreshMicrophoneStatus();
        }

        [RelayCommand]
        private async Task RestartMicrophoneAsync()
        {
            if (!CanRestartMicrophone) return;
            IsMicrophoneRestarting = true;
            try
            {
                await _kernelService.RestartMicrophoneAsync();
                RefreshMicrophoneStatus();
            }
            catch (Exception ex) { CallActionError = $"麦克风重试失败：{ex.GetType().Name}"; }
            finally { IsMicrophoneRestarting = false; }
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
