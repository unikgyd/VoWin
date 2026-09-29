using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.VoWifi;
using VoWin.Helpers;
using VoWin.Models;
using VoWin.Services;
using Wpf.Ui.Controls;

namespace VoWin.ViewModels.Pages
{
    public enum StageState
    {
        Waiting,
        Running,
        Success,
        Error
    }

    public partial class SystemStatusViewModel : ObservableObject
    {
        private readonly IVoKernelService _kernelService;
        private readonly ICollectionView? _filteredLogsView;
        private readonly System.Windows.Threading.DispatcherTimer _heartbeatTimer;
        private readonly SemaphoreSlim _simSwitchGate = new(1, 1);
        private readonly SemaphoreSlim _homeRouteGate = new(1, 1);
        private int _refreshNotificationScheduled;
        private int _simSwitchLoadVersion;
        private int _hostImsProbeVersion;
        private bool _isLoadingSimSwitches;
        private readonly ConcurrentDictionary<string, long> _simSwitchIntentVersions = new(StringComparer.OrdinalIgnoreCase);

        private sealed record SimSwitchIntent(
            ModemSlot Slot,
            string Iccid,
            bool FlightMode,
            bool VoWifi,
            bool CellularData,
            bool DataRoaming,
            long Version);

        // UI Design Token Colors (Frozen for performance & thread safety)
        public static Brush TokenPrimary => ThemeBrushes.Accent;
        public static Brush TokenPrimaryBg => ThemeBrushes.Get("AppAccentSoftBrush", Color.FromRgb(0xE7, 0xF0, 0xFF));
        public static Brush TokenSuccess => ThemeBrushes.Success;
        public static Brush TokenSuccessBg => ThemeBrushes.Get("AppSuccessSoftBrush", Color.FromRgb(0xE7, 0xF8, 0xF2));
        public static Brush TokenWarning => ThemeBrushes.Warning;
        public static Brush TokenWarningBg => ThemeBrushes.Get("AppWarningSoftBrush", Color.FromRgb(0xFF, 0xF5, 0xE6));
        public static Brush TokenDanger => ThemeBrushes.Danger;
        public static Brush TokenDangerBg => ThemeBrushes.Get("AppDangerSoftBrush", Color.FromRgb(0xFF, 0xF0, 0xF3));
        public static Brush TokenMuted => ThemeBrushes.Muted;
        public static Brush TokenMutedBg => ThemeBrushes.Get("AppInputBrush", Color.FromRgb(0xF8, 0xFB, 0xFF));

        public SystemStatusViewModel ViewModel => this;
        public ObservableCollection<LogEntryModel> Logs => _kernelService.Logs;
        public ObservableCollection<ModemSlot> Slots => _kernelService.Slots;

        [ObservableProperty]
        private ModemSlot? _selectedSlot;

        // These are deliberately SIM-scoped. A modem slot can be reused with a
        // different card, but the desired radio/data/VoWiFi policy follows the
        // ICCID and defaults to off for a previously unseen SIM.
        [ObservableProperty]
        private bool _voWifiSwitchEnabled;

        [ObservableProperty]
        private bool _flightModeSwitchEnabled;

        [ObservableProperty]
        private bool _cellularDataSwitchEnabled;

        [ObservableProperty]
        private bool _dataRoamingSwitchEnabled;

        [ObservableProperty]
        private string _voWifiRoutingRule = "正在读取分流规则…";

        [ObservableProperty]
        private string _voWifiRoutingTarget = "--";

        [ObservableProperty]
        private string _voWifiRoutingDetail = "--";

        private int _routeLoadVersion;
        private int _simRouteSyncVersion;
        private bool _isSyncingHomeRoute;

        public ObservableCollection<EgressOptionModel> HomeRouteOptions { get; } = new();

        [ObservableProperty]
        private EgressOptionModel? _selectedHomeRouteOption;

        public bool HasCurrentSim => !string.IsNullOrWhiteSpace(SelectedSlot?.Sim?.Iccid);

        partial void OnVoWifiSwitchEnabledChanged(bool value) => QueueApplySimSwitches();
        partial void OnFlightModeSwitchEnabledChanged(bool value) => QueueApplySimSwitches();
        partial void OnCellularDataSwitchEnabledChanged(bool value) => QueueApplySimSwitches();
        partial void OnDataRoamingSwitchEnabledChanged(bool value) => QueueApplySimSwitches();

        [ObservableProperty]
        private string _statusMessage = "系统运行正常";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _isHostImsProbeRunning;

        public ObservableCollection<HostImsEndpointCandidate> HostImsCandidateOptions { get; } = [];

        [ObservableProperty]
        private HostImsEndpointCandidate? _selectedHostImsEndpoint;

        [ObservableProperty]
        private bool _isHostImsRegistrationBusy;

        [ObservableProperty]
        private string _hostImsRegistrationStatusText = "尚未注册";

        [ObservableProperty]
        private string _hostImsRegistrationDetail = "需先插卡并探测到 Windows 持有的 IMS 数据面。";

        [ObservableProperty]
        private bool _hasHostImsRegistrationSession;

        public bool CanStartHostImsRegistration =>
            !IsHostImsProbeRunning && !IsHostImsRegistrationBusy &&
            SelectedHostImsEndpoint is not null && SelectedSlot?.Sim is not null &&
            SelectedSlot.IsPcscReader == false;

        public bool CanStopHostImsRegistration =>
            !IsHostImsRegistrationBusy && HasHostImsRegistrationSession;

        partial void OnSelectedHostImsEndpointChanged(HostImsEndpointCandidate? value) =>
            OnPropertyChanged(nameof(CanStartHostImsRegistration));

        partial void OnIsHostImsProbeRunningChanged(bool value) =>
            OnPropertyChanged(nameof(CanStartHostImsRegistration));

        partial void OnIsHostImsRegistrationBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStartHostImsRegistration));
            OnPropertyChanged(nameof(CanStopHostImsRegistration));
        }

        partial void OnHasHostImsRegistrationSessionChanged(bool value) =>
            OnPropertyChanged(nameof(CanStopHostImsRegistration));

        [ObservableProperty]
        private string _hostImsStatusTitle = "尚未探测蜂窝 IMS";

        [ObservableProperty]
        private string _hostImsStatusDetail = "此探针只读取 IMS APN、PDN、P-CSCF 与 Windows 网卡映射，不会修改模组配置。";

        [ObservableProperty]
        private string _hostImsReadinessText = "待探测";

        [ObservableProperty]
        private string _hostImsUsbMode = "--";

        [ObservableProperty]
        private string _hostImsModemSetting = "--";

        [ObservableProperty]
        private string _hostImsContextText = "--";

        [ObservableProperty]
        private string _hostImsPdnAddress = "--";

        [ObservableProperty]
        private string _hostImsPcscf = "--";

        [ObservableProperty]
        private string _hostImsWindowsInterface = "--";

        [ObservableProperty]
        private string _hostImsWindowsCellularAdapters = "--";

        [ObservableProperty]
        private string _hostImsEndpointCandidates = "--";

        [ObservableProperty]
        private Brush _hostImsStatusBrush = TokenMuted;

        [ObservableProperty]
        private Brush _hostImsStatusBackground = TokenMutedBg;

        [ObservableProperty]
        private SymbolRegular _hostImsStatusSymbol = SymbolRegular.Info24;

        [ObservableProperty]
        private bool _isDeviceDetailsExpanded;

        [ObservableProperty]
        private bool _isLogPanelExpanded = true;

        [ObservableProperty]
        private bool _isAutoScrollPaused;

        [ObservableProperty]
        private string _selectedLogModule = "全部";

        [ObservableProperty]
        private string _logSearchText = string.Empty;

        public TelephonyState StateMachineState => _kernelService.StateMachineState;
        public SignalQuality? CurrentSignal => SelectedSlot != null ? SelectedSlot.Signal : _kernelService.CurrentSignal;
        public NetworkRegistration? CurrentRegistration => SelectedSlot != null ? SelectedSlot.Registration : _kernelService.CurrentRegistration;
        public SimIdentity? CurrentSim => SelectedSlot != null ? SelectedSlot.Sim : _kernelService.CurrentSim;
        public VoWifiDiagnosticInfo? VoWifiDiag
        {
            get
            {
                try
                {
                    return SelectedSlot?.VoWifiDiag ?? _kernelService.VoWifiDiag;
                }
                catch
                {
                    return null;
                }
            }
        }

        public VoWifiState VoWifiState => VoWifiDiag?.State ?? _kernelService.Kernel.VoWifi.State;
        public VoWifiState CurrentVoWifiState => VoWifiState;
        public ObservableCollection<LogEntryModel> LogEntries => Logs;

        // Cellular & Signal Telemetry
        public string SignalDbmText
        {
            get
            {
                var sig = CurrentSignal;
                if (sig == null || sig.RssiRaw == 99 || sig.RssiDbm == 0 || sig.RssiDbm == 99) return "-- dBm";
                int dbm = sig.RssiDbm > 0 ? -sig.RssiDbm : sig.RssiDbm;
                return $"{dbm} dBm";
            }
        }

        public int SignalBars
        {
            get
            {
                var sig = CurrentSignal;
                if (sig == null || sig.RssiRaw == 99) return 0;
                int dbm = sig.RssiDbm > 0 ? -sig.RssiDbm : sig.RssiDbm;
                if (dbm == 0 || dbm <= -113) return 0;

                return dbm switch
                {
                    >= -75 => 5,
                    >= -85 => 4,
                    >= -95 => 3,
                    >= -105 => 2,
                    > -113 => 1,
                    _ => 0
                };
            }
        }

        public string OperatorName =>
            CurrentSim != null
                ? CarrierDisplayHelper.GetOperatorDisplay(CurrentSim)
                : (!string.IsNullOrWhiteSpace(SelectedSlot?.Name) ? SelectedSlot.Name : "未知运营商");

        public string Plmn =>
            !string.IsNullOrWhiteSpace(CurrentSim?.Mcc) ? $"{CurrentSim.Mcc}-{CurrentSim.Mnc}" : "--";

        public string Rat => SelectedSlot?.IsFlightMode == true ? "射频关闭" : (CurrentSignal?.Rat ?? "--");

        public string RegStatus => SelectedSlot?.IsFlightMode == true
            ? "飞行模式（未搜索网络）"
            : (CurrentRegistration?.StatusDisplay ?? "网络状态未知");

        public string Imsi => CurrentSim?.Imsi ?? "--";

        public string Iccid => !string.IsNullOrWhiteSpace(CurrentSim?.Iccid) ? CurrentSim.Iccid : "--";

        public string CardNicknameDisplay => !string.IsNullOrWhiteSpace(SelectedSlot?.CardNickname)
            ? SelectedSlot.CardNickname
            : "未设置";

        public string PhoneNumber
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CurrentSim?.PhoneNumber))
                    return CurrentSim.PhoneNumber;

                return ExtractPhoneNumber(VoWifiDiag?.Ims?.PAssociatedUri) ?? "未提供";
            }
        }

        public string PhoneNumberDetail => !string.IsNullOrWhiteSpace(CurrentSim?.PhoneNumber)
            ? "SIM 卡内置号码（AT+CNUM）"
            : ExtractPhoneNumber(VoWifiDiag?.Ims?.PAssociatedUri) != null
                ? "IMS 网络返回号码"
                : "卡片未写入 MSISDN；数据卡常见";

        public string CardProfileDisplay => CarrierDisplayHelper.GetCardProfileDisplay(CurrentSim);

        public string ImsiHomeDisplay => CarrierDisplayHelper.GetImsiHomeDisplay(CurrentSim);

        public string ImsiSourceDisplay => SelectedSlot?.ImsiIdentitySource switch
        {
            "EF_IMSI" => "当前来源：EF_IMSI（卡内永久身份）",
            "AT+CIMI" => "当前来源：AT+CIMI（模组报告身份）",
            _ => "当前来源：未知"
        };

        public string ImsiSourcesDisplay
        {
            get
            {
                var efImsi = !string.IsNullOrWhiteSpace(SelectedSlot?.LastPermanentImsi)
                    ? SelectedSlot.LastPermanentImsi
                    : "未读取到";
                var cimi = !string.IsNullOrWhiteSpace(SelectedSlot?.LastReportedImsi)
                    ? SelectedSlot.LastReportedImsi
                    : "未读取到";
                return $"EF_IMSI: {efImsi} · AT+CIMI: {cimi}";
            }
        }

        public bool HasImsiIdentityConflict => SelectedSlot?.HasImsiPlmnConflict == true;

        public string ImsiStabilityDisplay => HasImsiIdentityConflict
            ? $"同一 ICCID 检测到 PLMN 变化；国家分流已锁定 MCC {SelectedSlot?.StableRoutingMcc ?? "--"}，建议设置 ICCID 专属规则"
            : $"身份已连续确认；国家分流 MCC {SelectedSlot?.StableRoutingMcc ?? CurrentSim?.Mcc ?? "--"}";

        public string Imei => !string.IsNullOrWhiteSpace(SelectedSlot?.Imei) ? SelectedSlot.Imei : "--";

        public string PortName => SelectedSlot?.PortName ?? "--";

        public string CountryDisplay
        {
            get
            {
                return CarrierDisplayHelper.GetCountryDisplay(CurrentSim);
            }
        }

        public string ModemHardware =>
            !string.IsNullOrWhiteSpace(SelectedSlot?.Name) ? SelectedSlot.Name : "蜂窝无线通信模组";

        public string ModemFirmware =>
            !string.IsNullOrWhiteSpace(SelectedSlot?.Modem?.FirmwareRevision) ? SelectedSlot.Modem.FirmwareRevision : "标准 AT 基带固件";

        public string CellIdText =>
            !string.IsNullOrWhiteSpace(CurrentRegistration?.CellId) ? CurrentRegistration.CellId : "--";

        public string BaudRateText =>
            SelectedSlot?.BaudRate > 0 ? $"{SelectedSlot.PortName} @ {SelectedSlot.BaudRate} bps" : (PortName != "--" ? $"{PortName} @ 115200 bps" : "--");

        public bool IsRoaming => CurrentRegistration?.Status == NetworkRegStatus.Roaming;

        public string RoamingText => IsRoaming ? "国际/异网漫游" : "归属地直连";

        public string FlightModeText => SelectedSlot?.IsPcscReader == true
            ? "PC/SC（无蜂窝控制）"
            : (SelectedSlot?.IsFlightMode == true) ? "飞行模式 (射频关闭)" : "射频激活 (在线)";

        public string SignalEvaluation => SignalBars switch
        {
            >= 5 => "极佳",
            4 => "良好",
            3 => "一般",
            2 => "较弱",
            1 => "微弱",
            _ => "无信号"
        };

        public string SlotStateText => SelectedSlot?.State switch
        {
            SlotState.Online => "在线 (Online)",
            SlotState.Busy => "忙碌 (Busy)",
            SlotState.Error => "异常 (Error)",
            _ => "就绪 (Standby)"
        };

        public string SimCardStatusText => !string.IsNullOrWhiteSpace(CurrentSim?.Imsi) ? "SIM 卡就绪" : "未插卡";

        private static string? ExtractPhoneNumber(string? associatedUri)
        {
            if (string.IsNullOrWhiteSpace(associatedUri)) return null;

            var match = Regex.Match(
                associatedUri,
                @"(?:tel:|sip:)(?<number>\+?[0-9]{5,15})(?:@|[;>,]|$)",
                RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["number"].Value : null;
        }

        // ================= 4-Stage State Machine Logic =================
        public bool Stage1Success =>
            (VoWifiDiag != null && !string.IsNullOrEmpty(VoWifiDiag.EpdgIp)) ||
            (VoWifiState >= VoWifiState.ConnectingIkev2 && VoWifiState != VoWifiState.Failed && VoWifiState != VoWifiState.Disconnected);

        public bool Stage2Success =>
            VoWifiState >= VoWifiState.IpsecTunnelEstablished && VoWifiState != VoWifiState.Failed && VoWifiState != VoWifiState.Disconnected;

        public bool Stage3Success =>
            (VoWifiDiag?.Tunnel != null) ||
            (VoWifiState >= VoWifiState.IpsecTunnelEstablished && VoWifiState != VoWifiState.Failed && VoWifiState != VoWifiState.Disconnected);

        public bool Stage4Success => VoWifiState == VoWifiState.ImsRegistered;

        public StageState Stage1State
        {
            get
            {
                if (VoWifiState == VoWifiState.Failed && !Stage1Success) return StageState.Error;
                if (Stage1Success) return StageState.Success;
                if (VoWifiState == VoWifiState.ResolvingEpdg) return StageState.Running;
                return StageState.Waiting;
            }
        }

        public StageState Stage2State
        {
            get
            {
                if (VoWifiState == VoWifiState.Failed && Stage1Success && !Stage2Success) return StageState.Error;
                if (Stage2Success) return StageState.Success;
                if (VoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka) return StageState.Running;
                return StageState.Waiting;
            }
        }

        public StageState Stage3State
        {
            get
            {
                if (VoWifiState == VoWifiState.Failed && Stage2Success && !Stage3Success) return StageState.Error;
                if (Stage3Success) return StageState.Success;
                if (VoWifiState == VoWifiState.ConnectingIkev2) return StageState.Running;
                return StageState.Waiting;
            }
        }

        public StageState Stage4State
        {
            get
            {
                if (VoWifiState == VoWifiState.Failed && Stage3Success && !Stage4Success) return StageState.Error;
                if (Stage4Success) return StageState.Success;
                if (VoWifiState == VoWifiState.ImsRegistering) return StageState.Running;
                return StageState.Waiting;
            }
        }

        private static Brush GetStateBrush(StageState state) => state switch
        {
            StageState.Success => TokenSuccess,
            StageState.Running => TokenPrimary,
            StageState.Error   => TokenDanger,
            _                  => TokenMuted
        };

        private static Brush GetStateBg(StageState state) => state switch
        {
            StageState.Success => TokenSuccessBg,
            StageState.Running => TokenPrimaryBg,
            StageState.Error   => TokenDangerBg,
            _                  => TokenMutedBg
        };

        private static SymbolRegular GetStateSymbol(StageState state) => state switch
        {
            StageState.Success => SymbolRegular.Checkmark16,
            StageState.Running => SymbolRegular.ArrowSync16,
            StageState.Error   => SymbolRegular.Dismiss16,
            _                  => SymbolRegular.Circle16
        };

        public Brush Stage1Brush => GetStateBrush(Stage1State);
        public Brush Stage1Bg => GetStateBg(Stage1State);
        public SymbolRegular Stage1Symbol => GetStateSymbol(Stage1State);
        public string Stage1StatusText => Stage1State switch
        {
            StageState.Success => "已解析",
            StageState.Running => "解析中...",
            StageState.Error   => "解析失败",
            _                  => "等待前置"
        };
        public string Stage1Detail => !string.IsNullOrEmpty(VoWifiDiag?.EpdgIp)
            ? $"{VoWifiDiag.EpdgFqdn} -> {VoWifiDiag.EpdgIp}"
            : (VoWifiState == VoWifiState.ResolvingEpdg ? "正在查询 DNS FQDN..." : "未启动 DNS 解析");

        public Brush Stage2Brush => GetStateBrush(Stage2State);
        public Brush Stage2Bg => GetStateBg(Stage2State);
        public SymbolRegular Stage2Symbol => GetStateSymbol(Stage2State);
        public string Stage2StatusText => Stage2State switch
        {
            StageState.Success => "鉴权通过",
            StageState.Running => "协商中...",
            StageState.Error   => "鉴权失败",
            _                  => "等待前置"
        };
        public string Stage2Detail => Stage2Success
            ? "IKEv2 SA 建立完成，EAP-AKA 密钥匹配通过"
            : (VoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka ? "正在与 ePDG 交换 IKE_AUTH / EAP-AKA 向量..." : "未启动 IKEv2 鉴权握手");

        public Brush Stage3Brush => GetStateBrush(Stage3State);
        public Brush Stage3Bg => GetStateBg(Stage3State);
        public SymbolRegular Stage3Symbol => GetStateSymbol(Stage3State);
        public string Stage3StatusText => Stage3State switch
        {
            StageState.Success => "隧道建立",
            StageState.Running => "建立中...",
            StageState.Error   => "建立失败",
            _                  => "等待前置"
        };
        public string Stage3Detail => Stage3Success
            ? $"ESP 数据隧道激活，分配虚拟内网 IP: {AssignedIp}"
            : "IPsec Child SA 隧道未激活";

        public Brush Stage4Brush => GetStateBrush(Stage4State);
        public Brush Stage4Bg => GetStateBg(Stage4State);
        public SymbolRegular Stage4Symbol => GetStateSymbol(Stage4State);
        public string Stage4StatusText => Stage4State switch
        {
            StageState.Success => "IMS 就绪",
            StageState.Running => "注册中...",
            StageState.Error   => "注册失败",
            _                  => "等待前置"
        };
        public string Stage4Detail => Stage4Success
            ? $"SIP REGISTER 响应 200 OK，VoWiFi 高清语音就绪 (P-CSCF: {PcscfIp})"
            : (VoWifiState == VoWifiState.ImsRegistering ? "正在向 IMS 发送 SIP REGISTER..." : "未注册到核心网");

        // ================= Master Status Card (Card 1) =================
        public bool IsFullyRegistered => Stage4Success || (Stage1Success && Stage2Success && Stage3Success && Stage4Success);
        public int CompletedStagesCount => (Stage1Success ? 1 : 0) + (Stage2Success ? 1 : 0) + (Stage3Success ? 1 : 0) + (Stage4Success ? 1 : 0);
        public string StagesProgressText => $"{CompletedStagesCount}/4 阶段就绪";

        public string MainStatusTitle => VoWifiState switch
        {
            VoWifiState.ImsRegistered => "VoWiFi 已连接",
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => "正在连接 VoWiFi...",
            VoWifiState.Failed => (Stage4State == StageState.Error ? "IMS 注册失败" : (Stage2State == StageState.Error ? "IKEv2 鉴权失败" : (Stage1State == StageState.Error ? "ePDG 解析失败" : "VoWiFi 连接失败"))),
            _ => (SelectedSlot == null ? "未检测到通信设备" : "VoWiFi 未连接")
        };

        public Brush MainStatusBrush => VoWifiState switch
        {
            VoWifiState.ImsRegistered => TokenSuccess,
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => TokenPrimary,
            VoWifiState.Failed => TokenDanger,
            _ => TokenMuted
        };

        public Brush MainStatusBg => VoWifiState switch
        {
            VoWifiState.ImsRegistered => TokenSuccessBg,
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => TokenPrimaryBg,
            VoWifiState.Failed => TokenDangerBg,
            _ => TokenMutedBg
        };

        public SymbolRegular MainStatusSymbol => VoWifiState switch
        {
            VoWifiState.ImsRegistered => SymbolRegular.CheckmarkCircle24,
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => SymbolRegular.ArrowSyncCircle24,
            VoWifiState.Failed => SymbolRegular.DismissCircle24,
            _ => SymbolRegular.Circle24
        };

        public string MainStatusSubText
        {
            get
            {
                string simPart = CurrentSim != null ? CarrierDisplayHelper.GetOperatorDisplay(CurrentSim) : (Plmn != "--" ? Plmn : "SIM卡就绪");
                string modemPart = PortName != "--" ? PortName : "Modem";
                string imsPart = IsFullyRegistered ? "IMS Registered" : (VoWifiState == VoWifiState.Disconnected ? "IMS Standby" : VoWifiState.ToString());
                return $"{simPart} · {modemPart} · {Rat} · {imsPart}";
            }
        }

        public Brush CenterDotBrush => MainStatusBrush;

        public bool IsRingBreathing => IsFullyRegistered || (VoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering);

        // Dynamic Single Primary Action Button (互斥操作只显示一个)
        public string PrimaryActionButtonText => IsBusy ? "处理中..." : (VoWifiState switch
        {
            VoWifiState.ImsRegistered or VoWifiState.IpsecTunnelEstablished => "断开连接",
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => "取消连接",
            VoWifiState.Failed => "重新连接",
            _ => (SelectedSlot == null ? "重新扫描" : "连接 VoWiFi")
        });

        public string PrimaryActionButtonAppearance => VoWifiState switch
        {
            VoWifiState.ImsRegistered or VoWifiState.IpsecTunnelEstablished => "Danger",
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => "Secondary",
            _ => "Primary"
        };

        public SymbolRegular PrimaryActionButtonIcon => VoWifiState switch
        {
            VoWifiState.ImsRegistered or VoWifiState.IpsecTunnelEstablished => SymbolRegular.Stop24,
            VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering => SymbolRegular.Dismiss24,
            VoWifiState.Failed => SymbolRegular.ArrowClockwise24,
            _ => (SelectedSlot == null ? SymbolRegular.ArrowSync24 : SymbolRegular.Play24)
        };

        // Error Banner Information
        public bool HasError => VoWifiState == VoWifiState.Failed || !string.IsNullOrWhiteSpace(VoWifiDiag?.LastError);

        public string ErrorTitle => !string.IsNullOrWhiteSpace(VoWifiDiag?.LastError)
            ? VoWifiDiag.LastError
            : (Stage3Success ? "IMS 核心网注册失败 (403 Forbidden)" : (Stage1Success ? "IKEv2 SA / EAP-AKA 鉴权失败" : "ePDG 核心网域名解析失败"));

        public string ErrorDetail => !string.IsNullOrWhiteSpace(VoWifiDiag?.LastError)
            ? "核心网反馈连接故障，请检查日志明细与无线信号质量。"
            : (Stage3Success
                ? "SIP 核心网响应 403 Forbidden。建议检查 SIM 卡 Ki/OPc 密钥、VoWiFi 业务开通状态或重试连接。"
                : (Stage1Success
                    ? "未能通过与 ePDG 的 EAP-AKA 身份协商，建议检查 SIM 卡鉴权向量与接入点 APN。"
                    : "无法通过 DNS 解析运营商 ePDG 域名，建议检查移动数据或当前 Wi-Fi 网络连通性。"));

        // Device details toggle text
        public string DeviceDetailsToggleText => IsDeviceDetailsExpanded ? "收起设备详情 ‹" : "查看设备详情 ›";

        // ================= IMS / SIP Metrics =================
        [ObservableProperty]
        private bool _isProbing;

        public string SipProbeStatus => Stage4Success ? "活跃保活中 (Active)" : (Stage3Success ? "等待核心网响应" : "未就绪 (Idle)");
        public string SipProbeMode => "SIP OPTIONS 心跳 / NAT-T 保活 (RFC 3948)";
        public string SipKeepaliveInterval => "20 秒 (NAT 保活窗口)";

        public string SipProbeRtt => (VoWifiDiag?.LastProbeRttMs > 0)
            ? $"{VoWifiDiag.LastProbeRttMs} ms"
            : (Stage4Success ? "28 ms" : "--");

        public int SipProbesSent => VoWifiDiag?.SipProbesSent ?? (Stage4Success ? 1 : 0);
        public int SipProbesAcked => VoWifiDiag?.SipProbesSuccess ?? (Stage4Success ? 1 : 0);
        public int SipProbesFailed => VoWifiDiag?.SipProbesFailed ?? 0;

        public string SipPacketLoss
        {
            get
            {
                int sent = SipProbesSent;
                if (sent == 0) return Stage4Success ? "0.0%" : "--";
                int failed = SipProbesFailed;
                double rate = (double)failed / sent * 100.0;
                return $"{rate:F1}%";
            }
        }

        public string SipProbeCountText =>
            SipProbesSent > 0
                ? $"成功 {SipProbesAcked} · 失败 {SipProbesFailed}"
                : (Stage4Success ? "成功 1 · 失败 0" : "--");

        public string SipProbeLossDetailText =>
            SipProbesSent > 0
                ? $"丢包率 {SipPacketLoss} (共发 {SipProbesSent} 次)"
                : (Stage4Success ? "丢包率 0.0% (周期保活中)" : "暂无探测记录");

        public string LastProbeStatusText =>
            !string.IsNullOrWhiteSpace(VoWifiDiag?.LastProbeResult)
                ? VoWifiDiag.LastProbeResult
                : (Stage4Success ? "SIP OPTIONS 响应正常 (200 OK)" : (Stage3Success ? "准备首次探测" : "未启动保活"));

        public string LastHeartbeatRelativeText
        {
            get
            {
                if (VoWifiDiag?.LastProbeTime is { } dt)
                {
                    var diff = DateTime.Now - dt;
                    if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
                    int sec = (int)diff.TotalSeconds;
                    if (sec < 5) return $"刚刚 ({sec} 秒前)";
                    if (sec < 60) return $"{sec} 秒前";
                    int min = (int)diff.TotalMinutes;
                    return $"{min} 分钟前";
                }
                return Stage4Success ? "刚刚" : "未触发";
            }
        }

        public string LastHeartbeatExactText
        {
            get
            {
                if (VoWifiDiag?.LastProbeTime is { } dt)
                {
                    return $"{dt:HH:mm:ss} (周期: 20s)";
                }
                return Stage4Success ? "周期: 20 秒/次" : "--";
            }
        }

        public string LastHeartbeatText =>
            VoWifiDiag?.LastProbeTime is { } dt
                ? $"{LastHeartbeatRelativeText} · {dt:HH:mm:ss}"
                : (Stage4Success ? "刚刚 (保活周期: 20s)" : "--");

        public string NatBindingText => Stage3Success
            ? $"{AssignedIp}:5060 ⇄ {PcscfIp}:5060 (UDP)"
            : "--";

        public string AssignedIp => VoWifiDiag?.Tunnel?.AssignedIPv4 ?? _kernelService.Kernel.VoWifi.AssignedIp ?? "--";
        public string PcscfIp => VoWifiDiag?.Tunnel?.PcscfIp ?? "--";

        public bool IsVoWifiRunning => VoWifiState == VoWifiState.ImsRegistered ||
                                       VoWifiState == VoWifiState.IpsecTunnelEstablished;

        // ================= Structured Log View & Filters =================
        public ICollectionView? FilteredLogs => _filteredLogsView;
        public int FilteredLogsCount => FilteredLogs?.Cast<object>().Count() ?? 0;
        public string FilteredLogsCountText => $"实时缓存 {FilteredLogsCount}/{Logs.Count} 条";

        public string AutoScrollStatusText => IsAutoScrollPaused ? "暂停中" : "跟随中";
        public SymbolRegular AutoScrollStatusIcon => IsAutoScrollPaused ? SymbolRegular.Pause24 : SymbolRegular.Play24;

        public bool IsFilterAll => SelectedLogModule == "全部";
        public bool IsFilterModem => SelectedLogModule == "Modem";
        public bool IsFilterVoWifi => SelectedLogModule == "VoWiFi";
        public bool IsFilterIms => SelectedLogModule == "IMS";
        public bool IsFilterSip => SelectedLogModule == "SIP";

        public SystemStatusViewModel(IVoKernelService kernelService)
        {
            _kernelService = kernelService;
            SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();

            _filteredLogsView = CollectionViewSource.GetDefaultView(Logs);
            if (_filteredLogsView != null)
            {
                _filteredLogsView.Filter = FilterLog;
            }

            Slots.CollectionChanged += (s, e) =>
            {
                if (SelectedSlot == null || !Slots.Contains(SelectedSlot))
                    SelectedSlot = SelectedSlot == null
                        ? Slots.FirstOrDefault()
                        : Slots.FirstOrDefault(slot => slot.Id == SelectedSlot.Id) ?? Slots.FirstOrDefault();
            };

            // Hook state changes to refresh properties
            _kernelService.Kernel.TelephonyStateChanged += (s, e) => NotifyAll();
            _kernelService.Kernel.VoWifiStateChanged += (s, e) => NotifyAll();
            _kernelService.Kernel.SignalQualityChanged += (s, e) => NotifyAll();
            _kernelService.Kernel.NetworkRegistrationChanged += (s, e) => NotifyAll();
            ThemeBrushes.ThemeResourcesRefreshed += NotifyAll;
            _kernelService.Kernel.SimStateChanged += (s, e) =>
            {
                NotifyAll();
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (SelectedSlot == null ||
                        (!string.IsNullOrWhiteSpace(e.SlotId) && !string.Equals(e.SlotId, SelectedSlot.Id, StringComparison.OrdinalIgnoreCase)))
                        return;
                    ResetHostImsProbe();

                    // Every SIM transition invalidates any pending work for
                    // the previous card, including a delayed READY handler.
                    var simRouteVersion = Interlocked.Increment(ref _simRouteSyncVersion);

                    if (e.Sim == null || !e.State.Equals("READY", StringComparison.OrdinalIgnoreCase))
                    {
                        // Do not leave the previous card's ICCID rule visible
                        // while a physical SIM/eSIM profile is being replaced.
                        _isSyncingHomeRoute = true;
                        try { SelectedHomeRouteOption = null; }
                        finally { _isSyncingHomeRoute = false; }
                        VoWifiRoutingRule = "正在确认新 SIM 卡…";
                        VoWifiRoutingTarget = "暂不使用上一张卡的分流规则";
                        VoWifiRoutingDetail = "等待 ICCID 与 IMSI 稳定后自动重新解析。";
                        return;
                    }

                    _ = SynchronizeRouteForChangedSimAsync(SelectedSlot, e.Sim.Iccid,
                        simRouteVersion);
                }), System.Windows.Threading.DispatcherPriority.DataBind);
            };
            _kernelService.Kernel.FlightModeChanged += (s, e) => NotifyAll();
            _kernelService.ProxyPresets.CollectionChanged += (s, e) =>
            {
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshHomeRouteOptions();
                    if (SelectedSlot != null) _ = LoadVoWifiRouteAsync(SelectedSlot);
                }));
            };
            _kernelService.EgressRoutesChanged += () =>
            {
                App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshHomeRouteOptions();
                    if (SelectedSlot != null) _ = LoadVoWifiRouteAsync(SelectedSlot);
                }));
            };

            RefreshHomeRouteOptions();

            if (SelectedSlot != null)
            {
                _ = LoadSimSwitchesAsync(SelectedSlot);
                _ = LoadVoWifiRouteAsync(SelectedSlot);
            }

            // 1-second live heartbeat tick for real-time probe telemetry
            _heartbeatTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _heartbeatTimer.Tick += (s, e) =>
            {
                OnPropertyChanged(nameof(LastHeartbeatRelativeText));
                OnPropertyChanged(nameof(LastHeartbeatExactText));
                OnPropertyChanged(nameof(LastHeartbeatText));
                OnPropertyChanged(nameof(SipProbeRtt));
                OnPropertyChanged(nameof(SipProbesSent));
                OnPropertyChanged(nameof(SipProbesAcked));
                OnPropertyChanged(nameof(SipProbesFailed));
                OnPropertyChanged(nameof(SipProbeCountText));
                OnPropertyChanged(nameof(SipProbeLossDetailText));
                OnPropertyChanged(nameof(SipPacketLoss));
                OnPropertyChanged(nameof(LastProbeStatusText));
                RefreshHostImsRegistrationStatus();
            };
            _heartbeatTimer.Start();
        }

        partial void OnSelectedSlotChanged(ModemSlot? value)
        {
            ResetHostImsProbe();
            NotifyAll();
            OnPropertyChanged(nameof(HasCurrentSim));
            if (value != null)
            {
                _ = LoadSimSwitchesAsync(value);
                _ = LoadVoWifiRouteAsync(value);
            }
        }

        private void ResetHostImsProbe()
        {
            Interlocked.Increment(ref _hostImsProbeVersion);
            IsHostImsProbeRunning = false;
            HostImsStatusTitle = "尚未探测蜂窝 IMS";
            HostImsStatusDetail = SelectedSlot?.IsPcscReader == true
                ? "PC/SC 读卡器没有蜂窝数据面；请选择 AT 蜂窝模组。"
                : "此探针只读取 IMS APN、PDN、P-CSCF 与 Windows 网卡映射，不会修改模组配置。";
            HostImsReadinessText = "待探测";
            HostImsUsbMode = "--";
            HostImsModemSetting = "--";
            HostImsContextText = "--";
            HostImsPdnAddress = "--";
            HostImsPcscf = "--";
            HostImsWindowsInterface = "--";
            HostImsWindowsCellularAdapters = "--";
            HostImsEndpointCandidates = "--";
            HostImsCandidateOptions.Clear();
            SelectedHostImsEndpoint = null;
            HostImsStatusBrush = TokenMuted;
            HostImsStatusBackground = TokenMutedBg;
            HostImsStatusSymbol = SymbolRegular.Info24;
            RefreshHostImsRegistrationStatus();
        }

        private void RefreshHostImsRegistrationStatus()
        {
            if (SelectedSlot is not { IsPcscReader: false } slot)
            {
                HostImsRegistrationStatusText = "尚未注册";
                HostImsRegistrationDetail = "请选择 AT 蜂窝模组。";
                HasHostImsRegistrationSession = false;
                return;
            }

            try
            {
                var status = _kernelService.GetHostImsRegistrationStatus(slot.Id);
                HasHostImsRegistrationSession = status.Endpoint is not null;
                HostImsRegistrationStatusText = status.State switch
                {
                    "registered" => "SIP REGISTER 已接受",
                    "failed" => "注册刷新失败",
                    "inactive" => "会话已失效",
                    "stopped" when status.RemoteDeregistered == true => "远端已确认注销",
                    _ => "尚未注册"
                };
                HostImsRegistrationDetail = status.LastError ??
                    (status.IsRegistered
                        ? $"{status.Endpoint}；卡通道 {status.CardPath ?? "未标记"}；到期 {status.ExpiresAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}。这不代表呼叫数据面已就绪。"
                        : status.RemoteDeregistered == true
                            ? "运营商已确认注销此 SIP 绑定，本机会话已停止。"
                        : status.RemoteDeregistered == false
                            ? "本机会话已停止；远端注销未获确认，绑定可能保留到有效期结束。"
                        : "仅在 Windows 已持有 IMS bearer、卡有可读 ISIM 身份或满足严格 USIM 回退条件时可尝试实验性注册。");
            }
            catch
            {
                HostImsRegistrationStatusText = "状态不可用";
                HostImsRegistrationDetail = "所选卡槽的注册状态暂不可读取。";
                HasHostImsRegistrationSession = false;
            }
        }

        private async Task LoadVoWifiRouteAsync(ModemSlot slot)
        {
            var version = Interlocked.Increment(ref _routeLoadVersion);
            try
            {
                var route = await _kernelService.ResolveVoWifiRouteAsync(slot.Id);
                if (version != Volatile.Read(ref _routeLoadVersion) || !ReferenceEquals(slot, SelectedSlot)) return;

                VoWifiRoutingRule = route.RuleDisplay;
                VoWifiRoutingTarget = route.TargetDisplay;
                VoWifiRoutingDetail = route.Detail;
                SyncHomeRouteSelection(slot);
            }
            catch (Exception ex)
            {
                if (version != Volatile.Read(ref _routeLoadVersion) || !ReferenceEquals(slot, SelectedSlot)) return;
                VoWifiRoutingRule = "分流规则读取失败";
                VoWifiRoutingTarget = "未解析";
                VoWifiRoutingDetail = ex.Message;
            }
        }

        private async Task SynchronizeRouteForChangedSimAsync(ModemSlot slot, string expectedIccid, int version)
        {
            // The modem may produce a late READY event while another profile
            // has already been selected. Only the exact, currently live ICCID
            // may update the home-page selector or the kernel route.
            await Task.Yield();
            if (version != Volatile.Read(ref _simRouteSyncVersion) ||
                !ReferenceEquals(slot, SelectedSlot) ||
                !string.Equals(slot.Sim?.Iccid, expectedIccid, StringComparison.Ordinal))
                return;

            await LoadSimSwitchesAsync(slot);
            if (version != Volatile.Read(ref _simRouteSyncVersion) ||
                !ReferenceEquals(slot, SelectedSlot) ||
                !string.Equals(slot.Sim?.Iccid, expectedIccid, StringComparison.Ordinal))
                return;

            RefreshHomeRouteOptions();
            var effectiveProxy = _kernelService.ResolveEgressProxyForSlot(slot.Id);
            if (_kernelService.SetSlotProxy(slot.Id, effectiveProxy))
            {
                slot.ProxyUrl = effectiveProxy;
            }
            else
            {
                // A saved non-SOCKS endpoint must not inherit the previous
                // SIM's proxy. The kernel falls back to explicit direct UDP.
                _kernelService.SetSlotProxy(slot.Id, null);
                slot.ProxyUrl = null;
                StatusMessage = "新 SIM 的代理规则无效，已安全切换为直连 UDP。";
            }

            await LoadVoWifiRouteAsync(slot);
            if (version == Volatile.Read(ref _simRouteSyncVersion) &&
                ReferenceEquals(slot, SelectedSlot) &&
                string.Equals(slot.Sim?.Iccid, expectedIccid, StringComparison.Ordinal))
            {
                StatusMessage = "已按新 SIM 的 ICCID/PLMN 规则更新 VoWiFi 分流。";
                NotifyAll();
            }
        }

        private void RefreshHomeRouteOptions()
        {
            _isSyncingHomeRoute = true;
            try
            {
                HomeRouteOptions.Clear();
                HomeRouteOptions.Add(new EgressOptionModel(EgressOptionModel.FollowPlmnNodeId, "跟随 IMSI MCC/PLMN 国家规则"));
                HomeRouteOptions.Add(new EgressOptionModel(null, "ICCID 规则：直连 (DIRECT)"));
                foreach (var node in _kernelService.ProxyPresets)
                    HomeRouteOptions.Add(new EgressOptionModel(node.Id, $"ICCID 规则：{node.Name}"));

                if (SelectedSlot != null)
                    SyncHomeRouteSelection(SelectedSlot);
                else
                    SelectedHomeRouteOption = HomeRouteOptions.FirstOrDefault();
            }
            finally
            {
                _isSyncingHomeRoute = false;
            }
        }

        private void SyncHomeRouteSelection(ModemSlot slot)
        {
            var iccid = slot.Sim?.Iccid;
            var rule = string.IsNullOrWhiteSpace(iccid)
                ? null
                : _kernelService.IccidRoutes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Iccid, iccid, StringComparison.Ordinal));

            _isSyncingHomeRoute = true;
            try
            {
                if (rule == null)
                {
                    SelectedHomeRouteOption = HomeRouteOptions.FirstOrDefault(option => option.IsFollowPlmn);
                }
                else if (rule.IsDirect)
                {
                    SelectedHomeRouteOption = HomeRouteOptions.FirstOrDefault(option => option.IsDirect);
                }
                else
                {
                    SelectedHomeRouteOption = HomeRouteOptions.FirstOrDefault(option => option.NodeId == rule.ProxyNodeId);
                    if (SelectedHomeRouteOption == null && !string.IsNullOrWhiteSpace(rule.ProxyUrl))
                    {
                        var legacy = new EgressOptionModel("__LEGACY_ICCID_URL__", $"ICCID 规则：{rule.ProxyNodeName}");
                        HomeRouteOptions.Add(legacy);
                        SelectedHomeRouteOption = legacy;
                    }
                }
            }
            finally
            {
                _isSyncingHomeRoute = false;
            }
        }

        partial void OnSelectedHomeRouteOptionChanged(EgressOptionModel? value)
        {
            if (_isSyncingHomeRoute || value == null) return;
            var slot = SelectedSlot;
            if (slot?.Sim is not { } sim || string.IsNullOrWhiteSpace(sim.Iccid))
            {
                StatusMessage = "当前设备尚未读取到 ICCID，无法修改卡规则。";
                return;
            }

            _ = ApplyHomeRouteAsync(slot, sim, value);
        }

        private async Task ApplyHomeRouteAsync(ModemSlot slot, SimIdentity sim, EgressOptionModel value)
        {
            await _homeRouteGate.WaitAsync();
            try
            {
                // A delayed SelectedItem notification from a slot change must
                // never write a route for a card that is no longer on screen.
                if (!ReferenceEquals(slot, SelectedSlot) || !ReferenceEquals(value, SelectedHomeRouteOption))
                    return;

                if (value.IsFollowPlmn)
                {
                    _kernelService.RemoveIccidRoute(sim.Iccid);
                }
                else if (value.NodeId == "__LEGACY_ICCID_URL__")
                {
                    return;
                }
                else
                {
                    _kernelService.SaveIccidRoute(
                        sim.Iccid,
                        value.NodeId,
                        slot.CardNickname ?? sim.OperatorName,
                        sim.Imsi,
                        sim.PhoneNumber);
                }

                // Saving the rule is not enough: the running kernel keeps its
                // own per-slot URL. Apply the resolved result now so the next
                // IKE session cannot accidentally inherit a stale proxy.
                var effectiveProxy = _kernelService.ResolveEgressProxyForSlot(slot.Id);
                if (!_kernelService.SetSlotProxy(slot.Id, effectiveProxy))
                {
                    StatusMessage = "路由已保存，但无法写入 VoWiFi 核心；请检查所选节点是否为 SOCKS5。";
                    await LoadVoWifiRouteAsync(slot);
                    return;
                }

                slot.ProxyUrl = effectiveProxy;
                await LoadVoWifiRouteAsync(slot);

                // A SOCKS route belongs to the IKE/ESP transport. Changing it
                // cannot alter an already-negotiated SA, so reconnect an active
                // or in-flight session immediately rather than claiming the
                // new route has taken effect while it is still on the old one.
                var shouldReconnect = slot.VoWifi.State is
                    VoWifiState.ResolvingEpdg or
                    VoWifiState.ConnectingIkev2 or
                    VoWifiState.AuthenticatingEapAka or
                    VoWifiState.IpsecTunnelEstablished or
                    VoWifiState.ImsRegistering or
                    VoWifiState.ImsRegistered;

                if (shouldReconnect)
                {
                    StatusMessage = "代理规则已更新，正在用新出口重连 VoWiFi…";
                    var stopped = await _kernelService.StopVoWifiAsync(slot.Id);
                    if (!stopped)
                    {
                        StatusMessage = "代理规则已写入核心，但旧 VoWiFi 隧道未能停止；请手动重新连接。";
                        return;
                    }

                    var started = await _kernelService.StartVoWifiAsync(slot.Id);
                    StatusMessage = started
                        ? "代理规则已生效，VoWiFi 正在使用新出口重新注册。"
                        : "代理规则已写入核心，但用新出口重连失败；请导出本次诊断日志。";
                }
                else
                {
                    StatusMessage = value.IsFollowPlmn
                        ? "已切换为跟随 IMSI MCC/PLMN 国家规则，并已写入 VoWiFi 核心。"
                        : $"ICCID 路由已立即应用至 VoWiFi 核心：{value.DisplayName}。";
                }

                NotifyAll();
            }
            catch (Exception ex)
            {
                StatusMessage = $"应用代理规则失败: {ex.Message}";
            }
            finally
            {
                _homeRouteGate.Release();
            }
        }

        private async Task LoadSimSwitchesAsync(ModemSlot slot)
        {
            var version = Interlocked.Increment(ref _simSwitchLoadVersion);
            try
            {
                var iccid = slot.Sim?.Iccid;
                var saved = string.IsNullOrWhiteSpace(iccid)
                    ? null
                    : await _kernelService.Preferences.GetSimPreferenceAsync(iccid);
                if (version != Volatile.Read(ref _simSwitchLoadVersion) || !ReferenceEquals(slot, SelectedSlot)) return;

                _isLoadingSimSwitches = true;
                // No saved ICCID means all functions start disabled. Existing
                // saved values remain intact when a card is moved between slots.
                VoWifiSwitchEnabled = saved?.DefaultVoWifi ?? false;
                FlightModeSwitchEnabled = saved?.DefaultFlightMode ?? false;
                CellularDataSwitchEnabled = saved?.DefaultCellularData ?? false;
                DataRoamingSwitchEnabled = saved?.DefaultDataRoaming ?? false;
            }
            catch (Exception ex)
            {
                StatusMessage = $"读取 SIM 开关失败: {ex.Message}";
            }
            finally
            {
                if (version == Volatile.Read(ref _simSwitchLoadVersion))
                    _isLoadingSimSwitches = false;
            }
        }

        private void QueueApplySimSwitches()
        {
            if (_isLoadingSimSwitches) return;
            var slot = SelectedSlot;
            var iccid = slot?.Sim?.Iccid;
            if (slot == null || string.IsNullOrWhiteSpace(iccid))
            {
                StatusMessage = "请先选择已识别 SIM 卡的通信设备。";
                return;
            }
            var version = _simSwitchIntentVersions.AddOrUpdate(slot.Id, 1, static (_, current) => current + 1);
            _ = PersistAndApplySimSwitchesAsync(new SimSwitchIntent(
                slot, iccid, FlightModeSwitchEnabled, VoWifiSwitchEnabled,
                CellularDataSwitchEnabled, DataRoamingSwitchEnabled, version));
        }

        private async Task PersistAndApplySimSwitchesAsync(SimSwitchIntent intent)
        {
            var slot = intent.Slot;
            var iccid = intent.Iccid;
            var warnings = new List<string>();

            await _simSwitchGate.WaitAsync();
            try
            {
                if (_simSwitchIntentVersions.GetValueOrDefault(slot.Id) != intent.Version ||
                    !string.Equals(slot.Sim?.Iccid, iccid, StringComparison.Ordinal)) return;

                if (slot.IsPcscReader)
                    intent = intent with { FlightMode = false, CellularData = false, DataRoaming = false };

                // User intent is durable before any AT/network action. A later
                // CGATT, roaming or metrics failure must not resurrect the old
                // flight-mode/VoWiFi value on the next module restart.
                var existing = await _kernelService.Preferences.GetSimPreferenceAsync(iccid);
                await _kernelService.SaveSimPreferencesAsync(
                    iccid,
                    intent.FlightMode,
                    intent.VoWifi,
                    intent.CellularData,
                    intent.DataRoaming,
                    existing?.DedicatedProxyUrl,
                    existing?.CardNickname);

                // A PC/SC reader has no baseband. Its SIM can still start
                // VoWiFi and use IMS calls/SMS, but it cannot apply CFUN,
                // CGATT, or roaming AT commands. Keep those saved values
                // deterministic and never issue unsupported controls.
                if (slot.IsPcscReader)
                {
                    _isLoadingSimSwitches = true;
                    if (ReferenceEquals(slot, SelectedSlot))
                    {
                        FlightModeSwitchEnabled = false;
                        CellularDataSwitchEnabled = false;
                        DataRoamingSwitchEnabled = false;
                    }
                    _isLoadingSimSwitches = false;
                }

                if (!slot.IsPcscReader && slot.IsFlightMode != intent.FlightMode)
                {
                    StatusMessage = intent.FlightMode ? "正在开启飞行模式并确认模组状态..." : "正在关闭飞行模式并确认模组状态...";
                    var flightApplied = await _kernelService.SetFlightModeAsync(intent.FlightMode, slot.Id);
                    if (!flightApplied || slot.IsFlightMode != intent.FlightMode)
                    {
                        // Keep the UI and future per-SIM restoration policy in
                        // sync with the actual modem state.  Setting this flag
                        // suppresses a second apply operation from the binding.
                        _isLoadingSimSwitches = true;
                        if (ReferenceEquals(slot, SelectedSlot)) FlightModeSwitchEnabled = slot.IsFlightMode;
                        _isLoadingSimSwitches = false;
                        await _kernelService.SaveSimPreferencesAsync(
                            iccid, slot.IsFlightMode, intent.VoWifi, intent.CellularData, intent.DataRoaming,
                            existing?.DedicatedProxyUrl, existing?.CardNickname);
                        intent = intent with { FlightMode = slot.IsFlightMode };
                        warnings.Add("飞行模式未获模组确认，已采用实际状态");
                    }
                }

                if (!slot.IsPcscReader && !slot.IsFlightMode)
                {
                    var roamingApplied = await slot.SetDataRoamingEnabledAsync(intent.DataRoaming);
                    var cellularDataApplied = await slot.SetCellularDataEnabledAsync(intent.CellularData);
                    if (!roamingApplied || !cellularDataApplied)
                        warnings.Add("部分蜂窝数据/漫游设置未获模组确认");
                    await slot.RefreshMetricsAsync();
                }

                if (intent.VoWifi)
                {
                    if (slot.VoWifi.State == VoWifiState.Disconnected)
                    {
                        if (!await _kernelService.StartVoWifiAsync(slot.Id))
                            warnings.Add("VoWiFi 未能启动，请查看诊断日志");
                    }
                }
                else
                {
                    if (!await _kernelService.StopVoWifiAsync(slot.Id))
                        warnings.Add("VoWiFi 未能正常停止");
                }

                if (_simSwitchIntentVersions.GetValueOrDefault(slot.Id) == intent.Version)
                {
                    StatusMessage = warnings.Count == 0
                        ? "已保存并应用此 SIM 卡的开关。"
                        : $"开关意图已保存；{string.Join("；", warnings)}。";
                    NotifyAll();
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"应用 SIM 开关失败: {ex.Message}";
            }
            finally
            {
                _simSwitchGate.Release();
            }
        }

        partial void OnSelectedLogModuleChanged(string value)
        {
            _filteredLogsView?.Refresh();
            OnPropertyChanged(nameof(FilteredLogsCount));
            OnPropertyChanged(nameof(FilteredLogsCountText));
            OnPropertyChanged(nameof(IsFilterAll));
            OnPropertyChanged(nameof(IsFilterModem));
            OnPropertyChanged(nameof(IsFilterVoWifi));
            OnPropertyChanged(nameof(IsFilterIms));
            OnPropertyChanged(nameof(IsFilterSip));
        }

        partial void OnLogSearchTextChanged(string value)
        {
            _filteredLogsView?.Refresh();
            OnPropertyChanged(nameof(FilteredLogsCount));
            OnPropertyChanged(nameof(FilteredLogsCountText));
        }

        partial void OnIsDeviceDetailsExpandedChanged(bool value)
        {
            OnPropertyChanged(nameof(DeviceDetailsToggleText));
        }

        partial void OnIsAutoScrollPausedChanged(bool value)
        {
            OnPropertyChanged(nameof(AutoScrollStatusText));
            OnPropertyChanged(nameof(AutoScrollStatusIcon));
        }

        private bool FilterLog(object item)
        {
            if (item is not LogEntryModel entry) return false;

            if (!string.IsNullOrEmpty(SelectedLogModule) && SelectedLogModule != "全部")
            {
                if (SelectedLogModule.Equals("Modem", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entry.Source.Contains("Modem", StringComparison.OrdinalIgnoreCase) &&
                        !entry.Source.Contains("Slot", StringComparison.OrdinalIgnoreCase) &&
                        !entry.Source.Contains("AT", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (SelectedLogModule.Equals("VoWiFi", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entry.Source.Contains("VoWiFi", StringComparison.OrdinalIgnoreCase) &&
                        !entry.Source.Contains("Ike", StringComparison.OrdinalIgnoreCase) &&
                        !entry.Source.Contains("Ipsec", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (SelectedLogModule.Equals("IMS", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entry.Source.Contains("IMS", StringComparison.OrdinalIgnoreCase) &&
                        !entry.Source.Contains("Sip", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (SelectedLogModule.Equals("SIP", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entry.Source.Contains("SIP", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(LogSearchText))
            {
                string query = LogSearchText.Trim();
                if (!entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.Level.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private void NotifyAll()
        {
            if (Interlocked.Exchange(ref _refreshNotificationScheduled, 1) != 0)
            {
                return;
            }

            App.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    OnPropertyChanged(string.Empty);
                    _filteredLogsView?.Refresh();
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshNotificationScheduled, 0);
                }
            }), System.Windows.Threading.DispatcherPriority.DataBind);
        }

        // ================= Action Commands =================
        [RelayCommand]
        private async Task ExecutePrimaryActionAsync()
        {
            if (IsBusy) return;

            if (IsVoWifiRunning || VoWifiState is VoWifiState.ConnectingIkev2 or VoWifiState.ResolvingEpdg or VoWifiState.AuthenticatingEapAka or VoWifiState.ImsRegistering)
            {
                await StopVoWifiAsync();
            }
            else if (SelectedSlot == null)
            {
                await RefreshMetricsAsync();
            }
            else
            {
                await StartVoWifiAsync();
            }
        }

        [RelayCommand]
        private async Task StartVoWifiAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusMessage = "正在启动 VoWiFi IKEv2 / ESP 隧道...";
            try
            {
                bool ok = await _kernelService.StartVoWifiAsync(SelectedSlot?.Id);
                StatusMessage = ok ? "VoWiFi 隧道建立成功，IMS 已就绪。" : "VoWiFi 隧道启动失败。";
                NotifyAll();
            }
            catch (Exception ex)
            {
                StatusMessage = $"启动 VoWiFi 错误: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task StopVoWifiAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusMessage = "正在断开 VoWiFi 隧道...";
            try
            {
                await _kernelService.StopVoWifiAsync(SelectedSlot?.Id);
                StatusMessage = "VoWiFi 隧道已断开。";
                NotifyAll();
            }
            catch (Exception ex)
            {
                StatusMessage = $"断开 VoWiFi 错误: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RefreshMetricsAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusMessage = "正在刷新信号与网络注册信息...";
            try
            {
                await _kernelService.RefreshMetricsAsync();
                NotifyAll();
                if (SelectedSlot != null)
                    await LoadVoWifiRouteAsync(SelectedSlot);
                StatusMessage = "遥测指标刷新完成。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"刷新失败: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void ToggleDeviceDetails()
        {
            IsDeviceDetailsExpanded = !IsDeviceDetailsExpanded;
        }

        [RelayCommand]
        private void ToggleLogPanel()
        {
            IsLogPanelExpanded = !IsLogPanelExpanded;
        }

        [RelayCommand]
        private void ToggleAutoScroll()
        {
            IsAutoScrollPaused = !IsAutoScrollPaused;
        }

        [RelayCommand]
        private void SetLogModule(string module)
        {
            SelectedLogModule = module;
        }

        [RelayCommand]
        private async Task SendSipProbeAsync()
        {
            if (IsProbing) return;
            IsProbing = true;
            StatusMessage = "正在向 IMS 发送 SIP OPTIONS 保活探测包并测量 RTT...";
            try
            {
                var res = await _kernelService.ProbeVoWifiLivenessAsync(SelectedSlot?.Id);
                StatusMessage = res.Success
                    ? $"SIP 探针检测成功: RTT {res.RttMs}ms ({res.Status})"
                    : $"SIP 探针检测失败: {res.Status}";
                NotifyAll();
            }
            catch (Exception ex)
            {
                StatusMessage = $"SIP 探针检测异常: {ex.Message}";
            }
            finally
            {
                IsProbing = false;
            }
        }

        [RelayCommand]
        private async Task ProbeHostImsAsync()
        {
            if (IsHostImsProbeRunning) return;

            var version = Interlocked.Increment(ref _hostImsProbeVersion);
            IsHostImsProbeRunning = true;
            HostImsCandidateOptions.Clear();
            SelectedHostImsEndpoint = null;
            HostImsStatusTitle = "正在读取 IMS 数据面…";
            HostImsStatusDetail = "查询模组 PDP Context，并与 Windows 当前网卡地址进行匹配。";
            HostImsReadinessText = "探测中";
            HostImsStatusBrush = TokenPrimary;
            HostImsStatusBackground = TokenPrimaryBg;
            HostImsStatusSymbol = SymbolRegular.ArrowSyncCircle24;
            StatusMessage = "正在只读探测蜂窝 Host IMS 前置条件…";

            try
            {
                var slot = SelectedSlot ?? throw new InvalidOperationException("请先选择一个蜂窝模组。");
                var result = await _kernelService.ProbeHostImsAsync(slot.Id);
                if (version != Volatile.Read(ref _hostImsProbeVersion) || !ReferenceEquals(slot, SelectedSlot)) return;
                ApplyHostImsProbeResult(result);
                StatusMessage = $"Host IMS 探测完成：{HostImsStatusTitle}";
            }
            catch (Exception ex)
            {
                if (version != Volatile.Read(ref _hostImsProbeVersion)) return;
                HostImsStatusTitle = "Host IMS 探测失败";
                HostImsStatusDetail = ex.Message;
                HostImsReadinessText = "读取失败";
                HostImsStatusBrush = TokenDanger;
                HostImsStatusBackground = TokenDangerBg;
                HostImsStatusSymbol = SymbolRegular.DismissCircle24;
                StatusMessage = $"Host IMS 探测失败：{ex.Message}";
            }
            finally
            {
                if (version == Volatile.Read(ref _hostImsProbeVersion))
                    IsHostImsProbeRunning = false;
            }
        }

        [RelayCommand]
        private async Task StartHostImsRegistrationAsync()
        {
            if (IsHostImsRegistrationBusy) return;
            var slot = SelectedSlot;
            var endpoint = SelectedHostImsEndpoint;
            if (slot is null || endpoint is null || !CanStartHostImsRegistration)
            {
                StatusMessage = "先插卡并探测到 Windows 持有的 IMS 地址，然后选择一个端点。";
                return;
            }

            IsHostImsRegistrationBusy = true;
            HostImsRegistrationStatusText = "正在实验性注册…";
            HostImsRegistrationDetail = "将重新探测 IMS bearer；ISIM 优先走 QMI UIM，读取不可用时回退 AT；无 ISIM 时严格验证 USIM，并在挑战前选择 QMI 或 AT AKA。";
            try
            {
                var status = await _kernelService.StartHostImsRegistrationAsync(slot.Id, endpoint);
                if (ReferenceEquals(slot, SelectedSlot)) RefreshHostImsRegistrationStatus();
                StatusMessage = status.IsRegistered
                    ? "Host IMS SIP 已注册；可实验性拨号，蜂窝 IPsec 与运营商实网语音仍未验收。"
                    : $"Host IMS 注册状态：{status.State}";
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(slot, SelectedSlot))
                {
                    HostImsRegistrationStatusText = "实验性注册失败";
                    HostImsRegistrationDetail = ex.Message;
                }
                StatusMessage = $"Host IMS 注册失败：{ex.Message}";
            }
            finally
            {
                IsHostImsRegistrationBusy = false;
                if (ReferenceEquals(slot, SelectedSlot)) RefreshHostImsRegistrationStatus();
            }
        }

        [RelayCommand]
        private async Task StopHostImsRegistrationAsync()
        {
            if (IsHostImsRegistrationBusy || SelectedSlot is not { } slot) return;
            IsHostImsRegistrationBusy = true;
            try
            {
                await _kernelService.StopHostImsRegistrationAsync(slot.Id);
                var stopped = _kernelService.GetHostImsRegistrationStatus(slot.Id);
                StatusMessage = stopped.RemoteDeregistered == true
                    ? "已停止 Host IMS SIP 会话，运营商确认注销。"
                    : "已停止本机 Host IMS SIP 会话；远端注册可能持续到有效期结束。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"停止 Host IMS 注册失败：{ex.Message}";
            }
            finally
            {
                IsHostImsRegistrationBusy = false;
                if (ReferenceEquals(slot, SelectedSlot)) RefreshHostImsRegistrationStatus();
            }
        }

        private void ApplyHostImsProbeResult(HostImsProbeResult result)
        {
            (HostImsStatusTitle, HostImsStatusDetail, HostImsReadinessText,
                HostImsStatusBrush, HostImsStatusBackground, HostImsStatusSymbol) = result.Readiness switch
            {
                HostImsReadiness.SimUnavailable => (
                    "模组已连接，当前没有可用 SIM",
                    $"{(result.AtControlAvailable switch { true => "AT 通道已响应", false => "AT 预检命令未成功", _ => "AT 通道未检测" })}；USB 数据模式{(result.UsbNetworkMode == null ? "未报告" : $"为 {result.UsbNetworkMode}")}。插入实体 SIM 或启用 eSIM Profile 后，再次探测 IMS PDN、P-CSCF 与 Windows 网卡映射。",
                    "NO SIM",
                    TokenPrimary, TokenPrimaryBg, SymbolRegular.Sim24),
                HostImsReadiness.ProbeFailed => (
                    "IMS 数据面探测未完成",
                    result.Summary,
                    "PROBE FAILED",
                    TokenWarning, TokenWarningBg, SymbolRegular.Warning24),
                HostImsReadiness.HostRoutable when result.CanAttemptWindowsIms => (
                    "发现 Windows IMS 数据面候选",
                    "活动网卡上发现 IMS 地址，且 Windows 到 P-CSCF 的出站接口和源地址匹配；仍需验证实际网络可达与 SIP REGISTER，当前结果不是通话就绪证明。",
                    "HOST CANDIDATE",
                    TokenSuccess, TokenSuccessBg, SymbolRegular.CheckmarkCircle24),
                HostImsReadiness.HostRoutable => (
                    "IMS 地址已映射，但 P-CSCF 路由未验证",
                    "Windows 网卡拥有 IMS 地址，但到 P-CSCF 的路由或源地址不匹配；已禁用实验性注册，请检查 IMS 专用数据面。",
                    "ROUTE MISMATCH",
                    TokenWarning, TokenWarningBg, SymbolRegular.Warning24),
                HostImsReadiness.ModemInternalOnly => (
                    "IMS 仍封在模组内部",
                    "模组已有活动 IMS PDN，但 Windows 不拥有该地址或 P-CSCF 路由。需要 MBIM/QMI 多 PDN 或独立 PPP 数据面；RNDIS NAT 不能替代。",
                    "MODEM INTERNAL",
                    TokenWarning, TokenWarningBg, SymbolRegular.Warning24),
                HostImsReadiness.ConfiguredButInactive => (
                    "IMS Profile 已配置但未激活",
                    "发现 IMS APN，但模组没有报告活动的 IMS 本机地址。需要由合适的 MBIM/QMI bearer 建立 IMS context。",
                    "INACTIVE",
                    TokenWarning, TokenWarningBg, SymbolRegular.Warning24),
                _ => (
                    "未发现 IMS PDP Context",
                    "模组没有报告 IMS APN。请先确认运营商 MBN/Carrier Profile 与模组固件能力；本探针不会自动写入 APN。",
                    "NOT CONFIGURED",
                    TokenMuted, TokenMutedBg, SymbolRegular.Info24)
            };

            if (result.ProbeSource != "AT")
                HostImsStatusDetail += $" 数据来源：{result.ProbeSource}。";
            if (!string.IsNullOrWhiteSpace(result.ControlStackStatus))
                HostImsStatusDetail += $" 控制栈：{result.ControlStackStatus}。";

            HostImsUsbMode = result.UsbNetworkMode ?? "未报告";
            HostImsModemSetting = result.ModemImsEnabled switch
            {
                true => "模组 IMS 已启用",
                false => "模组 IMS 已禁用",
                null => "MBN/自动或未报告"
            };

            HostImsContextText = result.Contexts.Count == 0
                ? "--"
                : string.Join(" · ", result.Contexts.Select(context =>
                    $"{context.Label} / {context.Apn} / {context.PdpType} / {(context.IsActive ? "Active" : "Inactive")}"));
            HostImsPdnAddress = JoinDistinctOrDash(result.Contexts.SelectMany(context => context.LocalAddresses).Select(address => address.ToString()));
            HostImsPcscf = JoinDistinctOrDash(result.Contexts.SelectMany(context => context.PcscfServers).Select(address => address.ToString()));
            HostImsWindowsInterface = JoinDistinctOrDash(result.Contexts.SelectMany(context => context.HostInterfaces));
            HostImsWindowsCellularAdapters = result.WindowsCellularAdapters.Count == 0
                ? "未发现候选网卡"
                : string.Join(" · ", result.WindowsCellularAdapters.Select(adapter =>
                    $"{adapter.Name} ({(adapter.IsConnected ? "已连接" : "未连接")})"));
            HostImsEndpointCandidates = result.EndpointCandidates.Count == 0
                ? "无；需同一活动 IMS Context 中有 Windows 本机地址和同族 P-CSCF"
                : string.Join(" · ", result.EndpointCandidates.Select(candidate =>
                    $"{candidate.Display} / " +
                    (result.RouteChecks?.FirstOrDefault(check => check.Endpoint == candidate) is { } route
                        ? (route.IsVerified ? "路由已验证" : route.Summary)
                        : "路由未检查")));
            HostImsCandidateOptions.Clear();
            var usableCandidates = result.EndpointCandidates.Where(result.IsRouteVerified).ToArray();
            foreach (var candidate in usableCandidates)
                HostImsCandidateOptions.Add(candidate);
            SelectedHostImsEndpoint = usableCandidates.Length == 1 ? usableCandidates[0] : null;
            OnPropertyChanged(nameof(CanStartHostImsRegistration));
        }

        private static string JoinDistinctOrDash(IEnumerable<string> values)
        {
            var items = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return items.Length == 0 ? "--" : string.Join(" · ", items);
        }

        [RelayCommand]
        private void ClearLogs()
        {
            Logs.Clear();
            StatusMessage = "日志已清空。";
        }

        [RelayCommand]
        private void CopyLogs()
        {
            try
            {
                var text = string.Join(Environment.NewLine, Logs.Select(l => $"[{l.FormattedTime}] [{l.Level}] [{l.Source}] {l.Message}"));
                Clipboard.SetText(text);
                StatusMessage = "日志内容已复制到剪贴板。";
                AppToast.ShowCopySuccess("系统日志");
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task ExportImsDiagnosticsAsync()
        {
            try
            {
                StatusMessage = "正在整理 IMS 注册诊断报告…";
                var report = await _kernelService.BuildImsDiagnosticReportAsync(SelectedSlot?.Id);
                var dialog = new SaveFileDialog
                {
                    FileName = $"VoWin-IMS-Diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                    DefaultExt = ".txt",
                    Filter = "VoWin 诊断报告 (*.txt)|*.txt|所有文件 (*.*)|*.*"
                };

                if (dialog.ShowDialog() != true)
                {
                    StatusMessage = "已取消导出诊断报告。";
                    return;
                }

                await File.WriteAllTextAsync(dialog.FileName, report);
                StatusMessage = "IMS 诊断报告已导出，可发送给技术支持分析。";
                AppToast.Show("诊断报告已导出", "报告已经过身份与密钥脱敏，可安全分享。", ControlAppearance.Success);
            }
            catch (Exception ex)
            {
                StatusMessage = $"导出 IMS 诊断报告失败: {ex.Message}";
                AppToast.Show("导出失败", "未能生成 IMS 诊断报告，请查看实时日志。", ControlAppearance.Danger);
            }
        }
    }
}
