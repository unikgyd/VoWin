# VoSharp 核心开发与 API 参考文档 (GUI / MVVM 专用)

本文档系统介绍了 `VoSharp`（.NET 10 / Windows / C# 纯自研电信协议栈）对外暴露的全部核心 API、数据模型、事件体系及 GUI/MVVM 集成规范。

---

## 目录
1. [快速上手与核心生命周期](#一快速上手与核心生命周期)
2. [呼叫控制接口 (Calls API)](#二呼叫控制接口-calls-api)
3. [短信收发与管理 (SMS API)](#三短信收发与管理-sms-api)
4. [VoWiFi 与 IMS 隧道 (VoWiFi API)](#四vowifi-与-ims-隧道-vowifi-api)
5. [多卡池与卡槽运维 (Modem Pool API)](#五多卡池与卡槽运维-modem-pool-api)
6. [eSIM / eUICC 档案管理 (eUICC API)](#六esim--euicc-档案管理-euicc-api)
7. [遥测与状态快照 (Telemetry & Snapshots)](#七遥测与状态快照-telemetry--snapshots)
8. [响应式事件体系 (Events & EventBus)](#八响应式事件体系-events--eventbus)
9. [GUI / MVVM 最佳实践示例](#九gui--mvvm-最佳实践示例)

---

## 一、快速上手与核心生命周期

`VoSharp.Kernel` 提供了统一的外观入口接口 `IVoKernel`（实现类为 `VoKernel`）。

### 1. 命名空间引用
```csharp
using VoSharp.Kernel;
using VoSharp.Kernel.Events;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using VoSharp.Euicc.Models;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.Common.Events;
```

### 2. 实例生命周期
`VoKernel` 实现了 `IAsyncDisposable`，在应用程序退出或关闭窗口时调用 `DisposeAsync` 会安全释放后台遥测任务、串口连接、ESP 数据隧道及非托管音频缓冲区：

```csharp
// 创建内核实例（可作为单例注册到依赖注入容器）
await using var kernel = new VoKernel();

// 连接物理串口上的调制解调器（如 EC25）
bool attached = await kernel.AttachModemAsync("COM6", 115200);
```

---

## 二、呼叫控制接口 (Calls API)

提供电话拨打、挂断、接听、拒接与 DTMF 按键发送功能，优先采用 VoWiFi IMS 隧道传输（AMR / G.711 RTP 音频流），支持蜂窝回退。

### 1. 方法清单

```csharp
/// <summary>发起外呼通话</summary>
/// <param name="number">目标电话号码</param>
/// <param name="slotId">指定卡槽 ID（为 null 时自动优选或采用主卡槽）</param>
/// <param name="forceCellular">是否强制走蜂窝基带 ATD 拨号</param>
Task<CallInfo> DialAsync(string number, string? slotId = null, bool forceCellular = false, CancellationToken ct = default);

/// <summary>挂断当前呼叫或通话</summary>
Task<CallInfo?> HangupAsync(string? slotId = null, CancellationToken ct = default);

/// <summary>接听当前来电振铃</summary>
Task<CallInfo?> AnswerAsync(string? slotId = null, CancellationToken ct = default);

/// <summary>拒接当前来电振铃</summary>
Task<CallInfo?> RejectAsync(string? slotId = null, CancellationToken ct = default);

/// <summary>在通话中发送 DTMF 双音多频按键信号</summary>
/// <param name="digit">按键字符：'0'-'9', '*', '#'</param>
Task<bool> SendDtmfAsync(char digit, string? slotId = null, CancellationToken ct = default);
```

### 2. 核心数据模型

```csharp
public enum CallState
{
    Idle,       // 空闲
    Dialing,    // 拨号中
    Incoming,   // 来电振铃
    Ringing,    // 对端振铃（180 Ringing）
    Active,     // 通话中
    Held,       // 保持中
    Ended       // 已挂断
}

public record CallInfo(
    string CallId,
    string TargetNumber,
    CallState State,
    DateTime StartedAt,
    DateTime? ConnectedAt,
    DateTime? EndedAt,
    string? Codec,
    string? WavRecordingPath,
    bool IsOutgoing = true
);
```

---

## 三、短信收发与管理 (SMS API)

全面支持 3GPP PDU 编码、GSM 7-bit / UCS2 中英文混排、长短信自动分片重组、送达状态报告回执，以及 VoWiFi IMS (SIP MESSAGE + RP-DATA) 与蜂窝基带双通道发送。

### 1. 方法清单

```csharp
/// <summary>发送短信（支持自动多段切片与状态报告回执）</summary>
Task<SmsSubmitResult> SendSmsAsync(
    string recipient,
    string text,
    bool requestStatusReport = true,
    string? slotId = null,
    bool forceVowifi = false,
    bool forceCellular = false,
    CancellationToken ct = default);

/// <summary>获取当前内存收件箱列表</summary>
IReadOnlyList<SmsMessage> GetInbox();

/// <summary>获取当前内存发件箱列表</summary>
IReadOnlyList<SmsMessage> GetOutbox();

/// <summary>删除指定序号的收件箱消息</summary>
bool DeleteInboxMessage(int index);

/// <summary>清空收件箱</summary>
void ClearInbox();
```

### 2. 核心数据模型

```csharp
public enum SmsStatus { Unread, Read, Unsent, Sent }
public enum SmsDirection { Received, Submitted, StatusReport }

public record SmsMessage(
    int Index,
    SmsStatus Status,
    string SenderOrRecipient,
    string Text,
    DateTime Timestamp,
    string RawPdu,
    SmsDirection Direction = SmsDirection.Received,
    int? MessageReference = null,
    string? DeliveryStatus = null,
    DateTime? ServiceCenterTimestamp = null,
    DateTime? DischargeTimestamp = null,
    int? StatusCode = null,
    SmsConcatInfo? Concat = null
);

public record SmsSubmitResult(
    string Recipient,
    string Text,
    string Encoding,
    int? ConcatReference,
    int PartsTotal,
    int PartsAccepted,
    int PartsAttempted,
    bool AllPartsAccepted,
    string SubmissionStatus,
    List<SmsSubmitPartStatus> PartResults,
    DateTime SubmittedAt
);

public record SmsStatusReport(
    int MessageReference,
    string Recipient,
    int StatusCode,
    string DeliveryStatus,
    DateTime? ServiceCenterTimestamp,
    DateTime? DischargeTimestamp,
    DateTime Timestamp,
    string RawPdu
);
```

---

## 四、VoWiFi 与 IMS 隧道 (VoWiFi API)

包含纯 C# 用户态 ESP 数据隧道（消除 WinDivert 依赖）、IKEv2 鉴权与密钥派生、以及 SIP 注册。

### 1. 方法清单

```csharp
/// <summary>启动指定卡槽（或当前 SIM）的 VoWiFi 隧道并注册 IMS</summary>
Task<bool> StartVoWifiAsync(string? slotId = null, CancellationToken ct = default);

/// <summary>停止 VoWiFi 隧道并注销 SIP 注册</summary>
Task<bool> StopVoWifiAsync(string? slotId = null, CancellationToken ct = default);

/// <summary>获取 VoWiFi 隧道与 IMS 详细诊断信息</summary>
VoWifiDiagnosticInfo? GetVoWifiDiagnostics(string? slotId = null);
```

### 2. 核心数据模型

```csharp
public enum VoWifiState
{
    Disconnected,       // 已断开
    ResolvingEpdg,      // 正在解析 ePDG 域名
    ConnectingIkev2,    // IKEv2 / EAP-AKA 协商中
    TunnelEstablished,  // IPsec ESP 隧道已建立
    RegisteringIms,     // SIP Register 注册中
    ImsRegistered,      // IMS 已注册，可进行高清通话与短信
    Failed,             // 失败
    Disconnecting       // 正在断开
}

public record VoWifiDiagnosticInfo(
    VoWifiState State,
    string? AssignedIp,
    string? PcscfIp,
    int InboundEspPackets,
    int OutboundEspPackets,
    string? MatchedCarrier,
    string? LastError
);
```

---

## 五、多卡池与卡槽运维 (Modem Pool API)

支持多物理串口多卡并发运行、热插拔探测、独占租约机制（Slot Lease）与独立 SOCKS5 代理配置。

### 1. 方法清单

```csharp
/// <summary>获取所有已登记卡槽</summary>
IReadOnlyList<ModemSlot> GetSlots();

/// <summary>获取当前活动卡槽</summary>
ModemSlot? GetActiveSlot();

/// <summary>切换当前主活动卡槽</summary>
bool SelectSlot(string slotId);

/// <summary>为指定卡槽设置专用代理（如 socks5://127.0.0.1:1080）</summary>
bool SetSlotProxy(string slotId, string? proxyUrl);

/// <summary>自动扫描并探测系统全部串口 Modem 设备</summary>
Task<IReadOnlyList<ModemSlot>> DiscoverSlotsAsync(CancellationToken ct = default);

/// <summary>手动添加卡槽</summary>
Task<ModemSlot> AddSlotAsync(string portName, int baudRate = 115200, string? name = null, string? slotId = null, string? proxyUrl = null, CancellationToken ct = default);

/// <summary>安全移除卡槽（包含租约排空 Draining 保护）</summary>
Task<bool> RemoveSlotAsync(string slotId, CancellationToken ct = default);
```

---

## 六、eSIM / eUICC 档案管理 (eUICC API)

实现 GSMA SGP.22 ES10 规范的本地卡档案管理子集（包含 APDU 通道建立、TLV 解析与 Profile 生命周期）。

### 1. 方法清单

```csharp
/// <summary>获取 eSIM 内部所有 Profile 档案列表</summary>
Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(CancellationToken ct = default);

/// <summary>获取当前已启用的活跃 Profile</summary>
Task<Profile?> GetActiveEuiccProfileAsync(CancellationToken ct = default);

/// <summary>读取 eUICC 芯片 EID</summary>
Task<string> GetEuiccEidAsync(CancellationToken ct = default);

/// <summary>切换/启用指定 Profile（传入 ICCID 或 ISD-P AID）</summary>
Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default);

/// <summary>禁用指定 Profile</summary>
Task<bool> DisableEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default);

/// <summary>删除指定 Profile</summary>
Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, CancellationToken ct = default);

/// <summary>修改 Profile 别名（Nickname）</summary>
Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, CancellationToken ct = default);
```

### 2. 核心数据模型

```csharp
public enum ProfileState { Disabled = 0, Enabled = 1 }
public enum ProfileClass { Test = 0, Provisioning = 1, Operational = 2 }

public class Profile
{
    public string ICCID { get; set; }
    public string ISDPAID { get; set; }
    public ProfileState State { get; set; }
    public string? Nickname { get; set; }
    public string? ServiceProviderName { get; set; }
    public string? ProfileName { get; set; }
    public ProfileClass ProfileClass { get; set; }
}
```

---

## 七、遥测与状态快照 (Telemetry & Snapshots)

为 MVVM 数据绑定量身打造的**不可变点在时间状态快照**与指标轮询。

### 1. 方法清单

```csharp
/// <summary>抓取当前整个电话引擎的不可变状态快照（只读安全）</summary>
KernelSnapshot CreateSnapshot();

/// <summary>主动刷新指定卡槽的信号强度（CSQ / RSSI）</summary>
Task<SignalQuality?> RefreshSignalAsync(string? slotId = null, CancellationToken ct = default);

/// <summary>主动刷新指定卡槽的网络注册状态（CREG / CEREG）</summary>
Task<NetworkRegistration?> RefreshRegistrationAsync(string? slotId = null, CancellationToken ct = default);

/// <summary>主动刷新 SIM 卡信息（IMSI、ICCID）</summary>
Task<SimIdentity?> RefreshSimAsync(string? slotId = null, CancellationToken ct = default);
```

### 2. `KernelSnapshot` 模型字段

```csharp
public record KernelSnapshot(
    TelephonyState State,              // 状态机全局状态
    string StateDescription,           // 状态可读描述
    string? ModemPort,                 // 当前活动卡槽串口名
    bool IsModemConnected,             // 串口是否连接
    SimIdentity? Sim,                  // 当前 SIM 身份（IMSI, ICCID, 运营商）
    SignalQuality? Signal,             // 信号质量（RSSI dBm, 格数 0-5）
    NetworkRegistration? Network,      // 蜂窝网络附着状态（Home, Roaming 等）
    VoWifiState VoWifiState,           // VoWiFi 状态
    IPAddress? VoWifiIp,               // VoWiFi 分配的 IP 地址
    CallSession? ActiveCall,           // 当前呼叫会话
    int InboxCount,                    // 收件箱未读/总数
    int OutboxCount,                   // 发件箱总数
    string? ActiveSlotId,              // 当前主卡槽 ID
    int TotalSlots,                    // 已连接卡槽总数
    DateTime Timestamp                 // 快照生成时间
);
```

---

## 八、响应式事件体系 (Events & EventBus)

### 1. `IVoKernel` 强类型 C# 原生事件列表

```csharp
// ── 通话类事件 ──
event EventHandler<CallStateChangedEventArgs>? CallStateChanged;
event EventHandler<IncomingCallEventArgs>? IncomingCall;
event EventHandler<CallConnectedEventArgs>? CallConnected;
event EventHandler<CallEndedEventArgs>? CallEnded;
event EventHandler<DtmfReceivedEventArgs>? DtmfReceived;

// ── 短信类事件 ──
event EventHandler<SmsReceivedEventArgs>? SmsReceived;
event EventHandler<SmsSentEventArgs>? SmsSent;
event EventHandler<SmsStatusReportEventArgs>? SmsStatusReportReceived;

// ── 网络与 VoWiFi 事件 ──
event EventHandler<VoWifiStateChangedEventArgs>? VoWifiStateChanged;
event EventHandler<SignalChangedEventArgs>? SignalQualityChanged;
event EventHandler<NetworkRegistrationChangedEventArgs>? NetworkRegistrationChanged;
event EventHandler<SimStateChangedEventArgs>? SimStateChanged;
event EventHandler<ModemConnectionChangedEventArgs>? ModemConnectionChanged;
event EventHandler<FlightModeChangedEventArgs>? FlightModeChanged;

// ── 卡池与多卡槽事件 ──
event EventHandler<SlotStateChangedEventArgs>? SlotStateChanged;
event EventHandler<ActiveSlotChangedEventArgs>? ActiveSlotChanged;
event EventHandler<SlotListChangedEventArgs>? SlotListChanged;

// ── eSIM 事件 ──
event EventHandler<EuiccProfilesChangedEventArgs>? EuiccProfilesUpdated;
event EventHandler<EuiccOperationEventArgs>? EuiccOperationCompleted;

// ── 状态机与日志事件 ──
event EventHandler<TelephonyStateChangedEventArgs>? TelephonyStateChanged;
event EventHandler<LogEmittedEventArgs>? LogEmitted;
event EventHandler<SystemErrorEventArgs>? SystemErrorOccurred;
```

### 2. `AsyncEventBus` 异步事件总线 (支持 UI 线程自动调度)

```csharp
// 订阅所有事件并自动封送到 UI 线程（如 WPF / WinUI Dispatcher）
kernel.EventBus.Subscribe("*", async ev =>
{
    // 此处已处于 UI 线程，可直接修改 ObservableCollection
    ViewModel.LogEntries.Add($"[{ev.Source}] {ev.Topic}: {ev.Payload}");
}, SynchronizationContext.Current);
```

---

## 九、GUI / MVVM 最佳实践示例

以下展示如何在 WPF / WinUI 3 / Avalonia 的 ViewModel 中优雅地使用 `IVoKernel`：

```csharp
public class MainViewModel : INotifyPropertyChanged
{
    private readonly IVoKernel _kernel;
    public ObservableCollection<SmsMessage> Messages { get; } = new();
    public ObservableCollection<ModemSlot> Slots { get; } = new();

    private string _callStatus = "空闲";
    public string CallStatus
    {
        get => _callStatus;
        set { _callStatus = value; OnPropertyChanged(); }
    }

    public MainViewModel(IVoKernel kernel)
    {
        _kernel = kernel;

        // 1. 订阅呼叫状态
        _kernel.CallStateChanged += (s, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                CallStatus = $"呼叫 [{e.TargetNumber}]: {e.NewState}";
            });
        };

        // 2. 收到来电提醒
        _kernel.IncomingCall += (s, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                CallStatus = $"来电中: {e.CallerNumber} ({e.DisplayName ?? "未知"})";
            });
        };

        // 3. 收到新短信自动插入列表
        _kernel.SmsReceived += (s, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Messages.Insert(0, e.Message);
            });
        };

        // 4. 卡槽增减
        _kernel.SlotListChanged += (s, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Slots.Clear();
                foreach (var slot in e.Slots) Slots.Add(slot);
            });
        };
    }

    // 拨号命令
    public async Task DialNumberAsync(string number)
    {
        try
        {
            var callInfo = await _kernel.DialAsync(number);
            CallStatus = $"正在拨打 {callInfo.TargetNumber}...";
        }
        catch (Exception ex)
        {
            CallStatus = $"拨号失败: {ex.Message}";
        }
    }

    // 挂断命令
    public async Task HangupAsync()
    {
        await _kernel.HangupAsync();
        CallStatus = "已挂断";
    }

    // 发送短信命令
    public async Task SendSmsAsync(string recipient, string text)
    {
        var result = await _kernel.SendSmsAsync(recipient, text);
        if (result.AllPartsAccepted)
        {
            // 发送成功
        }
    }

    // 启动 VoWiFi
    public async Task StartVoWifiAsync()
    {
        bool ok = await _kernel.StartVoWifiAsync();
    }
}
```
