using System.Text.Json;
using System.Text.Json.Serialization;
using VoSharp.Common.Aka;
using VoSharp.Common.Events;
using VoSharp.Common.Utils;
using VoSharp.Crypto;
using VoSharp.Euicc;
using VoSharp.Euicc.Models;
using VoSharp.Kernel.Events;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Dtmf;
using VoSharp.Telephony.Emergency;
using VoSharp.Telephony.Mmi;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel;

public record KernelCommandResult(
    bool Success,
    string Message,
    object? Data = null
)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public class VoKernel : IVoKernel
{
    public AsyncEventBus EventBus { get; }
    public TelephonyStateMachine StateMachine { get; }
    public ModemPool Pool { get; }

    private readonly VoWifiManager _fallbackVoWifi;
    private readonly ImsCallManager _fallbackCalls;
    private readonly IAkaProvider _fallbackAka = SoftwareAkaProvider.FromTestVectors();

    public ModemDriver? Modem => Pool.ActiveSlot?.Modem;
    public IAkaProvider AkaProvider => Pool.ActiveSlot?.Aka ?? _fallbackAka;
    public SimIdentity? CurrentSim => Pool.ActiveSlot?.Sim;
    public VoWifiManager VoWifi => Pool.ActiveSlot?.VoWifi ?? _fallbackVoWifi;
    public ImsCallManager Calls => Pool.ActiveSlot?.Calls ?? _fallbackCalls;
    public SmsService? SmsService => Pool.ActiveSlot?.Sms;

    private EuiccManager? _euiccManager;
    public EuiccManager? EuiccManager
    {
        get => _euiccManager;
        set
        {
            _euiccManager = value;
            if (_euiccManager != null)
            {
                HookEuiccEvents(_euiccManager);
            }
        }
    }

    public CallSession? ActiveCall { get; private set; }
    public bool RoamingAllowed { get; private set; } = true;

    // ── Call Events ──────────────────────────────────────────────────────────
    public event EventHandler<CallStateChangedEventArgs>? CallStateChanged;
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;
    public event EventHandler<CallConnectedEventArgs>? CallConnected;
    public event EventHandler<CallEndedEventArgs>? CallEnded;
    public event EventHandler<DtmfReceivedEventArgs>? DtmfReceived;

    // ── SMS Events ───────────────────────────────────────────────────────────
    public event EventHandler<SmsReceivedEventArgs>? SmsReceived;
    public event EventHandler<SmsSentEventArgs>? SmsSent;
    public event EventHandler<SmsStatusReportEventArgs>? SmsStatusReportReceived;

    // ── VoWiFi & IMS Events ──────────────────────────────────────────────────
    public event EventHandler<VoWifiStateChangedEventArgs>? VoWifiStateChanged;

    // ── Cellular & Radio Events ──────────────────────────────────────────────
    public event EventHandler<SignalChangedEventArgs>? SignalQualityChanged;
    public event EventHandler<NetworkRegistrationChangedEventArgs>? NetworkRegistrationChanged;
    public event EventHandler<SimStateChangedEventArgs>? SimStateChanged;
    public event EventHandler<ModemConnectionChangedEventArgs>? ModemConnectionChanged;
    public event EventHandler<FlightModeChangedEventArgs>? FlightModeChanged;

    // ── Multi-Modem Pool & Slot Events ───────────────────────────────────────
    public event EventHandler<SlotStateChangedEventArgs>? SlotStateChanged;
    public event EventHandler<ActiveSlotChangedEventArgs>? ActiveSlotChanged;
    public event EventHandler<SlotListChangedEventArgs>? SlotListChanged;

    // ── State Machine Events ─────────────────────────────────────────────────
    public event EventHandler<TelephonyStateChangedEventArgs>? TelephonyStateChanged;

    // ── eUICC / eSIM Events ──────────────────────────────────────────────────
    public event EventHandler<EuiccProfilesChangedEventArgs>? EuiccProfilesUpdated;
    public event EventHandler<EuiccOperationEventArgs>? EuiccOperationCompleted;

    // ── Diagnostics & Logging Events ─────────────────────────────────────────
    public event EventHandler<LogEmittedEventArgs>? LogEmitted;
    public event EventHandler<SystemErrorEventArgs>? SystemErrorOccurred;

    private readonly List<SmsMessage> _inbox = new();
    private readonly object _inboxLock = new();
    public IReadOnlyList<SmsMessage> Inbox
    {
        get
        {
            lock (_inboxLock) return _inbox.ToList();
        }
    }

    private readonly List<SmsMessage> _outbox = new();
    private readonly object _outboxLock = new();
    public IReadOnlyList<SmsMessage> Outbox
    {
        get
        {
            lock (_outboxLock) return _outbox.ToList();
        }
    }

    private readonly CancellationTokenSource _cts = new();
    private Task? _telemetryTask;
    private readonly object _telemetryLock = new();

    public VoKernel(AsyncEventBus? eventBus = null, TelephonyStateMachine? stateMachine = null)
    {
        EventBus = eventBus ?? new AsyncEventBus();
        StateMachine = stateMachine ?? new TelephonyStateMachine();
        StateMachine.ConnectEventBus(EventBus);
        Pool = new ModemPool(EventBus);
        _fallbackVoWifi = new VoWifiManager(EventBus);
        _fallbackCalls = new ImsCallManager(EventBus);
        _fallbackVoWifi.Calls = _fallbackCalls;

        RegisterInternalEventHandlers();
    }

    private void RegisterInternalEventHandlers()
    {
        StateMachine.StateChanged += (s, e) =>
        {
            try { TelephonyStateChanged?.Invoke(this, e); } catch { }
        };

        Pool.SlotAdded += (s, slot) =>
        {
            HookSlotEvents(slot);
        };
        Pool.ActiveSlotChanged += (s, e) =>
        {
            try { ActiveSlotChanged?.Invoke(this, e); } catch { }
        };
        Pool.SlotStateChanged += (s, e) =>
        {
            try { SlotStateChanged?.Invoke(this, e); } catch { }
        };
        Pool.SlotListChanged += (s, e) =>
        {
            try { SlotListChanged?.Invoke(this, e); } catch { }
        };

        _fallbackCalls.CallStateChanged += (s, e) =>
        {
            try { CallStateChanged?.Invoke(this, e); } catch { }
        };
        _fallbackCalls.IncomingCall += (s, e) =>
        {
            try { IncomingCall?.Invoke(this, e); } catch { }
        };
        _fallbackCalls.CallConnected += (s, e) =>
        {
            try { CallConnected?.Invoke(this, e); } catch { }
        };
        _fallbackCalls.CallEnded += (s, e) =>
        {
            try { CallEnded?.Invoke(this, e); } catch { }
        };
        _fallbackCalls.DtmfReceived += (s, e) =>
        {
            try { DtmfReceived?.Invoke(this, e); } catch { }
        };
        _fallbackVoWifi.StateChanged += (s, e) =>
        {
            try { VoWifiStateChanged?.Invoke(this, e); } catch { }
        };

        EventBus.Subscribe(EventTopics.SystemError, ev =>
        {
            try { SystemErrorOccurred?.Invoke(this, new SystemErrorEventArgs(ev.Source, ev.Payload?.ToString() ?? string.Empty)); } catch { }
        });
        EventBus.Subscribe(EventTopics.SystemLog, ev =>
        {
            try { LogEmitted?.Invoke(this, new LogEmittedEventArgs("INFO", ev.Source, ev.Payload?.ToString() ?? string.Empty)); } catch { }
        });

        EventBus.Subscribe(EventTopics.CallIncoming, ev =>
        {
            var caller = ev.Payload?.ToString() ?? "Unknown";
            ActiveCall = new CallSession(caller, isOutgoing: false);
        });

        EventBus.Subscribe(EventTopics.CallDialing, ev =>
        {
            var dest = ev.Payload?.ToString() ?? "Unknown";
            ActiveCall = new CallSession(dest, isOutgoing: true);
        });

        EventBus.Subscribe(EventTopics.CallEnded, ev =>
        {
            ActiveCall?.End();
            ActiveCall = null;
        });

        EventBus.Subscribe(EventTopics.SmsStatusReport, ev =>
        {
            if (ev.Payload is SmsStatusReport rep)
            {
                RecordStatusReport(rep);
            }
        });

        EventBus.Subscribe(EventTopics.SmsSent, ev =>
        {
            if (ev.Payload is SmsSubmitResult subRes)
            {
                RecordOutboxSubmission(subRes);
            }
        });

        EventBus.Subscribe(EventTopics.SmsIncoming, async ev =>
        {
            if (ev.Payload == null) return;

            if (ev.Payload is IncomingSms incoming)
            {
                if (incoming.IsStatusReport && incoming.StatusReport != null)
                {
                    RecordStatusReport(incoming.StatusReport);
                    return;
                }

                SmsMessage? newMsg = null;
                lock (_inboxLock)
                {
                    if (!_inbox.Any(m => m.SenderOrRecipient == incoming.SenderNumber && m.Text == incoming.Text && m.Timestamp == incoming.Timestamp))
                    {
                        newMsg = new SmsMessage(
                            Index: _inbox.Count + 1,
                            Status: SmsStatus.Unread,
                            SenderOrRecipient: incoming.SenderNumber,
                            Text: incoming.Text,
                            Timestamp: incoming.Timestamp,
                            RawPdu: incoming.RawPdu,
                            Direction: SmsDirection.Received,
                            ServiceCenterTimestamp: incoming.ServiceCenterTimestamp,
                            Concat: incoming.Concat
                        );
                        _inbox.Add(newMsg);
                    }
                }

                if (newMsg != null)
                {
                    try { SmsReceived?.Invoke(this, new SmsReceivedEventArgs(newMsg, "VoWiFi")); } catch { }
                    EventBus.Publish(EventTopics.SmsReceived, "VoWiFi", newMsg);
                }
                return;
            }

            if (ev.Payload is SmsMessage directMsg)
            {
                if (directMsg.Direction == SmsDirection.StatusReport && directMsg.MessageReference.HasValue)
                {
                    RecordStatusReport(new SmsStatusReport(
                        MessageReference: directMsg.MessageReference.Value,
                        Recipient: directMsg.SenderOrRecipient,
                        StatusCode: directMsg.StatusCode ?? 0,
                        DeliveryStatus: directMsg.DeliveryStatus ?? "delivered",
                        ServiceCenterTimestamp: directMsg.ServiceCenterTimestamp,
                        DischargeTimestamp: directMsg.DischargeTimestamp,
                        Timestamp: directMsg.Timestamp,
                        RawPdu: directMsg.RawPdu
                    ));
                    return;
                }

                bool added = false;
                lock (_inboxLock)
                {
                    if (!_inbox.Any(m => m.Index == directMsg.Index && m.Text == directMsg.Text))
                    {
                        _inbox.Add(directMsg);
                        added = true;
                    }
                }

                if (added)
                {
                    try { SmsReceived?.Invoke(this, new SmsReceivedEventArgs(directMsg)); } catch { }
                }
                return;
            }

            if (SmsService != null)
            {
                try
                {
                    int idx = 0;
                    if (ev.Payload is int i) idx = i;
                    else if (int.TryParse(ev.Payload.ToString(), out var pIdx)) idx = pIdx;
                    else
                    {
                        var json = JsonSerializer.Serialize(ev.Payload);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("Index", out var idxProp) && idxProp.TryGetInt32(out var parsedIdx))
                            idx = parsedIdx;
                    }

                    if (idx > 0)
                    {
                        var sms = await SmsService.ReadSmsAsync(idx).ConfigureAwait(false);
                        if (sms != null)
                        {
                            if (sms.Direction == SmsDirection.StatusReport && sms.MessageReference.HasValue)
                            {
                                RecordStatusReport(new SmsStatusReport(
                                    MessageReference: sms.MessageReference.Value,
                                    Recipient: sms.SenderOrRecipient,
                                    StatusCode: sms.StatusCode ?? 0,
                                    DeliveryStatus: sms.DeliveryStatus ?? "delivered",
                                    ServiceCenterTimestamp: sms.ServiceCenterTimestamp,
                                    DischargeTimestamp: sms.DischargeTimestamp,
                                    Timestamp: sms.Timestamp,
                                    RawPdu: sms.RawPdu
                                ));
                                return;
                            }

                            lock (_inboxLock)
                            {
                                if (!_inbox.Any(m => m.Index == sms.Index && m.Timestamp == sms.Timestamp))
                                    _inbox.Add(sms);
                            }
                            try { SmsReceived?.Invoke(this, new SmsReceivedEventArgs(sms)); } catch { }
                            EventBus.Publish(EventTopics.SmsReceived, "Kernel", sms);
                        }
                    }
                }
                catch { }
            }
        });
    }

    private void HookSlotEvents(ModemSlot slot)
    {
        slot.SignalChanged += (s, e) =>
        {
            try { SignalQualityChanged?.Invoke(this, e); } catch { }
        };
        slot.RegistrationChanged += (s, e) =>
        {
            try { NetworkRegistrationChanged?.Invoke(this, e); } catch { }
        };
        slot.SimChanged += (s, e) =>
        {
            try { SimStateChanged?.Invoke(this, e); } catch { }
        };
        slot.IncomingCall += (s, e) =>
        {
            ActiveCall = new CallSession(e.CallerNumber, isOutgoing: false);
            try { IncomingCall?.Invoke(this, e); } catch { }
        };
        slot.SmsReceived += (s, e) =>
        {
            var added = false;
            lock (_inboxLock)
            {
                if (!_inbox.Any(m => m.Index == e.Message.Index && m.SenderOrRecipient == e.Message.SenderOrRecipient && m.Timestamp == e.Message.Timestamp))
                {
                    _inbox.Add(e.Message);
                    added = true;
                }
            }
            if (added)
            {
                try { SmsReceived?.Invoke(this, e); } catch { }
                EventBus.Publish(EventTopics.SmsReceived, e.SlotId ?? "Kernel", e.Message);
            }
        };
        slot.SmsSent += (s, e) =>
        {
            try { SmsSent?.Invoke(this, e); } catch { }
        };
        slot.SmsStatusReportReceived += (s, e) =>
        {
            RecordStatusReport(e.Report);
            try { SmsStatusReportReceived?.Invoke(this, e); } catch { }
            EventBus.Publish(EventTopics.SmsStatusReport, e.SlotId ?? "Kernel", e.Report);
        };
        slot.VoWifi.StateChanged += (s, e) =>
        {
            try { VoWifiStateChanged?.Invoke(this, e); } catch { }
        };
        slot.Calls.CallStateChanged += (s, e) =>
        {
            try { CallStateChanged?.Invoke(this, e); } catch { }
        };
        slot.CallStateChanged += (s, e) =>
        {
            if (e.NewState == CallState.Active)
                ActiveCall?.Connect();
            else if (e.NewState == CallState.Ended)
            {
                ActiveCall?.End();
                ActiveCall = null;
            }
            try { CallStateChanged?.Invoke(this, e); } catch { }
        };
        slot.Calls.CallConnected += (s, e) =>
        {
            try { CallConnected?.Invoke(this, e); } catch { }
        };
        slot.CallConnected += (s, e) =>
        {
            ActiveCall?.Connect();
            try { CallConnected?.Invoke(this, e); } catch { }
        };
        slot.Calls.CallEnded += (s, e) =>
        {
            try { CallEnded?.Invoke(this, e); } catch { }
        };
        slot.CallEnded += (s, e) =>
        {
            ActiveCall?.End();
            ActiveCall = null;
            try { CallEnded?.Invoke(this, e); } catch { }
        };
        slot.Calls.DtmfReceived += (s, e) =>
        {
            try { DtmfReceived?.Invoke(this, e); } catch { }
        };

        // AttachAsync can receive a call or read unread SMS before SlotAdded is
        // raised. Deliver those buffered events once all Kernel handlers exist.
        slot.FlushPendingEvents();

        if (slot.Modem != null)
        {
            slot.Modem.ConnectionChanged += (s, e) =>
            {
                try { ModemConnectionChanged?.Invoke(this, e); } catch { }
            };
            slot.Modem.FlightModeChanged += (s, e) =>
            {
                try { FlightModeChanged?.Invoke(this, e); } catch { }
            };
        }
    }

    private void HookEuiccEvents(EuiccManager euicc)
    {
        euicc.ProfilesUpdated += (s, e) =>
        {
            try { EuiccProfilesUpdated?.Invoke(this, e); } catch { }
        };
        euicc.OperationCompleted += (s, e) =>
        {
            try { EuiccOperationCompleted?.Invoke(this, e); } catch { }
        };
    }

    private void RecordOutboxSubmission(SmsSubmitResult subRes)
    {
        lock (_outboxLock)
        {
            int firstMr = subRes.PartResults.FirstOrDefault()?.Reference ?? 0;
            if (!_outbox.Any(m => m.SenderOrRecipient == subRes.Recipient && m.Text == subRes.Text && Math.Abs((m.Timestamp - subRes.SubmittedAt).TotalSeconds) < 2))
            {
                _outbox.Add(new SmsMessage(
                    Index: _outbox.Count + 1,
                    Status: SmsStatus.Sent,
                    SenderOrRecipient: subRes.Recipient,
                    Text: subRes.Text,
                    Timestamp: subRes.SubmittedAt,
                    Direction: SmsDirection.Submitted,
                    MessageReference: firstMr,
                    DeliveryStatus: "pending"
                ));
            }
        }
        try { SmsSent?.Invoke(this, new SmsSentEventArgs(subRes)); } catch { }
    }

    private void RecordStatusReport(SmsStatusReport rep)
    {
        lock (_outboxLock)
        {
            // Find matching sent message by Message Reference and/or Recipient
            var match = _outbox.LastOrDefault(m => m.MessageReference == rep.MessageReference || (m.SenderOrRecipient.EndsWith(rep.Recipient) && m.DeliveryStatus == "pending"));
            if (match != null)
            {
                int matchIdx = _outbox.IndexOf(match);
                _outbox[matchIdx] = match with
                {
                    DeliveryStatus = rep.DeliveryStatus,
                    DischargeTimestamp = rep.DischargeTimestamp ?? rep.Timestamp,
                    StatusCode = rep.StatusCode
                };
            }
        }
        try { SmsStatusReportReceived?.Invoke(this, new SmsStatusReportEventArgs(rep)); } catch { }
    }

    /// <summary>Builds a software Milenage provider, using explicit K/OPc when supplied.</summary>
    private static SoftwareAkaProvider BuildSoftwareAkaProvider(string[] parts)
    {
        var extras = parts.Skip(3)
            .Where(p => !p.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        if (extras.Length < 2)
            return SoftwareAkaProvider.FromTestVectors("Software Milenage (3GPP TS 35.208 test vectors)");

        return new SoftwareAkaProvider(
            HexUtils.FromHexString(extras[0]),
            HexUtils.FromHexString(extras[1]),
            "Software Milenage (explicit K/OPc)");
    }

    public KernelSnapshot CreateSnapshot()
    {
        var slot = Pool.ActiveSlot;
        return new KernelSnapshot(
            State: StateMachine.CurrentState,
            StateDescription: StateMachine.CurrentState.ToString(),
            ModemPort: slot?.PortName ?? Modem?.PortName,
            IsModemConnected: slot?.Modem?.IsOpen ?? Modem?.IsOpen ?? false,
            Sim: CurrentSim ?? slot?.Sim,
            Signal: slot?.Signal,
            Network: slot?.Registration,
            VoWifiState: VoWifi.State,
            VoWifiIp: VoWifi.AssignedIp,
            ActiveCall: Calls.ActiveCall,
            InboxCount: Inbox.Count,
            OutboxCount: Outbox.Count,
            ActiveSlotId: Pool.ActiveSlotId,
            TotalSlots: Pool.Slots.Count,
            Timestamp: DateTime.UtcNow
        );
    }

    // ── Direct Call Operations ───────────────────────────────────────────────
    public async Task<CallInfo> DialAsync(string number, string? slotId = null, bool forceCellular = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);

        ModemSlot? callSlot = null;
        if (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var explicitSlot))
            callSlot = explicitSlot;
        else
            callSlot = Pool.FindSlotForTarget(number) ?? Pool.ActiveSlot ?? Pool.Slots.Values.FirstOrDefault();

        var voWifiToUse = callSlot?.VoWifi ?? VoWifi;
        var callsToUse = callSlot?.Calls ?? Calls;

        if (EmergencyService.IsEmergencyNumber(number))
        {
            throw new NotSupportedException("IMS emergency calling is not supported without cellular emergency bearer.");
        }

        // If VoWiFi is already registered and cellular is not forced, route via VoWiFi HD voice
        if (!forceCellular && voWifiToUse.State == VoWifiState.ImsRegistered)
        {
            var dialed = await callsToUse.DialAsync(number, voWifiToUse, ct).ConfigureAwait(false);
            ActiveCall = new CallSession(dialed.TargetNumber, isOutgoing: true);
            return dialed;
        }

        // Standard phone call via cellular baseband (ATD) for normal mobile SIM cards
        if (callSlot?.Modem != null && callSlot.Modem.IsOpen)
        {
            ActiveCall = new CallSession(number, isOutgoing: true);
            return await callSlot.DialCellularAsync(number, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("当前卡槽通信模组未连接或串口未就绪。");
    }

    public async Task<CallInfo?> HangupAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        var callsToUse = slot?.Calls ?? Calls;

        if (callsToUse.ActiveCall != null)
        {
            var ended = await callsToUse.HangupAsync().ConfigureAwait(false);
            ActiveCall = null;
            return ended;
        }

        if (slot?.Modem != null && slot.Modem.IsOpen)
        {
            var ended = await slot.HangupAsync(ct).ConfigureAwait(false);
            ActiveCall = null;
            return ended;
        }

        return null;
    }

    public async Task<CallInfo?> AnswerAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        if (slot != null)
        {
            return await slot.AnswerCallAsync(ct).ConfigureAwait(false);
        }
        return await Calls.AnswerAsync(ct).ConfigureAwait(false);
    }

    public async Task<CallInfo?> RejectAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        if (slot != null)
        {
            return await slot.RejectCallAsync(ct).ConfigureAwait(false);
        }
        return await Calls.RejectAsync(ct: ct).ConfigureAwait(false);
    }

    public async Task<bool> SendDtmfAsync(char digit, string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        var callsToUse = slot?.Calls ?? Calls;

        if (callsToUse.ActiveCall != null)
        {
            await callsToUse.SendDtmfAsync(digit).ConfigureAwait(false);
            return true;
        }

        if (slot?.Modem != null && slot.Modem.IsOpen)
        {
            var res = await slot.Modem.SendRawAtCommandAsync($"AT+VTS={digit}", 2000, ct).ConfigureAwait(false);
            return res.Success;
        }

        return false;
    }

    // ── Direct SMS Operations ────────────────────────────────────────────────
    public async Task<SmsSubmitResult> SendSmsAsync(
        string recipient,
        string text,
        bool requestStatusReport = true,
        string? slotId = null,
        bool forceVowifi = false,
        bool forceCellular = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        ModemSlot? slot = null;
        if (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var sSlot))
            slot = sSlot;
        else
            slot = Pool.FindSlotForTarget(recipient) ?? Pool.ActiveSlot ?? Pool.Slots.Values.FirstOrDefault();

        var voWifiToUse = slot?.VoWifi ?? VoWifi;
        var smsServiceToUse = slot?.Sms ?? SmsService;

        if (!forceCellular && voWifiToUse.State == VoWifiState.ImsRegistered)
        {
            var result = await voWifiToUse.SendSmsOverImsAsync(recipient, text, requestStatusReport: requestStatusReport, ct: ct).ConfigureAwait(false);
            if (result.AllPartsAccepted)
            {
                RecordOutboxSubmission(result);
                return result;
            }
            if (forceVowifi)
            {
                throw new InvalidOperationException($"IMS SMS failed: {result.SubmissionStatus}");
            }
        }

        if (smsServiceToUse != null)
        {
            var detailed = await smsServiceToUse.SendSmsDetailedAsync(recipient, text, requestStatusReport, ct).ConfigureAwait(false);
            if (detailed.AllPartsAccepted)
            {
                RecordOutboxSubmission(detailed);
            }
            return detailed;
        }

        throw new InvalidOperationException("No suitable SMS transport (VoWiFi IMS or Cellular AT) is available.");
    }

    public IReadOnlyList<SmsMessage> GetInbox()
    {
        lock (_inboxLock) return _inbox.ToList();
    }

    public IReadOnlyList<SmsMessage> GetOutbox()
    {
        lock (_outboxLock) return _outbox.ToList();
    }

    public bool DeleteInboxMessage(int index)
    {
        lock (_inboxLock)
        {
            var item = _inbox.FirstOrDefault(m => m.Index == index);
            if (item != null)
            {
                _inbox.Remove(item);
                return true;
            }
        }
        return false;
    }

    public void ClearInbox()
    {
        lock (_inboxLock)
        {
            _inbox.Clear();
        }
    }

    // ── Direct VoWiFi & IMS Operations ───────────────────────────────────────
    public async Task<bool> StartVoWifiAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        if (slot != null)
        {
            return await slot.StartVoWifiAsync(ct: ct).ConfigureAwait(false);
        }
        if (CurrentSim != null)
        {
            return await VoWifi.StartVoWifiAsync(CurrentSim, ct: ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<bool> StopVoWifiAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        if (slot != null)
        {
            await slot.StopVoWifiAsync(ct).ConfigureAwait(false);
            return true;
        }
        await VoWifi.StopVoWifiAsync(ct).ConfigureAwait(false);
        return true;
    }

    public VoWifiDiagnosticInfo? GetVoWifiDiagnostics(string? slotId = null)
    {
        try
        {
            ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
            return (slot?.VoWifi ?? VoWifi)?.GetDiagnosticInfo();
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Success, long RttMs, string Status)> ProbeVoWifiLivenessAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        var vowifi = slot?.VoWifi ?? VoWifi;
        if (vowifi == null) return (false, 0, "VoWiFi 未就绪");
        return await vowifi.ProbeLivenessAsync(ct).ConfigureAwait(false);
    }

    // ── Direct Slot & Modem Pool Operations ──────────────────────────────────
    public IReadOnlyList<ModemSlot> GetSlots() => Pool.Slots.Values.ToList();
    public ModemSlot? GetActiveSlot() => Pool.ActiveSlot;
    public bool SelectSlot(string slotId) => Pool.SelectSlot(slotId);
    public bool SetSlotProxy(string slotId, string? proxyUrl) => Pool.SetSlotProxy(slotId, proxyUrl);
    public async Task<IReadOnlyList<ModemSlot>> DiscoverSlotsAsync(CancellationToken ct = default)
    {
        var slots = await Pool.DiscoverAndEnrichAsync(ct).ConfigureAwait(false);
        if (slots.Count > 0) EnsureBackgroundMonitoringStarted();
        return slots;
    }

    public async Task<ModemSlot> AddSlotAsync(string portName, int baudRate = 115200, string? name = null, string? slotId = null, string? proxyUrl = null, CancellationToken ct = default)
    {
        var slot = await Pool.AddOrUpdateSlotAsync(portName, baudRate, name, slotId, proxyUrl, ct).ConfigureAwait(false);
        if (slot.State == SlotState.Online) EnsureBackgroundMonitoringStarted();
        return slot;
    }
    public Task<bool> RemoveSlotAsync(string slotId, CancellationToken ct = default) => Pool.RemoveSlotAsync(slotId, ct);

    // ── Direct eUICC / eSIM Operations ───────────────────────────────────────
    public async Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(CancellationToken ct = default)
    {
        var mgr = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);
        return await mgr.ListProfilesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Profile?> GetActiveEuiccProfileAsync(CancellationToken ct = default)
    {
        var mgr = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);
        return await mgr.GetActiveProfileAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> GetEuiccEidAsync(CancellationToken ct = default)
    {
        var mgr = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);
        return await mgr.GetEIDAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        if (Pool.ActiveSlot != null)
            return await Pool.ActiveSlot.SwitchEuiccProfileAsync(iccidOrAid, refresh, ct).ConfigureAwait(false);

        var mgr = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);
        await mgr.SwitchProfileAsync(iccidOrAid, refresh, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DisableEuiccProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        var mgr = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);
        await mgr.DisableProfileAsync(iccidOrAid, refresh, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, CancellationToken ct = default)
    {
        var mgr = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);
        await mgr.DeleteProfileAsync(iccidOrAid, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, CancellationToken ct = default)
    {
        var mgr = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);
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
        var mgr = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);
        var imei = Modem is null ? null : await Modem.GetImeiAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(imei))
            throw new InvalidOperationException("尚未读取到模组 IMEI，无法下载 eSIM Profile。");
        return await mgr.DownloadProfileAsync(activationCode, imei, confirmationCode, progress, ct, allowUntrustedTls, allowRetryAfterUncertain).ConfigureAwait(false);
    }

    // ── Direct Metrics & Telemetry Operations ────────────────────────────────
    public async Task<SignalQuality?> RefreshSignalAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        if (slot != null)
        {
            await slot.RefreshMetricsAsync(ct).ConfigureAwait(false);
            return slot.Signal;
        }
        if (Modem != null && Modem.IsOpen)
        {
            return await Modem.GetSignalAsync(ct).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<NetworkRegistration?> RefreshRegistrationAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        if (slot != null)
        {
            await slot.RefreshMetricsAsync(ct).ConfigureAwait(false);
            return slot.Registration;
        }
        if (Modem != null && Modem.IsOpen)
        {
            return await Modem.GetRegistrationAsync(ct).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<SimIdentity?> RefreshSimAsync(string? slotId = null, CancellationToken ct = default)
    {
        ModemSlot? slot = (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var s)) ? s : Pool.ActiveSlot;
        if (slot != null)
        {
            await slot.RefreshMetricsAsync(ct).ConfigureAwait(false);
            return slot.Sim;
        }
        if (Modem != null && Modem.IsOpen)
        {
            await ReloadSimAndNetworkAsync(ct).ConfigureAwait(false);
            return CurrentSim;
        }
        return null;
    }

    public async Task<bool> AttachModemAsync(string portName, int baudRate = 115200)
    {
        try
        {
            var slot = await Pool.AddOrUpdateSlotAsync(portName, baudRate, ct: _cts.Token).ConfigureAwait(false);
            if (slot.State == SlotState.Online)
            {
                if (slot.Sim != null)
                {
                    EventBus.Publish(EventTopics.ModemSim, "Kernel", "READY");
                }

                if (slot.Registration?.Status is NetworkRegStatus.Home or NetworkRegStatus.Roaming)
                {
                    EventBus.Publish(EventTopics.NetworkRegistration, "Kernel", slot.Registration.Status.ToString().ToUpperInvariant());
                }

                // Start autonomous background telemetry & SMS monitoring loop
                EnsureBackgroundMonitoringStarted();

                return true;
            }
        }
        catch (Exception ex)
        {
            EventBus.Publish(EventTopics.SystemError, "Kernel", $"Failed to attach modem on {portName}: {ex.Message}");
        }

        return false;
    }

    private void EnsureBackgroundMonitoringStarted()
    {
        lock (_telemetryLock)
        {
            if (_telemetryTask == null || _telemetryTask.IsCompleted)
                _telemetryTask = Task.Run(() => RunBackgroundMonitoringLoopAsync(_cts.Token));
        }
    }

    private async Task RunBackgroundMonitoringLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);

                // Do not query modem AT commands while a call is active to avoid baseband/HSS signaling collisions
                if (Pool.Slots.Values.Any(slot => slot.Calls.ActiveCall != null || slot.HasCellularCall))
                {
                    continue;
                }

                foreach (var slot in Pool.Slots.Values)
                {
                    if (slot.Modem != null && slot.Modem.IsOpen)
                    {
                        // 1. Maintain signal & registration
                        await slot.RefreshMetricsAsync(ct).ConfigureAwait(false);

                        if (slot.Registration != null)
                        {
                            if (slot.Registration.Status is NetworkRegStatus.Home or NetworkRegStatus.Roaming)
                            {
                                if (StateMachine.CurrentState is TelephonyState.NetworkSearching or TelephonyState.SimReady)
                                {
                                    StateMachine.Fire(StateTrigger.TriggerNetAttach, slot.Registration.Status.ToString());
                                }
                            }
                            else if (slot.Registration.Status == NetworkRegStatus.Searching)
                            {
                                if (StateMachine.CurrentState == TelephonyState.NetworkRegistered)
                                {
                                    StateMachine.Fire(StateTrigger.TriggerNetLost);
                                }
                            }
                        }

                        // 2. Scan for any incoming SMS
                        if (slot.Sms != null)
                        {
                            var unreadMessages = await slot.Sms.ListSmsAsync(SmsStatus.Unread, ct).ConfigureAwait(false);
                            foreach (var msg in unreadMessages)
                            {
                                slot.Sms.ProcessDecodedSms(msg);
                                try { await slot.Sms.DeleteSmsAsync(msg.Index, ct).ConfigureAwait(false); } catch { }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public async Task<KernelCommandResult> ExecuteCommandAsync(string commandLine, CancellationToken ct = default)
    {
        var raw = commandLine.Trim();
        if (string.IsNullOrEmpty(raw))
            return new KernelCommandResult(true, "Empty command");

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        try
        {
            switch (cmd)
            {
                case "status":
                    return new KernelCommandResult(true, "System status retrieved", new
                    {
                        State = StateMachine.CurrentState.ToString(),
                        ActiveSlot = Pool.ActiveSlot?.Id,
                        TotalSlots = Pool.Slots.Count,
                        ModemAttached = Modem != null,
                        ModemPort = Modem?.PortName,
                        Sim = CurrentSim,
                        ActiveCall = ActiveCall != null ? new
                        {
                            ActiveCall.CallId,
                            ActiveCall.RemoteNumber,
                            ActiveCall.IsOutgoing,
                            State = ActiveCall.State.ToString(),
                            ActiveCall.StartTime
                        } : null,
                        Slots = Pool.GetSlotsDiagnostic()
                    });

                case "history":
                    var history = StateMachine.GetHistory();
                    return new KernelCommandResult(true, $"Retrieved {history.Count} state history records", history);

                case "signal":
                    if (Modem != null)
                    {
                        var sig = await Modem.GetSignalAsync(ct).ConfigureAwait(false);
                        return new KernelCommandResult(true, "Signal quality retrieved", sig);
                    }
                    return new KernelCommandResult(true, "No physical modem attached (simulated)", new SignalQuality(25, -63, 5, "LTE (Simulated)"));

                case "sim":
                    return new KernelCommandResult(true, "SIM identity retrieved", CurrentSim ?? new SimIdentity("460001234567890", "89860012345678901234", "460", "00", "China Mobile"));

                case "answer":
                    string? ansSlotId = null;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        if (parts[i].Equals("--slot", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                            ansSlotId = parts[++i];
                    }
                    ModemSlot? ansSlot = (!string.IsNullOrEmpty(ansSlotId) && Pool.Slots.TryGetValue(ansSlotId, out var aslt)) ? aslt : Pool.ActiveSlot;
                    object? answerResult;
                    string answerMessage;
                    if (ansSlot != null)
                    {
                        answerResult = await ansSlot.AnswerCallAsync(ct).ConfigureAwait(false);
                        answerMessage = $"Incoming call answered on slot '{ansSlot.Id}'";
                    }
                    else if (Calls.State == CallState.Ringing)
                    {
                        answerResult = await Calls.AnswerAsync(ct).ConfigureAwait(false);
                        answerMessage = "Incoming VoWiFi call answered";
                    }
                    else if (Modem != null)
                    {
                        if (!await Modem.AnswerVoiceAsync(ct).ConfigureAwait(false))
                            return new KernelCommandResult(false, "Cellular modem rejected the answer command.");
                        answerResult = null;
                        answerMessage = "Cellular call answered via AT";
                    }
                    else
                    {
                        return new KernelCommandResult(false, "No incoming call or connected call bearer is available.");
                    }
                    ActiveCall?.Connect();
                    StateMachine.Fire(StateTrigger.TriggerCallAnswer);
                    return new KernelCommandResult(true, answerMessage, answerResult);

                case "reject":
                    string? rejSlotId = null;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        if (parts[i].Equals("--slot", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                            rejSlotId = parts[++i];
                    }
                    ModemSlot? rejSlot = (!string.IsNullOrEmpty(rejSlotId) && Pool.Slots.TryGetValue(rejSlotId, out var rslt)) ? rslt : Pool.ActiveSlot;
                    object? rejectResult;
                    string rejectMessage;
                    if (rejSlot != null)
                    {
                        rejectResult = await rejSlot.RejectCallAsync(ct).ConfigureAwait(false);
                        rejectMessage = $"Incoming call rejected on slot '{rejSlot.Id}'";
                    }
                    else if (Calls.State == CallState.Ringing)
                    {
                        rejectResult = await Calls.RejectAsync(ct: ct).ConfigureAwait(false);
                        rejectMessage = "Incoming VoWiFi call rejected";
                    }
                    else if (Modem != null)
                    {
                        if (!await Modem.HangupVoiceAsync(ct).ConfigureAwait(false))
                            return new KernelCommandResult(false, "Cellular modem rejected the reject command.");
                        rejectResult = null;
                        rejectMessage = "Cellular call rejected via ATH";
                    }
                    else
                    {
                        return new KernelCommandResult(false, "No incoming call or connected call bearer is available.");
                    }
                    ActiveCall?.End();
                    ActiveCall = null;
                    StateMachine.Fire(StateTrigger.TriggerCallHangup);
                    return new KernelCommandResult(true, rejectMessage, rejectResult);

                case "hangup":
                    CallInfo? imsHangup = null;
                    if (Calls.ActiveCall != null)
                    {
                        imsHangup = await Calls.HangupAsync().ConfigureAwait(false);
                    }
                    else if (Modem != null)
                    {
                        if (!await Modem.HangupVoiceAsync(ct).ConfigureAwait(false))
                            return new KernelCommandResult(false, "Cellular modem rejected the hangup command.");
                    }
                    else
                    {
                        return new KernelCommandResult(false, "No active call or connected call bearer is available.");
                    }
                    ActiveCall?.End();
                    ActiveCall = null;
                    StateMachine.Fire(StateTrigger.TriggerCallHangup);
                    var hangupMsg = imsHangup?.WavRecordingPath != null
                        ? $"Call terminated. Recording saved: {imsHangup.WavRecordingPath}"
                        : "Call terminated";
                    return new KernelCommandResult(true, hangupMsg, imsHangup);

                case "roaming":
                    if (Modem == null)
                        return new KernelCommandResult(false, "No physical modem attached.");
                    if (parts.Length < 2)
                    {
                        var reg = await Modem.GetRegistrationAsync(ct).ConfigureAwait(false);
                        return new KernelCommandResult(true, $"Roaming policy: {(RoamingAllowed ? "ALLOWED" : "RESTRICTED")}. Current Network Status: {reg.Status} ({reg.OperatorName})", new
                        {
                            RoamingAllowed,
                            NetworkStatus = reg.Status.ToString(),
                            Operator = reg.OperatorName
                        });
                    }
                    var rMode = parts[1].ToLowerInvariant();
                    if (rMode is "on" or "enable" or "1")
                    {
                        RoamingAllowed = true;
                        await Modem.SendRawAtCommandAsync("AT+QCFG=\"roamsvc\",1", 2000, ct).ConfigureAwait(false);
                        await Modem.SendRawAtCommandAsync("AT+QNWCFG=\"roaming\",1", 2000, ct).ConfigureAwait(false);
                        EventBus.Publish("modem.roaming", "Kernel", "ENABLED");
                        return new KernelCommandResult(true, "International/Domestic Roaming ENABLED.");
                    }
                    else if (rMode is "off" or "disable" or "0")
                    {
                        RoamingAllowed = false;
                        await Modem.SendRawAtCommandAsync("AT+QCFG=\"roamsvc\",0", 2000, ct).ConfigureAwait(false);
                        await Modem.SendRawAtCommandAsync("AT+QNWCFG=\"roaming\",0", 2000, ct).ConfigureAwait(false);
                        EventBus.Publish("modem.roaming", "Kernel", "DISABLED");
                        return new KernelCommandResult(true, "Roaming DISABLED (Emergency & Home networks only).");
                    }
                    return new KernelCommandResult(false, "Usage: roaming <on|off>");

                case "sms":
                    if (parts.Length < 2)
                        return new KernelCommandResult(false, "Usage: sms <send|inbox|outbox|status|list|read|delete> [args...]");

                    var smsSub = parts[1].ToLowerInvariant();
                    if (smsSub == "send")
                    {
                        if (parts.Length < 4)
                            return new KernelCommandResult(false, "Usage: sms send <dest_number> <message_text> [--vowifi | --cellular | --no-receipt | --slot <id>]");

                        var smsDest = parts[2];
                        bool forceVowifi = false;
                        bool forceCellular = false;
                        bool noReceipt = false;
                        string? targetSlotId = null;
                        var textParts = new List<string>();

                        for (int i = 3; i < parts.Length; i++)
                        {
                            var p = parts[i];
                            if (p.Equals("--vowifi", StringComparison.OrdinalIgnoreCase))
                            {
                                forceVowifi = true;
                            }
                            else if (p.Equals("--cellular", StringComparison.OrdinalIgnoreCase))
                            {
                                forceCellular = true;
                            }
                            else if (p.Equals("--no-receipt", StringComparison.OrdinalIgnoreCase) || p.Equals("--no-status-report", StringComparison.OrdinalIgnoreCase))
                            {
                                noReceipt = true;
                            }
                            else if (p.Equals("--slot", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                            {
                                targetSlotId = parts[++i];
                            }
                            else
                            {
                                textParts.Add(p);
                            }
                        }

                        var smsText = string.Join(' ', textParts);
                        if (string.IsNullOrWhiteSpace(smsText))
                            return new KernelCommandResult(false, "SMS text cannot be empty.");

                        ModemSlot? slot = null;
                        if (!string.IsNullOrEmpty(targetSlotId) && Pool.Slots.TryGetValue(targetSlotId, out var sSlot))
                        {
                            slot = sSlot;
                        }
                        else
                        {
                            slot = Pool.FindSlotForTarget(smsDest) ?? Pool.ActiveSlot;
                        }

                        var voWifiToUse = slot?.VoWifi ?? VoWifi;

                        if ((forceVowifi || !forceCellular) && voWifiToUse.State == VoWifiState.ImsRegistered)
                        {
                            // Submit via VoWiFi IMS
                            var result = await voWifiToUse.SendSmsOverImsAsync(smsDest, smsText, requestStatusReport: !noReceipt, ct: ct).ConfigureAwait(false);
                            var summary = result.AllPartsAccepted
                                ? $"SMS submitted over VoWiFi IMS to {smsDest} ({result.PartsAccepted}/{result.PartsTotal} parts accepted, MR: {string.Join(",", result.PartResults.Select(r => r.Reference))})"
                                : $"SMS failed over VoWiFi IMS: {result.SubmissionStatus}";
                            if (result.AllPartsAccepted) RecordOutboxSubmission(result);
                            return new KernelCommandResult(result.AllPartsAccepted, summary, result);
                        }
                        else if (slot?.Modem != null && slot.Modem.IsOpen)
                        {
                            // Submit via Cellular AT Modem
                            var smsService = slot.Sms ?? SmsService ?? new SmsService(slot.Modem, EventBus);
                            var result = await smsService.SendSmsDetailedAsync(smsDest, smsText, requestStatusReport: !noReceipt, ct: ct).ConfigureAwait(false);
                            var summary = result.AllPartsAccepted
                                ? $"SMS submitted over Cellular Baseband to {smsDest} ({result.PartsAccepted}/{result.PartsTotal} parts accepted, MR: {string.Join(",", result.PartResults.Select(r => r.Reference))})"
                                : $"SMS failed over Cellular Baseband: {result.SubmissionStatus}";
                            if (result.AllPartsAccepted) RecordOutboxSubmission(result);
                            return new KernelCommandResult(result.AllPartsAccepted, summary, result);
                        }
                        else if (Modem != null && Modem.IsOpen)
                        {
                            var smsService = SmsService ?? new SmsService(Modem, EventBus);
                            var result = await smsService.SendSmsDetailedAsync(smsDest, smsText, requestStatusReport: !noReceipt, ct: ct).ConfigureAwait(false);
                            var summary = result.AllPartsAccepted
                                ? $"SMS submitted over Cellular Baseband to {smsDest} ({result.PartsAccepted}/{result.PartsTotal} parts accepted, MR: {string.Join(",", result.PartResults.Select(r => r.Reference))})"
                                : $"SMS failed over Cellular Baseband: {result.SubmissionStatus}";
                            if (result.AllPartsAccepted) RecordOutboxSubmission(result);
                            return new KernelCommandResult(result.AllPartsAccepted, summary, result);
                        }
                        else
                        {
                            return new KernelCommandResult(false,
                                "No registered IMS session or connected cellular modem is available. SMS was not sent.");
                        }
                    }
                    else if (smsSub is "inbox")
                    {
                        var messages = Inbox;
                        return new KernelCommandResult(true, $"Kernel inbox contains {messages.Count} SMS message(s)", messages);
                    }
                    else if (smsSub is "outbox" or "sent")
                    {
                        var messages = Outbox;
                        return new KernelCommandResult(true, $"Kernel outbox contains {messages.Count} sent SMS message(s)", messages);
                    }
                    else if (smsSub is "status" or "stats")
                    {
                        int inCount = Inbox.Count;
                        int outCount = Outbox.Count;
                        int pending = Outbox.Count(m => m.DeliveryStatus == "pending");
                        int delivered = Outbox.Count(m => m.DeliveryStatus == "delivered");
                        int failed = Outbox.Count(m => m.DeliveryStatus is "failed" or "temporary_error" or "permanent_error" or "temporary_error_no_retry");

                        return new KernelCommandResult(true, $"SMS Status: {inCount} Received | {outCount} Sent ({delivered} Delivered, {pending} Pending, {failed} Failed)", new
                        {
                            ReceivedTotal = inCount,
                            SentTotal = outCount,
                            PendingReceipts = pending,
                            DeliveredReceipts = delivered,
                            FailedReceipts = failed,
                            Inbox = Inbox,
                            Outbox = Outbox
                        });
                    }
                    else if (smsSub is "list" or "ls")
                    {
                        if (Modem == null)
                            return new KernelCommandResult(false, "No physical modem attached.");

                        var filter = SmsStatus.All;
                        if (parts.Length > 2 && Enum.TryParse<SmsStatus>(parts[2], true, out var f))
                            filter = f;

                        var smsService = SmsService ?? new SmsService(Modem, EventBus);
                        var messages = await smsService.ListSmsAsync(filter, ct).ConfigureAwait(false);
                        return new KernelCommandResult(true, $"Found {messages.Count} SMS message(s)", messages);
                    }
                    else if (smsSub is "read" or "get")
                    {
                        if (Modem == null)
                            return new KernelCommandResult(false, "No physical modem attached.");
                        if (parts.Length < 3 || !int.TryParse(parts[2], out var idx))
                            return new KernelCommandResult(false, "Usage: sms read <index>");

                        var smsService = SmsService ?? new SmsService(Modem, EventBus);
                        var msg = await smsService.ReadSmsAsync(idx, ct).ConfigureAwait(false);
                        if (msg != null)
                            return new KernelCommandResult(true, $"SMS [{idx}] from {msg.SenderOrRecipient} ({msg.Timestamp:yyyy-MM-dd HH:mm:ss}): {msg.Text}", msg);
                        return new KernelCommandResult(false, $"SMS message at index {idx} not found.");
                    }
                    else if (smsSub is "delete" or "del" or "rm")
                    {
                        if (Modem == null)
                            return new KernelCommandResult(false, "No physical modem attached.");
                        if (parts.Length < 3)
                            return new KernelCommandResult(false, "Usage: sms delete <index|all>");

                        int delIdx = parts[2].Equals("all", StringComparison.OrdinalIgnoreCase) ? 0 : int.Parse(parts[2]);
                        var smsService = SmsService ?? new SmsService(Modem, EventBus);
                        bool delOk = await smsService.DeleteSmsAsync(delIdx, ct).ConfigureAwait(false);
                        return new KernelCommandResult(delOk, delOk ? $"Deleted SMS {(delIdx == 0 ? "ALL" : delIdx.ToString())}" : "Failed to delete SMS.");
                    }
                    return new KernelCommandResult(false, "Usage: sms <send|inbox|outbox|status|list|read|delete>");

                case "charon":
                    if (parts.Length > 1 && parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = VoWifi.GetCharonDiagnosticInfo();
                        if (info != null)
                        {
                            return new KernelCommandResult(true, "charon-svc status retrieved.", info);
                        }
                        return new KernelCommandResult(true, "charon-svc is not currently running via VoWiFi manager.");
                    }
                    return new KernelCommandResult(false, "Usage: charon status");

                case "vowifi":
                    if (parts.Length < 2 || parts[1].Equals("status", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("diag", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("state", StringComparison.OrdinalIgnoreCase))
                    {
                        var diag = VoWifi.GetDiagnosticInfo();
                        var summaryMsg = diag.State == VoWifiState.ImsRegistered
                            ? $"VoWiFi Online | Carrier: {diag.MatchedCarrier} | Tunnel IP: {diag.Tunnel?.AssignedIPv4} | P-CSCF: {diag.Tunnel?.PcscfIp} | IMS: {diag.Ims?.RegistrationState} | Proxy: {VoWifi.ProxyUrl ?? "direct"}"
                            : $"VoWiFi State: {diag.State}{(diag.LastError != null ? $" (Error: {diag.LastError})" : "")} | Proxy: {VoWifi.ProxyUrl ?? "direct"}";
                        return new KernelCommandResult(true, summaryMsg, diag);
                    }
                    var voSub = parts[1].ToLowerInvariant();
                    if (voSub is "start" or "connect")
                    {
                        string? slotId = null;
                        string? proxyArg = null;
                        string? customEpdg = null;
                        var suite = IkeProposalSuite.Auto;
                        var useNative = false;

                        for (int i = 2; i < parts.Length; i++)
                        {
                            var arg = parts[i];
                            if (arg.Equals("--native", StringComparison.OrdinalIgnoreCase))
                            {
                                useNative = true;
                            }
                            else if (arg.Equals("--slot", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                            {
                                slotId = parts[++i];
                            }
                            else if (arg.Equals("--proxy", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                            {
                                proxyArg = parts[++i];
                            }
                            else if (Enum.TryParse<IkeProposalSuite>(arg, true, out var s))
                            {
                                suite = s;
                            }
                            else if (arg.Contains('.') || arg.Contains(':'))
                            {
                                customEpdg = arg;
                            }
                        }

                        ModemSlot? targetSlot = null;
                        if (!string.IsNullOrEmpty(slotId) && Pool.Slots.TryGetValue(slotId, out var sSlot))
                        {
                            targetSlot = sSlot;
                        }
                        else
                        {
                            targetSlot = Pool.ActiveSlot;
                        }

                        var simToUse = targetSlot?.Sim ?? CurrentSim;
                        var voWifiToUse = targetSlot?.VoWifi ?? VoWifi;
                        if (simToUse == null)
                            return new KernelCommandResult(false, "No active SIM identity found. Attach modem or probe slots first.");

                        if (!string.IsNullOrEmpty(proxyArg))
                        {
                            voWifiToUse.ProxyUrl = proxyArg.Equals("direct", StringComparison.OrdinalIgnoreCase) ? null : proxyArg;
                        }

                        bool started = useNative
                            ? await voWifiToUse.StartVoWifiNativeAsync(simToUse, customEpdg, suite, ct).ConfigureAwait(false)
                            : await voWifiToUse.StartVoWifiAsync(simToUse, customEpdg, suite, proxyUrl: voWifiToUse.ProxyUrl, ct: ct).ConfigureAwait(false);
                        var diag = voWifiToUse.GetDiagnosticInfo();
                        return new KernelCommandResult(started, started ? $"VoWiFi established & IMS registered on {diag.Tunnel?.AssignedIPv4} (ePDG: {diag.EpdgFqdn}, Suite: {diag.Suite}, Proxy: {voWifiToUse.ProxyUrl ?? "direct"})" : $"VoWiFi failed: {diag.LastError}", diag);
                    }
                    else if (voSub is "stop" or "disconnect")
                    {
                        await VoWifi.StopVoWifiAsync(ct).ConfigureAwait(false);
                        return new KernelCommandResult(true, "VoWiFi disconnected.");
                    }
                    else if (voSub is "info" or "epdg")
                    {
                        if (CurrentSim == null)
                            return new KernelCommandResult(false, "No active SIM identity.");
                        var customEpdg = parts.Length > 2 && !Enum.TryParse<IkeProposalSuite>(parts[2], true, out _) ? parts[2] : null;
                        var epdgRes = await EpdgResolver.ResolveAsync(CurrentSim.Imsi, customEpdg, CurrentSim.Iccid, CurrentSim.OperatorName, ct).ConfigureAwait(false);
                        return new KernelCommandResult(true, $"Carrier: {epdgRes.MatchedCarrier ?? "Generic 3GPP"} | ePDG: {epdgRes.Fqdn} (Resolved IPs: {(epdgRes.IpAddresses.Length > 0 ? string.Join(", ", epdgRes.IpAddresses.Select(a => a.ToString())) : "None - public DNS lookup failed, custom ePDG IP may be needed")})", new
                        {
                            MatchedCarrier = epdgRes.MatchedCarrier ?? "Generic 3GPP",
                            epdgRes.Fqdn,
                            epdgRes.ImsDomain,
                            epdgRes.Impi,
                            epdgRes.Impu,
                            epdgRes.Apn,
                            epdgRes.SmsCenter,
                            PreferredSuite = epdgRes.PreferredSuite.ToString(),
                            IpAddresses = epdgRes.IpAddresses.Select(a => a.ToString()).ToArray(),
                            CurrentSuite = VoWifi.CurrentSuite.ToString(),
                            SupportedSuites = new[] { "Modern (ECP-256 / SHA256 / AES-256)", "Standard (MODP-2048 / SHA256 / AES-128)", "Legacy (MODP-1024 / SHA1 / AES-128)" }
                        });
                    }
                    return new KernelCommandResult(false, "Usage: vowifi <status|start|stop|info> [custom_epdg] [modern|standard|legacy|auto] [--slot <id>] [--proxy <socks5_url>]");

                case "dtmf":
                    if (parts.Length < 2)
                        return new KernelCommandResult(false, "Usage: dtmf <digit>");
                    char d = parts[1][0];
                    var rfc4733Payload = DtmfPayload.BuildRfc4733(d, true);
                    if (Modem != null)
                    {
                        await Modem.SendDtmfAsync(d, ct).ConfigureAwait(false);
                    }
                    EventBus.Publish(EventTopics.CallDtmf, "Kernel", d.ToString());
                    return new KernelCommandResult(true, $"DTMF tone '{d}' sent", new
                    {
                        Digit = d,
                        Rfc4733PayloadHex = Convert.ToHexString(rfc4733Payload)
                    });

                case "mmi":
                    if (parts.Length < 2)
                        return new KernelCommandResult(false, "Usage: mmi <code> (e.g. *#06#, *21*13800000000#)");
                    var mmiCode = parts[1];
                    var parsedMmi = MmiParser.Parse(mmiCode);
                    if (parsedMmi == null)
                        return new KernelCommandResult(false, $"Unrecognized MMI code: {mmiCode}");

                    return new KernelCommandResult(true, $"Parsed MMI: {parsedMmi.Service} [{parsedMmi.Action}]", parsedMmi);

                case "aka":
                    // Usage: aka <rand_hex> <autn_hex> [--software [k_hex opc_hex]]
                    // With no flags this runs the challenge against the real USIM (modem or PC/SC),
                    // which is the G2 acceptance gate — it must work with no network and no IPsec.
                    if (parts.Length < 3)
                        return new KernelCommandResult(false,
                            "Usage: aka <rand_hex_16bytes> <autn_hex_16bytes> [--software [k_hex opc_hex]]");

                    var rand = HexUtils.FromHexString(parts[1]);
                    var autn = HexUtils.FromHexString(parts[2]);
                    var forceSoftware = parts.Skip(3).Any(p =>
                        p.Equals("--software", StringComparison.OrdinalIgnoreCase));

                    var provider = forceSoftware ? BuildSoftwareAkaProvider(parts) : AkaProvider;

                    try
                    {
                        var challenge = AkaChallenge.Create(rand, autn);
                        var vector = await provider.AuthenticateAsync(challenge, ct).ConfigureAwait(false);
                        try
                        {
                            if (vector.SynchronizationFailure)
                            {
                                return new KernelCommandResult(true,
                                    "USIM reported a synchronisation failure",
                                    new { SynchronizationFailure = true, AutsLength = vector.Auts!.Length });
                            }

                            if (!vector.Success)
                            {
                                return new KernelCommandResult(false, $"USIM AKA failed: {vector.ErrorMessage}");
                            }

                            return new KernelCommandResult(true, "3GPP AKA complete", new
                            {
                                Source = provider.GetType().Name,
                                ResLength = vector.Res!.Length,
                                CkLength = vector.Ck!.Length,
                                IkLength = vector.Ik!.Length
                            });
                        }
                        finally
                        {
                            vector.Clear();
                        }
                    }
                    catch (ArgumentException sizeError)
                    {
                        return new KernelCommandResult(false, sizeError.Message);
                    }

                case "call" or "dial":
                    if (parts.Length < 2)
                    {
                        var cur = Calls.ActiveCall;
                        return new KernelCommandResult(true, cur != null ? $"Active Call: {cur.TargetNumber} [{cur.State}]" : "No active call.", cur);
                    }
                    var callSub = parts[1].ToLowerInvariant();
                    if (callSub is "hangup" or "stop" or "end" or "cancel")
                    {
                        var ended = await Calls.HangupAsync().ConfigureAwait(false);
                        return new KernelCommandResult(true, ended != null ? $"Call ended. Recording: {ended.WavRecordingPath}" : "No active call to terminate.", ended);
                    }
                    else if (callSub is "answer" or "accept")
                    {
                        string? aSlotId = null;
                        for (int i = 2; i < parts.Length; i++)
                        {
                            if (parts[i].Equals("--slot", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                                aSlotId = parts[++i];
                        }
                        ModemSlot? aSlot = (!string.IsNullOrEmpty(aSlotId) && Pool.Slots.TryGetValue(aSlotId, out var cAslt)) ? cAslt : Pool.ActiveSlot;
                        if (aSlot != null)
                        {
                            var ansRes = await aSlot.AnswerCallAsync(ct).ConfigureAwait(false);
                            return new KernelCommandResult(true, $"Answered call on slot '{aSlot.Id}'", ansRes);
                        }
                        else
                        {
                            var ansRes = await Calls.AnswerAsync(ct).ConfigureAwait(false);
                            return new KernelCommandResult(true, "Answered incoming VoWiFi call", ansRes);
                        }
                    }
                    else if (callSub is "reject" or "decline")
                    {
                        string? rSlotId = null;
                        for (int i = 2; i < parts.Length; i++)
                        {
                            if (parts[i].Equals("--slot", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                                rSlotId = parts[++i];
                        }
                        ModemSlot? rSlot = (!string.IsNullOrEmpty(rSlotId) && Pool.Slots.TryGetValue(rSlotId, out var cRslt)) ? cRslt : Pool.ActiveSlot;
                        if (rSlot != null)
                        {
                            var rejRes = await rSlot.RejectCallAsync(ct).ConfigureAwait(false);
                            return new KernelCommandResult(true, $"Rejected call on slot '{rSlot.Id}'", rejRes);
                        }
                        else
                        {
                            var rejRes = await Calls.RejectAsync(ct: ct).ConfigureAwait(false);
                            return new KernelCommandResult(true, "Rejected incoming VoWiFi call", rejRes);
                        }
                    }
                    else if (callSub is "status" or "info")
                    {
                        var cur = Calls.ActiveCall;
                        return new KernelCommandResult(true, cur != null ? $"Call: {cur.TargetNumber} [{cur.State}]" : "No active call.", cur);
                    }
                    else if (callSub is "dtmf")
                    {
                        if (parts.Length < 3) return new KernelCommandResult(false, "Usage: call dtmf <digit>");
                        char digit = parts[2][0];
                        await Calls.SendDtmfAsync(digit).ConfigureAwait(false);
                        return new KernelCommandResult(true, $"Sent in-band DTMF '{digit}'");
                    }
                    else
                    {
                        var num = callSub == "dial" && parts.Length > 2 ? parts[2] : parts[1];
                        string? slotArg = null;
                        for (int i = 2; i < parts.Length; i++)
                        {
                            if (parts[i].Equals("--slot", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                                slotArg = parts[++i];
                        }

                        ModemSlot? callSlot = null;
                        if (!string.IsNullOrEmpty(slotArg) && Pool.Slots.TryGetValue(slotArg, out var explicitSlot))
                            callSlot = explicitSlot;
                        else
                            callSlot = Pool.FindSlotForTarget(num) ?? Pool.ActiveSlot;

                        var voWifiToUse = callSlot?.VoWifi ?? VoWifi;
                        var callsToUse = callSlot?.Calls ?? Calls;

                        if (EmergencyService.IsEmergencyNumber(num))
                        {
                            return new KernelCommandResult(false,
                                "IMS emergency calling is not implemented. No emergency call was placed.");
                        }
                        else if (voWifiToUse.State == VoWifiState.ImsRegistered)
                        {
                            var dialed = await callsToUse.DialAsync(num, voWifiToUse, ct).ConfigureAwait(false);
                            ActiveCall = new CallSession(dialed.TargetNumber, isOutgoing: true);
                            return new KernelCommandResult(true, $"Calling {dialed.TargetNumber} via VoWiFi SIP/RTP (Slot: {callSlot?.Id ?? "active"})...", dialed);
                        }
                        else if (callSlot?.Modem != null && callSlot.Modem.IsOpen)
                        {
                            var forceCellular = parts.Any(p => p.Equals("--force-cellular", StringComparison.OrdinalIgnoreCase));
                            if (forceCellular)
                            {
                                var dialed = await callSlot.DialCellularAsync(num, ct).ConfigureAwait(false);
                                ActiveCall = new CallSession(num, isOutgoing: true);
                                return new KernelCommandResult(true,
                                    $"Dialing {num} via modem cellular baseband (ATD, awaiting +CLCC)...", dialed);
                            }

                            return new KernelCommandResult(false, "VoWiFi is not registered. Cellular ATD fallback is disabled for safety. Run 'vowifi start' first, or specify '--force-cellular' if cellular dial is explicitly desired.");
                        }
                        else
                        {
                            return new KernelCommandResult(false,
                                "No registered VoWiFi session or explicitly enabled cellular bearer is available. No call was placed.");
                        }
                    }

                case "attach":
                    if (parts.Length < 2)
                        return new KernelCommandResult(false, "Usage: attach <COM_PORT> [baudRate:115200] (e.g. attach COM3)");
                    var portToAttach = parts[1];
                    int baud = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 115200;
                    bool attached = await AttachModemAsync(portToAttach, baud).ConfigureAwait(false);
                    if (attached)
                    {
                        EuiccManager = new Euicc.EuiccManager(new Euicc.Transport.AtModemEuiccTransport(Modem!), EventBus);
                        return new KernelCommandResult(true, $"Attached modem on {portToAttach} successfully. eUICC AT backend active.", new { Port = portToAttach, Baud = baud });
                    }
                    return new KernelCommandResult(false, $"Failed to attach modem on {portToAttach}");

                case "ports" or "scan":
                    var availPorts = WindowsModemDetector.GetAvailableComPorts();
                    return new KernelCommandResult(true, $"Found {availPorts.Length} COM port(s): {string.Join(", ", availPorts)}", availPorts);

                case "slots" or "pool":
                case "slot" when parts.Length == 1 || (parts.Length > 1 && parts[1].ToLowerInvariant() is "list" or "ls" or "status"):
                    return new KernelCommandResult(true, $"Modem Pool contains {Pool.Slots.Count} slot(s). Active: {Pool.ActiveSlot?.Id ?? "none"}", Pool.GetSlotsDiagnostic());

                case "slot":
                    var slotSub = parts[1].ToLowerInvariant();
                    if (slotSub is "probe" or "scan" or "discover")
                    {
                        var found = await Pool.DiscoverAndEnrichAsync(ct).ConfigureAwait(false);
                        return new KernelCommandResult(true, $"Discovered {found.Count} modem slot(s)", Pool.GetSlotsDiagnostic());
                    }
                    else if (slotSub is "select" or "switch" or "use")
                    {
                        if (parts.Length < 3) return new KernelCommandResult(false, "Usage: slot select <slot_id>");
                        bool sel = Pool.SelectSlot(parts[2]);
                        return new KernelCommandResult(sel, sel ? $"Selected active slot: {parts[2]}" : $"Slot '{parts[2]}' not found in pool.");
                    }
                    else if (slotSub is "add" or "attach")
                    {
                        if (parts.Length < 3) return new KernelCommandResult(false, "Usage: slot add <COM_PORT> [name]");
                        string pName = parts[2];
                        string? sName = parts.Length > 3 ? parts[3] : null;
                        var addedSlot = await Pool.AddOrUpdateSlotAsync(pName, name: sName, ct: ct).ConfigureAwait(false);
                        return new KernelCommandResult(addedSlot.State == SlotState.Online, $"Slot {addedSlot.Id} ({pName}): {addedSlot.State}", addedSlot.GetDiagnosticInfo());
                    }
                    else if (slotSub is "remove" or "rm" or "del")
                    {
                        if (parts.Length < 3) return new KernelCommandResult(false, "Usage: slot remove <slot_id>");
                        bool rm = await Pool.RemoveSlotAsync(parts[2], ct).ConfigureAwait(false);
                        return new KernelCommandResult(rm, rm ? $"Removed slot '{parts[2]}'" : $"Slot '{parts[2]}' not found.");
                    }
                    else if (slotSub is "proxy")
                    {
                        if (parts.Length < 3) return new KernelCommandResult(false, "Usage: slot proxy <slot_id> [socks5_url|direct]");
                        string targetId = parts[2];
                        string? proxyUrl = parts.Length > 3 ? (parts[3].Equals("direct", StringComparison.OrdinalIgnoreCase) ? null : parts[3]) : null;
                        bool setP = Pool.SetSlotProxy(targetId, proxyUrl);
                        return new KernelCommandResult(setP, setP ? $"Slot '{targetId}' proxy configured: {proxyUrl ?? "direct"}" : $"Slot '{targetId}' not found.");
                    }
                    else if (int.TryParse(parts[1], out var targetHwSlot) && targetHwSlot is 1 or 2)
                    {
                        if (Modem == null) return new KernelCommandResult(false, "No physical modem attached on active slot.");
                        bool slotSwitched = await Modem.SetSimSlotAsync(targetHwSlot, ct).ConfigureAwait(false);
                        if (slotSwitched)
                        {
                            EventBus.Publish(EventTopics.ModemSim, "Kernel", $"SLOT_{targetHwSlot}");
                            await ReloadSimAndNetworkAsync(ct).ConfigureAwait(false);
                            return new KernelCommandResult(true, $"Switched hardware SIM slot to {targetHwSlot} (1=Physical SIM, 2=Built-in eSIM). Active SIM: {CurrentSim}", new { Slot = targetHwSlot, ActiveSim = CurrentSim });
                        }
                        return new KernelCommandResult(false, $"Modem rejected switching to SIM slot {targetHwSlot}");
                    }
                    return new KernelCommandResult(false, "Usage: slot <list|probe|select|add|remove|proxy|1|2> [args...]");

                case "simslot":
                    if (Modem == null)
                        return new KernelCommandResult(false, "No physical modem attached. Use 'attach <COM_PORT>' first.");
                    if (parts.Length < 2)
                    {
                        int currentSlot = await Modem.GetSimSlotAsync(ct).ConfigureAwait(false);
                        return new KernelCommandResult(true, $"Current active SIM slot is {currentSlot} (1=Physical SIM, 2=Built-in eSIM)", new { Slot = currentSlot });
                    }
                    if (int.TryParse(parts[1], out var hwSlot) && hwSlot is 1 or 2)
                    {
                        bool slotSwitched = await Modem.SetSimSlotAsync(hwSlot, ct).ConfigureAwait(false);
                        if (slotSwitched)
                        {
                            EventBus.Publish(EventTopics.ModemSim, "Kernel", $"SLOT_{hwSlot}");
                            await ReloadSimAndNetworkAsync(ct).ConfigureAwait(false);
                            return new KernelCommandResult(true, $"Switched hardware SIM slot to {hwSlot} (1=Physical SIM, 2=Built-in eSIM). Active SIM: {CurrentSim}", new { Slot = hwSlot, ActiveSim = CurrentSim });
                        }
                        return new KernelCommandResult(false, $"Modem rejected switching to SIM slot {hwSlot}");
                    }
                    return new KernelCommandResult(false, "Usage: simslot <1|2> (1=Physical SIM, 2=Built-in eSIM)");

                case "flightmode" or "flight" or "airplane":
                    if (Modem == null)
                        return new KernelCommandResult(false, "No physical modem attached. Use 'attach <COM_PORT>' first.");
                    if (parts.Length < 2)
                    {
                        int currentCfun = await Modem.GetFlightModeAsync(ct).ConfigureAwait(false);
                        bool isFlight = currentCfun is 0 or 4;
                        return new KernelCommandResult(true, $"Flight mode is {(isFlight ? "ON (RF Disabled, CFUN=" + currentCfun + ")" : "OFF (RF Enabled, CFUN=" + currentCfun + ")")}", new { FlightMode = isFlight, CFUN = currentCfun });
                    }
                    var modeStr = parts[1].ToLowerInvariant();
                    if (modeStr is "on" or "enable" or "1")
                    {
                        bool setRes = await Modem.SetFlightModeAsync(true, ct).ConfigureAwait(false);
                        return new KernelCommandResult(setRes, setRes ? "Flight mode enabled (RF disabled, CFUN=4)" : "Failed to enable flight mode", new { FlightMode = true });
                    }
                    else if (modeStr is "off" or "disable" or "0")
                    {
                        bool setRes = await Modem.SetFlightModeAsync(false, ct).ConfigureAwait(false);
                        if (setRes)
                        {
                            await ReloadSimAndNetworkAsync(ct).ConfigureAwait(false);
                        }
                        return new KernelCommandResult(setRes, setRes ? "Flight mode disabled (RF enabled, CFUN=1). Network registration initiated." : "Failed to disable flight mode", new { FlightMode = false });
                    }
                    return new KernelCommandResult(false, "Usage: flightmode <on|off>");

                case "at" or "raw":
                    if (Modem == null)
                        return new KernelCommandResult(false, "No physical modem attached. Use 'attach <COM_PORT>' first.");
                    if (parts.Length < 2)
                        return new KernelCommandResult(false, "Usage: at <raw_at_command> (e.g. at AT+CSQ, at AT+QNWINFO)");
                    var rawAt = raw.Substring(parts[0].Length).Trim();
                    var atResp = await Modem.SendRawAtCommandAsync(rawAt, 5000, ct).ConfigureAwait(false);
                    return new KernelCommandResult(atResp.Success, atResp.RawOutput?.Trim() ?? string.Join(" ", atResp.Lines), new { atResp.Success, atResp.Lines, atResp.ErrorCode });

                case "cops" or "operator":
                    if (Modem == null)
                        return new KernelCommandResult(false, "No physical modem attached.");
                    var opsResp = await Modem.SendRawAtCommandAsync("AT+COPS?", 3000, ct).ConfigureAwait(false);
                    return new KernelCommandResult(opsResp.Success, opsResp.FirstDataLine.Trim(), opsResp.Lines);

                case "reboot" or "restart":
                    if (Modem == null)
                        return new KernelCommandResult(false, "No physical modem attached.");
                    bool reb = await Modem.RebootBasebandAsync(ct).ConfigureAwait(false);
                    return new KernelCommandResult(reb, reb ? "Baseband reboot command issued (AT+CFUN=1,1)." : "Reboot command failed.");

                case "euicc":
                    return await HandleEuiccCommandAsync(parts, ct).ConfigureAwait(false);

                case "reset":
                    StateMachine.Fire(StateTrigger.TriggerReset);
                    return new KernelCommandResult(true, $"Kernel reset. State is now {StateMachine.CurrentState}");

                default:
                    return new KernelCommandResult(false, $"Unknown command '{cmd}'. Available: status, history, signal, sim, flightmode, slot, euicc, attach, ports, cops, at, reboot, dial, answer, hangup, sms, dtmf, mmi, aka, reset, quit");
            }
        }
        catch (Exception ex)
        {
            return new KernelCommandResult(false, $"Execution error: {ex.Message}");
        }
    }

    public async Task ReloadSimAndNetworkAsync(CancellationToken ct = default)
    {
        if (Pool.ActiveSlot != null)
        {
            await Pool.ActiveSlot.RefreshSimAsync(ct).ConfigureAwait(false);
        }
        else if (Modem != null)
        {
            try
            {
                await Modem.RefreshSimAsync(ct).ConfigureAwait(false);
            }
            catch { }
        }

        try
        {
            // Update StateMachine: Reset -> SimReady
            StateMachine.Fire(StateTrigger.TriggerReset);
            StateMachine.Fire(StateTrigger.TriggerSimReady);

            // Quick probe for network registration
            if (Modem != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    var reg = await Modem.GetRegistrationAsync(ct).ConfigureAwait(false);
                    if (reg.Status is NetworkRegStatus.Home or NetworkRegStatus.Roaming)
                    {
                        EventBus.Publish(EventTopics.NetworkRegistration, "Kernel", reg.Status.ToString().ToUpperInvariant());
                        break;
                    }
                    try { await Task.Delay(800, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }
        catch (Exception ex)
        {
            EventBus.Publish(EventTopics.SystemError, "Kernel", $"ReloadSimAndNetwork error: {ex.Message}");
        }
    }

    private async Task<KernelCommandResult> HandleEuiccCommandAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            return new KernelCommandResult(false, "Usage: euicc <eid|list|switch|disable|delete|rename|info|backend|slot> [args...]");
        }

        var subCmd = parts[1].ToLowerInvariant();

        if (subCmd is "backend" or "transport")
        {
            if (parts.Length < 3)
            {
                var curBackend = EuiccManager?.Transport.BackendName ?? "None";
                return new KernelCommandResult(true, $"Current eUICC backend: {curBackend}", new { Backend = curBackend });
            }
            var targetBackend = parts[2].ToLowerInvariant();
            if (targetBackend is "pcsc" or "smartcard")
            {
                var pcsc = new VoSharp.Sim.Pcsc.PcscReader();
                EuiccManager = new Euicc.EuiccManager(new Euicc.Transport.PcscEuiccTransport(pcsc), EventBus);
                return new KernelCommandResult(true, "Switched eUICC backend to PC/SC SmartCard reader.");
            }
            else if (targetBackend is "modem" or "at" or "dji")
            {
                if (Modem == null)
                    return new KernelCommandResult(false, "No physical modem attached. Please attach a modem first with 'attach <COM_PORT>'.");
                EuiccManager = new Euicc.EuiccManager(new Euicc.Transport.AtModemEuiccTransport(Modem), EventBus);
                return new KernelCommandResult(true, $"Switched eUICC backend to AT Modem ({Modem.PortName}).");
            }
            return new KernelCommandResult(false, "Usage: euicc backend <pcsc|modem>");
        }

        if (subCmd is "slot")
        {
            return await ExecuteCommandAsync(parts.Length > 2 ? $"slot {parts[2]}" : "slot", ct).ConfigureAwait(false);
        }

        var euicc = await GetOrCreateEuiccManagerAsync(ct).ConfigureAwait(false);

        switch (subCmd)
        {
            case "eid":
                var eid = await euicc.GetEIDAsync(ct).ConfigureAwait(false);
                return new KernelCommandResult(true, $"EID: {eid}", new { EID = eid, Backend = euicc.Transport.BackendName });

            case "list" or "profiles":
                var profiles = await euicc.ListProfilesAsync(ct).ConfigureAwait(false);
                return new KernelCommandResult(true, $"Found {profiles.Count} installed eSIM profile(s) on {euicc.Transport.BackendName}", profiles);

            case "switch" or "enable":
                if (parts.Length < 3)
                    return new KernelCommandResult(false, "Usage: euicc switch <iccid_or_aid> [refresh:true|false]");
                var target = parts[2];
                bool refresh = parts.Length <= 3 || !bool.TryParse(parts[3], out var r) || r;
                await euicc.SwitchProfileAsync(target, refresh, ct).ConfigureAwait(false);

                // Auto reload SIM and refresh network
                if (Modem != null)
                {
                    await ReloadSimAndNetworkAsync(ct).ConfigureAwait(false);
                }

                return new KernelCommandResult(true, $"Switched and activated eSIM profile {target} (Backend: {euicc.Transport.BackendName}). Active SIM: {CurrentSim}", new
                {
                    Target = target,
                    Refresh = refresh,
                    ActiveSim = CurrentSim,
                    State = StateMachine.CurrentState.ToString()
                });

            case "disable":
                if (parts.Length < 3)
                    return new KernelCommandResult(false, "Usage: euicc disable <iccid_or_aid>");
                var disTarget = parts[2];
                await euicc.DisableProfileAsync(disTarget, true, ct).ConfigureAwait(false);
                return new KernelCommandResult(true, $"Disabled eSIM profile {disTarget}", new { Target = disTarget });

            case "delete":
                if (parts.Length < 3)
                    return new KernelCommandResult(false, "Usage: euicc delete <iccid_or_aid>");
                var delTarget = parts[2];
                await euicc.DeleteProfileAsync(delTarget, ct).ConfigureAwait(false);
                return new KernelCommandResult(true, $"Deleted eSIM profile {delTarget}", new { Target = delTarget });

            case "rename":
                if (parts.Length < 4)
                    return new KernelCommandResult(false, "Usage: euicc rename <iccid_or_aid> <new_nickname>");
                var renTarget = parts[2];
                var newNick = parts[3];
                await euicc.RenameProfileAsync(renTarget, newNick, ct).ConfigureAwait(false);
                return new KernelCommandResult(true, $"Renamed profile {renTarget} to '{newNick}'", new { Target = renTarget, Nickname = newNick });

            case "info":
                var info = await euicc.GetEuiccInfoAsync(ct).ConfigureAwait(false);
                return new KernelCommandResult(true, $"Retrieved eUICC chip information ({euicc.Transport.BackendName})", info);

            default:
                return new KernelCommandResult(false, $"Unknown euicc subcommand '{subCmd}'. Available: eid, list, switch, disable, delete, rename, info, backend, slot");
        }
    }

    public async Task<Euicc.EuiccManager> GetOrCreateEuiccManagerAsync(CancellationToken ct = default)
    {
        if (EuiccManager != null)
            return EuiccManager;

        // 1. If Modem is already attached, use AT Modem transport
        if (Modem != null)
        {
            EuiccManager = new Euicc.EuiccManager(new Euicc.Transport.AtModemEuiccTransport(Modem), EventBus);
            return EuiccManager;
        }

        // 2. Auto-probe available COM ports for DJI / Quectel modems
        var probedPort = await WindowsModemDetector.ProbeModemPortAsync(115200, ct).ConfigureAwait(false);
        if (probedPort != null && await AttachModemAsync(probedPort).ConfigureAwait(false))
        {
            EuiccManager = new Euicc.EuiccManager(new Euicc.Transport.AtModemEuiccTransport(Modem!), EventBus);
            return EuiccManager;
        }

        // 3. Try PC/SC SmartCard
        try
        {
            var pcsc = new VoSharp.Sim.Pcsc.PcscReader();
            var readers = pcsc.ListReaders();
            if (readers.Length > 0)
            {
                EuiccManager = new Euicc.EuiccManager(new Euicc.Transport.PcscEuiccTransport(pcsc), EventBus);
                return EuiccManager;
            }
        }
        catch { }

        throw new InvalidOperationException("No eUICC transport found. If using DJI/Quectel 4G Dongle, attach port with 'attach <COM_PORT>' (e.g. attach COM3); if using PC/SC smartcard reader, please insert reader.");
    }

    private int _disposed;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
        catch { }

        if (_telemetryTask != null)
        {
            try { await _telemetryTask.ConfigureAwait(false); } catch { }
        }
        try { await Pool.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _fallbackCalls.Dispose(); } catch { }
        try { _fallbackVoWifi.Dispose(); } catch { }
        try { await EventBus.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
