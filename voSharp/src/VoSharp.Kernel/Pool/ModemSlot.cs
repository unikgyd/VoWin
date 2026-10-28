using System.ComponentModel;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using VoSharp.Common.Aka;
using VoSharp.Common.Events;
using VoSharp.Euicc;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Transport;
using VoSharp.Kernel.Events;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using qmiSharp.Transport;

namespace VoSharp.Kernel.Pool;

public enum SlotState
{
    Offline,
    Online,
    Busy,
    Error
}

public enum HostImsLossKind { None, RegistrationRefreshFailed, BearerLost, RestorationFailed, ManuallyStopped }

public sealed record HostImsRegistrationStatus(
    bool IsRegistered,
    string State,
    string? Endpoint,
    DateTime? RegisteredAtUtc,
    DateTime? ExpiresAtUtc,
    string? LastError,
    bool? RemoteDeregistered = null,
    string? CardPath = null,
    bool SmsReceiveEnabled = false)
{
    public SipRegistrationFailureInfo? Failure { get; init; }
    public DateTime? RetryNotBeforeUtc { get; init; }
    public bool AutomaticRecoveryPaused { get; init; }
    public HostImsLossKind LossKind { get; init; }
}

public sealed record HostImsPppBearerStatus(
    bool IsActive,
    WindowsHostImsPppBearerInfo? Bearer,
    string? LastError,
    int RenewalCount = 0,
    DateTime? LastRenewedAtUtc = null,
    int MediaRouteCount = 0)
{
    public int DefaultRouteRepairCount { get; init; }
}

/// <summary>
/// Result of a read-only eUICC capability probe.  Unsupported is reserved for
/// cards on which the ISD-R application was explicitly absent; transient AT
/// or logical-channel failures remain an error instead of being mislabeled as
/// an ordinary SIM.
/// </summary>
public enum EuiccCapability
{
    Unknown,
    Probing,
    Supported,
    Unsupported,
    Error
}

public sealed record EuiccProbeResult(
    EuiccCapability Capability,
    string? Eid,
    IReadOnlyList<Profile> Profiles,
    string Message,
    IReadOnlyList<string>? Eids = null,
    IReadOnlyList<EuiccInventoryEntry>? Inventory = null)
{
    public bool IsSupported => Capability == EuiccCapability.Supported;
    public IReadOnlyList<string> DetectedEids => Inventory is { Count: > 0 }
        ? Inventory.Select(entry => entry.Eid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        : Eids is { Count: > 0 }
        ? Eids
        : string.IsNullOrWhiteSpace(Eid) ? Array.Empty<string>() : [Eid];
}

/// <summary>
/// Encapsulates a single modem hardware instance, its SIM identity, AKA credentials,
/// VoWiFi tunnel manager, IMS call manager, and dedicated SOCKS5 proxy configuration.
/// </summary>
public partial class ModemSlot : IAsyncDisposable, INotifyPropertyChanged
{
    private readonly SemaphoreSlim _profileSwitchGate = new(1, 1);
    private readonly SemaphoreSlim _euiccProbeGate = new(1, 1);
    private readonly SemaphoreSlim _hostImsRegistrationGate = new(1, 1);
    private WindowsHostImsPppBearerLease? _hostImsPppLease;
    private string? _hostImsPppError;
    private int _hostImsPppTransition;
    public bool IsHostImsPppTransition => Volatile.Read(ref _hostImsPppTransition) != 0;
    public bool IsHostImsPppTransitionOrActive =>
        Volatile.Read(ref _hostImsPppTransition) != 0 || Volatile.Read(ref _hostImsPppLease) is not null;
    private HostImsRegistrationClient? _hostImsRegistration;
    private readonly SipRegistrationRetryGate _hostImsRetryGate = new();
    private int _hostImsLossKind;
    /// <summary>Explicit stop intent only; background fallback does not raise it.</summary>
    public event Action? HostImsManualStopRequested;
    public bool HostImsRecoveryCleanupPending => IsHostImsPppTransitionOrActive ||
        Volatile.Read(ref _hostImsRegistration) is not null || IsProfileSwitchInProgress;

    private void NotifyHostImsManualStop()
    {
        InvalidateSmsIdentity();
        Volatile.Write(ref _hostImsLossKind, (int)HostImsLossKind.ManuallyStopped);
        try { HostImsManualStopRequested?.Invoke(); } catch { }
    }

    public Task WaitForHostImsRegistrarRetryAsync(string expectedIccid, CancellationToken ct) =>
        _hostImsRetryGate.WaitUntilAllowedAsync(expectedIccid, ct);
    private ModemDriver? _hostImsRegisteredModem;
    private string? _hostImsRegisteredIccid;
    private string? _hostImsRegisteredImsi;
    private Action<IncomingSms>? _hostImsIncomingSmsHandler;
    private string? _hostImsRegistrationError;
    private long _hostImsRegistrationAttemptGeneration;
    private int _hostImsRemoteDeregistration = -1;
    private readonly SimIdentityHistoryStore _identityHistory;
    private int _profileSwitchInProgress;
    public bool IsProfileSwitchInProgress => Volatile.Read(ref _profileSwitchInProgress) != 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public string Id { get; }
    // Null retains CLI/library automatic routing. Desktop mode selection is strict.
    public TelephonyMode? PreferredTelephonyMode { get; set; }
    private TelephonyMode? _cellularImsMode;
    /// <summary>Cellular owner only; VoWiFi has a separate lifecycle on this instance.</summary>
    public TelephonyMode? CellularImsMode
    {
        get => _cellularImsMode;
        set
        {
            if (value is not (null or TelephonyMode.ModemBridge or TelephonyMode.HostIms))
                throw new ArgumentOutOfRangeException(nameof(value));
            _cellularImsMode = value;
            OnPropertyChanged();
        }
    }
    public bool TelephonyEnabled { get; set; } = true;
    private ModemControlPreference _controlPreference;
    public ModemControlPreference ControlPreference
    {
        get => _controlPreference;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _controlPreference = value;
            if (Modem is { } modem) modem.ControlPreference = value;
            OnPropertyChanged();
        }
    }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DeviceTitle));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    private string? _cardNickname;
    public string? CardNickname
    {
        get => _cardNickname;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_cardNickname != normalized)
            {
                _cardNickname = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    private string _portName = string.Empty;
    public string PortName
    {
        get => _portName;
        set { if (_portName != value) { _portName = value; OnPropertyChanged(); } }
    }

    private int _baudRate = 115200;
    public int BaudRate
    {
        get => _baudRate;
        set { if (_baudRate != value) { _baudRate = value; OnPropertyChanged(); } }
    }

    private SlotState _state = SlotState.Offline;
    public SlotState State
    {
        get => _state;
        private set { if (_state != value) { _state = value; OnPropertyChanged(); } }
    }

    private string? _proxyUrl;
    public string? ProxyUrl
    {
        get => _proxyUrl;
        set { if (_proxyUrl != value) { _proxyUrl = value; OnPropertyChanged(); } }
    }

    public event EventHandler<SlotStateChangedEventArgs>? StateChanged;
    public event EventHandler<SignalChangedEventArgs>? SignalChanged;
    public event EventHandler<NetworkRegistrationChangedEventArgs>? RegistrationChanged;
    public event EventHandler<SimStateChangedEventArgs>? SimChanged;
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;
    public event EventHandler<CallStateChangedEventArgs>? CallStateChanged;
    public event EventHandler<CallConnectedEventArgs>? CallConnected;
    public event EventHandler<CallEndedEventArgs>? CallEnded;
    public event EventHandler<SmsReceivedEventArgs>? SmsReceived;
    public event EventHandler<SmsSentEventArgs>? SmsSent;
    public event EventHandler<SmsStatusReportEventArgs>? SmsStatusReportReceived;

    public ModemDriver? Modem { get; private set; }

    /// <summary>True when this slot is a Windows PC/SC smart-card reader rather than an AT modem.</summary>
    public bool IsPcscReader { get; private set; }

    /// <summary>The Windows reader name for a PC/SC-only VoWiFi slot.</summary>
    public string? PcscReaderName { get; private set; }

    /// <summary>
    /// PC/SC readers have no cellular radio controls. Once IMS is registered,
    /// calls and SMS continue over the VoWiFi SIP/IPsec data plane as normal.
    /// </summary>
    public bool IsVoWifiOnly => IsPcscReader;
    public bool SupportsAtCommands => !IsPcscReader;
    public bool SupportsCellularControls => !IsPcscReader;

    private string? _voWifiImei;
    /// <summary>
    /// Genuine terminal IMEI to send for a PC/SC card's IMS registration. It is
    /// deliberately separate from <see cref="Imei"/>, which belongs to an AT modem.
    /// </summary>
    public string? VoWifiImei
    {
        get => _voWifiImei;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_voWifiImei == normalized) return;
            _voWifiImei = normalized;
            VoWifi.DeviceImei = normalized;
            OnPropertyChanged();
        }
    }

    private EuiccCapability _euiccCapability = EuiccCapability.Unknown;
    public EuiccCapability EuiccCapability
    {
        get => _euiccCapability;
        private set
        {
            if (_euiccCapability == value) return;
            _euiccCapability = value;
            OnPropertyChanged();
        }
    }

    private string _euiccCapabilityMessage = "尚未检测 eUICC 芯片。";
    public string EuiccCapabilityMessage
    {
        get => _euiccCapabilityMessage;
        private set
        {
            if (_euiccCapabilityMessage == value) return;
            _euiccCapabilityMessage = value;
            OnPropertyChanged();
        }
    }

    private EuiccProbeResult? _lastEuiccProbe;
    public EuiccProbeResult? LastEuiccProbe
    {
        get => _lastEuiccProbe;
        private set
        {
            if (ReferenceEquals(_lastEuiccProbe, value)) return;
            _lastEuiccProbe = value;
            OnPropertyChanged();
        }
    }

    private SimIdentity? _sim;
    public SimIdentity? Sim
    {
        get => _sim;
        private set
        {
            if (_sim != value)
            {
                var oldIccid = _sim?.Iccid;
                var oldImsi = _sim?.Imsi;
                PublishSmsIdentity(value);
                if (!string.Equals(oldIccid, value?.Iccid, StringComparison.Ordinal) ||
                    !string.Equals(oldImsi, value?.Imsi, StringComparison.Ordinal))
                    Volatile.Read(ref _hostImsRegistration)?.Invalidate();
                if (!string.Equals(oldIccid, value?.Iccid, StringComparison.Ordinal))
                {
                    VoWifi.UserEpdgConfiguration = null;
                    CardNickname = null;
                    // A physical-card replacement (or a successful eSIM
                    // switch) invalidates any support result cached for the
                    // previous UICC.  The caller will probe the new card.
                    LastEuiccProbe = null;
                    SetEuiccCapability(EuiccCapability.Unknown, "SIM 卡已变化，等待检测 eUICC 芯片。", log: false);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(CarrierName));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    private string? _stableRoutingIccid;
    private string? _stableRoutingMcc;
    public string? StableRoutingMcc
    {
        get => _stableRoutingMcc;
        private set
        {
            if (_stableRoutingMcc == value) return;
            _stableRoutingMcc = value;
            OnPropertyChanged();
        }
    }

    private string? _lastReportedImsi;
    public string? LastReportedImsi
    {
        get => _lastReportedImsi;
        private set
        {
            if (_lastReportedImsi == value) return;
            _lastReportedImsi = value;
            OnPropertyChanged();
        }
    }

    private string? _lastPermanentImsi;
    public string? LastPermanentImsi
    {
        get => _lastPermanentImsi;
        private set
        {
            if (_lastPermanentImsi == value) return;
            _lastPermanentImsi = value;
            OnPropertyChanged();
        }
    }

    private string _imsiIdentitySource = "unknown";
    public string ImsiIdentitySource
    {
        get => _imsiIdentitySource;
        private set
        {
            if (_imsiIdentitySource == value) return;
            _imsiIdentitySource = value;
            OnPropertyChanged();
        }
    }

    private bool _hasImsiPlmnConflict;
    public bool HasImsiPlmnConflict
    {
        get => _hasImsiPlmnConflict;
        private set
        {
            if (_hasImsiPlmnConflict == value) return;
            _hasImsiPlmnConflict = value;
            OnPropertyChanged();
        }
    }

    public string CarrierName
    {
        get
        {
            if (Sim == null)
                return string.Empty;
            return Sim.OperatorName;
        }
    }

    public string DeviceTitle => Name == $"Slot {Id} ({PortName})" ? DeviceModel ?? Name : Name;

    public string DisplayTitle
    {
        get
        {
            var moduleName = !string.IsNullOrWhiteSpace(DeviceTitle) ? DeviceTitle : "模组";
            var cardName = !string.IsNullOrWhiteSpace(CardNickname)
                ? CardNickname
                : !string.IsNullOrWhiteSpace(CarrierName)
                    ? CarrierName
                    : null;
            return !string.IsNullOrWhiteSpace(cardName) &&
                   !string.Equals(cardName, moduleName, StringComparison.OrdinalIgnoreCase)
                ? $"{cardName} · {moduleName} ({PortName})"
                : $"{moduleName} ({PortName})";
        }
    }

    public IAkaProvider Aka { get; private set; } = SoftwareAkaProvider.FromTestVectors();
    public VoWifiManager VoWifi { get; }
    public ImsCallManager Calls { get; }
    public SmsService? Sms { get; private set; }

    // Some roaming and multi-IMSI profiles report a temporary serving IMSI to
    // AT+CIMI. Their home ePDG still requires the permanent identity for EAP-AKA;
    // the UI may restore a previously observed IMSI keyed by the same ICCID.
    public SimIdentity? VoWifiIdentityOverride { get; set; }

    private SignalQuality? _signal;
    public SignalQuality? Signal
    {
        get => _signal;
        private set { if (_signal != value) { _signal = value; OnPropertyChanged(); } }
    }

    private NetworkRegistration? _registration;
    public NetworkRegistration? Registration
    {
        get => _registration;
        private set { if (_registration != value) { _registration = value; OnPropertyChanged(); } }
    }

    private string? _imei;
    private string? _deviceModel;
    public string? DeviceModel
    {
        get => _deviceModel;
        private set
        {
            if (_deviceModel == value) return;
            _deviceModel = value; OnPropertyChanged();
            OnPropertyChanged(nameof(DeviceTitle)); OnPropertyChanged(nameof(DisplayTitle));
        }
    }
    public Guid? UsbContainerId { get; private set; }
    public string? Imei
    {
        get => _imei;
        private set { if (_imei != value) { _imei = value; OnPropertyChanged(); } }
    }

    public DateTime LastSeen { get; private set; } = DateTime.UtcNow;
    public string? LastError { get; private set; }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; OnPropertyChanged(); } }
    }

    private string? _lastTestResult;
    public string? LastTestResult
    {
        get => _lastTestResult;
        set { if (_lastTestResult != value) { _lastTestResult = value; OnPropertyChanged(); } }
    }

    private bool _isFlightMode;
    public bool IsFlightMode
    {
        get => _isFlightMode;
        set { if (_isFlightMode != value) { _isFlightMode = value; OnPropertyChanged(); } }
    }

    public VoWifiDiagnosticInfo? VoWifiDiag
    {
        get
        {
            try
            {
                return VoWifi?.GetDiagnosticInfo();
            }
            catch
            {
                return null;
            }
        }
    }

    private readonly AsyncEventBus _eventBus;
    private readonly ConcurrentQueue<IncomingCallEventArgs> _pendingIncomingCalls = new();
    private readonly ConcurrentQueue<SmsReceivedEventArgs> _pendingReceivedSms = new();
    private int _nextHostImsSmsIndex;
    private readonly object _lock = new();
    private readonly object _cellularCallLock = new();
    private string? _cellularCallId;
    private string? _cellularCallerNumber;
    private CallState _cellularCallState = CallState.Idle;
    private DateTime? _cellularCallStartedAt;
    private DateTime? _cellularCallConnectedAt;
    private bool _cellularCallIsOutgoing;
    private CancellationTokenSource? _cellularCallMonitorCts;
    private Task? _cellularCallMonitorTask;
    private CancellationTokenSource? _qdc507MediaBootstrapCts;
    private bool _cellularUsbAudioAvailable;
    private bool _hardwareWasDetached;

    public bool CellularUsbAudioAvailable
    {
        get => _cellularUsbAudioAvailable;
        private set { if (_cellularUsbAudioAvailable != value) { _cellularUsbAudioAvailable = value; OnPropertyChanged(); } }
    }

    public bool HasCellularCall
    {
        get
        {
            lock (_cellularCallLock)
            {
                return _cellularCallState is CallState.Dialing or CallState.Incoming or CallState.Ringing or CallState.Active or CallState.Held;
            }
        }
    }

    internal bool IsCurrentCellularCall(string callId)
    {
        lock (_cellularCallLock)
            return string.Equals(_cellularCallId, callId, StringComparison.OrdinalIgnoreCase) && HasCellularCall;
    }

    public ModemSlot(
        string id,
        string portName,
        int baudRate = 115200,
        string? name = null,
        string? proxyUrl = null,
        AsyncEventBus? eventBus = null,
        SimIdentityHistoryStore? identityHistory = null, IkeRetryGate? ikeRetry = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        PortName = portName ?? throw new ArgumentNullException(nameof(portName));
        BaudRate = baudRate;
        Name = name ?? $"Slot {id} ({portName})";
        ProxyUrl = proxyUrl;
        _eventBus = eventBus ?? new AsyncEventBus();
        _identityHistory = identityHistory ?? new SimIdentityHistoryStore();

        VoWifi = new VoWifiManager(_eventBus, ikeRetry: ikeRetry, retryDeviceId: Id)
        {
            ProxyUrl = proxyUrl
        };
        VoWifi.SmsReceived += (_, e) => RaiseReceivedSms(new SmsReceivedEventArgs(e.Message, Id, e.SimBinding));
        Calls = new ImsCallManager(_eventBus);
        VoWifi.Calls = Calls;

        Calls.IncomingCall += (s, e) =>
        {
            RaiseIncomingCall(new IncomingCallEventArgs(e.CallId, e.CallerNumber, e.DisplayName, e.IsVoWifi, e.Timestamp, Id));
        };
        Calls.CallEnded += (_, _) =>
        {
            if (State == SlotState.Busy && !HasCellularCall)
                SetState(SlotState.Online);
        };
    }

    /// <summary>
    /// Attaches a SIM exposed through Windows PC/SC as a VoWiFi-only slot.  No
    /// serial modem is involved: the card itself supplies IMSI/ICCID and AKA.
    /// </summary>
    public async Task<bool> AttachPcscAsync(string readerName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerName);
        ct.ThrowIfCancellationRequested();

        try
        {
            if (Aka is IDisposable oldProvider)
                oldProvider.Dispose();

            var pcsc = new PcscAkaProvider(readerName);
            var iccid = await pcsc.ReadIccidAsync(ct).ConfigureAwait(false);
            var imsi = await pcsc.ReadImsiAsync(ct).ConfigureAwait(false);

            if (!await pcsc.CheckReadyAsync(iccid, ct).ConfigureAwait(false))
            {
                pcsc.Dispose();
                throw new InvalidOperationException("PC/SC reader found a card, but ADF.USIM is not available.");
            }

            Aka = pcsc;
            IsPcscReader = true;
            PcscReaderName = readerName;
            PortName = $"PC/SC · {readerName}";
            VoWifi.Modem = null;
            VoWifi.AkaProvider = pcsc;
            VoWifi.DeviceImei = VoWifiImei;
            Sim = SimIdentity.FromImsiAndIccid(imsi, iccid, opName: $"PLMN {imsi[..3]}-{imsi.Substring(3, 2)}", imsiSource: "PC/SC EF.IMSI");
            LastSeen = DateTime.UtcNow;
            SetState(SlotState.Online);
            try { SimChanged?.Invoke(this, new SimStateChangedEventArgs(Sim, 1, "READY", Id)); } catch { }
            _eventBus.Publish(EventTopics.SystemLog, "PC/SC",
                $"Slot {Id}: reader '{readerName}' ready; EF.ICCID/EF.IMSI verified; VoWiFi-only mode.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsPcscReader = true;
            PcscReaderName = readerName;
            SetError($"PC/SC reader '{readerName}' is unavailable or has no usable USIM: {ex.Message}");
            return false;
        }
    }

    private void SetState(SlotState newState)
    {
        var old = State;
        State = newState;
        try { StateChanged?.Invoke(this, new SlotStateChangedEventArgs(Id, old, newState, this)); } catch { }
    }

    internal void HandleCellularCallUrc(string rawLine)
    {
        var line = rawLine.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line)) return;

        var parsed = UrcParser.Parse(line);
        if (parsed?.Category == "CallIncoming" && parsed.ParsedData is string caller)
        {
            BeginOrUpdateCellularIncomingCall(caller);
            return;
        }

        if (parsed?.Category != "Call") return;
        var state = parsed.ParsedData?.ToString() ?? string.Empty;
        if (state.Equals("RING", StringComparison.OrdinalIgnoreCase))
        {
            BeginOrUpdateCellularIncomingCall("Unknown");
        }
        else if (state.Equals("NO CARRIER", StringComparison.OrdinalIgnoreCase))
        {
            // Quectel exposes IMS/data bearers alongside voice in +CLCC. A bare
            // NO CARRIER may belong to a mode=1 bearer, so verify that the
            // mode=0 call is actually gone before dismissing the call in UI.
            _ = ConfirmCellularCallEndedAsync(state);
        }
        else if (state is "BUSY" or "NO ANSWER" or "NO DIALTONE")
        {
            EndCellularCall(state);
        }
    }

    private async Task ConfirmCellularCallEndedAsync(string reason)
    {
        var modem = Modem;
        if (modem == null || !modem.IsOpen)
        {
            EndCellularCall(reason);
            return;
        }

        try
        {
            await Task.Delay(250).ConfigureAwait(false);
            var voiceCalls = await modem.GetVoiceCallsAsync().ConfigureAwait(false);
            if (voiceCalls.Count > 0) return;

            var ceer = await modem.SendRawAtCommandAsync("AT+CEER", 2000).ConfigureAwait(false);
            var detail = ceer.Success
                ? ceer.Lines.FirstOrDefault(line => line.StartsWith("+CEER:", StringComparison.OrdinalIgnoreCase))
                : null;
            EndCellularCall(string.IsNullOrWhiteSpace(detail) ? reason : $"{reason} ({detail})");
        }
        catch
        {
            // The CLCC monitor remains authoritative when a diagnostic query fails.
        }
    }

    private void BeginOrUpdateCellularIncomingCall(string callerNumber)
    {
        string callId;
        string number;
        CallState previousState;
        bool raiseStateChanged;
        bool raiseIncoming;

        lock (_cellularCallLock)
        {
            previousState = _cellularCallState;
            raiseStateChanged = previousState is CallState.Idle or CallState.Ended;
            if (raiseStateChanged)
            {
                _cellularCallId = Guid.NewGuid().ToString("N");
                _cellularCallStartedAt = DateTime.UtcNow;
                _cellularCallConnectedAt = null;
                _cellularCallerNumber = null;
                _cellularCallIsOutgoing = false;
            }

            var normalizedCaller = string.IsNullOrWhiteSpace(callerNumber) ? "Unknown" : callerNumber.Trim();
            var callerImproved = !normalizedCaller.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(_cellularCallerNumber, normalizedCaller, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(_cellularCallerNumber) || callerImproved)
                _cellularCallerNumber = normalizedCaller;

            _cellularCallState = CallState.Incoming;
            callId = _cellularCallId!;
            number = _cellularCallerNumber ?? "Unknown";
            raiseIncoming = raiseStateChanged || callerImproved;
        }

        if (raiseStateChanged)
        {
            try
            {
                CallStateChanged?.Invoke(this, new CallStateChangedEventArgs(
                    callId, number, previousState, CallState.Incoming, isOutgoing: false, slotId: Id));
            }
            catch { }
        }

        if (raiseIncoming)
        {
            RaiseIncomingCall(new IncomingCallEventArgs(callId, number, isVoWifi: false, slotId: Id));
        }

        EnsureCellularCallMonitor();
    }

    private void RaiseIncomingCall(IncomingCallEventArgs args)
    {
        _pendingIncomingCalls.Enqueue(args);
        FlushPendingEvents();
    }

    private void RaiseReceivedSms(SmsReceivedEventArgs args)
    {
        _pendingReceivedSms.Enqueue(args);
        FlushPendingEvents();
    }

    internal void FlushPendingEvents()
    {
        var incomingHandler = IncomingCall;
        if (incomingHandler != null)
        {
            while (_pendingIncomingCalls.TryDequeue(out var incoming))
            {
                try { incomingHandler.Invoke(this, incoming); } catch { }
            }
        }

        var smsHandler = SmsReceived;
        if (smsHandler != null)
        {
            while (_pendingReceivedSms.TryDequeue(out var sms))
            {
                try { smsHandler.Invoke(this, sms); } catch { }
            }
        }
    }

    private void EndCellularCall(string reason)
    {
        string? callId;
        string number;
        CallState previousState;
        DateTime? startedAt;
        DateTime? connectedAt;
        bool isOutgoing;

        lock (_cellularCallLock)
        {
            if (_cellularCallState is CallState.Idle or CallState.Ended) return;
            callId = _cellularCallId;
            number = _cellularCallerNumber ?? "Unknown";
            previousState = _cellularCallState;
            startedAt = _cellularCallStartedAt;
            connectedAt = _cellularCallConnectedAt;
            isOutgoing = _cellularCallIsOutgoing;
            _cellularCallState = CallState.Ended;
            _cellularCallId = null;
            _cellularCallerNumber = null;
            _cellularCallStartedAt = null;
            _cellularCallConnectedAt = null;
            _cellularCallIsOutgoing = false;
        }

        _cellularCallMonitorCts?.Cancel();
        var mediaBootstrapCts = Interlocked.Exchange(ref _qdc507MediaBootstrapCts, null);
        mediaBootstrapCts?.Cancel();
        mediaBootstrapCts?.Dispose();
        CellularUsbAudioAvailable = false;
        if (Modem != null && Modem.IsOpen)
        {
            // Restore the normal terminal state only after the call PCM route
            // has stopped. QDC507 needs DTR released for its active-call UAC.
            Modem.SetDataTerminalReady(true);
            _ = Modem.DisableUsbVoiceAudioAsync();
        }

        var endedAt = DateTime.UtcNow;
        var duration = connectedAt.HasValue
            ? endedAt - connectedAt.Value
            : startedAt.HasValue ? endedAt - startedAt.Value : TimeSpan.Zero;
        var id = callId ?? Guid.NewGuid().ToString("N");

        try
        {
            CallStateChanged?.Invoke(this, new CallStateChangedEventArgs(
                id, number, previousState, CallState.Ended, isOutgoing: isOutgoing, slotId: Id));
        }
        catch { }
        try { CallEnded?.Invoke(this, new CallEndedEventArgs(id, number, duration, reason, null, endedAt, Id)); } catch { }
        SetState(SlotState.Online);
    }

    /// <summary>
    /// Attaches to the physical or virtual modem on the configured serial COM port,
    /// performs AT handshake, queries IMEI/IMSI/ICCID, sets up AKA provider, and configures VoWiFi.
    /// </summary>
    public async Task<bool> AttachAsync(CancellationToken ct = default, bool initializeSms = true)
    {
        if (IsHostImsPppTransitionOrActive)
            throw new InvalidOperationException("The modem is owned by a Windows Host IMS PPP handoff.");
        lock (_lock)
        {
            LastError = null;
        }

        try
        {
            var driver = new ModemDriver(PortName, BaudRate, _eventBus) { ControlPreference = ControlPreference };
            driver.UrcReceived += (_, e) => HandleCellularCallUrc(e.UrcLine);
            driver.Open();

            if (!await driver.PingAsync(ct).ConfigureAwait(false))
            {
                SetError($"Modem on {PortName} did not respond to AT ping.");
                await driver.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            Modem = driver;
            VoWifi.Modem = driver;
            VoWifi.ProxyUrl = ProxyUrl;
            Sms = new SmsService(driver, _eventBus);
            Aka = new Ec25AkaProvider(driver.Session);

            driver.SignalChanged += (s, e) =>
            {
                if (IsFlightMode) return;
                Signal = e.Signal;
                try { SignalChanged?.Invoke(this, new SignalChangedEventArgs(e.Signal, Id)); } catch { }
            };
            driver.RegistrationChanged += (s, e) =>
            {
                if (IsFlightMode) return;
                Registration = e.Registration;
                try { RegistrationChanged?.Invoke(this, new NetworkRegistrationChangedEventArgs(e.Registration, e.OldStatus, e.NewStatus, Id)); } catch { }
            };
            driver.SimStateChanged += (s, e) =>
            {
                try { SimChanged?.Invoke(this, new SimStateChangedEventArgs(Sim, e.SimSlot, e.State, Id)); } catch { }
            };
            driver.FlightModeChanged += (s, e) =>
            {
                IsFlightMode = e.IsFlightMode;
            };
            Sms.SmsReceived += (s, e) =>
            {
                RaiseReceivedSms(new SmsReceivedEventArgs(e.Message, Id));
            };
            Sms.SmsSent += (s, e) =>
            {
                try { SmsSent?.Invoke(this, new SmsSentEventArgs(e.Result, Id)); } catch { }
            };
            Sms.StatusReportReceived += (s, e) =>
            {
                try { SmsStatusReportReceived?.Invoke(this, new SmsStatusReportEventArgs(e.Report, Id)); } catch { }
            };

            // Probe flight mode first to know actual hardware RF state
            try
            {
                var cfun = await driver.GetFlightModeAsync(ct).ConfigureAwait(false);
                IsFlightMode = (cfun == 4 || cfun == 0);
            }
            catch { }

            // Probe device metadata
            await driver.GetFirmwareRevisionAsync(ct).ConfigureAwait(false);
            Imei = await driver.GetImeiAsync(ct).ConfigureAwait(false);
            var modelResponse = await driver.SendRawAtCommandAsync("AT+CGMM", 2000, ct).ConfigureAwait(false);
            if (modelResponse.Success)
                DeviceModel = modelResponse.Lines.Select(line => line.Trim()).FirstOrDefault(line =>
                    line.Length > 0 && !line.StartsWith("AT", StringComparison.OrdinalIgnoreCase) && line != "OK");
            try { UsbContainerId = ModemDiscoveryPolicy.FindContainer(WindowsUsbTopology.EnumeratePresent(), PortName); }
            catch { } // Non-USB modems remain usable, without claiming verified USB pairing.
            var reportedImsi = await driver.GetImsiAsync(ct).ConfigureAwait(false);
            var permanentImsi = await driver.GetPermanentImsiAsync(ct).ConfigureAwait(false);
            var imsi = !string.IsNullOrWhiteSpace(permanentImsi) ? permanentImsi : reportedImsi;
            var iccid = await driver.GetIccidAsync(ct).ConfigureAwait(false);

            // A modem can answer AT and still be midway through SIM/eSIM
            // initialization. Do not publish a usable identity (and therefore
            // do not allow restored auto-VoWiFi) until CPIN, ICCID and ADF.USIM
            // have remained usable across two checks.
            if (!string.IsNullOrWhiteSpace(imsi) || !string.IsNullOrWhiteSpace(iccid))
            {
                try
                {
                    var readyIccid = await WaitForStartupUsimReadinessAsync(ct).ConfigureAwait(false);
                    iccid = readyIccid;
                    reportedImsi = await driver.GetImsiAsync(ct).ConfigureAwait(false);
                    permanentImsi = await driver.GetPermanentImsiAsync(ct).ConfigureAwait(false);
                    imsi = !string.IsNullOrWhiteSpace(permanentImsi) ? permanentImsi : reportedImsi;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _eventBus.Publish(EventTopics.SystemLog, "SIM",
                        $"Slot {Id}: startup SIM readiness timed out; VoWiFi remains blocked until a later retry. reason={ex.Message}");
                    imsi = string.Empty;
                }
            }
            if (!string.IsNullOrEmpty(imsi))
            {
                // The first CIMI/EF_IMSI pair only proves that the card answers.
                // Publish an identity only after the selected IMSI is repeated,
                // otherwise a multi-IMSI applet can move the UI and routing on a
                // single transient read during startup.
                try
                {
                    var identity = await ReadSimIdentityUntilReadyAsync(iccid, previousIccid: null, ct: ct)
                        .ConfigureAwait(false);
                    await InvalidateHostImsForIdentityChangeAsync(identity).ConfigureAwait(false);
                    SetVerifiedSimIdentity(identity);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _eventBus.Publish(EventTopics.SystemLog, "SIM",
                        $"Slot {Id}: startup IMSI did not become stable; modem stays online but VoWiFi remains blocked. reason={ex.Message}");
                }
            }

            // Read-only bearer experiments do not need to touch SMS storage.
            // Normal application attachment retains its established behavior.
            if (initializeSms)
            {
                try
                {
                    await driver.InitializeSmsModeAsync(ct).ConfigureAwait(false);

                    // Fetch any unread SMS on the card received while offline
                    var unreadMessages = await Sms.ListSmsAsync(SmsStatus.Unread, ct).ConfigureAwait(false);
                    foreach (var unreadMsg in unreadMessages)
                    {
                        Sms.ProcessDecodedSms(unreadMsg);
                        _ = Sms.DeleteSmsAsync(unreadMsg.Index, ct);
                    }
                }
                catch { }
            }

            // Radio metrics are meaningless while RF is disabled. They are
            // otherwise refreshed by the background telemetry loop after the
            // application has restored persisted preferences.
            if (IsFlightMode)
            {
                ClearRadioMetrics();
            }

            SetState(SlotState.Online);
            LastSeen = DateTime.UtcNow;
            if (_hardwareWasDetached && Sim != null)
            {
                _hardwareWasDetached = false;
                VoWifi.NotifyModemReattached(driver, VoWifiIdentityOverride ?? Sim);
            }
            _eventBus.Publish("slot.attached", "ModemPool", GetDiagnosticInfo());
            return true;
        }
        catch (Exception ex)
        {
            SetError($"Failed to attach to {PortName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Releases only the physical modem resources after USB removal. The
    /// independent VoWiFi IKE/ESP/IMS data plane remains alive until its own
    /// liveness checks determine that the network session has expired.
    /// </summary>
    public async Task DetachHardwareAsync(CancellationToken ct = default)
    {
        // CFUN reboot intentionally removes USB ports. The managed owner, not
        // generic hotplug handling, owns AT lifetime until handoff/restore ends.
        if (IsHostImsPppTransition) return;
        if (_hostImsPppLease is not null)
        {
            try { await StopHostImsPppBearerAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
                    $"Slot {Id}: PPP restoration after hardware detach failed: {ex.Message}");
            }
        }
        // Serialize actual disposal against a handoff beginning after the first check.
        // Never wait behind an intentional reboot and then replay a stale detach.
        if (!await _profileSwitchGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try { await DetachHardwareCoreAsync(ct).ConfigureAwait(false); }
        finally { _profileSwitchGate.Release(); }
    }

    private async Task DetachHardwareCoreAsync(CancellationToken ct)
    {
        InvalidateSmsIdentity();
        await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
        if (IsPcscReader)
        {
            if (Aka is IDisposable pcsc)
                pcsc.Dispose();
            Aka = SoftwareAkaProvider.FromTestVectors();
            VoWifi.AkaProvider = null;
            VoWifi.NotifyModemDetached();
            Sim = null;
            SetState(SlotState.Offline);
            _eventBus.Publish(EventTopics.SystemLog, "PC/SC", $"Slot {Id} reader detached; VoWiFi is blocked until the card is reinserted.");
            return;
        }

        var modem = Modem;
        if (modem == null && State == SlotState.Offline)
            return;

        _hardwareWasDetached = true;
        Modem = null;
        VoWifi.NotifyModemDetached();
        Sms = null;
        Signal = null;
        Registration = null;
        CellularUsbAudioAvailable = false;
        _cellularCallMonitorCts?.Cancel();
        if (HasCellularCall)
            EndCellularCall("Modem hardware removed");

        if (modem != null)
        {
            try { await modem.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        SetState(SlotState.Offline);
        _eventBus.Publish(EventTopics.SystemLog, "ModemPool",
            $"Slot {Id} hardware detached; retained its VoWiFi session state.");
    }

    /// <summary>
    /// Queries the modem for updated signal strength and network registration.
    /// </summary>
    public async Task RefreshMetricsAsync(CancellationToken ct = default)
    {
        // A profile switch owns the modem/SIM command channel until the new
        // EF.ICCID and IMSI are verified. Page navigation may trigger telemetry
        // refreshes; they must not interleave AT commands with that sequence.
        if (IsProfileSwitchInProgress) return;
        if (IsPcscReader)
        {
            if (Aka is not PcscAkaProvider pcsc || !await pcsc.CheckReadyAsync(Sim?.Iccid, ct).ConfigureAwait(false))
                await DetachHardwareAsync(ct).ConfigureAwait(false);
            else
                LastSeen = DateTime.UtcNow;
            return;
        }
        if (Modem == null || !Modem.IsOpen) return;
        if (Calls.ActiveCall != null) return; // avoid baseband collision during call

        try
        {
            if (Sim is not null)
                await ClearSimIfAbsentAsync(ct).ConfigureAwait(false);
            var cfun = await Modem.GetFlightModeAsync(ct).ConfigureAwait(false);
            IsFlightMode = (cfun == 4 || cfun == 0);
            if (IsFlightMode)
            {
                ClearRadioMetrics();
                LastSeen = DateTime.UtcNow;
                return;
            }
            Signal = await Modem.GetSignalAsync(ct).ConfigureAwait(false);
            Registration = await Modem.GetRegistrationAsync(ct).ConfigureAwait(false);
            LastSeen = DateTime.UtcNow;
        }
        catch { }
    }

    public HostImsPppBearerStatus GetHostImsPppBearerStatus()
    {
        var lease = Volatile.Read(ref _hostImsPppLease);
        return new HostImsPppBearerStatus(
            lease is not null && !lease.IsLost,
            lease is not null && !lease.IsLost ? lease.Bearer : null,
            Volatile.Read(ref _hostImsPppError),
            lease?.RenewalCount ?? 0,
            lease?.LastRenewedAtUtc,
            lease?.MediaRouteCount ?? 0) { DefaultRouteRepairCount = lease?.DefaultRouteRepairCount ?? 0 };
    }

    /// <summary>Explicit user retry releases a rejection pause, never an unexpired carrier timer.</summary>
    public void PrepareExplicitHostImsRetry()
    {
        var scope = Sim?.Iccid ?? throw new InvalidOperationException("A verified SIM is required before retrying Host IMS.");
        _hostImsRetryGate.ReleasePause(scope);
        _hostImsRetryGate.ThrowIfBlocked(scope);
    }

    /// <summary>Verify the existing P-CSCF /128 through the owner's media-route control path, without sending carrier traffic.</summary>
    public async Task CheckHostImsPppRouteControlAsync(CancellationToken ct = default)
    {
        var lease = Volatile.Read(ref _hostImsPppLease);
        if (lease?.Bearer is not { PcscfAddresses: { Count: > 0 } } bearer || lease.IsLost)
            throw new InvalidOperationException("No live isolated IMS PPP bearer is available for route-control verification.");
        await lease.EnsureMediaRouteAsync(bearer.PcscfAddresses[0], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit, experimental PPP ownership of the IMS bearer. No SIP packet is
    /// sent here. The guarded child owns modem reboot, route isolation and
    /// recovery; this slot releases AT only while the child needs it.
    /// </summary>
    public async Task<HostImsPppBearerStatus> StartHostImsPppBearerAsync(
        string handoffScript, string rasProbeExecutable, int maxLeaseSeconds = 3600,
        CancellationToken ct = default)
    {
        await _profileSwitchGate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _profileSwitchInProgress, 1);
        Interlocked.Exchange(ref _hostImsPppTransition, 1);
        ModemDriver? modem = null;
        WindowsHostImsPppBearerLease? lease = null;
        try
        {
            if (_hostImsPppLease is not null)
                throw new InvalidOperationException("This slot already owns a Host IMS PPP bearer; stop it first.");
            if (IsPcscReader || Sim is null || Modem is not { IsOpen: true })
                throw new InvalidOperationException("A connected modem with a verified SIM is required. " +
                    (IsPcscReader ? "读卡器仅支持 VoWiFi，请选择蜂窝模组。" :
                        Modem is not { IsOpen: true } ? "模组串口已断开，请重新扫描设备；保留的 SIM 信息不代表硬件仍在线。" :
                        "SIM 尚未完成校验，请等待识别或先解锁 PIN。"));
            _hostImsRetryGate.ThrowIfBlocked(Sim.Iccid);
            if (HasCellularCall || Calls.ActiveCall is not null || _hostImsRegistration is not null ||
                VoWifi.State is not (VoWifiState.Disconnected or VoWifiState.Failed))
                throw new InvalidOperationException("Stop calls, Host IMS registration and VoWiFi before changing the modem IMS mode.");
            modem = Modem;
            var expectedImei = await modem.GetImeiAsync(ct).ConfigureAwait(false);
            var expectedIccid = Sim.Iccid;
            var expectedImsi = Sim.Imsi;
            var expectedReportedImsi = await modem.GetImsiAsync(ct).ConfigureAwait(false);
            if (!await modem.CloseAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("The AT handle did not close cleanly; IMS handoff was not started.");

            lease = await WindowsHostImsPppBearerLease.StartAsync(
                handoffScript, rasProbeExecutable, modem.PortName, maxLeaseSeconds, ct: ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _applicationExitRequested) != 0)
                throw new OperationCanceledException("Application is exiting; do not reopen the modem.");
            modem.Open();
            if (!await modem.PingAsync(ct).ConfigureAwait(false) ||
                !string.Equals(await modem.GetImeiAsync(ct).ConfigureAwait(false), expectedImei, StringComparison.Ordinal) ||
                !string.Equals(await modem.GetIccidAsync(ct).ConfigureAwait(false), expectedIccid, StringComparison.Ordinal) ||
                !string.Equals(await modem.GetImsiAsync(ct).ConfigureAwait(false), expectedReportedImsi, StringComparison.Ordinal) ||
                !ReferenceEquals(Modem, modem) || Sim?.Iccid != expectedIccid || Sim.Imsi != expectedImsi)
                throw new InvalidOperationException("The modem or SIM identity changed during the IMS handoff.");
            var probe = await modem.ProbeHostImsPdnAsync(ct).ConfigureAwait(false);
            if (lease.Bearer is { } ownedBearer)
                probe = BindHostImsProbeToBearer(probe, ownedBearer);
            if (!probe.CanAttemptWindowsIms || lease.Bearer is not { } bearer ||
                !probe.EndpointCandidates.Any(endpoint =>
                    endpoint.LocalAddress.Equals(bearer.LocalAddress) &&
                    bearer.PcscfAddresses.Contains(endpoint.PcscfAddress) && probe.IsRouteVerified(endpoint)))
                throw new InvalidOperationException($"The Windows-owned IMS route did not match PPP READY: {probe.Summary}");

            Volatile.Write(ref _hostImsPppLease, lease);
            Volatile.Write(ref _hostImsPppError, null);
            _ = ObserveHostImsPppOwnerAsync(lease);
            return GetHostImsPppBearerStatus();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _hostImsPppError, ex.Message);
            if (lease is not null)
            {
                if (modem?.IsOpen == true)
                {
                    try { await modem.CloseAsync().ConfigureAwait(false); }
                    catch (Exception close) { Volatile.Write(ref _hostImsPppError, $"{ex.Message}; AT close: {close.Message}"); }
                }
                try { await ReleaseHostImsLeaseAsync(lease).ConfigureAwait(false); }
                catch (Exception restore) { Volatile.Write(ref _hostImsPppError, $"{ex.Message}; restore: {restore.Message}"); }
            }
            if (Volatile.Read(ref _applicationExitRequested) == 0 &&
                modem is { IsOpen: false } && ReferenceEquals(Modem, modem))
            {
                try { modem.Open(); } catch { }
            }
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _hostImsPppTransition, 0);
            Interlocked.Exchange(ref _profileSwitchInProgress, 0);
            _profileSwitchGate.Release();
        }
    }

    private async Task ObserveHostImsPppOwnerAsync(WindowsHostImsPppBearerLease lease)
    {
        await Task.WhenAny(lease.Lost, lease.Completion).ConfigureAwait(false);
        if (!ReferenceEquals(Volatile.Read(ref _hostImsPppLease), lease)) return;
        Volatile.Write(ref _hostImsLossKind, (int)HostImsLossKind.BearerLost);
        if (lease.Completion.IsCompleted)
        {
            try { await lease.Completion.ConfigureAwait(false); }
            catch (Exception ex) { Volatile.Write(ref _hostImsPppError, ex.Message); }
        }
        else Volatile.Write(ref _hostImsPppError,
            "The isolated IMS PPP bearer was lost; local SIP signaling is disabled during modem restoration.");
        if (ReferenceEquals(Volatile.Read(ref _hostImsPppLease), lease))
        {
            try { await StopHostImsPppBearerCoreAsync(expectedRegistration: null).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Volatile.Write(ref _hostImsLossKind, (int)HostImsLossKind.RestorationFailed);
                _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
                    $"Slot {Id}: PPP owner exit cleanup failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A terminal REGISTER refresh failure leaves no live IMS binding. Release
    /// the Windows PPP owner so modem-internal IMS/USB voice can take over.
    /// Yield first: the failure event is raised by the refresh task, and Stop
    /// must not await that same task before its event handler has returned.
    /// </summary>
    internal async Task RestoreHostImsAfterRefreshFailureAsync(
        HostImsRegistrationClient failedClient, string reason)
    {
        await Task.Yield();
        if (!ReferenceEquals(Volatile.Read(ref _hostImsRegistration), failedClient)) return;
        var failedGeneration = Volatile.Read(ref _hostImsRegistrationAttemptGeneration);
        Interlocked.CompareExchange(ref _hostImsLossKind, (int)HostImsLossKind.RegistrationRefreshFailed, (int)HostImsLossKind.None);
        try
        {
            var hadPppOwner = Volatile.Read(ref _hostImsPppLease) is not null;
            if (hadPppOwner)
            {
                if (!await StopHostImsPppBearerCoreAsync(failedClient).ConfigureAwait(false))
                    return; // Another registration or bearer superseded this one.
            }
            else
            {
                await _hostImsRegistrationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(Volatile.Read(ref _hostImsRegistration), failedClient)) return;
                    await StopHostImsRegistrationCoreAsync().ConfigureAwait(false);
                }
                finally { _hostImsRegistrationGate.Release(); }
            }
            var bearerError = hadPppOwner ? Volatile.Read(ref _hostImsPppError) : null;
            if (failedGeneration != Volatile.Read(ref _hostImsRegistrationAttemptGeneration)) return;
            if (bearerError is not null) Volatile.Write(ref _hostImsLossKind, (int)HostImsLossKind.RestorationFailed);
            Volatile.Write(ref _hostImsRegistrationError,
                $"Host IMS registration refresh failed: {reason}; Windows IMS session stopped" +
                (hadPppOwner ? bearerError is null
                    ? "; PPP owner recovery completed."
                    : $"; PPP owner recovery reported: {bearerError}"
                    : "."));
        }
        catch (Exception ex)
        {
            if (failedGeneration != Volatile.Read(ref _hostImsRegistrationAttemptGeneration)) return;
            Volatile.Write(ref _hostImsLossKind, (int)HostImsLossKind.RestorationFailed);
            Volatile.Write(ref _hostImsRegistrationError,
                $"Host IMS registration refresh failed: {reason}; automatic fallback reported: {ex.Message}");
        }
    }

    public async Task StopHostImsPppBearerAsync()
    {
        NotifyHostImsManualStop();
        await StopHostImsPppBearerCoreAsync(expectedRegistration: null).ConfigureAwait(false);
    }

    private int _applicationExitRequested;
    private WindowsHostImsPppBearerLease? _hostImsStoppingLease;
    public void PrepareForApplicationExit()
    {
        Volatile.Write(ref _applicationExitRequested, 1);
        Volatile.Read(ref _hostImsPppLease)?.AllowBackgroundCleanupOnApplicationExit();
        Volatile.Read(ref _hostImsStoppingLease)?.AllowBackgroundCleanupOnApplicationExit();
        NotifyHostImsManualStop();
    }

    private async Task ReleaseHostImsLeaseAsync(WindowsHostImsPppBearerLease lease)
    {
        // Keep an in-progress explicit stop reachable by application exit too.
        // The active lease may already have been removed before the user exits.
        Volatile.Write(ref _hostImsStoppingLease, lease);
        try
        {
            if (Volatile.Read(ref _applicationExitRequested) != 0)
                lease.AllowBackgroundCleanupOnApplicationExit();
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        finally { Interlocked.CompareExchange(ref _hostImsStoppingLease, null, lease); }
    }

    /// <summary>Unwind an owner's failed automatic attempt without inventing new user stop intent.</summary>
    public async Task AbortHostImsRecoveryAttemptAsync()
    {
        try
        {
            if (!await StopHostImsPppBearerCoreAsync(expectedRegistration: null).ConfigureAwait(false))
                await StopHostImsRegistrationWithSignalingAsync().ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _hostImsLossKind, (int)HostImsLossKind.RestorationFailed);
            throw;
        }
    }

    private async Task<bool> StopHostImsPppBearerCoreAsync(
        HostImsRegistrationClient? expectedRegistration,
        WindowsHostImsPppBearerLease? expectedFailedRegistrationLease = null,
        long? expectedFailedRegistrationGeneration = null)
    {
        await _profileSwitchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hostImsPppLease is null ||
                (expectedRegistration is not null &&
                 !ReferenceEquals(Volatile.Read(ref _hostImsRegistration), expectedRegistration)))
                return false;
            if (expectedFailedRegistrationLease is not null)
            {
                // Serialize the stale-attempt check with registration startup.
                // Once transition is set, a queued start cannot publish a new
                // client even after this gate is released.
                await _hostImsRegistrationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(_hostImsPppLease, expectedFailedRegistrationLease) ||
                        expectedFailedRegistrationGeneration !=
                            Volatile.Read(ref _hostImsRegistrationAttemptGeneration) ||
                        Volatile.Read(ref _hostImsRegistration) is not null)
                        return false;
                    Interlocked.Exchange(ref _hostImsPppTransition, 1);
                }
                finally { _hostImsRegistrationGate.Release(); }
            }
            else Interlocked.Exchange(ref _hostImsPppTransition, 1);
            // SIP de-registration must happen before the transition flag makes
            // the current route ineligible for signaling. A failed SIP cleanup
            // must never strand the PPP owner with modem-internal IMS disabled.
            List<Exception> stopErrors = [];
            try
            {
                if (_hostImsPppLease?.IsLost == true)
                    await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
                else
                    await StopHostImsRegistrationWithSignalingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                stopErrors.Add(ex);
                // Close any still-live SIP transport locally before removing
                // its bearer, even if remote de-registration failed.
                try { await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false); }
                catch (Exception cleanupError) { stopErrors.Add(cleanupError); }
            }
            Interlocked.Exchange(ref _profileSwitchInProgress, 1);
            Volatile.Write(ref _hostImsStoppingLease, Volatile.Read(ref _hostImsPppLease));
            var lease = Interlocked.Exchange(ref _hostImsPppLease, null);
            if (lease is null) return false;
            var modem = Modem;
            try
            {
                Exception? closeError = null;
                if (modem?.IsOpen == true)
                {
                    try
                    {
                        if (!await modem.CloseAsync().ConfigureAwait(false))
                            closeError = new InvalidOperationException("The AT handle did not close cleanly before IMS restoration.");
                    }
                    catch (Exception ex) { closeError = ex; }
                }
                Exception? restoreError = null;
                try { await ReleaseHostImsLeaseAsync(lease).ConfigureAwait(false); }
                catch (Exception ex) { restoreError = ex; }
                if (closeError is not null) stopErrors.Add(closeError);
                if (restoreError is not null) stopErrors.Add(restoreError);
                if (stopErrors.Count == 1) throw stopErrors[0];
                if (stopErrors.Count > 1) throw new AggregateException(stopErrors);
                Volatile.Write(ref _hostImsPppError, null);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _hostImsPppError, ex.Message);
                throw;
            }
            finally
            {
                if (Volatile.Read(ref _applicationExitRequested) == 0 &&
                    modem is { IsOpen: false } && ReferenceEquals(Modem, modem))
                {
                    try { modem.Open(); } catch (Exception ex) { Volatile.Write(ref _hostImsPppError, ex.Message); }
                }
            }
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _hostImsPppTransition, 0);
            Interlocked.Exchange(ref _profileSwitchInProgress, 0);
            _profileSwitchGate.Release();
        }
    }

    public HostImsRegistrationStatus GetHostImsRegistrationStatus()
    {
        var client = Volatile.Read(ref _hostImsRegistration);
        var error = Volatile.Read(ref _hostImsRegistrationError);
        var remoteDeregistration = Volatile.Read(ref _hostImsRemoteDeregistration);
        var bearer = Volatile.Read(ref _hostImsPppLease);
        var registered = client?.IsRegistered == true && HostImsCardMatchesCurrentSlot() &&
            !IsProfileSwitchInProgress && (bearer is null || !bearer.IsLost);
        var result = registered ? client?.CurrentResult : null;
        return new HostImsRegistrationStatus(
            registered,
            registered ? "registered" : client is null && remoteDeregistration >= 0
                ? "stopped" : error is not null ? "failed" : client is not null ? "inactive" : "stopped",
            client?.Endpoint.Display,
            result?.RegisteredAt,
            result is null ? null : result.RegisteredAt.AddSeconds(result.ExpiresSeconds),
            error,
            remoteDeregistration < 0 ? null : remoteDeregistration == 1,
            client?.CardPath,
            registered && client?.CanReceiveSmsOverIms == true)
        {
            Failure = Sim is { } sim ? _hostImsRetryGate.GetFailure(sim.Iccid) : null,
            RetryNotBeforeUtc = Sim is { } currentSim ? _hostImsRetryGate.RetryNotBeforeUtc(currentSim.Iccid) : null,
            AutomaticRecoveryPaused = Sim is { } card && _hostImsRetryGate.IsPaused(card.Iccid),
            LossKind = (HostImsLossKind)Volatile.Read(ref _hostImsLossKind)
        };
    }

    public async Task<HostImsRegistrationStatus> StartHostImsRegistrationAsync(
        HostImsEndpointCandidate? selectedEndpoint = null,
        CancellationToken ct = default,
        IHostImsSecurityBackend? securityBackend = null,
        Action<string>? diagnosticLog = null,
        bool enableSmsReceiver = false)
    {
        var attemptGeneration = Interlocked.Increment(ref _hostImsRegistrationAttemptGeneration);
        try
        {
            return await StartHostImsRegistrationCoreAsync(
                selectedEndpoint, ct, securityBackend, diagnosticLog,
                enableSmsReceiver).ConfigureAwait(false);
        }
        catch (Exception registrationError)
        {
            // The core has released the registration gate before control gets
            // here. Roll back only this failed attempt's still-unregistered
            // PPP owner; an existing or newly published session must survive.
            var failedLease = Volatile.Read(ref _hostImsPppLease);
            if (failedLease is not null && Volatile.Read(ref _hostImsRegistration) is null)
            {
                try
                {
                    if (await StopHostImsPppBearerCoreAsync(
                            expectedRegistration: null,
                            expectedFailedRegistrationLease: failedLease,
                            expectedFailedRegistrationGeneration: attemptGeneration).ConfigureAwait(false))
                        Volatile.Write(ref _hostImsRegistrationError,
                            $"{registrationError.Message}; PPP stopped and modem-internal IMS restoration requested.");
                }
                catch (Exception rollbackError)
                {
                    Volatile.Write(ref _hostImsRegistrationError,
                        $"{registrationError.Message}; PPP fallback failed: {rollbackError.Message}");
                    throw new AggregateException(registrationError, rollbackError);
                }
            }
            throw;
        }
    }

    private async Task<HostImsRegistrationStatus> StartHostImsRegistrationCoreAsync(
        HostImsEndpointCandidate? selectedEndpoint,
        CancellationToken ct,
        IHostImsSecurityBackend? securityBackend,
        Action<string>? diagnosticLog,
        bool enableSmsReceiver)
    {
        await _hostImsRegistrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hostImsRegistration?.IsRegistered == true && HostImsCardMatchesCurrentSlot())
            {
                if (securityBackend is not null ||
                    _hostImsRegistration.CanReceiveSmsOverIms != enableSmsReceiver)
                    throw new InvalidOperationException("Stop the existing Host IMS registration before changing its security backend or SMS receiver capability.");
                return GetHostImsRegistrationStatus();
            }
            await StopHostImsRegistrationCoreAsync().ConfigureAwait(false);

            if (IsPcscReader || IsProfileSwitchInProgress || Volatile.Read(ref _hostImsPppTransition) != 0 ||
                Sim is null || Modem is not { IsOpen: true } modem)
                throw new InvalidOperationException("Host IMS registration requires a connected modem and a stable selected SIM.");
            var selectedSim = Sim;
            var expectedIccid = selectedSim.Iccid;
            var expectedImsi = selectedSim.Imsi;
            var pppLease = Volatile.Read(ref _hostImsPppLease);
            if (!IsHostImsPppOwnerCurrent(pppLease))
                throw new InvalidOperationException("The managed IMS bearer owner is no longer available; registration was refused.");
            var probe = await modem.ProbeHostImsPdnAsync(ct).ConfigureAwait(false);
            if (!IsHostImsPppOwnerCurrent(pppLease))
                throw new InvalidOperationException("The managed IMS bearer owner changed during the IMS probe.");
            var pppBearer = pppLease?.Bearer;
            if (pppBearer is not null)
                probe = BindHostImsProbeToBearer(probe, pppBearer);
            if (!probe.CanAttemptWindowsIms)
                throw new InvalidOperationException($"Windows does not own an active IMS bearer: {probe.Readiness}; {probe.Summary}");
            var candidates = probe.EndpointCandidates.Where(probe.IsRouteVerified).ToArray();
            var endpoint = SelectHostImsEndpoint(candidates, selectedEndpoint, pppBearer, probe.RouteChecks);
            if (endpoint is null || !candidates.Contains(endpoint))
                throw new InvalidOperationException("Select one current IMS address/P-CSCF candidate before registration.");

            var client = await HostImsRegistrationClient.RegisterWithCardAsync(
                modem, selectedSim, probe, endpoint, ct: ct,
                securityBackend: securityBackend, diagnosticLog: diagnosticLog,
                enableSmsReceiver: enableSmsReceiver, retryGate: _hostImsRetryGate).ConfigureAwait(false);
            if (!IsHostImsPppOwnerCurrent(pppLease))
            {
                client.Invalidate();
                await client.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The managed IMS bearer owner changed during registration.");
            }
            if (Volatile.Read(ref _hostImsPppLease) is { } mediaLease &&
                !mediaLease.Completion.IsCompleted)
            {
                if (mediaLease.Bearer is not { } mediaBearer ||
                    !mediaBearer.LocalAddress.Equals(endpoint.LocalAddress) ||
                    !mediaBearer.PcscfAddresses.Contains(endpoint.PcscfAddress) ||
                    client.InterfaceIndex != mediaBearer.InterfaceIndex ||
                    client.Transport.BoundInterfaceIndex != mediaBearer.InterfaceIndex ||
                    mediaBearer.InterfaceLuid is { } mediaLuid && client.InterfaceLuid != mediaLuid)
                {
                    client.Invalidate();
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw new InvalidOperationException("Host IMS registration did not use the active PPP bearer; media routing was refused.");
                }
                client.AttachMediaRouteProvisioner(mediaLease.EnsureMediaRouteAsync);
                client.AttachMediaRouteClearer(mediaLease.ClearMediaRoutesAsync);
            }
            if (IsProfileSwitchInProgress || Volatile.Read(ref _hostImsPppTransition) != 0 ||
                !IsHostImsPppOwnerCurrent(pppLease) ||
                !ReferenceEquals(Modem, modem) ||
                Sim?.Iccid != expectedIccid || Sim.Imsi != expectedImsi)
            {
                client.Invalidate();
                await client.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The selected SIM or modem changed during Host IMS registration.");
            }

            client.Registration.RegistrationFailed += (_, error) =>
            {
                if (!ReferenceEquals(Volatile.Read(ref _hostImsRegistration), client)) return;
                Volatile.Write(ref _hostImsRegistrationError, error);
                _ = RestoreHostImsAfterRefreshFailureAsync(client, error);
            };
            client.Transport.IncomingRequestReceived += OnHostImsIncomingRequest;
            _hostImsIncomingSmsHandler = incoming => OnHostImsSmsReceived(client, expectedIccid, incoming);
            client.IncomingSmsReceived += _hostImsIncomingSmsHandler;
            client.SmsStatusReportReceived += OnHostImsSmsStatusReportReceived;
            client.SmsDeliveryReportAccepted += OnHostImsSmsDeliveryReportAccepted;
            _hostImsRegisteredModem = modem;
            _hostImsRegisteredIccid = expectedIccid;
            _hostImsRegisteredImsi = expectedImsi;
            Volatile.Write(ref _hostImsRegistrationError, null);
            Volatile.Write(ref _hostImsRegistration, client);
            Volatile.Write(ref _hostImsLossKind, (int)HostImsLossKind.None);
            if (IsProfileSwitchInProgress || Volatile.Read(ref _hostImsPppTransition) != 0 ||
                !IsHostImsPppOwnerCurrent(pppLease) ||
                !ReferenceEquals(Modem, modem) ||
                Sim?.Iccid != expectedIccid || Sim.Imsi != expectedImsi)
            {
                await StopHostImsRegistrationCoreAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The selected SIM or modem changed during Host IMS registration.");
            }
            // Refreshing begins in RegisterWithCardAsync, before this slot can
            // attach its failure callback. Catch an unusually short grant that
            // expired in that handoff window and release its PPP owner too.
            if (!client.IsRegistered)
            {
                const string error = "Host IMS registration expired during slot activation.";
                _ = RestoreHostImsAfterRefreshFailureAsync(client, error);
                throw new InvalidOperationException(error);
            }
            return GetHostImsRegistrationStatus();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _hostImsRegistrationError, ex.Message);
            throw;
        }
        finally { _hostImsRegistrationGate.Release(); }
    }

    public static HostImsEndpointCandidate? SelectHostImsEndpoint(
        IReadOnlyList<HostImsEndpointCandidate> routeVerifiedCandidates,
        HostImsEndpointCandidate? selectedEndpoint,
        WindowsHostImsPppBearerInfo? pppBearer,
        IReadOnlyList<HostImsRouteCheck>? routeChecks = null)
    {
        if (pppBearer is not null)
        {
            if (pppBearer.InterfaceIndex <= 0 || pppBearer.InterfaceLuid is 0) return null;
            // The managed CID 5 lease has already verified the Windows address and
            // P-CSCF host routes. Respect the modem's advertised P-CSCF order.
            var owned = routeVerifiedCandidates.Where(candidate =>
                candidate.ContextId == 5 && candidate.LocalAddress.Equals(pppBearer.LocalAddress) &&
                pppBearer.PcscfAddresses.Contains(candidate.PcscfAddress) &&
                routeChecks?.Any(route => route.Endpoint == candidate && route.IsVerified &&
                    route.InterfaceIndex == pppBearer.InterfaceIndex &&
                    (pppBearer.InterfaceLuid is null || route.InterfaceLuid == pppBearer.InterfaceLuid)) == true).ToArray();
            if (selectedEndpoint is not null)
                return owned.Contains(selectedEndpoint) ? selectedEndpoint : null;
            foreach (var pcscf in pppBearer.PcscfAddresses)
            {
                var match = owned.FirstOrDefault(candidate =>
                    candidate.PcscfAddress.Equals(pcscf));
                if (match is not null) return match;
            }
            return null;
        }
        if (selectedEndpoint is not null)
            return routeVerifiedCandidates.Contains(selectedEndpoint) ? selectedEndpoint : null;
        return routeVerifiedCandidates.Count == 1 ? routeVerifiedCandidates[0] : null;
    }

    private static HostImsProbeResult BindHostImsProbeToBearer(HostImsProbeResult probe,
        WindowsHostImsPppBearerInfo bearer) => probe with
        {
            OwnedInterfaceIndex = bearer.InterfaceIndex,
            OwnedInterfaceLuid = bearer.InterfaceLuid,
            RouteChecks = HostImsRouteVerifier.Check(probe.EndpointCandidates,
                bearer.InterfaceIndex, bearer.InterfaceLuid)
        };

    internal bool IsHostImsPppOwnerCurrent(WindowsHostImsPppBearerLease? expected) =>
        ReferenceEquals(Volatile.Read(ref _hostImsPppLease), expected) &&
        (expected is null || !expected.IsLost && !expected.Completion.IsCompleted && expected.Bearer is not null);

    public async Task StopHostImsRegistrationAsync()
    {
        NotifyHostImsManualStop();
        await StopHostImsRegistrationWithSignalingAsync().ConfigureAwait(false);
    }

    private async Task StopHostImsRegistrationWithSignalingAsync()
    {
        // A dial attempt holds the registration gate until INVITE completes.
        // End it before waiting for that gate, otherwise Stop can stall for
        // the entire SIP transaction timeout while the bearer keeps sending.
        if (Calls.IsHostImsCall)
        {
            var canSignalNow = CanSignalHostImsDuringStop();
            try { await Calls.HangupAsync(sendSignaling: canSignalNow).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
                    $"Slot {Id}: call hangup before IMS de-registration failed: {ex.Message}");
            }
        }
        await _hostImsRegistrationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var canSignal = CanSignalHostImsDuringStop();
            if (Calls.IsHostImsCall)
            {
                try { await Calls.HangupAsync(sendSignaling: canSignal).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
                        $"Slot {Id}: call hangup before IMS de-registration failed: {ex.Message}");
                }
            }
            var client = Volatile.Read(ref _hostImsRegistration);
            if (client is null)
            {
                // PPP stop already released this registration. Do not erase a
                // confirmed (or failed) remote de-registration on an idempotent
                // second stop; a new registration flow clears it explicitly.
                if (Volatile.Read(ref _hostImsRemoteDeregistration) < 0)
                    Volatile.Write(ref _hostImsRegistrationError, null);
                return;
            }
            var hadRegistration = client?.IsRegistered == true;
            var remoteDeregistered = false;
            string? deregistrationError = null;
            if (hadRegistration && canSignal)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    remoteDeregistered = await client!.DeregisterAsync(timeout.Token).ConfigureAwait(false);
                    if (!remoteDeregistered)
                        deregistrationError = "Remote SIP de-registration was not confirmed because the session became inactive.";
                }
                catch (Exception ex) { deregistrationError = $"Remote SIP de-registration was not confirmed: {ex.Message}"; }
            }
            else if (hadRegistration)
            {
                deregistrationError = "Remote SIP de-registration was skipped because the selected modem or SIM is unavailable.";
            }

            await StopHostImsRegistrationCoreAsync().ConfigureAwait(false);
            if (hadRegistration)
            {
                Volatile.Write(ref _hostImsRemoteDeregistration, remoteDeregistered ? 1 : 0);
                Volatile.Write(ref _hostImsRegistrationError, deregistrationError);
            }
        }
        finally { _hostImsRegistrationGate.Release(); }
    }

    private async Task StopHostImsRegistrationLocallyAsync()
    {
        InvalidateSmsIdentity();
        if (Calls.IsHostImsCall)
        {
            try { await Calls.HangupAsync(sendSignaling: false).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
                    $"Slot {Id}: local call cleanup before bearer invalidation failed: {ex.Message}");
            }
        }
        // Closing the obsolete bearer also interrupts an in-flight INVITE,
        // so card removal does not wait for a 30-second SIP transaction while
        // the old socket remains open.
        Volatile.Read(ref _hostImsRegistration)?.Invalidate();
        await _hostImsRegistrationGate.WaitAsync().ConfigureAwait(false);
        try { await StopHostImsRegistrationCoreAsync().ConfigureAwait(false); }
        finally { _hostImsRegistrationGate.Release(); }
    }

    private async Task StopHostImsRegistrationCoreAsync()
    {
        if (Calls.IsHostImsCall)
        {
            try { await Calls.HangupAsync(sendSignaling: false).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
                    $"Slot {Id}: call cleanup before IMS registration stop failed: {ex.Message}");
            }
        }
        var client = Interlocked.Exchange(ref _hostImsRegistration, null);
        _hostImsRegisteredModem = null;
        _hostImsRegisteredIccid = null;
        _hostImsRegisteredImsi = null;
        if (client is not null)
        {
            client.Transport.IncomingRequestReceived -= OnHostImsIncomingRequest;
            if (_hostImsIncomingSmsHandler is { } smsHandler) client.IncomingSmsReceived -= smsHandler;
            _hostImsIncomingSmsHandler = null;
            client.SmsStatusReportReceived -= OnHostImsSmsStatusReportReceived;
            client.SmsDeliveryReportAccepted -= OnHostImsSmsDeliveryReportAccepted;
            client.Invalidate();
            await client.DisposeAsync().ConfigureAwait(false);
        }
        Volatile.Write(ref _hostImsRegistrationError, null);
        Volatile.Write(ref _hostImsRemoteDeregistration, -1);
    }

    private bool HostImsCardMatchesCurrentSlot() =>
        Modem is { IsOpen: true } modem &&
        ReferenceEquals(modem, _hostImsRegisteredModem) &&
        Sim is { } sim &&
        sim.Iccid == _hostImsRegisteredIccid &&
        sim.Imsi == _hostImsRegisteredImsi;

    private bool CanSignalHostImsDuringStop() =>
        HostImsCardMatchesCurrentSlot() && !IsProfileSwitchInProgress &&
        Volatile.Read(ref _hostImsPppLease)?.IsLost != true;

    internal static bool IsPppOwnerAvailableForSignaling(Task<int>? ownerCompletion) =>
        ownerCompletion is null || !ownerCompletion.IsCompleted;

    private void OnHostImsSmsReceived(HostImsRegistrationClient source, string sourceIccid, IncomingSms incoming)
    {
        if (!ReferenceEquals(Volatile.Read(ref _hostImsRegistration), source) || !source.IsRegistered ||
            !HostImsCardMatchesCurrentSlot()) return;
        var message = new SmsMessage(
            Index: -Interlocked.Increment(ref _nextHostImsSmsIndex),
            Status: SmsStatus.Unread,
            SenderOrRecipient: incoming.SenderNumber,
            Text: incoming.Text,
            Timestamp: incoming.Timestamp,
            RawPdu: incoming.RawPdu,
            Direction: SmsDirection.Received,
            ServiceCenterTimestamp: incoming.ServiceCenterTimestamp,
            Concat: incoming.Concat,
            Id: $"host-ims-{Guid.NewGuid():N}");
        RaiseReceivedSms(new SmsReceivedEventArgs(message, Id, SmsSubscriptionIdentity.ForIccid(sourceIccid)));
    }

    private void OnHostImsSmsStatusReportReceived(SmsStatusReport report)
    {
        if (Volatile.Read(ref _hostImsRegistration)?.IsRegistered != true ||
            !HostImsCardMatchesCurrentSlot()) return;
        try { SmsStatusReportReceived?.Invoke(this, new SmsStatusReportEventArgs(report, Id)); }
        catch { }
    }

    private void OnHostImsSmsDeliveryReportAccepted(int sipCode) =>
        _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
            $"Slot {Id}: SMS RP delivery report acknowledged by network with SIP {sipCode}.");

    private void OnHostImsIncomingRequest(object? sender, SipMessage request)
    {
        var client = Volatile.Read(ref _hostImsRegistration);
        if (client is null || !ReferenceEquals(sender, client.Transport)) return;
        _ = RouteHostImsIncomingRequestAsync(client, request);
    }

    private async Task RouteHostImsIncomingRequestAsync(
        HostImsRegistrationClient client, SipMessage request)
    {
        var method = request.Method.ToUpperInvariant();
        if (method is "INVITE" or "PRACK" or "UPDATE" or "CANCEL" or "BYE" or "ACK")
            _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
                $"Slot {Id}: incoming SIP {method}; call-state={Calls.State}; registered={client.IsRegistered}; no headers or subscriber identity logged.");
        if (!ReferenceEquals(Volatile.Read(ref _hostImsRegistration), client) ||
            !client.IsRegistered || !HostImsCardMatchesCurrentSlot() || IsProfileSwitchInProgress)
            return;

        Task Reply(SipMessage response) => client.Transport.SendAsync(response);
        try
        {
            switch (method)
            {
                case "INVITE":
                    await Calls.HandleIncomingInviteAsync(request, client, Reply).ConfigureAwait(false);
                    break;
                case "CANCEL":
                    await Calls.HandleIncomingCancelAsync(request, Reply).ConfigureAwait(false);
                    break;
                case "BYE":
                    if (Calls.State is CallState.Active or CallState.Held)
                        await Calls.HandleIncomingByeAsync(request, Reply).ConfigureAwait(false);
                    else
                        await Reply(request.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
                    break;
                case "UPDATE":
                    if (Calls.State is CallState.Incoming or CallState.Dialing or CallState.Ringing or CallState.Active or CallState.Held)
                        await Calls.HandleIncomingUpdateAsync(request, Reply).ConfigureAwait(false);
                    else
                        await Reply(request.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
                    break;
                case "MESSAGE":
                    await client.HandleIncomingSmsReportAsync(request, Reply).ConfigureAwait(false);
                    break;
                case "PRACK":
                    await Calls.HandleIncomingPrackAsync(request, Reply).ConfigureAwait(false);
                    break;
                case "ACK":
                    Calls.HandleIncomingAck(request);
                    break;
                case "OPTIONS":
                case "NOTIFY":
                    await Reply(request.CreateResponse(200, "OK")).ConfigureAwait(false);
                    break;
                default:
                    await Reply(request.CreateResponse(405, "Method Not Allowed")).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _eventBus.Publish(EventTopics.SystemLog, "Host IMS",
                $"Slot {Id}: incoming SIP {request.Method} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Refreshes SIM identity (e.g. after profile or slot switch) by querying IMSI and ICCID.
    /// </summary>
    public async Task<bool> RefreshSimAsync(CancellationToken ct = default)
        => await RunSmsSourceMutationAsync(RefreshSimCoreAsync, ct).ConfigureAwait(false);

    private async Task<bool> RefreshSimCoreAsync(CancellationToken ct)
    {
        InvalidateSmsIdentity();
        if (IsPcscReader)
        {
            if (Aka is not PcscAkaProvider pcsc)
            {
                SetState(SlotState.Offline);
                return false;
            }
            var iccid = await pcsc.ReadIccidAsync(ct).ConfigureAwait(false);
            var imsi = await pcsc.ReadImsiAsync(ct).ConfigureAwait(false);
            Sim = SimIdentity.FromImsiAndIccid(imsi, iccid, opName: $"PLMN {imsi[..3]}-{imsi.Substring(3, 2)}", imsiSource: "PC/SC EF.IMSI");
            LastSeen = DateTime.UtcNow;
            try { SimChanged?.Invoke(this, new SimStateChangedEventArgs(Sim, 1, "READY", Id)); } catch { }
            return true;
        }
        if (Modem == null || !Modem.IsOpen) return false;

        try
        {
            // RefreshSimAsync cycles CFUN and invalidates the cellular IMS bearer.
            // Tear down the old Host SIP/media locally before changing the baseband.
            await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
            if (!await Modem.RefreshSimAsync(ct, preserveFlightMode: IsFlightMode).ConfigureAwait(false))
            {
                await ClearSimIfAbsentAsync(ct).ConfigureAwait(false);
                return false;
            }
            var expectedIccid = await WaitForStartupUsimReadinessAsync(ct).ConfigureAwait(false);
            var identity = await ReadSimIdentityUntilReadyAsync(expectedIccid, previousIccid: null, ct: ct)
                .ConfigureAwait(false);
            await InvalidateHostImsForIdentityChangeAsync(identity).ConfigureAwait(false);
            SetVerifiedSimIdentity(identity);
            if (!IsFlightMode)
            {
                try
                {
                    Signal = await Modem.GetSignalAsync(ct).ConfigureAwait(false);
                    Registration = await Modem.GetRegistrationAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _eventBus.Publish(EventTopics.SystemLog, "SIM",
                        $"Slot {Id}: SIM identity verified, but follow-up radio metrics failed: {ex.Message}");
                }
            }
            LastSeen = DateTime.UtcNow;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ClearSimIfAbsentAsync(ct).ConfigureAwait(false);
            _eventBus.Publish(EventTopics.SystemLog, "SIM",
                $"Slot {Id}: SIM identity refresh failed; previous identity is retained only if card absence was not confirmed. reason={ex.Message}");
            return false;
        }
    }

    private async Task ClearSimIfAbsentAsync(CancellationToken ct)
    {
        var modem = Modem;
        var previousIdentity = Sim;
        if (previousIdentity is null || modem is not { IsOpen: true } || IsProfileSwitchInProgress)
            return;
        bool? inserted;
        try { inserted = await modem.GetSimInsertedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return; } // Unknown card state must not erase a verified identity.
        if (inserted != false || !ReferenceEquals(Modem, modem) || IsProfileSwitchInProgress)
            return;

        await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
        if (!ReferenceEquals(Sim, previousIdentity) || !ReferenceEquals(Modem, modem) || IsProfileSwitchInProgress)
            return;
        ClearConfirmedAbsentSimIdentity();
    }

    internal void ClearConfirmedAbsentSimIdentity()
    {
        if (Sim is null) return;
        Sim = null;
        VoWifiIdentityOverride = null;
        LastReportedImsi = null;
        LastPermanentImsi = null;
        ImsiIdentitySource = "unknown";
        _stableRoutingIccid = null;
        StableRoutingMcc = null;
        HasImsiPlmnConflict = false;
        ClearRadioMetrics();
        try { SimChanged?.Invoke(this, new SimStateChangedEventArgs(null, 1, "ABSENT", Id)); } catch { }
        _eventBus.Publish(EventTopics.SystemLog, "SIM",
            $"Slot {Id}: selected SIM is absent; stale identity and Host IMS session were cleared.");
    }

    /// <summary>
    /// Waits for a stable initial USIM before an attached slot is exposed to
    /// preference restoration or automatic VoWiFi. If the module cannot read
    /// EF.ICCID directly, one guarded CFUN reload refreshes its cache before a
    /// compatibility fallback is accepted.
    /// </summary>
    private async Task<string> WaitForStartupUsimReadinessAsync(CancellationToken ct)
    {
        if (Aka is not Ec25AkaProvider ec25 || Modem == null || !Modem.IsOpen)
            throw new InvalidOperationException("USIM provider is unavailable during modem startup.");

        const int requiredConfirmations = 2;
        const int maxAttempts = 16;
        var confirmations = 0;
        var refreshedCachedIdentity = false;
        var cachedIdentityIsSafe = true;
        string? lastFailure = null;
        string? lastIccid = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ec25.VerifyUsimReadyAsync(
                expectedIccid: null,
                requireCardIccid: false,
                ct: ct).ConfigureAwait(false);
            lastIccid = result.CardIccid ?? lastIccid;

            if (result.Ready)
            {
                if (!result.UsedCardIccid && !refreshedCachedIdentity)
                {
                    refreshedCachedIdentity = true;
                    confirmations = 0;
                    _eventBus.Publish(EventTopics.SystemLog, "SIM",
                        $"Slot {Id}: startup stage=SIM readiness; attempt={attempt}/{maxAttempts}; ICCID source=baseband cache; reloading SIM before compatibility fallback.");
                    if (!await Modem.RefreshSimAsync(ct, preserveFlightMode: IsFlightMode).ConfigureAwait(false))
                    {
                        lastFailure = "baseband SIM reload did not confirm CPIN READY twice";
                        cachedIdentityIsSafe = false;
                    }
                    continue;
                }

                if (!result.UsedCardIccid && !cachedIdentityIsSafe)
                {
                    confirmations = 0;
                    _eventBus.Publish(EventTopics.SystemLog, "SIM",
                        $"Slot {Id}: startup stage=SIM readiness; attempt={attempt}/{maxAttempts}; cache fallback remains blocked because the SIM reload did not complete.");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                confirmations++;
                var source = result.UsedCardIccid ? "EF.ICCID" : "refreshed modem ICCID";
                _eventBus.Publish(EventTopics.SystemLog, "SIM",
                    $"Slot {Id}: startup stage=SIM readiness; attempt={attempt}/{maxAttempts}; ICCID={MaskIccid(lastIccid)}; source={source}; ADF.USIM=selected; confirmations={confirmations}/{requiredConfirmations}.");
                if (confirmations >= requiredConfirmations && !string.IsNullOrWhiteSpace(lastIccid))
                    return lastIccid;
            }
            else
            {
                confirmations = 0;
                lastFailure = result.Failure ?? "unknown USIM readiness failure";
                _eventBus.Publish(EventTopics.SystemLog, "SIM",
                    $"Slot {Id}: startup stage=SIM readiness; attempt={attempt}/{maxAttempts}; ICCID={MaskIccid(lastIccid)}; pending={lastFailure}.");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"SIM/USIM 未在启动窗口内稳定就绪（ICCID {MaskIccid(lastIccid)}，原因：{lastFailure ?? "未完成两次确认"}）。");
    }

    /// <summary>
    /// Starts VoWiFi session for this slot, using the slot's SIM and dedicated SOCKS proxy.
    /// </summary>
    public async Task<bool> StartVoWifiAsync(string? customEpdg = null, IkeProposalSuite suite = IkeProposalSuite.Auto, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _profileSwitchInProgress) != 0)
            throw new InvalidOperationException($"Slot {Id} is switching eSIM profiles; VoWiFi cannot start until the new SIM identity is verified.");

        if (Sim == null)
            await RecoverSimIdentityForVoWifiAsync(ct).ConfigureAwait(false);
        if (Sim == null)
            throw new InvalidOperationException($"Slot {Id} has no active SIM identity after the readiness check.");

        VoWifi.ProxyUrl = ProxyUrl;
        VoWifi.AkaProvider = Aka;
        VoWifi.DeviceImei = VoWifiImei;
        var canReadReady = true;
        try { await VoWifi.PreflightIkeRetryAsync(Sim, customEpdg, ct).ConfigureAwait(false); }
        catch (IkeRetryGateException) { canReadReady = false; } // Manager records the user's start intent and wait/pause state below.
        if (canReadReady && !await Aka.CheckReadyAsync(Sim.Iccid, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"Slot {Id} live USIM does not match the refreshed VoWiFi identity. Registration was blocked before EAP-AKA.");

        var candidates = BuildVoWifiIdentityCandidates();

        bool started = await VoWifi.StartVoWifiAsync(candidates, customEpdg, suite, proxyUrl: ProxyUrl, ct: ct).ConfigureAwait(false);
        if (started)
        {
            LastSeen = DateTime.UtcNow;
            var successfulImsi = VoWifi.EpdgInfo?.Impi.Split('@')[0];
            var successfulIdentity = candidates.FirstOrDefault(candidate => candidate.Imsi == successfulImsi);
            if (successfulIdentity != null)
            {
                var discoveredHome = VoWifi.EpdgInfo!;
                if (EpdgResolver.BuildHomePlmnCandidates(successfulIdentity).Contains((discoveredHome.HomeMcc, discoveredHome.HomeMnc)))
                    successfulIdentity = successfulIdentity with { Mcc = discoveredHome.HomeMcc, Mnc = discoveredHome.HomeMnc };
                _identityHistory.MarkSuccessful(successfulIdentity);
            }
        }
        return started;

    }

    internal IReadOnlyList<SimIdentity> BuildVoWifiIdentityCandidates()
    {
        if (Sim is not { } live) return [];
        return _identityHistory.GetCandidates(live, successfulOnly: true)
            .Concat(new[] { live, VoWifiIdentityOverride }.OfType<SimIdentity>())
            .Concat(_identityHistory.GetCandidates(live))
            .Where(identity => identity.Iccid == live.Iccid)
            .DistinctBy(identity => identity.Imsi, StringComparer.Ordinal).ToArray();
    }

    private async Task RecoverSimIdentityForVoWifiAsync(CancellationToken ct)
    {
        if (Modem == null || !Modem.IsOpen)
            throw new InvalidOperationException($"Slot {Id} modem is unavailable while waiting for SIM readiness.");

        _eventBus.Publish(EventTopics.SystemLog, "SIM",
            $"Slot {Id}: VoWiFi start requested before SIM initialization completed; retrying the SIM readiness sequence.");
        await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
        if (!await Modem.RefreshSimAsync(ct, preserveFlightMode: IsFlightMode).ConfigureAwait(false))
            throw new InvalidOperationException("SIM 重新初始化未连续确认 CPIN READY。");

        var expectedIccid = await WaitForStartupUsimReadinessAsync(ct).ConfigureAwait(false);
        var identity = await ReadSimIdentityUntilReadyAsync(expectedIccid, previousIccid: null, ct: ct)
            .ConfigureAwait(false);
        await InvalidateHostImsForIdentityChangeAsync(identity).ConfigureAwait(false);
        SetVerifiedSimIdentity(identity);
        _eventBus.Publish(EventTopics.SystemLog, "SIM",
            $"Slot {Id}: delayed SIM initialization completed; VoWiFi may now continue.");
    }

    /// <summary>
    /// Stops active VoWiFi session for this slot.
    /// </summary>
    public async Task StopVoWifiAsync(CancellationToken ct = default)
    {
        await VoWifi.StopVoWifiAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dials a target number using this slot's VoWiFi session and IMS call manager.
    /// </summary>
    public async Task<CallInfo> DialVoWifiAsync(string number, CancellationToken ct = default)
    {
        if (VoWifi.State != VoWifiState.ImsRegistered)
            throw new InvalidOperationException($"Slot {Id} VoWiFi is not registered (State: {VoWifi.State}).");

        State = SlotState.Busy;
        try
        {
            var call = await Calls.DialAsync(number, VoWifi, ct).ConfigureAwait(false);
            return call;
        }
        finally
        {
            if (Calls.ActiveCall == null)
                State = SlotState.Online;
        }
    }

    /// <summary>Dials through the registered Windows-owned cellular IMS bearer.</summary>
    public async Task<CallInfo> DialHostImsAsync(string number, CancellationToken ct = default)
    {
        await _hostImsRegistrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _hostImsRegistration;
            if (client?.IsRegistered != true || Volatile.Read(ref _hostImsPppLease)?.IsLost == true ||
                IsProfileSwitchInProgress ||
                !HostImsCardMatchesCurrentSlot())
                throw new InvalidOperationException($"Slot {Id} has no active Host IMS registration on a stable modem and SIM.");
            SetState(SlotState.Busy);
            try { return await Calls.DialAsync(number, client, ct).ConfigureAwait(false); }
            finally
            {
                if (Calls.ActiveCall is null)
                    SetState(SlotState.Online);
            }
        }
        finally { _hostImsRegistrationGate.Release(); }
    }

    /// <summary>Explicit experimental MO SMS over the Windows Host IMS SIP session.</summary>
    public async Task<SmsSubmitResult> SendHostImsSmsAsync(
        string recipient, string text, bool requestStatusReport = false,
        CancellationToken ct = default)
    {
        await _hostImsRegistrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _hostImsRegistration;
            var modem = Modem;
            if (client?.IsRegistered != true || modem is not { IsOpen: true } ||
                Volatile.Read(ref _hostImsPppLease)?.IsLost == true ||
                IsProfileSwitchInProgress || !HostImsCardMatchesCurrentSlot())
                throw new InvalidOperationException($"Slot {Id} has no stable Windows Host IMS registration for SMS.");
            var smsc = await modem.GetSmsCenterAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(smsc))
                throw new InvalidOperationException("The selected SIM did not provide an SMS centre; no IMS SMS was sent.");
            if (!ReferenceEquals(client, _hostImsRegistration) ||
                !HostImsCardMatchesCurrentSlot())
                throw new InvalidOperationException("The Host IMS card changed before SMS submission; no SMS was sent.");
            return await client.SendSmsOverImsAsync(recipient, text, smsc,
                requestStatusReport, ct).ConfigureAwait(false);
        }
        finally { _hostImsRegistrationGate.Release(); }
    }

    /// <summary>
    /// Dials through the cellular baseband and emits the same call events used
    /// by VoWiFi so GUI clients have one transport-independent call model.
    /// </summary>
    public async Task<CallInfo> DialCellularAsync(string number, CancellationToken ct = default)
    {
        if (IsHostImsPppTransitionOrActive)
            throw new InvalidOperationException("Cellular AT dialing is unavailable while Windows owns the IMS PPP bearer; stop the experimental bearer first.");
        if (Modem == null || !Modem.IsOpen)
            throw new InvalidOperationException($"Slot {Id} cellular modem is not ready.");

        var callId = Guid.NewGuid().ToString("N");
        var startedAt = DateTime.UtcNow;
        lock (_cellularCallLock)
        {
            if (_cellularCallState is not (CallState.Idle or CallState.Ended))
                throw new InvalidOperationException($"Slot {Id} already has a cellular call.");
            _cellularCallId = callId;
            _cellularCallerNumber = number;
            _cellularCallState = CallState.Dialing;
            _cellularCallStartedAt = startedAt;
            _cellularCallConnectedAt = null;
            _cellularCallIsOutgoing = true;
        }

        try
        {
            CallStateChanged?.Invoke(this, new CallStateChangedEventArgs(
                callId, number, CallState.Idle, CallState.Dialing, isOutgoing: true, slotId: Id));
        }
        catch { }

        // QDC507 samples DTR while the IMS bearer is being created. Releasing
        // it only after CLCC becomes Active is too late: the USB capture pipe
        // is then created successfully but carries silence.
        var qdc507 = Modem.FirmwareRevision?.Contains("QDC507", StringComparison.OrdinalIgnoreCase) == true;
        if (qdc507) Modem.SetDataTerminalReady(false);
        var accepted = await Modem.DialVoiceAsync(number, ct).ConfigureAwait(false);
        if (!accepted)
        {
            if (qdc507) Modem.SetDataTerminalReady(true);
            EndCellularCall("DIAL_REJECTED");
            throw new InvalidOperationException("Cellular modem rejected the dial command.");
        }

        SetState(SlotState.Busy);
        EnsureCellularCallMonitor();

        return new CallInfo(callId, number, CallState.Dialing, startedAt, null, null, "Cellular", null, true);
    }

    /// <summary>
    /// Answers an incoming ringing call on this slot (via VoWiFi SIP or modem cellular).
    /// </summary>
    public async Task<CallInfo?> AnswerCallAsync(CancellationToken ct = default)
    {
        if (Calls.State == CallState.Ringing && Calls.IsIncoming)
        {
            SetState(SlotState.Busy);
            return await Calls.AnswerAsync(ct).ConfigureAwait(false);
        }

        if (Modem != null && Modem.IsOpen && HasCellularCall)
        {
            string callId;
            string number;
            CallState oldState;
            DateTime startedAt;
            lock (_cellularCallLock)
            {
                callId = _cellularCallId ?? Guid.NewGuid().ToString("N");
                number = _cellularCallerNumber ?? "Unknown";
                oldState = _cellularCallState;
                startedAt = _cellularCallStartedAt ?? DateTime.UtcNow;
            }

            var qdc507 = Modem.FirmwareRevision?.Contains("QDC507", StringComparison.OrdinalIgnoreCase) == true;
            if (qdc507) Modem.SetDataTerminalReady(false);
            if (!await Modem.AnswerVoiceAsync(ct).ConfigureAwait(false))
            {
                if (qdc507) Modem.SetDataTerminalReady(true);
                throw new InvalidOperationException("Cellular modem rejected the answer command.");
            }

            SetState(SlotState.Busy);
            EnsureCellularCallMonitor();
            return new CallInfo(callId, number, oldState, startedAt, null, null, "Cellular", null, false);
        }

        return null;
    }

    private void EnsureCellularCallMonitor()
    {
        if (Modem == null || !Modem.IsOpen) return;
        lock (_cellularCallLock)
        {
            if (_cellularCallMonitorCts is { IsCancellationRequested: false } &&
                _cellularCallMonitorTask is { IsCompleted: false }) return;

            _cellularCallMonitorCts?.Dispose();
            _cellularCallMonitorCts = new CancellationTokenSource();
            var token = _cellularCallMonitorCts.Token;
            _cellularCallMonitorTask = Task.Run(() => MonitorCellularCallAsync(token), token);
        }
    }

    private async Task MonitorCellularCallAsync(CancellationToken ct)
    {
        var sawVoiceEntry = false;
        var missingCount = 0;
        var started = DateTime.UtcNow;

        while (!ct.IsCancellationRequested && Modem is { IsOpen: true })
        {
            try
            {
                var calls = await Modem.GetVoiceCallsAsync(ct).ConfigureAwait(false);
                bool outgoing;
                string currentNumber;
                lock (_cellularCallLock)
                {
                    if (_cellularCallState is CallState.Idle or CallState.Ended) return;
                    outgoing = _cellularCallIsOutgoing;
                    currentNumber = _cellularCallerNumber ?? string.Empty;
                }

                var call = calls.FirstOrDefault(item => item.IsOutgoing == outgoing &&
                    (string.IsNullOrEmpty(currentNumber) || string.IsNullOrEmpty(item.Number) ||
                     item.Number.Equals(currentNumber, StringComparison.OrdinalIgnoreCase)))
                    ?? calls.FirstOrDefault(item => item.IsOutgoing == outgoing);

                if (call != null)
                {
                    sawVoiceEntry = true;
                    missingCount = 0;
                    await ApplyCellularClccStateAsync(call, ct).ConfigureAwait(false);
                }
                else if (sawVoiceEntry && ++missingCount >= 2)
                {
                    EndCellularCall("CLCC_ENDED");
                    return;
                }
                else if (!sawVoiceEntry && DateTime.UtcNow - started > TimeSpan.FromSeconds(60))
                {
                    EndCellularCall("CALL_SETUP_TIMEOUT");
                    return;
                }

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                try { await Task.Delay(1500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ApplyCellularClccStateAsync(ModemVoiceCall call, CancellationToken ct)
    {
        var nextState = call.Status switch
        {
            0 => CallState.Active,
            1 => CallState.Held,
            2 => CallState.Dialing,
            3 => CallState.Ringing,
            4 or 5 => CallState.Incoming,
            _ => CallState.Dialing
        };

        string callId;
        string number;
        CallState oldState;
        DateTime? connectedAt = null;
        bool isOutgoing;
        lock (_cellularCallLock)
        {
            oldState = _cellularCallState;
            if (oldState == nextState) return;
            if (!string.IsNullOrWhiteSpace(call.Number)) _cellularCallerNumber = call.Number;
            _cellularCallState = nextState;
            _cellularCallId ??= Guid.NewGuid().ToString("N");
            callId = _cellularCallId;
            number = _cellularCallerNumber ?? "Unknown";
            isOutgoing = _cellularCallIsOutgoing;
            if (nextState == CallState.Active)
            {
                _cellularCallConnectedAt ??= DateTime.UtcNow;
                connectedAt = _cellularCallConnectedAt;
            }
        }

        var codec = "Cellular";
        if (nextState == CallState.Active && Modem != null)
        {
            var isQdc507 = Modem.FirmwareRevision?.Contains("QDC507", StringComparison.OrdinalIgnoreCase) == true;
            if (isQdc507)
            {
                // QDC507 keeps the QPCMV parser but rejects the route command.
                // Its D4 runtime must be started by the Windows client after CLCC=Active.
                // Keeping DTR asserted produces a valid but permanently silent
                // UAC capture endpoint on this firmware.
                Modem.SetDataTerminalReady(false);
                CellularUsbAudioAvailable = false;
                codec = "Cellular/QDC507 runtime";
            }
            else
            {
                CellularUsbAudioAvailable = await Modem.EnableUsbVoiceAudioAsync(ct).ConfigureAwait(false);
                codec = CellularUsbAudioAvailable ? "Cellular/UAC" : "Cellular/No USB audio";
                if (!CellularUsbAudioAvailable)
                {
                    _eventBus.Publish(EventTopics.SystemError, "CellularAudio",
                        "The modem rejected AT+QPCMV=1,2; this firmware cannot expose VoLTE/VoNR call PCM over USB.");
                }
            }
        }

        try
        {
            CallStateChanged?.Invoke(this, new CallStateChangedEventArgs(
                callId, number, oldState, nextState, codec, isOutgoing: isOutgoing, slotId: Id));
        }
        catch { }

        if (nextState == CallState.Active && connectedAt.HasValue)
        {
            // Close the channel before subscribers start the D4 media route.
            // The QDC507 USB firmware otherwise allocates a silent capture pipe.
            if (codec == "Cellular/QDC507 runtime")
                await ReleaseQdc507AtChannelForMediaBootstrapAsync(ct).ConfigureAwait(false);
            try { CallConnected?.Invoke(this, new CallConnectedEventArgs(callId, number, codec, connectedAt.Value, Id)); } catch { }
        }
    }

    /// <summary>
    /// QDC507 only places live PCM on D4 when the AT USB function is released
    /// while the call media route is enabled. Reopen it after the UAC endpoint
    /// has been created so CLCC/URC monitoring can continue for the rest of the
    /// call.
    /// </summary>
    private async Task ReleaseQdc507AtChannelForMediaBootstrapAsync(CancellationToken ct)
    {
        var modem = Modem;
        if (modem == null || !modem.IsOpen) return;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _qdc507MediaBootstrapCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        if (!await modem.CloseAsync(ct).ConfigureAwait(false)) return;

        // SerialPort.Close returns before the USB CDC control transfer has
        // settled. The known-good CLI sequence leaves a short quiet period
        // here; without it QDC507 binds D4 to a silent UAC capture pipe.
        await Task.Delay(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);

        _ = Task.Run(async () =>
        {
            try
            {
                // Leave the AT function detached through the first UAC open.
                // Reattaching it too early recreates QDC507's silent PCM pipe.
                await Task.Delay(TimeSpan.FromSeconds(12), cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested || !ReferenceEquals(modem, Modem)) return;
                modem.Open();
                EnsureCellularCallMonitor();
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _qdc507MediaBootstrapCts, null, cts), cts))
                    cts.Dispose();
            }
        });
    }

    /// <summary>
    /// Rejects or declines an incoming ringing call on this slot.
    /// </summary>
    public async Task<CallInfo?> RejectCallAsync(CancellationToken ct = default)
    {
        if (Calls.State == CallState.Ringing && Calls.IsIncoming)
        {
            var res = await Calls.RejectAsync(ct: ct).ConfigureAwait(false);
            SetState(SlotState.Online);
            return res;
        }
        else if (Modem != null && Modem.IsOpen && HasCellularCall)
        {
            await Modem.HangupVoiceAsync(ct).ConfigureAwait(false);
            EndCellularCall("REJECTED");
        }
        return null;
    }

    /// <summary>
    /// Hangs up any active call on this slot.
    /// </summary>
    public async Task<CallInfo?> HangupAsync(CancellationToken ct = default)
    {
        if (Calls.ActiveCall != null)
        {
            var result = await Calls.HangupAsync().ConfigureAwait(false);
            SetState(SlotState.Online);
            return result;
        }

        if (Modem != null && HasCellularCall)
        {
            string callId;
            string number;
            DateTime startedAt;
            DateTime? connectedAt;
            bool isOutgoing;
            lock (_cellularCallLock)
            {
                callId = _cellularCallId ?? Guid.NewGuid().ToString("N");
                number = _cellularCallerNumber ?? "Unknown";
                startedAt = _cellularCallStartedAt ?? DateTime.UtcNow;
                connectedAt = _cellularCallConnectedAt;
                isOutgoing = _cellularCallIsOutgoing;
            }

            if (!Modem.IsOpen)
            {
                // QDC507 releases its AT function briefly to expose live USB PCM.
                // A user hangup cannot wait for the scheduled reopen.
                _qdc507MediaBootstrapCts?.Cancel();
                Modem.Open();
            }
            if (!await Modem.HangupVoiceAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Cellular modem did not confirm call hangup.");
            EndCellularCall("LOCAL_HANGUP");
            return new CallInfo(callId, number, CallState.Ended, startedAt, connectedAt, DateTime.UtcNow, "Cellular", null, isOutgoing);
        }

        return null;
    }

    /// <summary>
    /// Sets flight mode (airplane mode) for this slot.
    /// </summary>
    public async Task<bool> SetFlightModeAsync(bool enabled, CancellationToken ct = default)
        => await RunSmsSourceMutationAsync(token => SetFlightModeCoreAsync(enabled, token), ct).ConfigureAwait(false);

    private async Task<bool> SetFlightModeCoreAsync(bool enabled, CancellationToken ct)
    {
        InvalidateSmsIdentity();
        if (IsHostImsPppTransitionOrActive)
            throw new InvalidOperationException("Stop the Windows Host IMS PPP bearer before changing flight mode.");
        if (IsPcscReader)
            throw new InvalidOperationException("PC/SC 读卡器没有蜂窝射频，无法切换飞行模式。");
        if (Modem == null || !Modem.IsOpen) return false;
        if (enabled)
            await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
        bool ok = await Modem.SetFlightModeAsync(enabled, ct).ConfigureAwait(false);
        if (ok)
        {
            IsFlightMode = enabled;
            if (enabled)
            {
                ClearRadioMetrics();
            }
            else
            {
                await RefreshSimCoreAsync(ct).ConfigureAwait(false);
                await RefreshMetricsAsync(ct).ConfigureAwait(false);
            }
        }
        return ok;
    }

    /// <summary>
    /// Enables or disables packet-domain attachment for cellular data.
    /// </summary>
    public async Task<bool> SetCellularDataEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (IsHostImsPppTransitionOrActive)
            throw new InvalidOperationException("Stop the Windows Host IMS PPP bearer before changing packet attachment.");
        if (IsPcscReader)
            throw new NotSupportedException("PC/SC 卡槽没有蜂窝数据附着功能。");
        if (Modem == null || !Modem.IsOpen || IsFlightMode) return false;
        if (!enabled)
            await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
        var response = await Modem.SendRawAtCommandAsync(
            $"AT+CGATT={(enabled ? 1 : 0)}", 10000, ct).ConfigureAwait(false);
        return response.Success;
    }

    /// <summary>
    /// Applies the Quectel roaming policy used by the supported modem family.
    /// Firmware variants expose either QCFG or QNWCFG, so success from either
    /// command is accepted.
    /// </summary>
    public async Task<bool> SetDataRoamingEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (IsPcscReader)
            throw new NotSupportedException("PC/SC 卡槽没有蜂窝漫游控制功能。");
        if (Modem == null || !Modem.IsOpen || IsFlightMode) return false;
        var value = enabled ? 1 : 0;
        var qcfg = await Modem.SendRawAtCommandAsync(
            $"AT+QCFG=\"roamsvc\",{value}", 3000, ct).ConfigureAwait(false);
        var qnwcfg = await Modem.SendRawAtCommandAsync(
            $"AT+QNWCFG=\"roaming\",{value}", 3000, ct).ConfigureAwait(false);
        return qcfg.Success || qnwcfg.Success;
    }

    private void ClearRadioMetrics()
    {
        Signal = null;
        Registration = null;
    }

    /// <summary>
    /// Sends a USSD command (e.g. *100#) and returns the modem response.
    /// </summary>
    public async Task<string> SendUssdAsync(string code, int timeoutMs = 15000, CancellationToken ct = default)
    {
        if (IsPcscReader)
            throw new NotSupportedException("PC/SC 卡槽不支持 USSD；仅支持 USIM/eSIM 操作和已注册 VoWiFi 的 IMS 通话、短信。");
        if (Modem == null || !Modem.IsOpen) return "Modem 未就绪。";
        var resp = await Modem.SendRawAtCommandAsync($"AT+CUSD=1,\"{code}\",15", timeoutMs, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            var match = System.Text.RegularExpressions.Regex.Match(resp.RawOutput ?? "", @"\+CUSD:\s*\d+,\s*""([^""]*)""");
            if (match.Success) return match.Groups[1].Value;
            return resp.RawOutput ?? "OK";
        }
        return $"ERROR: {string.Join(" ", resp.Lines)}";
    }

    /// <summary>
    /// Soft-reboots the modem via AT+CFUN=1,1.
    /// </summary>
    public async Task<bool> RebootAsync(CancellationToken ct = default)
        => await RunSmsSourceMutationAsync(RebootCoreAsync, ct).ConfigureAwait(false);

    private async Task<bool> RebootCoreAsync(CancellationToken ct)
    {
        InvalidateSmsIdentity();
        if (IsPcscReader)
            throw new NotSupportedException("PC/SC 卡槽没有可重启的蜂窝模组。");
        if (Modem == null || !Modem.IsOpen) return false;
        // A baseband restart invalidates every tunnel and always requests CFUN=1.
        // Stop recovery first and update the cached state immediately so a SIM
        // refresh cannot restore stale pre-reboot flight mode.
        await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
        await VoWifi.StopVoWifiAsync(ct).ConfigureAwait(false);
        var rebooted = await Modem.RebootBasebandAsync(ct).ConfigureAwait(false);
        if (rebooted)
        {
            IsFlightMode = false;
            ClearRadioMetrics();
            _eventBus.Publish(EventTopics.SystemLog, "ModemPool",
                $"Slot {Id}: baseband reboot accepted; stale flight-mode and VoWiFi recovery state cleared.");
        }
        return rebooted;
    }

    // ── eUICC / eSIM Operations ──────────────────────────────────────────────
    private EuiccManager? _euicc;
    public EuiccManager? Euicc => GetOrCreateEuiccManager();

    public EuiccManager? GetOrCreateEuiccManager()
    {
        if (_euicc != null) return _euicc;
        if (IsPcscReader && !string.IsNullOrWhiteSpace(PcscReaderName))
        {
            _euicc = new EuiccManager(new PcscEuiccTransport(readerName: PcscReaderName), _eventBus);
        }
        else if (Modem != null)
        {
            _euicc = new EuiccManager(new AtModemEuiccTransport(Modem), _eventBus);
        }
        return _euicc;
    }

    public async Task<string> GetEuiccEidAsync(CancellationToken ct = default)
    {
        var mgr = GetOrCreateEuiccManager();
        if (mgr == null) throw new InvalidOperationException("当前卡槽未就绪，无法访问 eUICC 芯片。");
        await _euiccProbeGate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await ReadEuiccInventoryAndRestoreUsimAsync(mgr, ct).ConfigureAwait(false)).First().Eid; }
        finally { _euiccProbeGate.Release(); }
    }

    public async Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(CancellationToken ct = default)
    {
        var mgr = GetOrCreateEuiccManager();
        if (mgr == null) return Array.Empty<Profile>();
        await _euiccProbeGate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await ReadEuiccInventoryAndRestoreUsimAsync(mgr, ct).ConfigureAwait(false)).SelectMany(entry => entry.Profiles).ToArray(); }
        finally { _euiccProbeGate.Release(); }
    }

    /// <summary>
    /// Performs a read-only eUICC detection pass.  It deliberately verifies
    /// both an EID read and the profile-list path so that a card with no
    /// installed profiles is still recognised as an eUICC.
    /// </summary>
    public async Task<EuiccProbeResult> ProbeEuiccAsync(CancellationToken ct = default)
    {
        await _euiccProbeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetEuiccCapability(EuiccCapability.Probing, "正在读取 eUICC 芯片…", log: false);

            var mgr = GetOrCreateEuiccManager();
            var pcscReady = IsPcscReader && Aka is PcscAkaProvider pcsc &&
                await pcsc.CheckReadyAsync(Sim?.Iccid, ct).ConfigureAwait(false);
            if (mgr == null || (!pcscReady && (Modem == null || !Modem.IsOpen)))
            {
                const string message = "卡槽尚未就绪，暂时无法检测 eUICC 芯片。";
                return CompleteEuiccProbe(EuiccCapability.Error, null, Array.Empty<Profile>(), message);
            }

            string? eid = null;
            IReadOnlyList<string> eids = Array.Empty<string>();
            IReadOnlyList<EuiccInventoryEntry> inventory = Array.Empty<EuiccInventoryEntry>();
            Exception? eidError = null;
            IReadOnlyList<Profile> profiles = Array.Empty<Profile>();
            Exception? profilesError = null;
            var profilesRead = false;

            try
            {
                inventory = await ReadEuiccInventoryAndRestoreUsimAsync(mgr, ct).ConfigureAwait(false);
                eids = inventory.Select(entry => entry.Eid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                eid = eids.FirstOrDefault();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                eidError = ex;
            }

            if (eidError == null)
            {
                profiles = inventory.SelectMany(entry => entry.Profiles).ToArray();
                profilesRead = true;
            }
            else
            {
                profilesError = eidError;
            }

            if (!string.IsNullOrWhiteSpace(eid) || profilesRead)
            {
                var profileWarning = profilesError == null
                    ? string.Empty
                    : "；Profile 列表暂时无法读取，可稍后重试。";
                var message = !string.IsNullOrWhiteSpace(eid)
                    ? eids.Count > 1
                        ? $"已识别 {eids.Count} 个可独立选择的 eUICC；每组 Profile 已绑定其 ISD-R AID。{profileWarning}"
                        : $"已识别 eUICC 芯片{profileWarning}"
                    : $"已识别 eUICC 芯片，当前 {profiles.Count} 个 Profile（EID 未返回）。{profileWarning}";
                return CompleteEuiccProbe(EuiccCapability.Supported, eid, profiles, message, eids, inventory);
            }

            var errors = new[] { eidError, profilesError }
                .Where(error => error != null)
                .Cast<Exception>()
                .ToArray();
            if (errors.Any(IsExplicitlyNonEuiccCard))
            {
                const string message = "当前卡片是普通实体 SIM，不支持 eSIM Profile 切换或写卡。";
                return CompleteEuiccProbe(EuiccCapability.Unsupported, null, Array.Empty<Profile>(), message, Array.Empty<string>());
            }

            var detail = errors
                .Select(error => error.Message.Trim())
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            var unavailableMessage = string.IsNullOrWhiteSpace(detail)
                ? "无法确认 eUICC 芯片状态，请在模组和 SIM 就绪后重试。"
                : $"无法确认 eUICC 芯片状态：{detail}";
            return CompleteEuiccProbe(EuiccCapability.Error, null, Array.Empty<Profile>(), unavailableMessage, Array.Empty<string>());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetEuiccCapability(EuiccCapability.Unknown, "eUICC 检测已取消。", log: false);
            throw;
        }
        finally
        {
            _euiccProbeGate.Release();
        }
    }

    private async Task<IReadOnlyList<EuiccInventoryEntry>> ReadEuiccInventoryAndRestoreUsimAsync(
        EuiccManager manager, CancellationToken ct)
    {
        var originalUsim = Aka as Ec25AkaProvider;
        var originalModem = Modem;
        try { return await manager.GetInventoryAsync(ct).ConfigureAwait(false); }
        finally
        {
            // Inventory must not leave a verified modem's baseband USIM view
            // unusable. Restore before returning to preference/radio restoration;
            // waiting for CPIN to fail first is too late on affected EC20 firmware.
            if (originalUsim != null && ReferenceEquals(Aka, originalUsim) &&
                ReferenceEquals(Modem, originalModem) && originalModem is { IsOpen: true } &&
                Sim != null && !IsProfileSwitchInProgress)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    if (!await originalUsim.RestoreBasicUsimApplicationAsync(cleanup.Token).ConfigureAwait(false))
                        _eventBus.Publish(EventTopics.SystemLog, "SIM", $"Slot {Id}: USIM application restoration after eUICC probe was not confirmed; registration still requires full SIM readiness verification.");
                }
                catch (Exception ex)
                {
                    _eventBus.Publish(EventTopics.SystemLog, "SIM", $"Slot {Id}: USIM application restoration after eUICC probe failed ({ex.GetType().Name}); no profile or identity was changed.");
                }
            }
        }
    }

    private EuiccProbeResult CompleteEuiccProbe(
        EuiccCapability capability,
        string? eid,
        IReadOnlyList<Profile> profiles,
        string message,
        IReadOnlyList<string>? eids = null,
        IReadOnlyList<EuiccInventoryEntry>? inventory = null)
    {
        SetEuiccCapability(capability, message);
        var result = new EuiccProbeResult(capability, eid, profiles.ToArray(), message, eids, inventory);
        LastEuiccProbe = result;
        return result;
    }

    private void SetEuiccCapability(EuiccCapability capability, string message, bool log = true)
    {
        EuiccCapability = capability;
        EuiccCapabilityMessage = message;
        if (log)
        {
            _eventBus.Publish(EventTopics.SystemLog, "eSIM",
                $"Slot {Id}: eUICC probe={capability}; {message}");
        }
    }

    private static bool IsExplicitlyNonEuiccCard(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("普通物理实体 SIM 卡", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("未检测到 eUICC / eSIM (ISD-R) 应用", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default, string? euiccAid = null)
    {
        using var smsMutation = BeginSmsSourceMutation();
        await _profileSwitchGate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _profileSwitchInProgress, 1);
        try
        {
            if (_hostImsPppLease is not null)
                throw new InvalidOperationException("Stop the Windows Host IMS PPP bearer before switching an eUICC profile.");
            await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
            if (!IsPcscReader && (Modem == null || !Modem.IsOpen))
                throw new InvalidOperationException("当前卡槽模组未就绪。");
            var targetEuicc = await GetVerifiedEuiccManagerAsync(iccidOrAid, euiccAid, ct).ConfigureAwait(false);
            var mgr = targetEuicc.Manager;

            // Stop the old tunnel and its automatic recovery loop before the
            // eUICC changes which USIM application is active.
            await VoWifi.StopVoWifiAsync(ct).ConfigureAwait(false);

            var previousIccid = Sim?.Iccid;
            VoWifiIdentityOverride = null;
            Sim = null;
            ClearRadioMetrics();
            try { SimChanged?.Invoke(this, new SimStateChangedEventArgs(null, 1, "SWITCHING", Id)); } catch { }
            _eventBus.Publish(EventTopics.SystemLog, "eSIM", $"Slot {Id}: old VoWiFi identity cleared before profile switch.");

            await mgr.SwitchProfileAsync(iccidOrAid, targetEuicc.Aid, refresh, ct).ConfigureAwait(false);

            var expectedIccid = NormalizeIccidCandidate(iccidOrAid);
            if (expectedIccid == null)
            {
                try
                {
                    expectedIccid = NormalizeIccidCandidate(
                        (await mgr.GetActiveProfileAsync(targetEuicc.Aid, ct).ConfigureAwait(false))?.ICCID);
                }
                catch { }
            }

            if (IsPcscReader)
            {
                // PC/SC has no baseband to refresh. Re-read EF.ICCID/EF.IMSI
                // from the card itself and reject an unexpected active profile.
                await RefreshSimCoreAsync(ct).ConfigureAwait(false);
                if (Sim == null || (expectedIccid != null &&
                    !string.Equals(NormalizeIccidCandidate(Sim.Iccid), expectedIccid, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("PC/SC eSIM 切换后未读到目标 EF.ICCID；VoWiFi 已保持停止。");
                }
                _eventBus.Publish(EventTopics.SystemLog, "eSIM",
                    $"Slot {Id}: PC/SC profile switch verified from EF.ICCID; VoWiFi may now restart.");
                return true;
            }

            // Force a baseband/SIM reload even in flight mode, then reject any
            // stale CIMI/QCCID result. A failed verification deliberately leaves
            // Sim=null so no caller can authenticate using the previous card.
            var modem = Modem ?? throw new InvalidOperationException("切换 eSIM 时模组已断开。");
            _eventBus.Publish(EventTopics.SystemLog, "eSIM",
                $"Slot {Id}: stage=baseband reload; target={MaskIccid(expectedIccid)}; flight-mode={IsFlightMode}.");
            var refreshCompleted = await modem.RefreshSimAsync(ct, preserveFlightMode: IsFlightMode).ConfigureAwait(false);
            if (!refreshCompleted)
                throw new InvalidOperationException("基带在 eSIM REFRESH 后未连续确认 CPIN READY，已取消使用新卡注册。");
            _eventBus.Publish(EventTopics.SystemLog, "eSIM",
                $"Slot {Id}: stage=baseband reload complete; CPIN=READY confirmed twice.");
            var identity = await ReadSimIdentityUntilReadyAsync(expectedIccid, previousIccid, ct, requireCardIccid: true)
                .ConfigureAwait(false);
            await WaitForPostProfileSwitchUsimReadinessAsync(expectedIccid, ct).ConfigureAwait(false);
            await InvalidateHostImsForIdentityChangeAsync(identity).ConfigureAwait(false);
            SetVerifiedSimIdentity(identity);
            LastSeen = DateTime.UtcNow;

            _eventBus.Publish(EventTopics.SystemLog, "eSIM",
                $"Slot {Id}: new SIM identity verified after profile switch (ICCID ****{identity.Iccid[^Math.Min(4, identity.Iccid.Length)..]}). VoWiFi may now restart.");
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _profileSwitchInProgress, 0);
            _profileSwitchGate.Release();
        }
    }

    private async Task<SimIdentity> ReadSimIdentityUntilReadyAsync(
        string? expectedIccid,
        string? previousIccid,
        CancellationToken ct,
        bool requireCardIccid = false)
    {
        if (Modem == null || !Modem.IsOpen)
            throw new InvalidOperationException("模组已断开，无法验证切卡后的 SIM 身份。");

        var normalizedExpected = NormalizeIccidCandidate(expectedIccid);
        var normalizedPrevious = NormalizeIccidCandidate(previousIccid);
        string? lastIccid = null;
        string? previousFingerprint = null;
        var identityConfirmations = 0;
        var observedPlmnConflict = false;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // The EF.ICCID read belongs to the card itself. QCCID/CCID are only
            // a compatibility fallback during ordinary refreshes; after a
            // profile switch they must never be used to prove the new profile.
            var cardIccid = Aka is Ec25AkaProvider ec25
                ? await ec25.ReadIccidFromCardAsync(ct).ConfigureAwait(false)
                : null;
            var iccid = cardIccid ?? (requireCardIccid
                ? null
                : await Modem.GetIccidAsync(ct).ConfigureAwait(false));
            var normalizedIccid = NormalizeIccidCandidate(iccid);
            lastIccid = normalizedIccid;

            var isExpectedCard = normalizedIccid != null &&
                (normalizedExpected != null
                    ? normalizedIccid.Equals(normalizedExpected, StringComparison.Ordinal)
                    : normalizedPrevious == null || !normalizedIccid.Equals(normalizedPrevious, StringComparison.Ordinal));

            if (requireCardIccid)
            {
                _eventBus.Publish(EventTopics.SystemLog, "eSIM",
                    $"Slot {Id}: stage=EF.ICCID identity; attempt={attempt + 1}/20; read={MaskIccid(normalizedIccid)}; expected={MaskIccid(normalizedExpected)}; match={isExpectedCard}.");
            }

            if (isExpectedCard)
            {
                var reportedImsi = await Modem.GetImsiAsync(ct).ConfigureAwait(false);
                var permanentImsi = await Modem.GetPermanentImsiAsync(ct).ConfigureAwait(false);
                var imsi = !string.IsNullOrWhiteSpace(permanentImsi) ? permanentImsi : reportedImsi;
                if (imsi.Length is >= 5 and <= 16 && imsi.All(char.IsAsciiDigit))
                {
                    var fingerprint = $"{normalizedIccid}|{imsi}";
                    if (string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        identityConfirmations++;
                    }
                    else
                    {
                        if (previousFingerprint != null)
                            observedPlmnConflict = true;
                        previousFingerprint = fingerprint;
                        identityConfirmations = 1;
                    }

                    // EF_AD is read only after the identity has stabilised, so
                    // the MNC length is not known at this point. Compare MCC
                    // here; a full MCC/MNC comparison is performed when a
                    // later verified identity replaces the current one.
                    observedPlmnConflict |= IsDifferentMcc(reportedImsi, permanentImsi);
                    var selectedSource = !string.IsNullOrWhiteSpace(permanentImsi) ? "EF_IMSI" : "AT+CIMI";
                    _eventBus.Publish(EventTopics.SystemLog, "SIM",
                        $"Slot {Id}: stage=IMSI stability; ICCID={MaskIccid(normalizedIccid)}; selected={DescribeImsiPlmn(imsi)}; source={selectedSource}; " +
                        $"EF_IMSI={DescribeImsiPlmn(permanentImsi)}; AT+CIMI={DescribeImsiPlmn(reportedImsi)}; confirmations={identityConfirmations}/2; conflict={observedPlmnConflict}.");

                    if (identityConfirmations < 2)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        continue;
                    }

                    var phoneNumber = await Modem.GetPhoneNumberAsync(ct).ConfigureAwait(false);
                    var providerName = await Modem.GetServiceProviderNameAsync(ct).ConfigureAwait(false);
                    var mncLength = await Modem.GetHomeMncLengthAsync(ct).ConfigureAwait(false);
                    var homePlmns = await Modem.GetHomePlmnsAsync(ct).ConfigureAwait(false);
                    return SimIdentity.FromImsiAndIccid(
                        imsi,
                        normalizedIccid!,
                        string.IsNullOrWhiteSpace(providerName) ? null : providerName,
                        phoneNumber,
                        mncLength,
                        homePlmns,
                        reportedImsi,
                        permanentImsi,
                        selectedSource,
                        observedPlmnConflict);
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        var expectedText = normalizedExpected != null
            ? $"目标 ICCID ****{normalizedExpected[^Math.Min(4, normalizedExpected.Length)..]}"
            : "新的活动 ICCID";
        var actualText = lastIccid != null
            ? $"****{lastIccid[^Math.Min(4, lastIccid.Length)..]}"
            : "未读取到";
        var evidenceText = requireCardIccid ? "EF.ICCID" : "SIM ICCID";
        throw new InvalidOperationException(
            $"Profile 已切换，但未能通过 {evidenceText} 验证新 SIM 身份（{expectedText}，当前读取 {actualText}）。已保持 VoWiFi 停止，避免使用旧卡身份注册。");
    }

    private async Task WaitForPostProfileSwitchUsimReadinessAsync(string? expectedIccid, CancellationToken ct)
    {
        if (Aka is not Ec25AkaProvider ec25)
            throw new InvalidOperationException("当前 AKA 提供程序无法确认 eSIM 切换后的 EF.ICCID/ADF.USIM 状态。");

        const int requiredConfirmations = 2;
        const int maxAttempts = 12;
        var confirmations = 0;
        string? lastFailure = null;
        string? lastCardIccid = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ec25.VerifyPostProfileSwitchReadyAsync(expectedIccid, ct).ConfigureAwait(false);
            lastCardIccid = result.CardIccid ?? lastCardIccid;
            if (result.Ready)
            {
                confirmations++;
                _eventBus.Publish(EventTopics.SystemLog, "eSIM",
                    $"Slot {Id}: stage=USIM readiness; attempt={attempt}/{maxAttempts}; EF.ICCID={MaskIccid(lastCardIccid)}; ADF.USIM=selected; confirmations={confirmations}/{requiredConfirmations}.");
                if (confirmations >= requiredConfirmations)
                    return;
            }
            else
            {
                confirmations = 0;
                lastFailure = result.Failure ?? "unknown card readiness failure";
                _eventBus.Publish(EventTopics.SystemLog, "eSIM",
                    $"Slot {Id}: stage=USIM readiness; attempt={attempt}/{maxAttempts}; EF.ICCID={MaskIccid(lastCardIccid)}; pending={lastFailure}.");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Profile 已切换，但 USIM 应用未稳定就绪（EF.ICCID {MaskIccid(lastCardIccid)}，原因：{lastFailure ?? "未完成两次确认"}）。已保持 VoWiFi 停止。");
    }

    private static string MaskIccid(string? iccid) =>
        string.IsNullOrWhiteSpace(iccid) ? "unavailable" : $"****{iccid[^Math.Min(4, iccid.Length)..]}";

    private static bool IsDifferentMcc(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        if (first.Length < 3 || second.Length < 3) return false;
        return !string.Equals(first[..3], second[..3], StringComparison.Ordinal);
    }

    private static string DescribeImsiPlmn(string? imsi)
    {
        if (string.IsNullOrWhiteSpace(imsi) || imsi.Length < 5) return "unavailable";
        // Never write the subscriber part of an IMSI to the ordinary log.
        return $"{imsi[..3]}-{imsi.Substring(3, 2)}…";
    }

    private async Task InvalidateHostImsForIdentityChangeAsync(SimIdentity identity)
    {
        if (Volatile.Read(ref _hostImsRegistration) is null) return;
        if (identity.Iccid == _hostImsRegisteredIccid &&
            identity.Imsi == _hostImsRegisteredImsi &&
            ReferenceEquals(Modem, _hostImsRegisteredModem))
            return;
        await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
    }

    internal void SetVerifiedSimIdentity(SimIdentity identity)
    {
        var isNewCard = !string.Equals(_stableRoutingIccid, identity.Iccid, StringComparison.Ordinal);
        var previousIdentity = Sim;

        LastReportedImsi = identity.ReportedImsi;
        LastPermanentImsi = identity.PermanentImsi;
        ImsiIdentitySource = identity.ImsiSource;

        if (isNewCard)
        {
            _stableRoutingIccid = identity.Iccid;
            StableRoutingMcc = identity.Mcc;
            HasImsiPlmnConflict = identity.HasPlmnConflict;
        }
        else
        {
            var changedPlmn = previousIdentity != null &&
                (!string.Equals(previousIdentity.Mcc, identity.Mcc, StringComparison.Ordinal) ||
                 !string.Equals(previousIdentity.Mnc, identity.Mnc, StringComparison.Ordinal));
            HasImsiPlmnConflict |= identity.HasPlmnConflict || changedPlmn;

            if (changedPlmn)
            {
                _eventBus.Publish(EventTopics.SystemLog, "SIM",
                    $"Slot {Id}: same ICCID {MaskIccid(identity.Iccid)} changed stable identity from " +
                    $"{previousIdentity!.Mcc}-{previousIdentity.Mnc} to {identity.Mcc}-{identity.Mnc}; " +
                    $"source={identity.ImsiSource}; country routing remains pinned to MCC {StableRoutingMcc ?? "unknown"} until an ICCID rule is configured or the card changes.");
            }
        }

        identity = identity with { HasPlmnConflict = HasImsiPlmnConflict };
        Sim = identity;
        _identityHistory.ObserveProfileIdentities(identity);
        VoWifiIdentityOverride = _identityHistory.GetCandidates(identity)
            .FirstOrDefault(candidate => candidate.Imsi != identity.Imsi);
        try { SimChanged?.Invoke(this, new SimStateChangedEventArgs(identity, 1, "READY", Id)); } catch { }
        _eventBus.Publish(EventTopics.ModemSim, Id, "READY");
    }

    public void RememberVoWifiIdentity(SimIdentity identity, bool succeeded = false)
    {
        if (Sim == null || identity.Iccid != Sim.Iccid)
            throw new ArgumentException("The learned identity must belong to the live ICCID.", nameof(identity));
        if (succeeded) _identityHistory.MarkSuccessful(identity);
        else _identityHistory.Observe(identity);
        VoWifiIdentityOverride = identity;
    }

    private static string? NormalizeIccidCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        return candidate.Length is >= 18 and <= 22 && candidate.All(char.IsAsciiDigit)
            ? candidate
            : null;
    }

    public async Task<bool> DisableEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default, string? euiccAid = null)
    {
        using var smsMutation = BeginSmsSourceMutation();
        await _profileSwitchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            InvalidateSmsIdentity();
            var targetEuicc = await GetVerifiedEuiccManagerAsync(iccidOrAid, euiccAid, ct).ConfigureAwait(false);
            await targetEuicc.Manager.DisableProfileAsync(iccidOrAid, targetEuicc.Aid, refresh, ct).ConfigureAwait(false);
            Sim = null;
            await RefreshSimCoreAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally { _profileSwitchGate.Release(); }
    }

    public async Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, CancellationToken ct = default, string? euiccAid = null)
    {
        using var smsMutation = BeginSmsSourceMutation();
        await _profileSwitchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            InvalidateSmsIdentity();
            var targetEuicc = await GetVerifiedEuiccManagerAsync(iccidOrAid, euiccAid, ct).ConfigureAwait(false);
            await targetEuicc.Manager.DeleteProfileAsync(iccidOrAid, targetEuicc.Aid, ct).ConfigureAwait(false);
            Sim = null;
            await RefreshSimCoreAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally { _profileSwitchGate.Release(); }
    }

    public async Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, CancellationToken ct = default, string? euiccAid = null)
    {
        var targetEuicc = await GetVerifiedEuiccManagerAsync(iccidOrAid, euiccAid, ct).ConfigureAwait(false);
        await targetEuicc.Manager.RenameProfileAsync(iccidOrAid, nickname, targetEuicc.Aid, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<EuiccDownloadResult> DownloadEuiccProfileAsync(
        string activationCode,
        string? confirmationCode = null,
        IProgress<EuiccDownloadProgress>? progress = null,
        CancellationToken ct = default,
        bool allowUntrustedTls = false,
        bool allowRetryAfterUncertain = false,
        string? euiccAid = null)
    {
        var targetEuicc = await GetVerifiedEuiccManagerAsync(null, euiccAid, ct).ConfigureAwait(false);
        var mgr = targetEuicc.Manager;
        var deviceImei = IsPcscReader ? VoWifiImei : Imei;
        if (string.IsNullOrWhiteSpace(deviceImei))
            throw new InvalidOperationException(IsPcscReader
                ? "请先在设备页填写并保存该 PC/SC 卡槽的真实 VoWiFi IMEI，再下载 eSIM Profile。"
                : "当前卡槽尚未读取到 IMEI，无法下载 eSIM Profile。");
        return await mgr.DownloadProfileAsync(activationCode, deviceImei, confirmationCode, progress, ct, allowUntrustedTls, allowRetryAfterUncertain, targetEuicc.Aid).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves an operation to one independently selectable ISD-R AID and
    /// verifies that its profile belongs to that exact eUICC storage.
    /// </summary>
    private async Task<VerifiedEuiccTarget> GetVerifiedEuiccManagerAsync(string? expectedProfile, string? requestedAid, CancellationToken ct)
    {
        var mgr = GetOrCreateEuiccManager();
        if (mgr == null) throw new InvalidOperationException("当前卡槽模组未就绪。请先读取 eUICC 芯片。 ");

        var inventory = await mgr.GetInventoryAsync(ct).ConfigureAwait(false);
        var probed = LastEuiccProbe?.Inventory ?? Array.Empty<EuiccInventoryEntry>();
        if (probed.Count > 0 && !probed.Any(previous => inventory.Any(current =>
            current.Eid.Equals(previous.Eid, StringComparison.OrdinalIgnoreCase) &&
            current.Aid.Equals(previous.Aid, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                "当前 eUICC 身份已与上次探测结果不同。为避免向另一颗芯片写入，请重新读取 eUICC 芯片后再操作。");
        }

        var candidates = string.IsNullOrWhiteSpace(requestedAid)
            ? inventory
            : inventory.Where(entry => entry.Aid.Equals(requestedAid, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "目标 eUICC ISD-R 不在当前卡片中。请重新读取芯片列表后再操作。");
        }

        if (!string.IsNullOrWhiteSpace(expectedProfile))
        {
            candidates = candidates.Where(entry => entry.Profiles.Any(profile =>
                profile.ICCID.Equals(expectedProfile, StringComparison.OrdinalIgnoreCase) ||
                profile.ISDPAID.Equals(expectedProfile, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    "目标 Profile 不属于指定 eUICC 芯片。请重新读取芯片列表并确认 EID/Profile 后再操作。");
            }
        }

        if (candidates.Count > 1)
            throw new InvalidOperationException("该操作对应多个 eUICC 芯片；请在目标 eUICC 下拉列表中明确选择后重试。");

        var selected = candidates[0];
        return new VerifiedEuiccTarget(mgr, selected.Aid);
    }

    private sealed record VerifiedEuiccTarget(EuiccManager Manager, string Aid);

    private void SetError(string msg)
    {
        LastError = msg;
        LastSeen = DateTime.UtcNow;
        SetState(SlotState.Error);
        _eventBus.Publish("slot.error", Id, msg);
    }

    public object GetDiagnosticInfo()
    {
        return new
        {
            Id,
            Name,
            PortName,
            BaudRate,
            State = State.ToString(),
            IsActive,
            Imei,
            Sim = Sim != null ? new
            {
                Sim.Imsi,
                Sim.Iccid,
                Sim.PhoneNumber,
                Sim.Mcc,
                Sim.Mnc,
                Sim.OperatorName,
                DetectedCarrier = CarrierName
            } : null,
            Signal = Signal != null ? new
            {
                Signal.RssiDbm,
                Signal.Bars,
                Signal.Rat
            } : null,
            Registration = Registration?.Status.ToString(),
            VoWifiState = VoWifi.State.ToString(),
            VoWifiTunnelIp = VoWifi.AssignedIp,
            ProxyUrl = ProxyUrl ?? "direct",
            LastSeen,
            LastError
        };
    }

    public async ValueTask DisposeAsync()
    {
        InvalidateSmsIdentity();
        NotifyHostImsManualStop();
        _cellularCallMonitorCts?.Cancel();
        if (Volatile.Read(ref _applicationExitRequested) != 0)
        {
            try { await StopHostImsRegistrationWithSignalingAsync().ConfigureAwait(false); } catch { }
        }
        else
            try { await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false); } catch { }
        try { await StopHostImsPppBearerAsync().ConfigureAwait(false); } catch { }
        if (Volatile.Read(ref _applicationExitRequested) == 0)
            try { await HangupAsync().ConfigureAwait(false); } catch { }
        try { await VoWifi.StopVoWifiAsync().ConfigureAwait(false); } catch { }
        VoWifi.Dispose();
        Calls.Dispose();

        if (Modem != null)
        {
            try { await Modem.DisposeAsync().ConfigureAwait(false); } catch { }
            Modem = null;
        }

        if (Aka is IDisposable aka)
        {
            try { aka.Dispose(); } catch { }
        }

        SetState(SlotState.Offline);
        _cellularCallMonitorCts?.Dispose();
        _cellularCallMonitorCts = null;
    }
}
