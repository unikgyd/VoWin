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
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel.Pool;

public enum SlotState
{
    Offline,
    Online,
    Busy,
    Error
}

/// <summary>
/// Encapsulates a single modem hardware instance, its SIM identity, AKA credentials,
/// VoWiFi tunnel manager, IMS call manager, and dedicated SOCKS5 proxy configuration.
/// </summary>
public class ModemSlot : IAsyncDisposable, INotifyPropertyChanged
{
    private readonly SemaphoreSlim _profileSwitchGate = new(1, 1);
    private readonly SimIdentityHistoryStore _identityHistory;
    private int _profileSwitchInProgress;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public string Id { get; }

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

    private SimIdentity? _sim;
    public SimIdentity? Sim
    {
        get => _sim;
        private set
        {
            if (_sim != value)
            {
                var oldIccid = _sim?.Iccid;
                _sim = value;
                if (!string.Equals(oldIccid, value?.Iccid, StringComparison.Ordinal))
                {
                    CardNickname = null;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(CarrierName));
                OnPropertyChanged(nameof(DisplayTitle));
            }
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

    public string DisplayTitle
    {
        get
        {
            var moduleName = !string.IsNullOrWhiteSpace(Name) ? Name : "模组";
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
    private readonly object _lock = new();
    private readonly object _cellularCallLock = new();
    private string? _cellularCallId;
    private string? _cellularCallerNumber;
    private CallState _cellularCallState = CallState.Idle;
    private DateTime? _cellularCallStartedAt;
    private DateTime? _cellularCallConnectedAt;
    private bool _cellularCallIsOutgoing;
    private CancellationTokenSource? _cellularCallMonitorCts;
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

    public ModemSlot(
        string id,
        string portName,
        int baudRate = 115200,
        string? name = null,
        string? proxyUrl = null,
        AsyncEventBus? eventBus = null,
        SimIdentityHistoryStore? identityHistory = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        PortName = portName ?? throw new ArgumentNullException(nameof(portName));
        BaudRate = baudRate;
        Name = name ?? $"Slot {id} ({portName})";
        ProxyUrl = proxyUrl;
        _eventBus = eventBus ?? new AsyncEventBus();
        _identityHistory = identityHistory ?? new SimIdentityHistoryStore();

        VoWifi = new VoWifiManager(_eventBus)
        {
            ProxyUrl = proxyUrl
        };
        Calls = new ImsCallManager(_eventBus);
        VoWifi.Calls = Calls;

        Calls.IncomingCall += (s, e) =>
        {
            RaiseIncomingCall(new IncomingCallEventArgs(e.CallId, e.CallerNumber, e.DisplayName, e.IsVoWifi, e.Timestamp, Id));
        };
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
    public async Task<bool> AttachAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            LastError = null;
        }

        try
        {
            var driver = new ModemDriver(PortName, BaudRate, _eventBus);
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
            var reportedImsi = await driver.GetImsiAsync(ct).ConfigureAwait(false);
            var permanentImsi = await driver.GetPermanentImsiAsync(ct).ConfigureAwait(false);
            var imsi = !string.IsNullOrWhiteSpace(permanentImsi) ? permanentImsi : reportedImsi;
            var iccid = await driver.GetIccidAsync(ct).ConfigureAwait(false);
            var phoneNumber = await driver.GetPhoneNumberAsync(ct).ConfigureAwait(false);
            var providerName = await driver.GetServiceProviderNameAsync(ct).ConfigureAwait(false);
            var mncLength = await driver.GetHomeMncLengthAsync(ct).ConfigureAwait(false);
            var homePlmns = await driver.GetHomePlmnsAsync(ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(imsi))
            {
                var identity = SimIdentity.FromImsiAndIccid(
                    imsi,
                    iccid,
                    string.IsNullOrWhiteSpace(providerName) ? null : providerName,
                    phoneNumber,
                    mncLength,
                    homePlmns);
                SetVerifiedSimIdentity(identity);
            }

            // Initialize 3GPP Phase 2+ SMS mode, storage, and real-time indications (AT+CNMI)
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
        if (Modem == null || !Modem.IsOpen) return;
        if (Calls.ActiveCall != null) return; // avoid baseband collision during call

        try
        {
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

    /// <summary>
    /// Refreshes SIM identity (e.g. after profile or slot switch) by querying IMSI and ICCID.
    /// </summary>
    public async Task RefreshSimAsync(CancellationToken ct = default)
    {
        if (Modem == null || !Modem.IsOpen) return;

        try
        {
            await Modem.RefreshSimAsync(ct, preserveFlightMode: IsFlightMode).ConfigureAwait(false);
            var identity = await ReadSimIdentityUntilReadyAsync(expectedIccid: null, previousIccid: null, ct: ct)
                .ConfigureAwait(false);
            SetVerifiedSimIdentity(identity);
            if (!IsFlightMode)
            {
                Signal = await Modem.GetSignalAsync(ct).ConfigureAwait(false);
                Registration = await Modem.GetRegistrationAsync(ct).ConfigureAwait(false);
            }
            LastSeen = DateTime.UtcNow;
        }
        catch { }
    }

    /// <summary>
    /// Starts VoWiFi session for this slot, using the slot's SIM and dedicated SOCKS proxy.
    /// </summary>
    public async Task<bool> StartVoWifiAsync(string? customEpdg = null, IkeProposalSuite suite = IkeProposalSuite.Auto, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _profileSwitchInProgress) != 0)
            throw new InvalidOperationException($"Slot {Id} is switching eSIM profiles; VoWiFi cannot start until the new SIM identity is verified.");

        if (Sim == null)
            throw new InvalidOperationException($"Slot {Id} has no active SIM identity.");

        VoWifi.ProxyUrl = ProxyUrl;
        if (!await Aka.CheckReadyAsync(Sim.Iccid, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"Slot {Id} live USIM does not match the refreshed VoWiFi identity. Registration was blocked before EAP-AKA.");

        var candidates = new List<SimIdentity>();
        AddCandidate(VoWifiIdentityOverride);
        foreach (var learned in _identityHistory.GetCandidates(Sim)) AddCandidate(learned);
        AddCandidate(Sim);

        bool started = await VoWifi.StartVoWifiAsync(candidates, customEpdg, suite, proxyUrl: ProxyUrl, ct: ct).ConfigureAwait(false);
        if (started)
        {
            LastSeen = DateTime.UtcNow;
            var successfulImsi = VoWifi.EpdgInfo?.Impi.Split('@')[0];
            var successfulIdentity = candidates.FirstOrDefault(candidate => candidate.Imsi == successfulImsi);
            if (successfulIdentity != null)
                _identityHistory.MarkSuccessful(successfulIdentity);
        }
        return started;

        void AddCandidate(SimIdentity? identity)
        {
            if (identity != null && identity.Iccid == Sim.Iccid &&
                candidates.All(candidate => candidate.Imsi != identity.Imsi))
                candidates.Add(identity);
        }
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

    /// <summary>
    /// Dials through the cellular baseband and emits the same call events used
    /// by VoWiFi so GUI clients have one transport-independent call model.
    /// </summary>
    public async Task<CallInfo> DialCellularAsync(string number, CancellationToken ct = default)
    {
        if (Modem == null || !Modem.IsOpen)
            throw new InvalidOperationException($"Slot {Id} cellular modem is not ready.");

        var callId = Guid.NewGuid().ToString("N");
        var startedAt = DateTime.UtcNow;
        lock (_cellularCallLock)
        {
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
        if (_cellularCallMonitorCts is { IsCancellationRequested: false }) return;

        _cellularCallMonitorCts?.Dispose();
        _cellularCallMonitorCts = new CancellationTokenSource();
        var token = _cellularCallMonitorCts.Token;
        _ = Task.Run(() => MonitorCellularCallAsync(token), token);
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

        if (Modem != null && Modem.IsOpen && HasCellularCall)
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

            await Modem.HangupVoiceAsync(ct).ConfigureAwait(false);
            EndCellularCall("LOCAL_HANGUP");
            return new CallInfo(callId, number, CallState.Ended, startedAt, connectedAt, DateTime.UtcNow, "Cellular", null, isOutgoing);
        }

        return null;
    }

    /// <summary>
    /// Sets flight mode (airplane mode) for this slot.
    /// </summary>
    public async Task<bool> SetFlightModeAsync(bool enabled, CancellationToken ct = default)
    {
        if (Modem == null || !Modem.IsOpen) return false;
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
                await RefreshSimAsync(ct).ConfigureAwait(false);
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
        if (Modem == null || !Modem.IsOpen || IsFlightMode) return false;
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
    {
        if (Modem == null || !Modem.IsOpen) return false;
        var resp = await Modem.SendRawAtCommandAsync("AT+CFUN=1,1", 5000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    // ── eUICC / eSIM Operations ──────────────────────────────────────────────
    private EuiccManager? _euicc;
    public EuiccManager? Euicc => GetOrCreateEuiccManager();

    public EuiccManager? GetOrCreateEuiccManager()
    {
        if (_euicc != null) return _euicc;
        if (Modem == null) return null;
        _euicc = new EuiccManager(new AtModemEuiccTransport(Modem), _eventBus);
        return _euicc;
    }

    public async Task<string> GetEuiccEidAsync(CancellationToken ct = default)
    {
        var mgr = GetOrCreateEuiccManager();
        if (mgr == null) throw new InvalidOperationException("当前卡槽模组未就绪或串口未打开。");
        return await mgr.GetEIDAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(CancellationToken ct = default)
    {
        var mgr = GetOrCreateEuiccManager();
        if (mgr == null) return Array.Empty<Profile>();
        return await mgr.ListProfilesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        await _profileSwitchGate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _profileSwitchInProgress, 1);
        try
        {
            var mgr = GetOrCreateEuiccManager();
            if (mgr == null || Modem == null || !Modem.IsOpen)
                throw new InvalidOperationException("当前卡槽模组未就绪。");

            // Stop the old tunnel and its automatic recovery loop before the
            // eUICC changes which USIM application is active.
            await VoWifi.StopVoWifiAsync(ct).ConfigureAwait(false);

            var previousIccid = Sim?.Iccid;
            VoWifiIdentityOverride = null;
            Sim = null;
            ClearRadioMetrics();
            try { SimChanged?.Invoke(this, new SimStateChangedEventArgs(null, 1, "SWITCHING", Id)); } catch { }
            _eventBus.Publish(EventTopics.SystemLog, "eSIM", $"Slot {Id}: old VoWiFi identity cleared before profile switch.");

            await mgr.SwitchProfileAsync(iccidOrAid, refresh, ct).ConfigureAwait(false);

            var expectedIccid = NormalizeIccidCandidate(iccidOrAid);
            if (expectedIccid == null)
            {
                try
                {
                    expectedIccid = NormalizeIccidCandidate(
                        (await mgr.GetActiveProfileAsync(ct).ConfigureAwait(false))?.ICCID);
                }
                catch { }
            }

            // Force a baseband/SIM reload even in flight mode, then reject any
            // stale CIMI/QCCID result. A failed verification deliberately leaves
            // Sim=null so no caller can authenticate using the previous card.
            await Modem.RefreshSimAsync(ct, preserveFlightMode: IsFlightMode).ConfigureAwait(false);
            var identity = await ReadSimIdentityUntilReadyAsync(expectedIccid, previousIccid, ct)
                .ConfigureAwait(false);
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
        CancellationToken ct)
    {
        if (Modem == null || !Modem.IsOpen)
            throw new InvalidOperationException("模组已断开，无法验证切卡后的 SIM 身份。");

        var normalizedExpected = NormalizeIccidCandidate(expectedIccid);
        var normalizedPrevious = NormalizeIccidCandidate(previousIccid);
        string? lastIccid = null;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var iccid = await Modem.GetIccidAsync(ct).ConfigureAwait(false);
            var normalizedIccid = NormalizeIccidCandidate(iccid);
            lastIccid = normalizedIccid;

            var isExpectedCard = normalizedIccid != null &&
                (normalizedExpected != null
                    ? normalizedIccid.Equals(normalizedExpected, StringComparison.Ordinal)
                    : normalizedPrevious == null || !normalizedIccid.Equals(normalizedPrevious, StringComparison.Ordinal));

            if (isExpectedCard)
            {
                var reportedImsi = await Modem.GetImsiAsync(ct).ConfigureAwait(false);
                var permanentImsi = await Modem.GetPermanentImsiAsync(ct).ConfigureAwait(false);
                var imsi = !string.IsNullOrWhiteSpace(permanentImsi) ? permanentImsi : reportedImsi;
                if (imsi.Length is >= 5 and <= 16 && imsi.All(char.IsAsciiDigit))
                {
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
                        homePlmns);
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
        throw new InvalidOperationException(
            $"Profile 已切换，但未能验证新 SIM 身份（{expectedText}，当前读取 {actualText}）。已保持 VoWiFi 停止，避免使用旧卡身份注册。");
    }

    private void SetVerifiedSimIdentity(SimIdentity identity)
    {
        Sim = identity;
        _identityHistory.Observe(identity);
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

    public async Task<bool> DisableEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        var mgr = GetOrCreateEuiccManager();
        if (mgr == null) throw new InvalidOperationException("当前卡槽模组未就绪。");
        await mgr.DisableProfileAsync(iccidOrAid, refresh, ct).ConfigureAwait(false);
        await RefreshSimAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, CancellationToken ct = default)
    {
        var mgr = GetOrCreateEuiccManager();
        if (mgr == null) throw new InvalidOperationException("当前卡槽模组未就绪。");
        await mgr.RenameProfileAsync(iccidOrAid, nickname, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<EuiccDownloadResult> DownloadEuiccProfileAsync(
        string activationCode,
        string? confirmationCode = null,
        IProgress<EuiccDownloadProgress>? progress = null,
        CancellationToken ct = default,
        bool allowUntrustedTls = false,
        bool allowRetryAfterUncertain = false)
    {
        var mgr = GetOrCreateEuiccManager();
        if (mgr == null) throw new InvalidOperationException("当前卡槽模组未就绪。");
        if (string.IsNullOrWhiteSpace(Imei))
            throw new InvalidOperationException("当前卡槽尚未读取到 IMEI，无法下载 eSIM Profile。");
        return await mgr.DownloadProfileAsync(activationCode, Imei, confirmationCode, progress, ct, allowUntrustedTls, allowRetryAfterUncertain).ConfigureAwait(false);
    }

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
        _cellularCallMonitorCts?.Cancel();
        try { await HangupAsync().ConfigureAwait(false); } catch { }
        try { await VoWifi.StopVoWifiAsync().ConfigureAwait(false); } catch { }
        VoWifi.Dispose();
        Calls.Dispose();

        if (Modem != null)
        {
            try { await Modem.DisposeAsync().ConfigureAwait(false); } catch { }
            Modem = null;
        }

        SetState(SlotState.Offline);
        _cellularCallMonitorCts?.Dispose();
        _cellularCallMonitorCts = null;
    }
}
