using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Common.Events;
using VoSharp.Sip;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Telephony.Calls;

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

public class ImsCallManager : IDisposable
{
    public CallState State { get; private set; } = CallState.Idle;
    public CallInfo? ActiveCall { get; private set; }
    public AsyncEventBus? EventBus { get; }
    public bool IsIncoming => ActiveCall != null && !ActiveCall.IsOutgoing;
    public bool SaveAudioRecordings { get; private set; } = true;
    public string? RecordingDirectory { get; private set; }

    public event EventHandler<CallStateChangedEventArgs>? CallStateChanged;
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;
    public event EventHandler<CallConnectedEventArgs>? CallConnected;
    public event EventHandler<CallEndedEventArgs>? CallEnded;
    public event EventHandler<DtmfReceivedEventArgs>? DtmfReceived;
    public event EventHandler<AudioStreamStateChangedEventArgs>? AudioStreamStateChanged;

    private readonly WindowsAudioDevice _audio;
    private RtpSession? _rtp;
    private SipTransport? _callTransport;
    private string? _currentCallId;
    private string? _currentFromTag;
    private string? _currentLocalIp;
    // The IMS security agreement replaces 5060 with a negotiated UE port.  Keep
    // dialog requests on that actual transport endpoint instead of advertising a
    // stale pre-registration port in Via/Contact.
    private int _currentSignalingPort = 5060;
    private string? _targetUri;
    private string? _dialogTargetUri;
    private string? _dialogTo;
    private string? _dialogFrom;
    private List<string>? _dialogRoutes;
    private VoWifi.VoWifiManager? _currentVoWifi;
    private int _cseq = 1;
    private string? _lastInviteBranch;
    private int _lastInviteCSeq;
    private string? _lastInviteVia;
    private readonly object _prackGate = new();
    private readonly HashSet<string> _reliableProvisionals = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _recordingRetentionGate = new(1, 1);
    private const int MaxSavedRecordingCalls = 20;

    private SipMessage? _incomingInvite;
    private Func<SipMessage, Task>? _incomingReplySender;
    private string? _remoteOfferSdp;

    public ImsCallManager(AsyncEventBus? eventBus = null)
    {
        EventBus = eventBus;
        _audio   = new WindowsAudioDevice(sampleRate: 8000);
    }

    /// <summary>Configures whether completed IMS calls are written to WAV/AMR files.</summary>
    public void ConfigureAudioRecording(bool enabled, string? directory)
    {
        SaveAudioRecordings = enabled;
        RecordingDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
    }

    /// <summary>
    /// Dials a number over VoWiFi by sending a real SIP INVITE to the P-CSCF.
    /// BUG-11 FIX: INVITE is now transmitted via SipTransport and a response is awaited.
    /// </summary>
    public async Task<CallInfo> DialAsync(
        string number,
        VoWifiManager voWifi,
        CancellationToken ct = default)
    {
        if (voWifi.State != VoWifiState.ImsRegistered || string.IsNullOrEmpty(voWifi.AssignedIp))
            throw new InvalidOperationException("IMS: no active VoWiFi session. Register first.");

        if (State == CallState.Active || State == CallState.Dialing)
            throw new InvalidOperationException("Another call is already in progress.");

        var cleanNumber = number.Trim();
        _cseq = 1;
        lock (_prackGate) _reliableProvisionals.Clear();
        _currentCallId  = Guid.NewGuid().ToString("N") + "@" + voWifi.AssignedIp;
        _currentFromTag = Guid.NewGuid().ToString("N")[..8];

        State = CallState.Dialing;
        var startedAt = DateTime.UtcNow;

        // BUG-20 FIX: clear recorded audio buffer before starting a new call
        _audio.ClearRecording();

        _audio.ClearRecording();
        ReleaseRtpSession();
        _rtp = new RtpSession(_audio, EventBus);
        _currentVoWifi = voWifi;
        voWifi.RegisterRtpSession(_rtp);

        var localIp = voWifi.AssignedIp;
        var signalingEndpoint = voWifi.SipTransport?.LocalEndPoint
            ?? new IPEndPoint(IPAddress.Parse(localIp), 5060);
        var signalingHost = ImsRegisterBuilder.FormatHost(signalingEndpoint.Address.ToString());

        // ── Build SDP Offer ───────────────────────────────────────────────────
        var sessionID = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var family = SdpAddressFamily(localIp);
        var sdp = new StringBuilder();
        sdp.Append("v=0\r\n");
        sdp.Append($"o=- {sessionID} {sessionID} IN {family} {localIp}\r\n");
        sdp.Append("s=VoCat\r\n");
        sdp.Append($"c=IN {family} {localIp}\r\n");
        sdp.Append("t=0 0\r\n");
        sdp.Append($"m=audio {_rtp.LocalPort} RTP/AVP 104 102 101\r\n");
        sdp.Append("a=rtpmap:104 AMR-WB/16000/1\r\n");
        sdp.Append("a=fmtp:104 mode-change-capability=2; max-red=0\r\n");
        sdp.Append("a=rtpmap:102 AMR/8000/1\r\n");
        sdp.Append("a=fmtp:102 mode-change-capability=2; max-red=0\r\n");
        sdp.Append("a=rtpmap:101 telephone-event/8000\r\n");
        sdp.Append("a=fmtp:101 0-15\r\n");
        sdp.Append("a=ptime:20\r\n");
        sdp.Append("a=maxptime:240\r\n");
        sdp.Append("a=sendrecv\r\n");
        var sdpStr = sdp.ToString();

        // ── Build SIP INVITE (matching voCore 3GPP TS 24.229) ──────────────────
        var epdgInfo = voWifi.EpdgInfo
            ?? throw new InvalidOperationException("IMS network identity is unavailable for the active VoWiFi session.");
        var homeDomain = epdgInfo.ImsDomain;
        var targetUri = cleanNumber.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || cleanNumber.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
            ? cleanNumber
            : cleanNumber.StartsWith('+')
                ? "tel:" + cleanNumber
                : $"tel:{cleanNumber};phone-context={homeDomain}";

        // Use primary public identity from P-Associated-URI or fallback to Impu
        string publicURI = epdgInfo.Impu;
        if (!string.IsNullOrWhiteSpace(voWifi.ImsInfo?.PAssociatedUri))
        {
            var match = Regex.Match(voWifi.ImsInfo.PAssociatedUri, @"<([^>]+)>");
            if (match.Success)
            {
                publicURI = match.Groups[1].Value.Trim();
            }
        }

        var user = publicURI;
        if (user.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
        {
            user = user[4..];
            var at = user.IndexOf('@');
            if (at >= 0) user = user[..at];
        }
        else if (user.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            user = user[4..];
            var semi = user.IndexOf(';');
            if (semi >= 0) user = user[..semi];
        }

        // An MSISDN associated with the registered IMPU is the originating
        // identity.  The SIP From URI stays in the home IMS domain while the
        // preferred identity is the matching tel URI (the same generic rule
        // used by VoCat); do not derive a telephone number from an IMSI.
        var associatedNumber = ExtractNumberFromUri(voWifi.ImsInfo?.PAssociatedUri ?? publicURI);
        var fromIdentity = associatedNumber.StartsWith('+')
            ? $"sip:{associatedNumber}@{homeDomain}"
            : publicURI;
        var preferredIdentity = associatedNumber.StartsWith('+')
            ? $"tel:{associatedNumber}"
            : publicURI;

        var contactUser = !string.IsNullOrEmpty(voWifi.ImsInfo?.ContactUri)
            ? Regex.Match(voWifi.ImsInfo.ContactUri, @"<sip:([^@]+)@").Groups[1].Value
            : epdgInfo.Impi.Split('@')[0];
        if (string.IsNullOrEmpty(contactUser)) contactUser = user;

        var contactAddress = ExtractRegisteredContactAddress(voWifi.ImsInfo?.ContactUri)
            ?? $"{signalingHost}:{signalingEndpoint.Port}";
        string contact = $"<sip:{contactUser}@{contactAddress};transport=udp>;+g.3gpp.icsi-ref=\"urn%3Aurn-7%3A3gpp-service.ims.icsi.mmtel\";audio";
        if (!string.IsNullOrWhiteSpace(voWifi.ImsInfo?.ContactUri))
        {
            var instMatch = Regex.Match(voWifi.ImsInfo.ContactUri, @"(\+sip\.instance=""[^""]+"")");
            if (instMatch.Success)
                contact += $";{instMatch.Groups[1].Value}";
        }

        _currentLocalIp = signalingEndpoint.Address.ToString();
        _currentSignalingPort = signalingEndpoint.Port;
        _targetUri = targetUri;

        var invite = new SipMessage
        {
            IsRequest  = true,
            Method     = "INVITE",
            RequestUri = targetUri,
            SipVersion = "SIP/2.0",
            Body       = sdpStr
        };

        if (!string.IsNullOrEmpty(voWifi.ImsInfo?.ServiceRoute))
        {
            invite.SetHeader("Route", voWifi.ImsInfo.ServiceRoute);
        }

        var branch = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];
        _lastInviteBranch = branch;
        _lastInviteCSeq = _cseq;
        // An IPv6 address must be bracketed or "host:port" becomes ambiguous (RFC 3261 §25.1).
        _lastInviteVia = $"SIP/2.0/UDP {signalingHost}:{signalingEndpoint.Port};branch={branch};rport";
        _dialogFrom = $"<{fromIdentity}>;tag={_currentFromTag}";
        _dialogTo = $"<{targetUri}>";
        _targetUri = targetUri;
        _currentLocalIp = signalingEndpoint.Address.ToString();
        _currentSignalingPort = signalingEndpoint.Port;

        invite.SetHeader("Via",      _lastInviteVia);
        invite.SetHeader("Max-Forwards", "70");
        invite.SetHeader("From",     _dialogFrom);
        invite.SetHeader("To",       _dialogTo);
        invite.SetHeader("Call-ID",  _currentCallId);
        invite.SetHeader("CSeq",     $"{_cseq++} INVITE");
        invite.SetHeader("Contact",  contact);
        invite.SetHeader("P-Preferred-Identity", $"<{preferredIdentity}>");
        invite.SetHeader("P-Preferred-Service",  "urn:urn-7:3gpp-service.ims.icsi.mmtel");
        invite.SetHeader("Accept-Contact",      "*;+g.3gpp.icsi-ref=\"urn%3Aurn-7%3A3gpp-service.ims.icsi.mmtel\"");
        invite.SetHeader("P-Access-Network-Info", ImsRegisterBuilder.BuildAccessNetworkInfo(epdgInfo.Impi));
        invite.SetHeader("Allow",         "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE, PRACK, UPDATE, INFO");
        invite.SetHeader("Supported",     "100rel, timer, replaces");
        invite.SetHeader("Session-Expires", "1800;refresher=uac");
        invite.SetHeader("Min-SE",        "90");
        invite.SetHeader("Accept",        "application/sdp");
        invite.SetHeader("Content-Type",   "application/sdp");
        invite.SetHeader("Content-Length", sdpStr.Length.ToString());
        invite.SetHeader("User-Agent",    "iPhone Pro/17");

        Console.WriteLine($"[ImsCallManager] INVITE prepared for {targetUri} via IMS signalling port {signalingEndpoint.Port}.");

        var wavPath = CreateRecordingPath(cleanNumber, isIncoming: false);

        var call = new CallInfo(
            CallId:           _currentCallId,
            TargetNumber:     cleanNumber,
            State:            CallState.Dialing,
            StartedAt:        startedAt,
            ConnectedAt:      null,
            EndedAt:          null,
            Codec:            "Negotiating...",
            WavRecordingPath: wavPath
        );
        ActiveCall = call;

        NotifyCallStateChanged(CallState.Idle, CallState.Dialing);
        EventBus?.Publish("call.dialing", "ImsCallManager", ActiveCall);

        // ── Single INVITE send + non-resending provisional response loop ──
        var sipTransport = voWifi.SipTransport;
        if (sipTransport != null)
        {
            _callTransport = sipTransport; // reuse VoWiFi SIP transport

            try
            {
                var finalResp = await sipTransport.SendAndReceiveFinalAsync(
                    invite,
                    onProvisional: prov =>
                    {
                        if (RequiresReliableProvisional(prov))
                            _ = SendPrackAsync(sipTransport, prov, ct);
                        if (prov.StatusCode is 180 or 183)
                        {
                            var old = State;
                            State = CallState.Ringing;
                            call = (ActiveCall ?? call) with { State = CallState.Ringing };
                            ActiveCall = call;
                            NotifyCallStateChanged(old, CallState.Ringing);
                            EventBus?.Publish("call.ringing", "ImsCallManager", ActiveCall);
                        }
                    },
                    timeoutMs: 30000,
                    ct: ct
                ).ConfigureAwait(false);

                if (finalResp?.StatusCode == 200)
                {
                    var contactHeader = finalResp.GetHeader("Contact") ?? "";
                    var contactMatch = Regex.Match(contactHeader, @"<([^>]+)>");
                    _dialogTargetUri = contactMatch.Success ? contactMatch.Groups[1].Value.Trim() : (string.IsNullOrWhiteSpace(contactHeader) ? targetUri : contactHeader.Trim());
                    _dialogTo = finalResp.GetHeader("To") ?? invite.GetHeader("To") ?? "";
                    _dialogFrom = invite.GetHeader("From") ?? "";
                    if (finalResp.Headers.TryGetValue("Record-Route", out var recordRoutes) && recordRoutes.Count > 0)
                    {
                        _dialogRoutes = ParseAndReverseRecordRoute(recordRoutes);
                    }
                    else if (invite.Headers.TryGetValue("Route", out var invRoutes) && invRoutes.Count > 0)
                    {
                        _dialogRoutes = invRoutes;
                    }
                    else if (!string.IsNullOrEmpty(voWifi.ImsInfo?.ServiceRoute))
                    {
                        _dialogRoutes = [voWifi.ImsInfo.ServiceRoute];
                    }

                    // Send ACK (RFC 3261 §13.2.2.4)
                    await SendAckAsync(sipTransport, invite, finalResp, ct).ConfigureAwait(false);

                    // Extract remote RTP endpoint from SDP Answer
                    SetRtpFromSdpAnswer(finalResp.Body ?? string.Empty);

                    var codec = ExtractCodecFromSdp(finalResp.Body ?? string.Empty);
                    var old = State;
                    call = (ActiveCall ?? call) with
                    {
                        State       = CallState.Active,
                        ConnectedAt = DateTime.UtcNow,
                        Codec       = codec
                    };
                    ActiveCall = call;
                    State = CallState.Active;
                    NotifyCallStateChanged(old, CallState.Active, codec);
                    NotifyCallConnected(ActiveCall);
                    EventBus?.Publish("call.connected", "ImsCallManager", ActiveCall);
                }
                else if (finalResp != null)
                {
                    // Call rejected
                    var old = State;
                    State = CallState.Ended;
                    call = (ActiveCall ?? call) with { State = CallState.Ended, EndedAt = DateTime.UtcNow };
                    ActiveCall = call;
                    NotifyCallStateChanged(old, CallState.Ended);
                    NotifyCallEnded(ActiveCall, finalResp.ReasonPhrase);
                    EventBus?.Publish("call.rejected", "ImsCallManager",
                        new { Code = finalResp.StatusCode, Reason = finalResp.ReasonPhrase });
                    throw new InvalidOperationException($"Call rejected: {finalResp.StatusCode} {finalResp.ReasonPhrase}");
                }
            }
            catch (TimeoutException)
            {
                // P-CSCF unreachable — call failed
                var old = State;
                State      = CallState.Ended;
                call       = (ActiveCall ?? call) with { State = CallState.Ended, EndedAt = DateTime.UtcNow };
                ActiveCall = call;
                NotifyCallStateChanged(old, CallState.Ended);
                NotifyCallEnded(ActiveCall, "INVITE timeout");
                EventBus?.Publish("call.failed", "ImsCallManager", "INVITE timeout");
                throw;
            }
        }
        else
        {
            // No SIP transport available — still emit event so CLI shows the call state
            EventBus?.Publish("call.dialing", "ImsCallManager",
                "WARNING: No SIP transport available. Call cannot be established.");
        }

        return ActiveCall ?? call;
    }

    /// <summary>
    /// Handles an incoming SIP INVITE from the network (someone is calling us).
    /// Sends 100 Trying and 180 Ringing, alerts the event bus, and prepares the call state.
    /// </summary>
    public async Task HandleIncomingInviteAsync(
        SipMessage invite,
        VoWifiManager voWifi,
        Func<SipMessage, Task> replySender)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(voWifi);
        ArgumentNullException.ThrowIfNull(replySender);

        if (State == CallState.Active || State == CallState.Dialing)
        {
            // Busy here (486)
            var busyResp = new SipMessage
            {
                IsRequest = false,
                StatusCode = 486,
                ReasonPhrase = "Busy Here",
                SipVersion = "SIP/2.0"
            };
            busyResp.SetHeader("Via", invite.GetHeader("Via") ?? string.Empty);
            busyResp.SetHeader("From", invite.GetHeader("From") ?? string.Empty);
            busyResp.SetHeader("To", $"{invite.GetHeader("To")};tag={Guid.NewGuid().ToString("N")[..8]}");
            busyResp.SetHeader("Call-ID", invite.GetHeader("Call-ID") ?? string.Empty);
            busyResp.SetHeader("CSeq", invite.GetHeader("CSeq") ?? "1 INVITE");
            busyResp.SetHeader("Content-Length", "0");
            await replySender(busyResp).ConfigureAwait(false);
            return;
        }

        _incomingInvite = invite;
        _incomingReplySender = replySender;
        _currentVoWifi = voWifi;
        _currentCallId = invite.GetHeader("Call-ID") ?? Guid.NewGuid().ToString("N");
        _currentFromTag = Guid.NewGuid().ToString("N")[..8];
        _remoteOfferSdp = invite.Body;
        var incomingEndpoint = voWifi.SipTransport?.LocalEndPoint;
        _currentLocalIp = incomingEndpoint?.Address.ToString() ?? voWifi.AssignedIp ?? "127.0.0.1";
        _currentSignalingPort = incomingEndpoint?.Port ?? 5060;

        var fromHeader = invite.GetHeader("From") ?? "Unknown";
        var callerNumber = ExtractNumberFromUri(fromHeader);

        _dialogFrom = $"{invite.GetHeader("To")};tag={_currentFromTag}";
        _dialogTo = fromHeader;
        if (invite.Headers.TryGetValue("Record-Route", out var rrs))
        {
            _dialogRoutes = rrs.AsEnumerable().Reverse().ToList();
        }
        var contactHeader = invite.GetHeader("Contact") ?? "";
        var contactMatch = Regex.Match(contactHeader, @"<([^>]+)>");
        _dialogTargetUri = contactMatch.Success ? contactMatch.Groups[1].Value : (string.IsNullOrWhiteSpace(contactHeader) ? fromHeader : contactHeader);

        // 1. Send 100 Trying
        var resp100 = new SipMessage
        {
            IsRequest = false,
            StatusCode = 100,
            ReasonPhrase = "Trying",
            SipVersion = "SIP/2.0"
        };
        resp100.SetHeader("Via", invite.GetHeader("Via") ?? string.Empty);
        resp100.SetHeader("From", invite.GetHeader("From") ?? string.Empty);
        resp100.SetHeader("To", invite.GetHeader("To") ?? string.Empty);
        resp100.SetHeader("Call-ID", _currentCallId);
        resp100.SetHeader("CSeq", invite.GetHeader("CSeq") ?? "1 INVITE");
        resp100.SetHeader("Content-Length", "0");
        await replySender(resp100).ConfigureAwait(false);

        // 2. Send 180 Ringing
        var localIp = _currentLocalIp;
        var resp180 = new SipMessage
        {
            IsRequest = false,
            StatusCode = 180,
            ReasonPhrase = "Ringing",
            SipVersion = "SIP/2.0"
        };
        resp180.SetHeader("Via", invite.GetHeader("Via") ?? string.Empty);
        resp180.SetHeader("From", invite.GetHeader("From") ?? string.Empty);
        resp180.SetHeader("To", $"{invite.GetHeader("To")};tag={_currentFromTag}");
        resp180.SetHeader("Call-ID", _currentCallId);
        resp180.SetHeader("CSeq", invite.GetHeader("CSeq") ?? "1 INVITE");
        resp180.SetHeader("Contact", $"<sip:{ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};transport=udp>;audio");
        resp180.SetHeader("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE");
        resp180.SetHeader("Content-Length", "0");
        await replySender(resp180).ConfigureAwait(false);

        // 3. Set Ringing state and notify event bus
        var oldState = State;
        State = CallState.Ringing;
        var startedAt = DateTime.UtcNow;
        var wavPath = CreateRecordingPath(callerNumber, isIncoming: true);

        ActiveCall = new CallInfo(
            CallId: _currentCallId,
            TargetNumber: callerNumber,
            State: CallState.Ringing,
            StartedAt: startedAt,
            ConnectedAt: null,
            EndedAt: null,
            Codec: ExtractCodecFromSdp(_remoteOfferSdp ?? string.Empty),
            WavRecordingPath: wavPath,
            IsOutgoing: false
        );

        NotifyCallStateChanged(oldState, CallState.Ringing);
        NotifyIncomingCall(_currentCallId, callerNumber);
        EventBus?.Publish("call.incoming", "ImsCallManager", ActiveCall);
        EventBus?.Publish(EventTopics.CallIncoming, "ImsCallManager", callerNumber);
    }

    /// <summary>
    /// Answers an incoming ringing call by sending 200 OK with SDP answer and starting the RTP media session.
    /// </summary>
    public async Task<CallInfo> AnswerAsync(CancellationToken ct = default)
    {
        if (State != CallState.Ringing || _incomingInvite == null || _incomingReplySender == null || _currentVoWifi == null)
            throw new InvalidOperationException("No incoming call in ringing state to answer.");

        _audio.ClearRecording();
        var voWifi = _currentVoWifi;
        ReleaseRtpSession();
        _rtp = new RtpSession(_audio, EventBus);
        _currentVoWifi = voWifi;
        voWifi.RegisterRtpSession(_rtp);

        var localIp = _currentLocalIp ?? voWifi.AssignedIp ?? "127.0.0.1";
        var sessionID = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var family = SdpAddressFamily(localIp);
        var sdp = new StringBuilder();
        sdp.Append("v=0\r\n");
        sdp.Append($"o=- {sessionID} {sessionID} IN {family} {localIp}\r\n");
        sdp.Append("s=VoCat\r\n");
        sdp.Append($"c=IN {family} {localIp}\r\n");
        sdp.Append("t=0 0\r\n");
        sdp.Append($"m=audio {_rtp.LocalPort} RTP/AVP 104 102 101\r\n");
        sdp.Append("a=rtpmap:104 AMR-WB/16000/1\r\n");
        sdp.Append("a=fmtp:104 mode-change-capability=2; max-red=0\r\n");
        sdp.Append("a=rtpmap:102 AMR/8000/1\r\n");
        sdp.Append("a=fmtp:102 mode-change-capability=2; max-red=0\r\n");
        sdp.Append("a=rtpmap:101 telephone-event/8000\r\n");
        sdp.Append("a=fmtp:101 0-15\r\n");
        sdp.Append("a=ptime:20\r\n");
        sdp.Append("a=maxptime:240\r\n");
        sdp.Append("a=sendrecv\r\n");
        var sdpStr = sdp.ToString();

        var resp200 = new SipMessage
        {
            IsRequest = false,
            StatusCode = 200,
            ReasonPhrase = "OK",
            SipVersion = "SIP/2.0",
            Body = sdpStr
        };
        resp200.SetHeader("Via", _incomingInvite.GetHeader("Via") ?? string.Empty);
        resp200.SetHeader("From", _incomingInvite.GetHeader("From") ?? string.Empty);
        resp200.SetHeader("To", $"{_incomingInvite.GetHeader("To")};tag={_currentFromTag}");
        resp200.SetHeader("Call-ID", _currentCallId ?? string.Empty);
        resp200.SetHeader("CSeq", _incomingInvite.GetHeader("CSeq") ?? "1 INVITE");
        resp200.SetHeader("Contact", $"<sip:{ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};transport=udp>;audio");
        resp200.SetHeader("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE");
        resp200.SetHeader("Content-Type", "application/sdp");
        resp200.SetHeader("Content-Length", sdpStr.Length.ToString());

        await _incomingReplySender(resp200).ConfigureAwait(false);

        // Configure RTP session from remote SDP offer
        SetRtpFromSdpAnswer(_remoteOfferSdp ?? string.Empty);

        var codec = ExtractCodecFromSdp(_remoteOfferSdp ?? string.Empty);
        var old = State;
        State = CallState.Active;
        if (ActiveCall != null)
        {
            ActiveCall = ActiveCall with
            {
                State = CallState.Active,
                ConnectedAt = DateTime.UtcNow,
                Codec = codec
            };
        }

        NotifyCallStateChanged(old, CallState.Active, codec);
        if (ActiveCall != null)
        {
            NotifyCallConnected(ActiveCall);
            EventBus?.Publish("call.connected", "ImsCallManager", ActiveCall);
        }
        EventBus?.Publish(EventTopics.CallState, "ImsCallManager", "ACTIVE");
        return ActiveCall!;
    }

    /// <summary>
    /// Rejects an incoming ringing call with 603 Decline or 486 Busy Here.
    /// </summary>
    public async Task<CallInfo?> RejectAsync(int statusCode = 603, string reason = "Decline", CancellationToken ct = default)
    {
        if (State != CallState.Ringing || _incomingInvite == null)
            return await HangupAsync().ConfigureAwait(false);

        var resp = new SipMessage
        {
            IsRequest = false,
            StatusCode = statusCode,
            ReasonPhrase = reason,
            SipVersion = "SIP/2.0"
        };
        resp.SetHeader("Via", _incomingInvite.GetHeader("Via") ?? string.Empty);
        resp.SetHeader("From", _incomingInvite.GetHeader("From") ?? string.Empty);
        resp.SetHeader("To", $"{_incomingInvite.GetHeader("To")};tag={_currentFromTag}");
        resp.SetHeader("Call-ID", _currentCallId ?? string.Empty);
        resp.SetHeader("CSeq", _incomingInvite.GetHeader("CSeq") ?? "1 INVITE");
        resp.SetHeader("Content-Length", "0");

        if (_incomingReplySender != null)
        {
            try { await _incomingReplySender(resp).ConfigureAwait(false); } catch { }
        }

        var old = State;
        State = CallState.Ended;
        var ended = ActiveCall != null ? ActiveCall with { State = CallState.Ended, EndedAt = DateTime.UtcNow } : null;
        ActiveCall = null;
        _incomingInvite = null;
        _incomingReplySender = null;
        State = CallState.Idle;
        NotifyCallStateChanged(old, CallState.Ended);
        NotifyCallEnded(ended, reason);
        EventBus?.Publish("call.ended", "ImsCallManager", ended);
        return ended;
    }

    /// <summary>
    /// Handles incoming CANCEL request from remote party before call was answered.
    /// </summary>
    public async Task HandleIncomingCancelAsync(SipMessage cancel, Func<SipMessage, Task> replySender)
    {
        // 1. Reply 200 OK to CANCEL
        var cancel200 = new SipMessage
        {
            IsRequest = false,
            StatusCode = 200,
            ReasonPhrase = "OK",
            SipVersion = "SIP/2.0"
        };
        cancel200.SetHeader("Via", cancel.GetHeader("Via") ?? string.Empty);
        cancel200.SetHeader("From", cancel.GetHeader("From") ?? string.Empty);
        cancel200.SetHeader("To", cancel.GetHeader("To") ?? string.Empty);
        cancel200.SetHeader("Call-ID", cancel.GetHeader("Call-ID") ?? string.Empty);
        cancel200.SetHeader("CSeq", cancel.GetHeader("CSeq") ?? "1 CANCEL");
        cancel200.SetHeader("Content-Length", "0");
        await replySender(cancel200).ConfigureAwait(false);

        // 2. Reply 487 Request Terminated to original INVITE
        if (_incomingInvite != null)
        {
            var resp487 = new SipMessage
            {
                IsRequest = false,
                StatusCode = 487,
                ReasonPhrase = "Request Terminated",
                SipVersion = "SIP/2.0"
            };
            resp487.SetHeader("Via", _incomingInvite.GetHeader("Via") ?? string.Empty);
            resp487.SetHeader("From", _incomingInvite.GetHeader("From") ?? string.Empty);
            resp487.SetHeader("To", $"{_incomingInvite.GetHeader("To")};tag={_currentFromTag}");
            resp487.SetHeader("Call-ID", _incomingInvite.GetHeader("Call-ID") ?? string.Empty);
            resp487.SetHeader("CSeq", _incomingInvite.GetHeader("CSeq") ?? "1 INVITE");
            resp487.SetHeader("Content-Length", "0");
            await replySender(resp487).ConfigureAwait(false);
        }

        var old = State;
        State = CallState.Ended;
        var ended = ActiveCall != null ? ActiveCall with { State = CallState.Ended, EndedAt = DateTime.UtcNow } : null;
        ActiveCall = null;
        _incomingInvite = null;
        _incomingReplySender = null;
        State = CallState.Idle;
        NotifyCallStateChanged(old, CallState.Ended);
        NotifyCallEnded(ended, "CANCEL");
        EventBus?.Publish("call.ended", "ImsCallManager", ended);
    }

    /// <summary>
    /// Handles incoming BYE request from remote party when they hang up.
    /// </summary>
    public async Task HandleIncomingByeAsync(SipMessage bye, Func<SipMessage, Task> replySender)
    {
        var bye200 = new SipMessage
        {
            IsRequest = false,
            StatusCode = 200,
            ReasonPhrase = "OK",
            SipVersion = "SIP/2.0"
        };
        bye200.SetHeader("Via", bye.GetHeader("Via") ?? string.Empty);
        bye200.SetHeader("From", bye.GetHeader("From") ?? string.Empty);
        bye200.SetHeader("To", bye.GetHeader("To") ?? string.Empty);
        bye200.SetHeader("Call-ID", bye.GetHeader("Call-ID") ?? string.Empty);
        bye200.SetHeader("CSeq", bye.GetHeader("CSeq") ?? "1 BYE");
        bye200.SetHeader("Content-Length", "0");
        await replySender(bye200).ConfigureAwait(false);

        // Terminate call and save recording asynchronously
        if (ActiveCall?.WavRecordingPath != null)
        {
            var wavPath = ActiveCall.WavRecordingPath;
            var rtpToSave = _rtp;
            _ = Task.Run(() => SaveRecordingAndPrune(wavPath, rtpToSave));
        }

        ReleaseRtpSession();

        var old = State;
        State = CallState.Ended;
        var ended = ActiveCall != null ? ActiveCall with { State = CallState.Ended, EndedAt = DateTime.UtcNow } : null;
        ActiveCall = null;
        _incomingInvite = null;
        _incomingReplySender = null;
        State = CallState.Idle;
        NotifyCallStateChanged(old, CallState.Ended);
        NotifyCallEnded(ended, "BYE");
        EventBus?.Publish("call.ended", "ImsCallManager", ended);
    }

    public async Task<CallInfo?> HangupAsync()
    {
        if (ActiveCall == null || State == CallState.Idle)
            return null;

        // If incoming and ringing, reject
        if (State == CallState.Ringing && !ActiveCall.IsOutgoing)
        {
            return await RejectAsync().ConfigureAwait(false);
        }

        // Cancel if outgoing and ringing, BYE if active
        if (State == CallState.Ringing && _callTransport != null && _currentCallId != null)
        {
            try
            {
                var cancel = BuildCancelRequest();
                _ = _callTransport.SendAsync(cancel);
            }
            catch { }
        }
        else if (State == CallState.Active && _callTransport != null && _currentCallId != null)
        {
            try
            {
                var bye = BuildByeRequest();
                _ = _callTransport.SendAsync(bye);
            }
            catch { /* hangup must succeed even if BYE send fails */ }
        }

        var old = State;
        State = CallState.Ended;
        var endedAt = DateTime.UtcNow;

        if (ActiveCall?.WavRecordingPath != null)
        {
            var wavPath = ActiveCall.WavRecordingPath;
            var rtpToSave = _rtp;
            _ = Task.Run(() => SaveRecordingAndPrune(wavPath, rtpToSave));
        }

        if (ActiveCall != null)
        {
            ActiveCall = ActiveCall with { State = CallState.Ended, EndedAt = endedAt };
            NotifyCallEnded(ActiveCall, "HANGUP");
            EventBus?.Publish("call.ended", "ImsCallManager", ActiveCall);
        }
        NotifyCallStateChanged(old, CallState.Ended);

        ReleaseRtpSession();

        var result = ActiveCall;
        State       = CallState.Idle;
        ActiveCall  = null;
        _incomingInvite = null;
        _incomingReplySender = null;
        return result;
    }

    private void NotifyCallStateChanged(CallState oldState, CallState newState, string? codec = null)
    {
        try
        {
            CallStateChanged?.Invoke(this, new CallStateChangedEventArgs(
                ActiveCall?.CallId ?? _currentCallId ?? "",
                ActiveCall?.TargetNumber ?? _targetUri ?? "",
                oldState,
                newState,
                codec ?? ActiveCall?.Codec,
                ActiveCall?.WavRecordingPath,
                ActiveCall?.IsOutgoing ?? true
            ));
        }
        catch { }
    }

    private void NotifyIncomingCall(string callId, string callerNumber)
    {
        try
        {
            IncomingCall?.Invoke(this, new IncomingCallEventArgs(
                callId,
                callerNumber,
                callerNumber,
                isVoWifi: true,
                DateTime.UtcNow
            ));
        }
        catch { }
    }

    private void NotifyCallConnected(CallInfo call)
    {
        try
        {
            CallConnected?.Invoke(this, new CallConnectedEventArgs(
                call.CallId,
                call.TargetNumber,
                call.Codec,
                call.ConnectedAt ?? DateTime.UtcNow
            ));
        }
        catch { }
    }

    private void NotifyCallEnded(CallInfo? call, string? reason = null)
    {
        if (call == null) return;
        try
        {
            var dur = (call.EndedAt ?? DateTime.UtcNow) - (call.ConnectedAt ?? call.StartedAt);
            CallEnded?.Invoke(this, new CallEndedEventArgs(
                call.CallId,
                call.TargetNumber,
                dur,
                reason,
                call.WavRecordingPath,
                call.EndedAt ?? DateTime.UtcNow
            ));
            AudioStreamStateChanged?.Invoke(this, new AudioStreamStateChangedEventArgs(false, false));
        }
        catch { }
    }

    private static string ExtractNumberFromUri(string uriOrHeader)
    {
        var match = Regex.Match(uriOrHeader, @"(?<=(sip:|tel:))(\+?\d+)");
        if (match.Success) return match.Groups[2].Value;

        var nameMatch = Regex.Match(uriOrHeader, @"""([^""]+)""");
        if (nameMatch.Success) return nameMatch.Groups[1].Value;

        return uriOrHeader;
    }

    private static string? ExtractRegisteredContactAddress(string? contact)
    {
        if (string.IsNullOrWhiteSpace(contact)) return null;

        // Contact is a SIP URI, not merely a host:port.  Preserve the address
        // that the registrar accepted: under ipsec-3gpp it is the protected
        // server port, distinct from the client source port in Via.
        var match = Regex.Match(contact,
            @"<\s*sip:(?:[^@;>]+@)?(?<host>\[[^\]]+\]|[^;>:]+):(?<port>\d{1,5})(?:[;>])",
            RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535)
            return null;
        return $"{match.Groups["host"].Value}:{port}";
    }

    private static bool RequiresReliableProvisional(SipMessage response) =>
        response.StatusCode is > 100 and < 200 &&
        !string.IsNullOrWhiteSpace(response.GetHeader("RSeq")) &&
        (response.GetHeader("Require") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => value.Equals("100rel", StringComparison.OrdinalIgnoreCase));

    private async Task SendPrackAsync(SipTransport transport, SipMessage provisional, CancellationToken ct)
    {
        var rseq = provisional.GetHeader("RSeq")?.Trim();
        var inviteCseq = provisional.GetHeader("CSeq")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var callId = provisional.GetHeader("Call-ID")?.Trim();
        if (string.IsNullOrWhiteSpace(rseq) || string.IsNullOrWhiteSpace(inviteCseq) || string.IsNullOrWhiteSpace(callId))
            return;

        var key = $"{callId}|{rseq}|{inviteCseq}";
        lock (_prackGate)
        {
            if (!_reliableProvisionals.Add(key)) return;
        }

        var to = provisional.GetHeader("To");
        if (!string.IsNullOrWhiteSpace(to)) _dialogTo = to;
        if (provisional.Headers.TryGetValue("Record-Route", out var recordRoutes) && recordRoutes.Count > 0)
            _dialogRoutes = ParseAndReverseRecordRoute(recordRoutes);

        var localIp = _currentLocalIp ?? "127.0.0.1";
        var branch = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];
        var prack = new SipMessage
        {
            IsRequest = true,
            Method = "PRACK",
            RequestUri = _dialogTargetUri ?? _targetUri ?? string.Empty,
            SipVersion = "SIP/2.0"
        };
        prack.SetHeader("Via", $"SIP/2.0/UDP {ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};branch={branch};rport");
        prack.SetHeader("Max-Forwards", "70");
        prack.SetHeader("From", _dialogFrom ?? string.Empty);
        prack.SetHeader("To", _dialogTo ?? string.Empty);
        prack.SetHeader("Call-ID", callId);
        prack.SetHeader("CSeq", $"{_cseq++} PRACK");
        prack.SetHeader("RAck", $"{rseq} {inviteCseq} INVITE");
        if (_dialogRoutes is { Count: > 0 }) prack.SetHeader("Route", string.Join(", ", _dialogRoutes));
        if (_currentVoWifi?.EpdgInfo is { } epdg)
            prack.SetHeader("P-Access-Network-Info", ImsRegisterBuilder.BuildAccessNetworkInfo(epdg.Impi));
        prack.SetHeader("User-Agent", "iPhone Pro/17");
        prack.SetHeader("Content-Length", "0");

        try
        {
            var response = await transport.SendAndReceiveFinalAsync(prack, timeoutMs: 10000, ct: ct).ConfigureAwait(false);
            if (response.StatusCode is < 200 or >= 300)
                Console.WriteLine($"[ImsCallManager] PRACK rejected: {response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            Console.WriteLine($"[ImsCallManager] PRACK failed: {ex.Message}");
        }
    }

    public Task<bool> SendDtmfAsync(char digit)
    {
        if (State != CallState.Active)
            return Task.FromResult(false);

        try { DtmfReceived?.Invoke(this, new DtmfReceivedEventArgs(digit)); } catch { }
        EventBus?.Publish("call.dtmf.sent", "ImsCallManager", new { Digit = digit });
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        ReleaseRtpSession();
        _audio.Dispose();
    }

    private void ReleaseRtpSession()
    {
        var rtp = _rtp;
        _rtp = null;

        if (rtp != null)
            _currentVoWifi?.UnregisterRtpSession(rtp);
        _currentVoWifi = null;
        rtp?.Dispose();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Sends a SIP ACK for a 2xx response (RFC 3261 §13.2.2.4).</summary>
    private async Task SendAckAsync(
        SipTransport transport,
        SipMessage invite,
        SipMessage resp200,
        CancellationToken ct)
    {
        var ack = new SipMessage
        {
            IsRequest  = true,
            Method     = "ACK",
            RequestUri = _dialogTargetUri ?? invite.RequestUri,
            SipVersion = "SIP/2.0"
        };

        // RFC 3261 §13.2.2.4: ACK for 2xx has its own Via branch
        var branch = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];
        var via    = invite.GetHeader("Via") ?? string.Empty;
        var viaHost = Regex.Match(via, @"SIP/2\.0/UDP ([^;]+)").Groups[1].Value;
        ack.SetHeader("Via",          $"SIP/2.0/UDP {viaHost};branch={branch}");
        ack.SetHeader("Max-Forwards", "70");
        ack.SetHeader("From",         _dialogFrom ?? invite.GetHeader("From") ?? string.Empty);
        ack.SetHeader("To",           _dialogTo ?? resp200.GetHeader("To") ?? string.Empty);
        ack.SetHeader("Call-ID",      invite.GetHeader("Call-ID") ?? string.Empty);
        var inviteCseqNum = invite.GetHeader("CSeq")?.Split(' ')[0] ?? "1";
        ack.SetHeader("CSeq",         $"{inviteCseqNum} ACK");
        if (_dialogRoutes != null && _dialogRoutes.Count > 0)
        {
            ack.SetHeader("Route", string.Join(", ", _dialogRoutes));
        }
        ack.SetHeader("Content-Length", "0");

        Console.WriteLine("[ImsCallManager] ACK sent for established IMS call.");
        await transport.SendAsync(ack, ct).ConfigureAwait(false);
    }

    private static List<string> ParseAndReverseRecordRoute(List<string> rawHeaders)
    {
        var entries = new List<string>();
        foreach (var headerVal in rawHeaders)
        {
            var matches = Regex.Matches(headerVal, @"<[^>]+>");
            if (matches.Count > 0)
            {
                foreach (Match m in matches)
                {
                    entries.Add(m.Value.Trim());
                }
            }
            else
            {
                var parts = headerVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                entries.AddRange(parts);
            }
        }
        entries.Reverse();
        return entries;
    }

    /// <summary>Builds a SIP BYE request for the current active call.</summary>
    private SipMessage BuildByeRequest()
    {
        var bye = new SipMessage
        {
            IsRequest  = true,
            Method     = "BYE",
            RequestUri = _dialogTargetUri ?? _targetUri ?? ActiveCall?.TargetNumber ?? string.Empty,
            SipVersion = "SIP/2.0"
        };

        var branch = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];
        var localIp = _currentLocalIp ?? "127.0.0.1";

        bye.SetHeader("Via",            $"SIP/2.0/UDP {ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};branch={branch};rport");
        bye.SetHeader("Max-Forwards",   "70");
        bye.SetHeader("From",           _dialogFrom ?? $"<sip:ue@{ImsRegisterBuilder.FormatHost(localIp)}>;tag={_currentFromTag}");
        bye.SetHeader("To",             _dialogTo ?? $"<{ActiveCall?.TargetNumber}>");
        bye.SetHeader("Call-ID",        _currentCallId ?? string.Empty);
        bye.SetHeader("CSeq",           $"{_cseq++} BYE");
        if (_dialogRoutes != null && _dialogRoutes.Count > 0)
        {
            bye.SetHeader("Route", string.Join(", ", _dialogRoutes));
        }
        bye.SetHeader("Content-Length", "0");
        return bye;
    }

    /// <summary>Builds a SIP CANCEL request for a ringing call (RFC 3261 §9).</summary>
    private SipMessage BuildCancelRequest()
    {
        var cancel = new SipMessage
        {
            IsRequest  = true,
            Method     = "CANCEL",
            RequestUri = _targetUri ?? ActiveCall?.TargetNumber ?? string.Empty,
            SipVersion = "SIP/2.0"
        };

        var localIp = _currentLocalIp ?? "127.0.0.1";
        var viaHeader = _lastInviteVia ?? $"SIP/2.0/UDP {ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};branch={_lastInviteBranch ?? ("z9hG4bK" + Guid.NewGuid().ToString("N")[..12])}";

        cancel.SetHeader("Via",            viaHeader);
        cancel.SetHeader("Max-Forwards",   "70");
        cancel.SetHeader("From",           _dialogFrom ?? $"<sip:ue@{ImsRegisterBuilder.FormatHost(localIp)}>;tag={_currentFromTag}");
        cancel.SetHeader("To",             _dialogTo ?? $"<{_targetUri ?? ActiveCall?.TargetNumber}>");
        cancel.SetHeader("Call-ID",        _currentCallId ?? string.Empty);
        cancel.SetHeader("CSeq",           $"{_lastInviteCSeq} CANCEL");
        cancel.SetHeader("Content-Length", "0");
        return cancel;
    }

    private string? CreateRecordingPath(string remoteNumber, bool isIncoming)
    {
        if (!SaveAudioRecordings) return null;

        try
        {
            var directory = string.IsNullOrWhiteSpace(RecordingDirectory)
                ? Directory.GetCurrentDirectory()
                : RecordingDirectory;
            Directory.CreateDirectory(directory);
            var safeNumber = Regex.Replace(remoteNumber, @"[^0-9A-Za-z+_-]", "_");
            var direction = isIncoming ? "incoming" : "outgoing";
            return Path.Combine(directory, $"call_{direction}_{safeNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        }
        catch (Exception ex)
        {
            EventBus?.Publish(EventTopics.SystemError, "ImsCallManager",
                $"Call recording was enabled but its folder is unavailable: {ex.Message}");
            return null;
        }
    }

    private void SaveRecordingAndPrune(string wavPath, RtpSession? rtpToSave)
    {
        try
        {
            if (rtpToSave != null)
                rtpToSave.SaveAudioRecording(wavPath);
            else
                _audio.SaveToWavFile(wavPath);

            PruneSavedRecordings(Path.GetDirectoryName(wavPath));
        }
        catch (Exception ex)
        {
            EventBus?.Publish(EventTopics.SystemError, "ImsCallManager", $"Could not save call recording: {ex.Message}");
        }
    }

    private void PruneSavedRecordings(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        _recordingRetentionGate.Wait();
        try
        {
            var recordings = Directory.EnumerateFiles(directory, "call_*.*", SearchOption.TopDirectoryOnly)
                .Where(path => IsRecordingExtension(Path.GetExtension(path)))
                .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    BaseName = group.Key,
                    Files = group.ToArray(),
                    OldestWriteTime = group.Min(path => File.GetLastWriteTimeUtc(path))
                })
                .OrderBy(recording => recording.OldestWriteTime)
                .ThenBy(recording => recording.BaseName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var recording in recordings.Take(Math.Max(0, recordings.Count - MaxSavedRecordingCalls)))
            {
                foreach (var path in recording.Files)
                {
                    try { File.Delete(path); }
                    catch (Exception ex)
                    {
                        EventBus?.Publish(EventTopics.SystemError, "ImsCallManager",
                            $"Could not remove expired call recording '{Path.GetFileName(path)}': {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            _recordingRetentionGate.Release();
        }
    }

    private static bool IsRecordingExtension(string extension) =>
        extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".amr", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the remote SDP Answer and configures the RTP session endpoint.
    /// </summary>
    /// <summary>
    /// SDP announces the connection address family explicitly (RFC 4566 §8.2.6). A dual-stack
    /// ePDG may assign IPv6, and "c=IN IP4 2001:db8::1" makes the peer reject the offer or send
    /// its RTP to a host it cannot resolve. The address itself stays unbracketed in SDP.
    /// </summary>
    private static string SdpAddressFamily(string? address) =>
        IPAddress.TryParse(address, out var parsed) &&
        parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "IP6"
            : "IP4";

    private void SetRtpFromSdpAnswer(string sdpBody)
    {
        if (_rtp == null) return;

        var connectionMatch = Regex.Match(sdpBody, @"c=IN IP[46] (\S+)");
        var mediaMatch      = Regex.Match(sdpBody, @"m=audio (\d+)");
        var ptMatch         = Regex.Match(sdpBody, @"a=rtpmap:(\d+) ([A-Za-z0-9\-]+)/");

        if (connectionMatch.Success && mediaMatch.Success)
        {
            if (IPAddress.TryParse(connectionMatch.Groups[1].Value, out var remoteIp)
                && int.TryParse(mediaMatch.Groups[1].Value, out var remotePort))
            {
                byte pt = 8;
                if (ptMatch.Success && byte.TryParse(ptMatch.Groups[1].Value, out var parsedPt))
                {
                    pt = parsedPt;
                }

                if (_currentVoWifi?.EspTunnel != null && _currentVoWifi.Transport != null && IPAddress.TryParse(_currentVoWifi.AssignedIp, out var localIp))
                {
                    var esp = _currentVoWifi.EspTunnel;
                    var tr = _currentVoWifi.Transport;
                    _rtp.CustomSender = async rtpBytes =>
                    {
                        var inner = VoWifi.IpPacketUtils.BuildUdpPacket(localIp, remoteIp, (ushort)_rtp.LocalPort, (ushort)remotePort, rtpBytes);
                        var nextHeader = localIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? (byte)41 : (byte)4;
                        var sealedEsp = esp.Seal(inner, nextHeader);
                        await tr.SendEspAsync(sealedEsp, CancellationToken.None).ConfigureAwait(false);
                    };
                }

                _rtp.SetRemoteEndpoint(remoteIp, remotePort, pt);
                try
                {
                    AudioStreamStateChanged?.Invoke(this, new AudioStreamStateChangedEventArgs(true, true, sampleRate: 8000, codec: pt == 8 ? "PCMA" : "PCMU"));
                }
                catch { }
            }
        }
    }

    /// <summary>Returns a human-readable codec string from the SDP answer.</summary>
    private static string ExtractCodecFromSdp(string sdpBody)
    {
        var match = Regex.Match(sdpBody, @"a=rtpmap:(\d+) ([A-Za-z0-9\-]+)/(\d+)");
        if (match.Success)
            return $"{match.Groups[2].Value}/{match.Groups[3].Value}";
        return "PCMA/8000";
    }
}
