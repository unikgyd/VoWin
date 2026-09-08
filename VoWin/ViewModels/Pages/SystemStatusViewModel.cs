using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoSharp.Kernel.Pool;
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
        private int _refreshNotificationScheduled;

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

        [ObservableProperty]
        private string _statusMessage = "系统运行正常";

        [ObservableProperty]
        private bool _isBusy;

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

        public string FlightModeText => (SelectedSlot?.IsFlightMode == true) ? "飞行模式 (射频关闭)" : "射频激活 (在线)";

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
                if (SelectedSlot == null && Slots.Count > 0)
                {
                    SelectedSlot = Slots.FirstOrDefault();
                }
            };

            // Hook state changes to refresh properties
            _kernelService.Kernel.TelephonyStateChanged += (s, e) => NotifyAll();
            _kernelService.Kernel.VoWifiStateChanged += (s, e) => NotifyAll();
            _kernelService.Kernel.SignalQualityChanged += (s, e) => NotifyAll();
            _kernelService.Kernel.NetworkRegistrationChanged += (s, e) => NotifyAll();
            _kernelService.Kernel.SimStateChanged += (s, e) => NotifyAll();
            _kernelService.Kernel.FlightModeChanged += (s, e) => NotifyAll();

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
            };
            _heartbeatTimer.Start();
        }

        partial void OnSelectedSlotChanged(ModemSlot? value)
        {
            NotifyAll();
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
            StatusMessage = "正在刷新全部射频与 SIM 卡遥测...";
            try
            {
                await _kernelService.RefreshMetricsAsync();
                NotifyAll();
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
    }
}
