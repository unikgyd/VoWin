using VoSharp.Common.Aka;
using VoSharp.Common.Events;
using VoSharp.Euicc;
using VoSharp.Euicc.Models;
using VoSharp.Kernel.Events;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel;

/// <summary>
/// Unified abstraction for the VoSharp core engine.
/// Designed for easy consumption by GUI applications (WPF, WinUI 3, Avalonia, MAUI) and MVVM architectures.
/// </summary>
public interface IVoKernel : IAsyncDisposable
{
    // ── Subsystems & State ───────────────────────────────────────────────────
    AsyncEventBus EventBus { get; }
    TelephonyStateMachine StateMachine { get; }
    ModemPool Pool { get; }

    ModemDriver? Modem { get; }
    IAkaProvider AkaProvider { get; }
    SimIdentity? CurrentSim { get; }
    VoWifiManager VoWifi { get; }
    ImsCallManager Calls { get; }
    SmsService? SmsService { get; }
    EuiccManager? EuiccManager { get; set; }

    CallSession? ActiveCall { get; }
    bool RoamingAllowed { get; }
    IReadOnlyList<SmsMessage> Inbox { get; }
    IReadOnlyList<SmsMessage> Outbox { get; }

    // ── High-Level Operations ────────────────────────────────────────────────
    Task<bool> AttachModemAsync(string portName, int baudRate = 115200);
    Task<KernelCommandResult> ExecuteCommandAsync(string commandLine, CancellationToken ct = default);
    Task ReloadSimAndNetworkAsync(CancellationToken ct = default);
    Task<EuiccManager> GetOrCreateEuiccManagerAsync(CancellationToken ct = default);
    KernelSnapshot CreateSnapshot();

    // ── Direct Call Operations ───────────────────────────────────────────────
    Task<CallInfo> DialAsync(string number, string? slotId = null, bool forceCellular = false, CancellationToken ct = default);
    Task<CallInfo?> HangupAsync(string? slotId = null, CancellationToken ct = default);
    Task<CallInfo?> AnswerAsync(string? slotId = null, CancellationToken ct = default);
    Task<CallInfo?> RejectAsync(string? slotId = null, CancellationToken ct = default);
    Task<bool> SendDtmfAsync(char digit, string? slotId = null, CancellationToken ct = default);

    // ── Direct SMS Operations ────────────────────────────────────────────────
    Task<SmsSubmitResult> SendSmsAsync(
        string recipient,
        string text,
        bool requestStatusReport = true,
        string? slotId = null,
        bool forceVowifi = false,
        bool forceCellular = false,
        CancellationToken ct = default);
    IReadOnlyList<SmsMessage> GetInbox();
    IReadOnlyList<SmsMessage> GetOutbox();
    bool DeleteInboxMessage(int index);
    void ClearInbox();

    // ── Direct VoWiFi & IMS Operations ───────────────────────────────────────
    Task<bool> StartVoWifiAsync(string? slotId = null, CancellationToken ct = default);
    Task<bool> StopVoWifiAsync(string? slotId = null, CancellationToken ct = default);
    VoWifiDiagnosticInfo? GetVoWifiDiagnostics(string? slotId = null);
    Task<(bool Success, long RttMs, string Status)> ProbeVoWifiLivenessAsync(string? slotId = null, CancellationToken ct = default);

    // ── Direct Slot & Modem Pool Operations ──────────────────────────────────
    IReadOnlyList<ModemSlot> GetSlots();
    ModemSlot? GetActiveSlot();
    bool SelectSlot(string slotId);
    bool SetSlotProxy(string slotId, string? proxyUrl);
    Task<IReadOnlyList<ModemSlot>> DiscoverSlotsAsync(CancellationToken ct = default);
    Task<ModemSlot> AddSlotAsync(string portName, int baudRate = 115200, string? name = null, string? slotId = null, string? proxyUrl = null, CancellationToken ct = default);
    Task<bool> RemoveSlotAsync(string slotId, CancellationToken ct = default);

    // ── Direct eUICC / eSIM Operations ───────────────────────────────────────
    Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(CancellationToken ct = default);
    Task<Profile?> GetActiveEuiccProfileAsync(CancellationToken ct = default);
    Task<string> GetEuiccEidAsync(CancellationToken ct = default);
    Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default);
    Task<bool> DisableEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default);
    Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, CancellationToken ct = default);
    Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, CancellationToken ct = default);
    Task<EuiccDownloadResult> DownloadEuiccProfileAsync(string activationCode, string? confirmationCode = null, IProgress<EuiccDownloadProgress>? progress = null, CancellationToken ct = default);

    // ── Direct Metrics & Telemetry Operations ────────────────────────────────
    Task<SignalQuality?> RefreshSignalAsync(string? slotId = null, CancellationToken ct = default);
    Task<NetworkRegistration?> RefreshRegistrationAsync(string? slotId = null, CancellationToken ct = default);
    Task<SimIdentity?> RefreshSimAsync(string? slotId = null, CancellationToken ct = default);


    // ── Call Events ──────────────────────────────────────────────────────────
    event EventHandler<CallStateChangedEventArgs>? CallStateChanged;
    event EventHandler<IncomingCallEventArgs>? IncomingCall;
    event EventHandler<CallConnectedEventArgs>? CallConnected;
    event EventHandler<CallEndedEventArgs>? CallEnded;
    event EventHandler<DtmfReceivedEventArgs>? DtmfReceived;

    // ── SMS Events ───────────────────────────────────────────────────────────
    event EventHandler<SmsReceivedEventArgs>? SmsReceived;
    event EventHandler<SmsSentEventArgs>? SmsSent;
    event EventHandler<SmsStatusReportEventArgs>? SmsStatusReportReceived;

    // ── VoWiFi & IMS Events ──────────────────────────────────────────────────
    event EventHandler<VoWifiStateChangedEventArgs>? VoWifiStateChanged;

    // ── Cellular & Radio Events ──────────────────────────────────────────────
    event EventHandler<SignalChangedEventArgs>? SignalQualityChanged;
    event EventHandler<NetworkRegistrationChangedEventArgs>? NetworkRegistrationChanged;
    event EventHandler<SimStateChangedEventArgs>? SimStateChanged;
    event EventHandler<ModemConnectionChangedEventArgs>? ModemConnectionChanged;
    event EventHandler<FlightModeChangedEventArgs>? FlightModeChanged;

    // ── Multi-Modem Pool & Slot Events ───────────────────────────────────────
    event EventHandler<SlotStateChangedEventArgs>? SlotStateChanged;
    event EventHandler<ActiveSlotChangedEventArgs>? ActiveSlotChanged;
    event EventHandler<SlotListChangedEventArgs>? SlotListChanged;

    // ── State Machine Events ─────────────────────────────────────────────────
    event EventHandler<TelephonyStateChangedEventArgs>? TelephonyStateChanged;

    // ── eUICC / eSIM Events ──────────────────────────────────────────────────
    event EventHandler<EuiccProfilesChangedEventArgs>? EuiccProfilesUpdated;
    event EventHandler<EuiccOperationEventArgs>? EuiccOperationCompleted;

    // ── Diagnostics & Logging Events ─────────────────────────────────────────
    event EventHandler<LogEmittedEventArgs>? LogEmitted;
    event EventHandler<SystemErrorEventArgs>? SystemErrorOccurred;
}
