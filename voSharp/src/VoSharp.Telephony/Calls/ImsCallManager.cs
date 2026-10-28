using System.Net;
using System.Diagnostics;
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

public class ImsCallManager : IDisposable, ICallPcmMedia
{
    public CallState State { get; private set; } = CallState.Idle;
    public CallInfo? ActiveCall { get; private set; }
    public AsyncEventBus? EventBus { get; }
    public bool IsIncoming => ActiveCall != null && !ActiveCall.IsOutgoing;
    public bool IsHostImsCall => _currentHostIms is not null && ActiveCall is not null;
    public RtpMediaStats? GetMediaStats() => _rtp?.GetMediaStats();
    public bool SaveAudioRecordings { get; private set; } = true;
    public string? RecordingDirectory { get; private set; }

    public event EventHandler<CallStateChangedEventArgs>? CallStateChanged;
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;
    public event EventHandler<CallConnectedEventArgs>? CallConnected;
    public event EventHandler<CallEndedEventArgs>? CallEnded;
    public event EventHandler<DtmfReceivedEventArgs>? DtmfReceived;
    public event EventHandler<AudioStreamStateChangedEventArgs>? AudioStreamStateChanged;
    /// <summary>
    /// Decoded 8 kHz mono PCM received from the carrier RTP leg.  A SIP B2BUA can
    /// subscribe to this without routing carrier audio through a Windows device.
    /// </summary>
    public event Action<short[]>? RemotePcmReceived;

    /// <summary>
    /// When enabled, SDP prefers codecs that the in-process SIP media bridge can
    /// encode in both directions (AMR-NB/PCMA/PCMU) instead of AMR-WB.
    /// </summary>
    public bool ExternalMediaBridgeEnabled { get; set; }

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
    private HostImsRegistrationClient? _currentHostIms;
    private string? _currentAccessNetworkInfo;
    private int _cseq = 1;
    private long _remoteDialogCseq;
    private SipMessage? _pendingOutgoingInvite;
    private int _outgoingInviteInProgress;
    private int _outgoingInviteCancelled;
    private int _outgoingInviteMaySignal;
    private int _outgoingInviteProvisionalReceived;
    private int _outgoingInviteFinalReceived;
    private int _outgoingInviteCancelSent;
    private CancellationTokenSource? _outgoingInviteDrain;
    private string? _outgoingEarlyAnswerSdp;
    private readonly object _prackGate = new();
    private readonly HashSet<string> _reliableProvisionals = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _recordingRetentionGate = new(1, 1);
    private readonly object _sessionTimerGate = new();
    private CancellationTokenSource? _sessionTimerCts;
    private Task? _sessionTimerTask;
    private long _sessionTimerGeneration;
    private int _sessionExpiresSeconds = 1800;
    private bool _localSessionRefresher;
    private const int MaxSavedRecordingCalls = 20;
    private const int MinimumSessionExpiresSeconds = 90;

    private SipMessage? _incomingInvite;
    private Func<SipMessage, Task>? _incomingReplySender;
    private string? _remoteOfferSdp;
    private volatile bool _incomingAnswerAwaitingAck;
    private volatile bool _incomingQosEarlyAnswer;
    private volatile bool _incomingPrackReceived;
    private volatile bool _incomingQosSatisfied;
    private int _incomingRingingStarted;
    private const int IncomingReliableRseq = 1;
    private int _incomingReliableOutstandingRseq;

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
        _audio.SetRecordingEnabled(enabled);
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

        var epdgInfo = voWifi.EpdgInfo
            ?? throw new InvalidOperationException("IMS network identity is unavailable for the active VoWiFi session.");
        var transport = voWifi.SipTransport
            ?? throw new InvalidOperationException("IMS SIP transport is unavailable for the active VoWiFi session.");
        var access = new ImsDialAccess(transport, voWifi.AssignedIp, epdgInfo.ImsDomain,
            epdgInfo.Impi, epdgInfo.Impu, voWifi.ImsInfo?.ContactUri,
            voWifi.ImsInfo?.ServiceRoute, voWifi.ImsInfo?.PAssociatedUri,
            ImsRegisterBuilder.BuildAccessNetworkInfo(epdgInfo.Impi), voWifi, null, null);
        return await DialCoreAsync(number, access, ct).ConfigureAwait(false);
    }

    /// <summary>Places a call over an already registered Windows-owned cellular IMS bearer.</summary>
    public async Task<CallInfo> DialAsync(
        string number,
        HostImsRegistrationClient hostIms,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hostIms);
        if (!hostIms.IsRegistered)
            throw new InvalidOperationException("Host IMS registration is not active.");
        var transport = hostIms.Transport;
        if (transport.BoundInterfaceIndex is not > 0 ||
            transport.LocalEndPoint?.Address.Equals(hostIms.Endpoint.LocalAddress) != true)
            throw new InvalidOperationException("Host IMS signaling is not bound to a verified cellular interface.");
        // A cell can change between the last REGISTER and this call. Do not
        // announce the previous cell in PANI on a new originating INVITE.
        var preparation = Stopwatch.StartNew();
        await hostIms.RefreshAccessNetworkInfoAsync(ct, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        hostIms.Registration.TraceCallSetup($"Host IMS access metadata prepared; elapsed={preparation.ElapsedMilliseconds}ms.");
        var result = hostIms.CurrentResult
            ?? throw new InvalidOperationException("Host IMS registration ended before the call started.");
        var profile = hostIms.Profile;
        var access = new ImsDialAccess(transport, hostIms.Endpoint.LocalAddress.ToString(),
            profile.HomeDomain, profile.PrivateIdentity, profile.PublicIdentity,
            result.ContactUri, result.ServiceRoute, result.PAssociatedUri,
            profile.PAccessNetworkInfo, null, hostIms, transport.BoundInterfaceIndex);
        return await DialCoreAsync(number, access, ct).ConfigureAwait(false);
    }

    private sealed record ImsDialAccess(
        SipTransport Transport, string LocalIp, string HomeDomain,
        string PrivateIdentity, string PublicIdentity, string? ContactUri,
        string? ServiceRoute, string? PAssociatedUri, string? AccessNetworkInfo,
        VoWifiManager? VoWifi, HostImsRegistrationClient? HostIms, int? InterfaceIndex);

    private async Task<CallInfo> DialCoreAsync(
        string number, ImsDialAccess access, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _outgoingInviteInProgress, 1, 0) != 0)
            throw new InvalidOperationException("The previous IMS INVITE is still awaiting its final response.");
        try
        {
            Volatile.Write(ref _outgoingInviteCancelled, 0);
            Volatile.Write(ref _outgoingInviteMaySignal, 0);
            Volatile.Write(ref _outgoingInviteProvisionalReceived, 0);
            Volatile.Write(ref _outgoingInviteFinalReceived, 0);
            Volatile.Write(ref _outgoingInviteCancelSent, 0);
            return await DialCoreImplementationAsync(number, access, ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingOutgoingInvite = null;
            Volatile.Write(ref _outgoingInviteInProgress, 0);
        }
    }

    private async Task<CallInfo> DialCoreImplementationAsync(
        string number, ImsDialAccess access, CancellationToken ct)
    {

        if (State is CallState.Active or CallState.Dialing or CallState.Ringing or CallState.Held)
            throw new InvalidOperationException("Another call is already in progress.");

        var cleanNumber = number.Trim();
        _cseq = 1;
        lock (_prackGate) _reliableProvisionals.Clear();
        _currentCallId  = Guid.NewGuid().ToString("N") + "@" + access.LocalIp;
        _currentFromTag = Guid.NewGuid().ToString("N")[..8];
        _remoteDialogCseq = 0;
        _dialogRoutes = null;
        _dialogTargetUri = null;
        _outgoingEarlyAnswerSdp = null;
        CancelSessionTimer();

        var startedAt = DateTime.UtcNow;
        var setupClock = Stopwatch.StartNew();

        // BUG-20 FIX: clear recorded audio buffer before starting a new call
        _audio.ClearRecording();
        ReleaseRtpSession();
        _rtp = access.InterfaceIndex is { } interfaceIndex
            ? new RtpSession(_audio, EventBus, localAddress: IPAddress.Parse(access.LocalIp),
                outgoingInterfaceIndex: interfaceIndex, recordAudio: SaveAudioRecordings)
            : new RtpSession(_audio, EventBus, recordAudio: SaveAudioRecordings);
        AttachExternalMediaSink(_rtp);
        _currentVoWifi = access.VoWifi;
        _currentHostIms = access.HostIms;
        _currentAccessNetworkInfo = access.AccessNetworkInfo;
        access.VoWifi?.RegisterRtpSession(_rtp);
        State = CallState.Dialing;

        var localIp = access.LocalIp;
        var signalingEndpoint = access.Transport.LocalEndPoint
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
        // The bundled codec can decode AMR-WB but cannot encode it. Offer only
        // codecs that can carry audio in both directions.
        sdp.Append(ExternalMediaBridgeEnabled
            ? $"m=audio {_rtp.LocalPort} RTP/AVP 8 0 102 101\r\n"
            : $"m=audio {_rtp.LocalPort} RTP/AVP 102 8 0 101\r\n");
        sdp.Append($"a=rtcp:{_rtp.LocalRtcpPort}\r\n");
        sdp.Append("a=rtpmap:102 AMR/8000/1\r\n");
        sdp.Append("a=fmtp:102 mode-change-capability=2; max-red=0\r\n");
        sdp.Append("a=rtpmap:8 PCMA/8000/1\r\n");
        sdp.Append("a=rtpmap:0 PCMU/8000/1\r\n");
        sdp.Append("a=rtpmap:101 telephone-event/8000\r\n");
        sdp.Append("a=fmtp:101 0-15\r\n");
        sdp.Append("a=ptime:20\r\n");
        sdp.Append("a=maxptime:240\r\n");
        sdp.Append("a=sendrecv\r\n");
        var sdpStr = sdp.ToString();

        // ── Build SIP INVITE (matching voCore 3GPP TS 24.229) ──────────────────
        var homeDomain = access.HomeDomain;
        var targetUri = cleanNumber.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || cleanNumber.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
            ? cleanNumber
            : cleanNumber.StartsWith('+')
                ? "tel:" + cleanNumber
                : $"tel:{cleanNumber};phone-context={homeDomain}";

        // Use primary public identity from P-Associated-URI or fallback to Impu
        string publicURI = access.PublicIdentity;
        if (!string.IsNullOrWhiteSpace(access.PAssociatedUri))
        {
            var match = Regex.Match(access.PAssociatedUri, @"<([^>]+)>");
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
        var associatedNumber = ExtractNumberFromUri(access.PAssociatedUri ?? publicURI);
        var fromIdentity = associatedNumber.StartsWith('+')
            ? $"sip:{associatedNumber}@{homeDomain}"
            : publicURI;
        var preferredIdentity = associatedNumber.StartsWith('+')
            ? $"tel:{associatedNumber}"
            : publicURI;

        var contactUser = !string.IsNullOrEmpty(access.ContactUri)
            ? Regex.Match(access.ContactUri, @"<sip:([^@]+)@").Groups[1].Value
            : access.PrivateIdentity.Split('@')[0];
        if (string.IsNullOrEmpty(contactUser)) contactUser = user;

        var signalingReceivePort = access.HostIms?.Transport.ProtectedServerEndPoint?.Port
            ?? signalingEndpoint.Port;
        var contactAddress = ExtractRegisteredContactAddress(access.ContactUri)
            ?? $"{signalingHost}:{signalingReceivePort}";
        string contact = $"<sip:{contactUser}@{contactAddress};transport=udp>;+g.3gpp.icsi-ref=\"urn%3Aurn-7%3A3gpp-service.ims.icsi.mmtel\";audio";
        if (!string.IsNullOrWhiteSpace(access.ContactUri))
        {
            var instMatch = Regex.Match(access.ContactUri, @"(\+sip\.instance=""[^""]+"")");
            if (instMatch.Success)
                contact += $";{instMatch.Groups[1].Value}";
        }

        _currentLocalIp = signalingEndpoint.Address.ToString();
        _currentSignalingPort = signalingReceivePort;
        _targetUri = targetUri;

        var invite = new SipMessage
        {
            IsRequest  = true,
            Method     = "INVITE",
            RequestUri = targetUri,
            SipVersion = "SIP/2.0",
            Body       = sdpStr
        };

        if (!string.IsNullOrEmpty(access.ServiceRoute))
        {
            invite.SetHeader("Route", access.ServiceRoute);
        }

        var branch = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];
        // An IPv6 address must be bracketed or "host:port" becomes ambiguous (RFC 3261 §25.1).
        var inviteVia = $"SIP/2.0/UDP {signalingHost}:{signalingReceivePort};branch={branch};rport";
        _dialogFrom = $"<{fromIdentity}>;tag={_currentFromTag}";
        _dialogTo = $"<{targetUri}>";
        _targetUri = targetUri;
        _currentLocalIp = signalingEndpoint.Address.ToString();
        _currentSignalingPort = signalingReceivePort;

        invite.SetHeader("Via",      inviteVia);
        invite.SetHeader("Max-Forwards", "70");
        invite.SetHeader("From",     _dialogFrom);
        invite.SetHeader("To",       _dialogTo);
        invite.SetHeader("Call-ID",  _currentCallId);
        invite.SetHeader("CSeq",     $"{_cseq++} INVITE");
        invite.SetHeader("Contact",  contact);
        invite.SetHeader("P-Preferred-Identity", $"<{preferredIdentity}>");
        invite.SetHeader("P-Preferred-Service",  "urn:urn-7:3gpp-service.ims.icsi.mmtel");
        invite.SetHeader("Accept-Contact",      "*;+g.3gpp.icsi-ref=\"urn%3Aurn-7%3A3gpp-service.ims.icsi.mmtel\"");
        if (!string.IsNullOrWhiteSpace(access.AccessNetworkInfo))
            invite.SetHeader("P-Access-Network-Info", access.AccessNetworkInfo);
        invite.SetHeader("Allow", access.HostIms is null
            ? "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE, PRACK, UPDATE, INFO"
            : "INVITE, ACK, CANCEL, BYE, OPTIONS, PRACK, UPDATE, INFO");
        invite.SetHeader("Supported",     "100rel, timer, replaces");
        invite.SetHeader("Session-Expires", "1800;refresher=uac");
        invite.SetHeader("Min-SE",        "90");
        invite.SetHeader("Accept",        "application/sdp");
        invite.SetHeader("Content-Type",   "application/sdp");
        invite.SetHeader("Content-Length", sdpStr.Length.ToString());
        invite.SetHeader("User-Agent",    "iPhone Pro/17");
        _pendingOutgoingInvite = invite;

        Console.WriteLine($"[ImsCallManager] INVITE prepared for {targetUri} via IMS receive port {signalingReceivePort}.");

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
        var sipTransport = access.Transport;
        if (sipTransport != null)
        {
            _callTransport = sipTransport; // reuse VoWiFi SIP transport

            // Cancelling a bridge request means hang up, not discard the IMS
            // transaction reader. Keep it alive for 487 or a late 200 (ACK/BYE).
            using var cancellationDrain = new CancellationTokenSource();
            Volatile.Write(ref _outgoingInviteDrain, cancellationDrain);
            Task cancellationCleanup = Task.CompletedTask;
            using var cancellationRegistration = ct.Register(() =>
            {
                cancellationDrain.CancelAfter(TimeSpan.FromSeconds(32));
                cancellationCleanup = HangupAsync();
            });

            try
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _outgoingInviteCancelled) != 0)
                    throw new OperationCanceledException("IMS call ended before INVITE send.");
                EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager", "Outbound IMS INVITE starting; identities omitted.");
                var finalResp = await sipTransport.SendAndReceiveFinalAsync(
                    invite,
                    onProvisional: prov =>
                    {
                        EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager",
                            $"Outbound IMS provisional; status={prov.StatusCode}; elapsed={setupClock.ElapsedMilliseconds}ms; identities omitted.");
                        Volatile.Write(ref _outgoingInviteProvisionalReceived, 1);
                        if (prov.StatusCode > 100 &&
                            !string.IsNullOrWhiteSpace(ExtractTag(prov.GetHeader("To"))))
                        {
                            _dialogTo = prov.GetHeader("To");
                            if (prov.Headers.TryGetValue("Record-Route", out var earlyRoutes) && earlyRoutes.Count > 0)
                                _dialogRoutes = ParseRecordRoute(earlyRoutes, reverse: true);
                            var earlyContact = prov.GetHeader("Contact");
                            var earlyTarget = Regex.Match(earlyContact ?? string.Empty, @"<([^>]+)>");
                            if (earlyTarget.Success) _dialogTargetUri = earlyTarget.Groups[1].Value.Trim();
                            if (!string.IsNullOrWhiteSpace(prov.Body))
                                _outgoingEarlyAnswerSdp = prov.Body;
                        }
                        if (Volatile.Read(ref _outgoingInviteCancelled) != 0)
                        {
                            if (Volatile.Read(ref _outgoingInviteMaySignal) != 0)
                                _ = SendPendingCancelAsync(sipTransport);
                            return;
                        }
                        if (RequiresReliableProvisional(prov))
                            _ = SendPrackAsync(sipTransport, prov, cancellationDrain.Token);
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
                    provisionalTimeoutMs: 180000,
                    ct: cancellationDrain.Token
                ).ConfigureAwait(false);
                Volatile.Write(ref _outgoingInviteFinalReceived, 1);
                EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager",
                    $"Outbound IMS final; status={finalResp.StatusCode}; elapsed={setupClock.ElapsedMilliseconds}ms; cancelled={Volatile.Read(ref _outgoingInviteCancelled) != 0}.");

                if (finalResp?.StatusCode == 200)
                {
                    var contactHeader = finalResp.GetHeader("Contact") ?? "";
                    var contactMatch = Regex.Match(contactHeader, @"<([^>]+)>");
                    _dialogTargetUri = contactMatch.Success ? contactMatch.Groups[1].Value.Trim() : (string.IsNullOrWhiteSpace(contactHeader) ? targetUri : contactHeader.Trim());
                    _dialogTo = finalResp.GetHeader("To") ?? invite.GetHeader("To") ?? "";
                    _dialogFrom = invite.GetHeader("From") ?? "";
                    if (finalResp.Headers.TryGetValue("Record-Route", out var recordRoutes) && recordRoutes.Count > 0)
                    {
                        _dialogRoutes = ParseRecordRoute(recordRoutes, reverse: true);
                    }
                    // RFC 3261 §12.1.2: a final response without Record-Route
                    // establishes an empty dialog route set. The REGISTER
                    // Service-Route only applied to the initial INVITE.
                    else _dialogRoutes = [];

                    // CANCEL can lose the race with a 200 OK. A user hangup
                    // still needs ACK then BYE, but a card-removal abort must
                    // never send more traffic on the stale bearer.
                    if (Volatile.Read(ref _outgoingInviteCancelled) != 0)
                    {
                        if (Volatile.Read(ref _outgoingInviteMaySignal) != 0)
                        {
                            await SendAckAsync(sipTransport, invite, finalResp, CancellationToken.None)
                                .ConfigureAwait(false);
                            await sipTransport.SendAsync(BuildByeRequest(), CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        throw new OperationCanceledException("IMS call was hung up before the network answered.");
                    }

                    // Send ACK (RFC 3261 §13.2.2.4)
                    await SendAckAsync(sipTransport, invite, finalResp, CancellationToken.None).ConfigureAwait(false);

                    // Extract remote RTP endpoint from SDP Answer
                    try
                    {
                        await SetRtpFromSdpAnswerAsync(
                            string.IsNullOrWhiteSpace(finalResp.Body) ? _outgoingEarlyAnswerSdp ?? string.Empty : finalResp.Body,
                            cancellationDrain.Token,
                            new HashSet<byte> { 102, 8, 0 }).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        try { await sipTransport.SendAsync(BuildByeRequest(), CancellationToken.None).ConfigureAwait(false); }
                        catch { /* The local media session must still fail closed. */ }
                        throw;
                    }

                    // Cancellation can also win while route/media setup awaits.
                    if (Volatile.Read(ref _outgoingInviteCancelled) != 0)
                    {
                        if (Volatile.Read(ref _outgoingInviteMaySignal) != 0)
                            await sipTransport.SendAsync(BuildByeRequest(), CancellationToken.None).ConfigureAwait(false);
                        throw new OperationCanceledException("IMS call ended during media setup.");
                    }

                    var codec = _rtp?.Codec ?? ExtractCodecFromSdp(finalResp.Body ?? string.Empty);
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
                    StartSessionTimer(finalResp.GetHeader("Session-Expires"), localIsUac: true);
                }
                else if (finalResp != null)
                {
                    await SendRejectedInviteAckAsync(sipTransport, invite, finalResp).ConfigureAwait(false);
                    if (Volatile.Read(ref _outgoingInviteCancelled) != 0)
                        throw new OperationCanceledException("IMS call was hung up before the network answered.");
                    CancelSessionTimer();
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
                if (Volatile.Read(ref _outgoingInviteCancelled) != 0)
                    throw new OperationCanceledException("IMS call setup ended after local hangup.");
                // A ringing INVITE must not simply disappear locally when the
                // post-provisional wait expires. Ask the network to clear it.
                if (Volatile.Read(ref _outgoingInviteProvisionalReceived) != 0)
                    await SendPendingCancelAsync(sipTransport).ConfigureAwait(false);
                CancelSessionTimer();
                // No final response arrived before the bounded setup deadline.
                var old = State;
                State      = CallState.Ended;
                call       = (ActiveCall ?? call) with { State = CallState.Ended, EndedAt = DateTime.UtcNow };
                ActiveCall = call;
                NotifyCallStateChanged(old, CallState.Ended);
                NotifyCallEnded(ActiveCall, "INVITE timeout");
                EventBus?.Publish("call.failed", "ImsCallManager", "INVITE timeout");
                throw;
            }
            catch (Exception ex)
            {
                // Transport errors, cancellation during setup and media errors
                // must all emit a terminal event before finally clears ActiveCall.
                // Otherwise the kernel/UI retain a call which can no longer hang up.
                if (ActiveCall is { State: not CallState.Ended } failed)
                {
                    CancelSessionTimer();
                    var old = State;
                    State = CallState.Ended;
                    ActiveCall = failed with { State = CallState.Ended, EndedAt = DateTime.UtcNow };
                    NotifyCallStateChanged(old, CallState.Ended);
                    NotifyCallEnded(ActiveCall, ex is OperationCanceledException ? "CANCEL" : "Call setup failed");
                    EventBus?.Publish("call.ended", "ImsCallManager", ActiveCall);
                }
                throw;
            }
            finally
            {
                // Stop further callbacks before disposing their drain token.
                await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
                Interlocked.CompareExchange(ref _outgoingInviteDrain, null, cancellationDrain);
                try { await cancellationCleanup.ConfigureAwait(false); } catch { }
                if (State is not (CallState.Active or CallState.Held))
                {
                    ReleaseRtpSession();
                    _callTransport = null;
                    ActiveCall = null;
                    State = CallState.Idle;
                }
            }
        }
        else
        {
            throw new InvalidOperationException("IMS SIP transport is unavailable; no call was placed.");
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
        ArgumentNullException.ThrowIfNull(voWifi);
        await HandleIncomingInviteCoreAsync(invite, voWifi, null,
            voWifi.SipTransport, voWifi.AssignedIp,
            voWifi.EpdgInfo is { } epdg ? ImsRegisterBuilder.BuildAccessNetworkInfo(epdg.Impi) : null,
            replySender).ConfigureAwait(false);
    }

    /// <summary>Accepts an incoming INVITE on an active Windows Host IMS registration.</summary>
    public Task HandleIncomingInviteAsync(
        SipMessage invite,
        HostImsRegistrationClient hostIms,
        Func<SipMessage, Task> replySender)
    {
        ArgumentNullException.ThrowIfNull(hostIms);
        if (!hostIms.IsRegistered || hostIms.Transport.BoundInterfaceIndex is not > 0)
            throw new InvalidOperationException("Host IMS registration is not active on a verified cellular interface.");
        return HandleIncomingInviteCoreAsync(invite, null, hostIms,
            hostIms.Transport, hostIms.Endpoint.LocalAddress.ToString(),
            hostIms.Profile.PAccessNetworkInfo, replySender);
    }

    private async Task HandleIncomingInviteCoreAsync(
        SipMessage invite, VoWifiManager? voWifi, HostImsRegistrationClient? hostIms,
        SipTransport? transport, string? localAddress, string? accessNetworkInfo,
        Func<SipMessage, Task> replySender)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(replySender);
        if (Volatile.Read(ref _outgoingInviteInProgress) != 0 && State == CallState.Idle)
        {
            await replySender(invite.CreateResponse(486, "Busy Here")).ConfigureAwait(false);
            return;
        }

        if (State is CallState.Active or CallState.Held or CallState.Dialing or CallState.Incoming or CallState.Ringing)
        {
            if (string.Equals(_currentCallId, invite.GetHeader("Call-ID"), StringComparison.OrdinalIgnoreCase))
            {
                if (State is CallState.Incoming or CallState.Ringing)
                {
                    await replySender(invite.CreateResponse(491, "Request Pending")).ConfigureAwait(false);
                    return;
                }
                if (!MatchesCurrentDialog(invite))
                {
                    await replySender(invite.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
                    return;
                }
                if (!TryAdvanceRemoteDialogCseq(invite))
                {
                    var stale = invite.CreateResponse(500, "CSeq Out of Order");
                    stale.SetHeader("Retry-After", "0");
                    await replySender(stale).ConfigureAwait(false);
                    return;
                }
                await HandleInDialogOfferAsync(invite, replySender, sendTrying: true).ConfigureAwait(false);
                return;
            }

            // Busy here (486)
            var busyResp = invite.CreateResponse(486, "Busy Here");
            busyResp.SetHeader("To", EnsureTag(invite.GetHeader("To") ?? string.Empty, Guid.NewGuid().ToString("N")[..8]));
            await replySender(busyResp).ConfigureAwait(false);
            return;
        }

        _incomingInvite = invite;
        _incomingAnswerAwaitingAck = false;
        _incomingQosEarlyAnswer = false;
        _incomingPrackReceived = false;
        _incomingQosSatisfied = false;
        Interlocked.Exchange(ref _incomingRingingStarted, 0);
        _incomingReliableOutstandingRseq = 0;
        _incomingReplySender = replySender;
        _currentVoWifi = voWifi;
        _currentHostIms = hostIms;
        _currentAccessNetworkInfo = accessNetworkInfo;
        _callTransport = transport;
        _currentCallId = invite.GetHeader("Call-ID") ?? Guid.NewGuid().ToString("N");
        _currentFromTag = Guid.NewGuid().ToString("N")[..8];
        _remoteDialogCseq = ParseCseqNumber(invite);
        CancelSessionTimer();
        _remoteOfferSdp = invite.Body;
        var incomingEndpoint = transport?.LocalEndPoint;
        _currentLocalIp = incomingEndpoint?.Address.ToString() ?? localAddress ?? "127.0.0.1";
        _currentSignalingPort = _currentHostIms?.Transport.ProtectedServerEndPoint?.Port
            ?? incomingEndpoint?.Port ?? 5060;

        var callerIdentity = ReadIncomingCallerIdentity();
        EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager",
            $"Incoming IMS INVITE accepted for negotiation; {callerIdentity}");

        var fromHeader = invite.GetHeader("From") ?? "Unknown";

        _dialogFrom = EnsureTag(invite.GetHeader("To") ?? string.Empty, _currentFromTag);
        _dialogTo = fromHeader;
        // The UAS copies request Record-Route values in their original order.
        _dialogRoutes = invite.Headers.TryGetValue("Record-Route", out var rrs)
            ? ParseRecordRoute(rrs, reverse: false) : [];
        var contactHeader = invite.GetHeader("Contact") ?? "";
        var contactMatch = Regex.Match(contactHeader, @"<([^>]+)>");
        _dialogTargetUri = contactMatch.Success ? contactMatch.Groups[1].Value : (string.IsNullOrWhiteSpace(contactHeader) ? fromHeader : contactHeader);

        // 1. Send 100 Trying
        var resp100 = invite.CreateResponse(100, "Trying");
        await replySender(resp100).ConfigureAwait(false);

        if (HasUnverifiedLocalMandatoryQos(_remoteOfferSdp))
        {
            var failure = invite.CreateResponse(580, "Precondition Failure");
            failure.SetHeader("To", _dialogFrom!);
            await replySender(failure).ConfigureAwait(false);
            _incomingInvite = null;
            _incomingReplySender = null;
            return;
        }

        if (RequiresEarlyQosAnswer(_remoteOfferSdp))
        {
            if (!HeaderHasOption(invite.GetHeader("Supported"), "100rel") &&
                !HeaderHasOption(invite.GetHeader("Require"), "100rel"))
            {
                var failure = invite.CreateResponse(580, "Precondition Failure");
                failure.SetHeader("To", _dialogFrom!);
                await replySender(failure).ConfigureAwait(false);
                _incomingInvite = null;
                _incomingReplySender = null;
                return;
            }

            EnsureIncomingRtpSession();
            try
            {
                await SetRtpFromSdpAnswerAsync(_remoteOfferSdp!, CancellationToken.None,
                    startMediaSenders: false).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                var unsupported = invite.CreateResponse(488, "Not Acceptable Here");
                unsupported.SetHeader("To", _dialogFrom!);
                await replySender(unsupported).ConfigureAwait(false);
                ReleaseRtpSession();
                _incomingInvite = null;
                _incomingReplySender = null;
                return;
            }
            var earlySdp = BuildLocalSdpAnswer() + BuildQosAnswer(_remoteOfferSdp!, requestConfirmation: true);
            var progress = invite.CreateResponse(183, "Session Progress");
            progress.Body = earlySdp;
            progress.SetHeader("To", _dialogFrom!);
            CopyRecordRoute(invite, progress);
            progress.SetHeader("Contact", BuildDialogContact(_currentLocalIp!));
            progress.SetHeader("Require", "100rel");
            progress.SetHeader("Supported", "precondition, 100rel, timer, replaces");
            progress.SetHeader("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, PRACK, UPDATE");
            progress.SetHeader("RSeq", IncomingReliableRseq.ToString());
            progress.SetHeader("Content-Type", "application/sdp");
            progress.SetHeader("Content-Length", Encoding.UTF8.GetByteCount(earlySdp).ToString());
            _incomingQosEarlyAnswer = true;
            State = CallState.Incoming;
            ActiveCall = CreateIncomingCallInfo(CallState.Incoming);
            _incomingReliableOutstandingRseq = IncomingReliableRseq;
            await replySender(progress).ConfigureAwait(false);
            EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager",
                "Incoming IMS reliable 183 sent; waiting for PRACK and originator QoS confirmation before alerting.");
            _ = ExpirePendingIncomingQosAsync(_currentCallId!);
            return;
        }

        // 2. No pending mandatory precondition: alert the user.
        await BeginIncomingRingingAsync().ConfigureAwait(false);
    }

    private async Task BeginIncomingRingingAsync()
    {
        if (_incomingInvite is not { } invite || _incomingReplySender is not { } replySender)
            return;
        if (Interlocked.Exchange(ref _incomingRingingStarted, 1) != 0) return;
        var localIp = _currentLocalIp!;
        var resp180 = invite.CreateResponse(180, "Ringing");
        resp180.SetHeader("To", _dialogFrom!);
        CopyRecordRoute(invite, resp180);
        resp180.SetHeader("Contact", BuildDialogContact(localIp));
        var requireReliable = HeaderHasOption(invite.GetHeader("Require"), "100rel");
        resp180.SetHeader("Allow", _currentHostIms is null
            ? requireReliable
                ? "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE, PRACK, UPDATE"
                : "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE"
            : requireReliable
                ? "INVITE, ACK, CANCEL, BYE, OPTIONS, PRACK, UPDATE"
                : "INVITE, ACK, CANCEL, BYE, OPTIONS");
        if (requireReliable)
        {
            resp180.SetHeader("Require", "100rel");
            _incomingReliableOutstandingRseq = _incomingQosEarlyAnswer
                ? IncomingReliableRseq + 1 : IncomingReliableRseq;
            resp180.SetHeader("RSeq", _incomingReliableOutstandingRseq.ToString());
        }
        var oldState = State;
        State = CallState.Ringing;
        ActiveCall = ActiveCall is { } pending
            ? pending with { State = CallState.Ringing }
            : CreateIncomingCallInfo(CallState.Ringing);
        await replySender(resp180).ConfigureAwait(false);

        NotifyCallStateChanged(oldState, CallState.Ringing);
        NotifyIncomingCall(_currentCallId!, ActiveCall.TargetNumber);
        EventBus?.Publish("call.incoming", "ImsCallManager", ActiveCall);
        EventBus?.Publish(EventTopics.CallIncoming, "ImsCallManager", ActiveCall.TargetNumber);
    }

    private ImsIncomingCallerIdentity ReadIncomingCallerIdentity() =>
        ImsIncomingCallerIdentity.Read(_incomingInvite ?? new SipMessage(),
            trustedCarrier: _currentHostIms is not null || _currentVoWifi is not null);

    private CallInfo CreateIncomingCallInfo(CallState state) => new(
            CallId: _currentCallId!,
            TargetNumber: ReadIncomingCallerIdentity().Number,
            State: state,
            StartedAt: DateTime.UtcNow,
            ConnectedAt: null,
            EndedAt: null,
            Codec: ExtractCodecFromSdp(_remoteOfferSdp ?? string.Empty),
            WavRecordingPath: CreateRecordingPath(
                ReadIncomingCallerIdentity().Number, isIncoming: true),
            IsOutgoing: false
        );

    private static bool HeaderHasOption(string? header, string option) =>
        (header ?? string.Empty).Split(',').Any(value =>
            value.Trim().Equals(option, StringComparison.OrdinalIgnoreCase));

    private static bool RequiresEarlyQosAnswer(string? offer) =>
        Regex.IsMatch(offer ?? string.Empty,
            @"(?im)^a=des:qos\s+mandatory\s+local\s+sendrecv\s*$") &&
        !Regex.IsMatch(offer ?? string.Empty,
            @"(?im)^a=curr:qos\s+local\s+sendrecv\s*$");

    private static bool HasUnverifiedLocalMandatoryQos(string? offer) =>
        Regex.IsMatch(offer ?? string.Empty,
            @"(?im)^a=des:qos\s+mandatory\s+remote\s+(?:sendrecv|send|recv)\s*$");

    private static bool IsOriginatorQosReady(string? offer) =>
        Regex.IsMatch(offer ?? string.Empty,
            @"(?im)^a=curr:qos\s+local\s+sendrecv\s*$");

    private static string BuildQosAnswer(string offer, bool requestConfirmation)
    {
        var lines = Regex.Matches(offer, @"(?im)^a=(?:curr|des):qos[^\r\n]*")
            .Select(match => Regex.IsMatch(match.Value, @"^a=curr:qos\s+remote\b",
                    RegexOptions.IgnoreCase)
                ? "a=curr:qos local none" // No verified Windows-side reservation.
                : Regex.Replace(match.Value,
                @"\b(local|remote|send|recv)\b", token => token.Value.ToLowerInvariant() switch
                {
                    "local" => "remote",
                    "remote" => "local",
                    "send" => "recv",
                    _ => "send"
                }, RegexOptions.IgnoreCase))
            .ToArray();
        return string.Concat(lines.Select(line => line + "\r\n")) +
            (requestConfirmation ? "a=conf:qos remote sendrecv\r\n" : string.Empty);
    }

    private void EnsureIncomingRtpSession()
    {
        if (_rtp is not null) return;
        _audio.ClearRecording();
        var hostIms = _currentHostIms;
        _rtp = hostIms?.Transport.BoundInterfaceIndex is { } interfaceIndex
            ? new RtpSession(_audio, EventBus, localAddress: hostIms.Endpoint.LocalAddress,
                outgoingInterfaceIndex: interfaceIndex, recordAudio: SaveAudioRecordings)
            : new RtpSession(_audio, EventBus, recordAudio: SaveAudioRecordings);
        _rtp.MediaEnabled = false;
        AttachExternalMediaSink(_rtp);
        _currentVoWifi?.RegisterRtpSession(_rtp);
    }

    /// <summary>
    /// Answers an incoming ringing call by sending 200 OK with SDP answer and starting the RTP media session.
    /// </summary>
    public async Task<CallInfo> AnswerAsync(CancellationToken ct = default)
    {
        if (State != CallState.Ringing || _incomingAnswerAwaitingAck || _incomingInvite == null || _incomingReplySender == null ||
            (_currentVoWifi == null && _currentHostIms?.IsRegistered != true))
            throw new InvalidOperationException("No incoming call in ringing state to answer.");

        var voWifi = _currentVoWifi;
        var hostIms = _currentHostIms;
        if (!_incomingQosEarlyAnswer)
        {
            ReleaseRtpSession();
            _currentVoWifi = voWifi;
            _currentHostIms = hostIms;
        }
        EnsureIncomingRtpSession();

        try { await SetRtpFromSdpAnswerAsync(_remoteOfferSdp ?? string.Empty, ct,
            startMediaSenders: false).ConfigureAwait(false); }
        catch (InvalidOperationException)
        {
            await RejectAsync(488, "Not Acceptable Here", ct).ConfigureAwait(false);
            ReleaseRtpSession();
            throw;
        }
        var localIp = _currentLocalIp ?? voWifi?.AssignedIp ?? hostIms?.Endpoint.LocalAddress.ToString() ?? "127.0.0.1";
        var sdpStr = _incomingQosEarlyAnswer ? string.Empty : BuildLocalSdpAnswer();

        var resp200 = _incomingInvite.CreateResponse(200, "OK");
        resp200.Body = sdpStr;
        resp200.SetHeader("To", _dialogFrom ?? EnsureTag(_incomingInvite.GetHeader("To") ?? string.Empty, _currentFromTag ?? string.Empty));
        CopyRecordRoute(_incomingInvite, resp200);
        resp200.SetHeader("Contact", BuildDialogContact(localIp));
        resp200.SetHeader("Allow", _incomingQosEarlyAnswer
            ? _currentHostIms is null
                ? "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE, PRACK, UPDATE"
                : "INVITE, ACK, CANCEL, BYE, OPTIONS, PRACK, UPDATE"
            : _currentHostIms is null
                ? "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE"
                : "INVITE, ACK, CANCEL, BYE, OPTIONS");
        resp200.SetHeader("Supported", _incomingQosEarlyAnswer
            ? "precondition, 100rel, timer, replaces" : "100rel, timer, replaces");
        if (TryParseSessionExpires(_incomingInvite.GetHeader("Session-Expires"), out var sessionExpires, out var refresher))
        {
            resp200.SetHeader("Session-Expires", $"{sessionExpires};refresher={refresher}");
            resp200.SetHeader("Require", "timer");
        }
        if (sdpStr.Length != 0)
        {
            resp200.SetHeader("Content-Type", "application/sdp");
            resp200.SetHeader("Content-Length", Encoding.UTF8.GetByteCount(sdpStr).ToString());
        }

        _incomingAnswerAwaitingAck = true;
        try { await _incomingReplySender(resp200).ConfigureAwait(false); }
        catch
        {
            _incomingAnswerAwaitingAck = false;
            throw;
        }
        _ = ExpireUnacknowledgedIncomingAnswerAsync(_currentCallId!);
        return ActiveCall!;
    }

    /// <summary>Only the matching ACK proves that an incoming 2xx dialog was established.</summary>
    public void HandleIncomingAck(SipMessage ack)
    {
        if (!_incomingAnswerAwaitingAck || State != CallState.Ringing ||
            !string.Equals(ack.Method, "ACK", StringComparison.OrdinalIgnoreCase) ||
            !MatchesCurrentDialog(ack) || _incomingInvite == null ||
            ParseCseqNumber(ack) != ParseCseqNumber(_incomingInvite))
            return;

        _incomingAnswerAwaitingAck = false;
        if (_rtp is not null) _rtp.MediaEnabled = true;
        _rtp?.StartSenders();
        var codec = _rtp?.Codec ?? ExtractCodecFromSdp(_remoteOfferSdp ?? string.Empty);
        try { AudioStreamStateChanged?.Invoke(this,
            new AudioStreamStateChangedEventArgs(true, true, 8000, codec)); }
        catch { }
        State = CallState.Active;
        if (ActiveCall != null)
            ActiveCall = ActiveCall with { State = CallState.Active, ConnectedAt = DateTime.UtcNow, Codec = codec };
        NotifyCallStateChanged(CallState.Ringing, CallState.Active, codec);
        if (ActiveCall != null)
        {
            NotifyCallConnected(ActiveCall);
            EventBus?.Publish("call.connected", "ImsCallManager", ActiveCall);
        }
        EventBus?.Publish(EventTopics.CallState, "ImsCallManager", "ACTIVE");
        StartSessionTimer(_incomingInvite.GetHeader("Session-Expires"), localIsUac: false);
    }

    private async Task ExpireUnacknowledgedIncomingAnswerAsync(string callId)
    {
        await Task.Delay(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
        if (!_incomingAnswerAwaitingAck || State != CallState.Ringing ||
            !string.Equals(_currentCallId, callId, StringComparison.OrdinalIgnoreCase))
            return;
        EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager", "Incoming 200 OK was not acknowledged; clearing unestablished call.");
        await HangupAsync(sendSignaling: false).ConfigureAwait(false);
    }

    private async Task ExpirePendingIncomingQosAsync(string callId)
    {
        await Task.Delay(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
        if (!_incomingQosEarlyAnswer || State != CallState.Incoming ||
            !string.Equals(_currentCallId, callId, StringComparison.OrdinalIgnoreCase))
            return;
        EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager",
            "Mandatory QoS precondition was not confirmed; ending the early IMS dialog.");
        try
        {
            if (_incomingInvite is { } invite && _incomingReplySender is { } reply)
            {
                var failure = invite.CreateResponse(580, "Precondition Failure");
                failure.SetHeader("To", _dialogFrom!);
                await reply(failure).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            EventBus?.Publish(EventTopics.SystemError, "ImsCallManager",
                $"QoS timeout response could not be sent: {ex.Message}");
        }
        await HangupAsync(sendSignaling: false).ConfigureAwait(false);
    }

    public async Task HandleIncomingPrackAsync(SipMessage prack, Func<SipMessage, Task> replySender)
    {
        if (_incomingReliableOutstandingRseq == 0 ||
            State is not (CallState.Incoming or CallState.Ringing or CallState.Active) ||
            _incomingInvite is null || !MatchesCurrentDialog(prack))
        {
            await replySender(prack.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
            return;
        }
        var rack = Regex.Match(prack.GetHeader("RAck") ?? string.Empty,
            @"^\s*(\d+)\s+(\d+)\s+INVITE\s*$", RegexOptions.IgnoreCase);
        if (!rack.Success || !int.TryParse(rack.Groups[1].Value, out var rseq) ||
            rseq != _incomingReliableOutstandingRseq || !long.TryParse(rack.Groups[2].Value, out var inviteCseq) ||
            inviteCseq != ParseCseqNumber(_incomingInvite) ||
            ParseCseqNumber(prack) <= inviteCseq)
        {
            await replySender(prack.CreateResponse(481, "RAck Mismatch")).ConfigureAwait(false);
            return;
        }
        if (!_incomingQosEarlyAnswer || rseq != IncomingReliableRseq)
        {
            if (!string.IsNullOrWhiteSpace(prack.Body))
            {
                await replySender(prack.CreateResponse(488, "Not Acceptable Here")).ConfigureAwait(false);
                return;
            }
            TryAdvanceRemoteDialogCseq(prack);
            await replySender(prack.CreateResponse(200, "OK")).ConfigureAwait(false);
            _incomingReliableOutstandingRseq = 0;
            return;
        }
        var response = prack.CreateResponse(200, "OK");
        if (!string.IsNullOrWhiteSpace(prack.Body))
        {
            try
            {
                await SetRtpFromSdpAnswerAsync(prack.Body, CancellationToken.None,
                    startMediaSenders: false).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await replySender(prack.CreateResponse(488, "Not Acceptable Here")).ConfigureAwait(false);
                return;
            }
            var sdp = BuildLocalSdpAnswer(GetAnswerDirection(prack.Body)) +
                BuildQosAnswer(prack.Body, requestConfirmation: false);
            response.Body = sdp;
            response.SetHeader("Content-Type", "application/sdp");
            response.SetHeader("Content-Length", Encoding.UTF8.GetByteCount(sdp).ToString());
            _remoteOfferSdp = prack.Body;
            _incomingQosSatisfied = IsOriginatorQosReady(prack.Body);
        }
        // A valid RAck identifies the 183 even if a later UPDATE was delivered
        // first over UDP. Do not reject that PRACK solely for older CSeq.
        TryAdvanceRemoteDialogCseq(prack);
        await replySender(response).ConfigureAwait(false);
        _incomingReliableOutstandingRseq = 0;
        _incomingPrackReceived = true;
        EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager",
            $"Incoming IMS PRACK accepted; originator-QoS-ready={_incomingQosSatisfied}.");
        if (_incomingQosSatisfied)
            await BeginIncomingRingingAsync().ConfigureAwait(false);
    }

    /// <summary>Handles an in-dialog UPDATE without mistaking it for a second call.</summary>
    public async Task HandleIncomingUpdateAsync(SipMessage update, Func<SipMessage, Task> replySender)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(replySender);
        if (ActiveCall?.IsOutgoing == true &&
            Volatile.Read(ref _outgoingInviteInProgress) != 0 &&
            State is CallState.Dialing or CallState.Ringing)
        {
            if (!MatchesCurrentDialog(update))
            {
                await replySender(update.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
                return;
            }
            if (!TryAdvanceRemoteDialogCseq(update))
            {
                await replySender(update.CreateResponse(500, "CSeq Out of Order")).ConfigureAwait(false);
                return;
            }
            // An UPDATE offer cannot overtake the unanswered INVITE offer.
            if (!string.IsNullOrWhiteSpace(update.Body) &&
                string.IsNullOrWhiteSpace(_outgoingEarlyAnswerSdp))
            {
                await replySender(update.CreateResponse(491, "Request Pending")).ConfigureAwait(false);
                return;
            }
            var earlyResponse = update.CreateResponse(200, "OK");
            if (!string.IsNullOrWhiteSpace(update.Body))
            {
                try
                {
                    await SetRtpFromSdpAnswerAsync(update.Body, CancellationToken.None,
                        new HashSet<byte> { 102, 8, 0 }, startMediaSenders: false).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    await replySender(update.CreateResponse(488, "Not Acceptable Here")).ConfigureAwait(false);
                    return;
                }
                var answer = BuildLocalSdpAnswer(GetAnswerDirection(update.Body)) +
                    BuildQosAnswer(update.Body, requestConfirmation: false);
                earlyResponse.Body = answer;
                earlyResponse.SetHeader("Content-Type", "application/sdp");
                earlyResponse.SetHeader("Content-Length", Encoding.UTF8.GetByteCount(answer).ToString());
            }
            earlyResponse.SetHeader("Contact", BuildDialogContact(_currentLocalIp ?? "127.0.0.1"));
            await replySender(earlyResponse).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(update.Body))
                _outgoingEarlyAnswerSdp = update.Body;
            var updatedContact = Regex.Match(update.GetHeader("Contact") ?? string.Empty, @"<([^>]+)>");
            if (updatedContact.Success) _dialogTargetUri = updatedContact.Groups[1].Value.Trim();
            return;
        }
        if (_incomingQosEarlyAnswer && State == CallState.Incoming)
        {
            if (!MatchesCurrentDialog(update))
            {
                await replySender(update.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
                return;
            }
            if (!TryAdvanceRemoteDialogCseq(update))
            {
                await replySender(update.CreateResponse(500, "CSeq Out of Order")).ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(update.Body))
            {
                await replySender(update.CreateResponse(488, "Not Acceptable Here")).ConfigureAwait(false);
                return;
            }
            try
            {
                await SetRtpFromSdpAnswerAsync(update.Body, CancellationToken.None,
                    startMediaSenders: false).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await replySender(update.CreateResponse(488, "Not Acceptable Here")).ConfigureAwait(false);
                return;
            }
            var sdp = BuildLocalSdpAnswer(GetAnswerDirection(update.Body)) +
                BuildQosAnswer(update.Body, requestConfirmation: false);
            var earlyResponse = update.CreateResponse(200, "OK");
            earlyResponse.Body = sdp;
            earlyResponse.SetHeader("Contact", BuildDialogContact(_currentLocalIp ?? "127.0.0.1"));
            earlyResponse.SetHeader("Content-Type", "application/sdp");
            earlyResponse.SetHeader("Content-Length", Encoding.UTF8.GetByteCount(sdp).ToString());
            await replySender(earlyResponse).ConfigureAwait(false);
            _remoteOfferSdp = update.Body;
            _incomingQosSatisfied = IsOriginatorQosReady(update.Body);
            EventBus?.Publish(EventTopics.SystemLog, "ImsCallManager",
                $"Incoming IMS early UPDATE accepted; PRACK-received={_incomingPrackReceived}; originator-QoS-ready={_incomingQosSatisfied}.");
            if (_incomingPrackReceived && _incomingQosSatisfied)
                await BeginIncomingRingingAsync().ConfigureAwait(false);
            return;
        }
        if (State is not (CallState.Active or CallState.Held) || !MatchesCurrentDialog(update))
        {
            await replySender(update.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
            return;
        }
        if (!TryAdvanceRemoteDialogCseq(update))
        {
            var stale = update.CreateResponse(500, "CSeq Out of Order");
            stale.SetHeader("Retry-After", "0");
            await replySender(stale).ConfigureAwait(false);
            return;
        }
        await HandleInDialogOfferAsync(update, replySender, sendTrying: false).ConfigureAwait(false);
    }

    private async Task HandleInDialogOfferAsync(
        SipMessage request,
        Func<SipMessage, Task> replySender,
        bool sendTrying)
    {
        if (sendTrying)
            await replySender(request.CreateResponse(100, "Trying")).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            try { await SetRtpFromSdpAnswerAsync(request.Body, CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException)
            {
                await replySender(request.CreateResponse(488, "Not Acceptable Here")).ConfigureAwait(false);
                return;
            }
        }

        var response = request.CreateResponse(200, "OK");
        if (!string.IsNullOrWhiteSpace(request.Body) && _rtp != null)
        {
            var answer = BuildLocalSdpAnswer(GetAnswerDirection(request.Body));
            response.Body = answer;
            response.SetHeader("Content-Type", "application/sdp");
            response.SetHeader("Content-Length", Encoding.UTF8.GetByteCount(answer).ToString());
        }
        response.SetHeader("Contact", BuildDialogContact(_currentLocalIp ?? "127.0.0.1"));
        response.SetHeader("Allow", _currentHostIms is null
            ? "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE, PRACK, UPDATE, INFO, NOTIFY"
            : "INVITE, ACK, CANCEL, BYE, OPTIONS, PRACK, UPDATE, INFO, NOTIFY");
        response.SetHeader("Supported", "100rel, timer, replaces");
        if (TryParseSessionExpires(request.GetHeader("Session-Expires"), out var sessionExpires, out var refresher))
        {
            response.SetHeader("Session-Expires", $"{sessionExpires};refresher={refresher}");
            response.SetHeader("Require", "timer");
        }
        await replySender(response).ConfigureAwait(false);
        ApplyRemoteMediaDirection(request.Body);
        StartSessionTimer(request.GetHeader("Session-Expires"), localIsUac: false);
    }

    private string BuildDialogContact(string localIp)
    {
        var fallback = $"<sip:{ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};transport=udp>;audio";
        var registered = _currentHostIms?.CurrentResult?.ContactUri;
        if (string.IsNullOrWhiteSpace(registered) ||
            !string.Equals(ExtractRegisteredContactAddress(registered),
                $"{ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort}",
                StringComparison.OrdinalIgnoreCase))
            return fallback;

        // Keep the registrar-accepted UE identity, instance and MMTel feature
        // tags on the terminating dialog. The registration lifetime itself is
        // not a dialog Contact parameter.
        return Regex.Replace(registered, @";\s*expires\s*=\s*\d+(?=\s*(?:;|$))", "",
            RegexOptions.IgnoreCase);
    }

    private string BuildLocalSdpAnswer(string direction = "sendrecv")
    {
        var localIp = _currentLocalIp ?? _currentVoWifi?.AssignedIp ?? "127.0.0.1";
        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var family = SdpAddressFamily(localIp);
        var rtp = _rtp;
        var port = rtp?.LocalPort ?? 0;
        var rtcpPort = rtp?.LocalRtcpPort ?? port + 1;
        var payloadType = rtp?.PayloadType ?? 8;
        var codec = rtp?.Codec ?? "PCMA";
        var clockRate = codec == "AMR-WB" ? 16000 : 8000;
        var amrModeSet = rtp?.AmrModeSet is { } modes ? $"; mode-set={modes}" : string.Empty;
        var codecParameters = codec.StartsWith("AMR", StringComparison.Ordinal)
            ? $"a=fmtp:{payloadType} mode-change-capability=2; max-red=0; octet-align={(rtp?.AmrOctetAligned == true ? 1 : 0)}{amrModeSet}\r\n"
            : string.Empty;
        return $"v=0\r\n" +
               $"o=- {sessionId} {sessionId} IN {family} {localIp}\r\n" +
               "s=VoCat\r\n" +
               $"c=IN {family} {localIp}\r\n" +
               "t=0 0\r\n" +
               $"m=audio {port} RTP/AVP {payloadType}\r\n" +
               $"a=rtcp:{rtcpPort}\r\n" +
               $"a=rtpmap:{payloadType} {codec}/{clockRate}/1\r\n" +
               codecParameters +
               $"a=ptime:20\r\na=maxptime:240\r\na={direction}\r\n";
    }

    /// <summary>
    /// Rejects an incoming ringing call with 603 Decline or 486 Busy Here.
    /// </summary>
    public async Task<CallInfo?> RejectAsync(int statusCode = 603, string reason = "Decline", CancellationToken ct = default)
    {
        if (State is not (CallState.Incoming or CallState.Ringing) || _incomingAnswerAwaitingAck || _incomingInvite == null)
            return await HangupAsync().ConfigureAwait(false);

        var resp = _incomingInvite.CreateResponse(statusCode, reason);
        resp.SetHeader("To", EnsureTag(_incomingInvite.GetHeader("To") ?? string.Empty,
            _currentFromTag ?? string.Empty));

        if (_incomingReplySender != null)
        {
            try { await _incomingReplySender(resp).ConfigureAwait(false); } catch { }
        }

        CancelSessionTimer();
        var old = State;
        State = CallState.Ended;
        var ended = ActiveCall != null ? ActiveCall with { State = CallState.Ended, EndedAt = DateTime.UtcNow } : null;
        ActiveCall = null;
        _incomingInvite = null;
        _incomingReplySender = null;
        _incomingAnswerAwaitingAck = false;
        _incomingQosEarlyAnswer = false;
        _incomingReliableOutstandingRseq = 0;
        State = CallState.Idle;
        NotifyCallStateChanged(old, CallState.Ended);
        NotifyCallEnded(ended, reason);
        ReleaseRtpSession();
        EventBus?.Publish("call.ended", "ImsCallManager", ended);
        return ended;
    }

    /// <summary>
    /// Handles incoming CANCEL request from remote party before call was answered.
    /// </summary>
    public async Task HandleIncomingCancelAsync(SipMessage cancel, Func<SipMessage, Task> replySender)
    {
        var invite = _incomingInvite;
        var localTag = _currentFromTag;
        if (State is not (CallState.Incoming or CallState.Ringing) || invite == null ||
            !MatchesIncomingCancelTransaction(invite, cancel))
        {
            await replySender(cancel.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
            return;
        }
        if (_incomingAnswerAwaitingAck)
        {
            // The INVITE already has a final response; CANCEL cannot undo it.
            await replySender(cancel.CreateResponse(200, "OK")).ConfigureAwait(false);
            return;
        }

        // 1. Reply 200 OK to CANCEL
        var cancel200 = cancel.CreateResponse(200, "OK");
        var resp487 = invite.CreateResponse(487, "Request Terminated");
        resp487.SetHeader("To", EnsureTag(invite.GetHeader("To") ?? string.Empty, localTag ?? string.Empty));
        await replySender(cancel200).ConfigureAwait(false);

        // A reply callback can finish this lifetime, accept a different request,
        // or send the INVITE's final 200 while this await is suspended. Wire
        // identity alone is not ownership of that new call/session.
        bool StillOwnsPendingInvite() => ReferenceEquals(_incomingInvite, invite) &&
            string.Equals(_currentFromTag, localTag, StringComparison.Ordinal) &&
            State is CallState.Incoming or CallState.Ringing && !_incomingAnswerAwaitingAck;
        if (!StillOwnsPendingInvite()) return;

        // 2. Reply 487 only to the captured original INVITE, then recheck after
        // the second externally supplied callback before touching call/media.
        await replySender(resp487).ConfigureAwait(false);
        if (!StillOwnsPendingInvite()) return;

        CancelSessionTimer();
        var old = State;
        State = CallState.Ended;
        var ended = ActiveCall != null ? ActiveCall with { State = CallState.Ended, EndedAt = DateTime.UtcNow } : null;
        ActiveCall = null;
        _incomingInvite = null;
        _incomingReplySender = null;
        _incomingAnswerAwaitingAck = false;
        _incomingQosEarlyAnswer = false;
        _incomingReliableOutstandingRseq = 0;
        State = CallState.Idle;
        // Detach this call's media before observers can synchronously accept a
        // new call. Releasing current media after notifications could tear down
        // that observer-created replacement instead.
        ReleaseRtpSession();
        NotifyCallStateChanged(old, CallState.Ended);
        NotifyCallEnded(ended, "CANCEL");
        EventBus?.Publish("call.ended", "ImsCallManager", ended);
    }

    private static bool MatchesIncomingCancelTransaction(SipMessage invite, SipMessage cancel)
    {
        // RFC 3261 9.1/9.2 and 17.2.3: CANCEL names the original INVITE
        // transaction, not an early dialog. In particular To is the original
        // request's value, never the tag we added to 180/183/200 responses.
        if (!invite.IsRequest || !cancel.IsRequest || invite.Method != "INVITE" || cancel.Method != "CANCEL" ||
            invite.SipVersion != "SIP/2.0" || cancel.SipVersion != "SIP/2.0" ||
            string.IsNullOrWhiteSpace(invite.RequestUri) ||
            !string.Equals(invite.RequestUri, cancel.RequestUri, StringComparison.Ordinal) ||
            cancel.Headers.ContainsKey("Require") || cancel.Headers.ContainsKey("Proxy-Require"))
            return false;
        foreach (var name in new[] { "Call-ID", "From", "To" })
            if (!TryReadCancelSingleton(invite, name, out var original) ||
                !TryReadCancelSingleton(cancel, name, out var actual) ||
                !string.Equals(original, actual, StringComparison.Ordinal))
                return false;
        if (!TryReadCancelCseq(invite, "INVITE", out var originalCseq) ||
            !TryReadCancelCseq(cancel, "CANCEL", out var cancelCseq) || originalCseq != cancelCseq ||
            !TryReadCancelVia(invite, out var originalVia, out var originalProtocol, out var originalSentBy, out var originalBranch) ||
            !TryReadCancelVia(cancel, out var actualVia, out var actualProtocol, out var actualSentBy, out var actualBranch))
            return false;
        if (originalBranch.Length == 0 || actualBranch.Length == 0)
            // RFC 2543 compatibility uses the original top Via, in addition
            // to the complete identity already checked above. Never match two
            // missing branches while ignoring different sent-by/parameters.
            return originalBranch.Length == 0 && actualBranch.Length == 0 &&
                string.Equals(originalVia, actualVia, StringComparison.Ordinal);
        return string.Equals(originalBranch, actualBranch, StringComparison.Ordinal) &&
            string.Equals(originalProtocol, actualProtocol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(originalSentBy, actualSentBy, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadCancelSingleton(SipMessage message, string name, out string value)
    {
        value = string.Empty;
        if (!message.Headers.TryGetValue(name, out var values) || values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            return false;
        value = values[0].Trim();
        return !value.Contains('\r') && !value.Contains('\n');
    }

    private static bool TryReadCancelCseq(SipMessage message, string method, out uint sequence)
    {
        sequence = 0;
        if (!TryReadCancelSingleton(message, "CSeq", out var value)) return false;
        var parts = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[1] == method &&
            parts[0].All(c => c is >= '0' and <= '9') &&
            uint.TryParse(parts[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out sequence) && sequence < (1u << 31);
    }

    private static bool TryReadCancelVia(SipMessage message, out string topVia, out string protocol, out string sentBy, out string branch)
    {
        topVia = protocol = sentBy = branch = string.Empty;
        var via = message.GetHeader("Via");
        if (string.IsNullOrWhiteSpace(via)) return false;
        topVia = via.Split(',')[0].Trim();
        var match = Regex.Match(topVia,
            @"^(?<protocol>SIP/2\.0/[A-Z]+)[ \t]+(?<host>\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9._-]+)(?<port>:[0-9]+)?(?<parameters>(?:[ \t]*;[^\r\n]*)?)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var port = match.Groups["port"].Value;
        if (port.Length > 0 && (!ushort.TryParse(port.AsSpan(1), out var number) || number == 0)) return false;
        protocol = match.Groups["protocol"].Value;
        sentBy = match.Groups["host"].Value + port;
        var sawBranch = false;
        foreach (var parameter in match.Groups["parameters"].Value.Split(';'))
        {
            var part = parameter.Trim();
            var equal = part.IndexOf('=');
            var name = equal < 0 ? part : part[..equal].Trim();
            if (!name.Equals("branch", StringComparison.OrdinalIgnoreCase)) continue;
            if (sawBranch || equal < 0) return false;
            sawBranch = true;
            branch = part[(equal + 1)..].Trim();
            if (!Regex.IsMatch(branch, @"^[A-Za-z0-9\-_.!%*+`'~]+$", RegexOptions.CultureInvariant)) return false;
        }
        return true;
    }

    /// <summary>
    /// Handles incoming BYE request from remote party when they hang up.
    /// </summary>
    public async Task HandleIncomingByeAsync(SipMessage bye, Func<SipMessage, Task> replySender)
    {
        if (State is not (CallState.Active or CallState.Held) || !MatchesCurrentDialog(bye))
        {
            await replySender(bye.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
            return;
        }
        if (!TryAdvanceRemoteDialogCseq(bye))
        {
            var stale = bye.CreateResponse(500, "CSeq Out of Order");
            stale.SetHeader("Retry-After", "0");
            await replySender(stale).ConfigureAwait(false);
            return;
        }

        var bye200 = bye.CreateResponse(200, "OK");
        await replySender(bye200).ConfigureAwait(false);

        // Terminate call and save recording asynchronously
        if (ActiveCall?.WavRecordingPath != null)
        {
            var wavPath = ActiveCall.WavRecordingPath;
            var rtpToSave = _rtp;
            var pcmToSave = _audio.TakeRecording();
            _ = Task.Run(() => SaveRecordingAndPrune(wavPath, rtpToSave, pcmToSave));
        }

        CancelSessionTimer();
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

    public async Task<CallInfo?> HangupAsync(bool sendSignaling = true)
    {
        if (ActiveCall == null || State is CallState.Idle or CallState.Ended)
            return null;

        CancelSessionTimer();
        var outgoingInviteBeingResolved = ActiveCall.IsOutgoing &&
            Volatile.Read(ref _outgoingInviteInProgress) != 0 &&
            State is CallState.Dialing or CallState.Ringing;
        var outgoingInvitePending = outgoingInviteBeingResolved &&
            Volatile.Read(ref _outgoingInviteFinalReceived) == 0;
        if (outgoingInviteBeingResolved)
        {
            Volatile.Write(ref _outgoingInviteMaySignal, sendSignaling ? 1 : 0);
            Volatile.Write(ref _outgoingInviteCancelled, 1);
            try { Volatile.Read(ref _outgoingInviteDrain)?.CancelAfter(TimeSpan.FromSeconds(32)); }
            catch (ObjectDisposedException) { }
        }

        // If incoming and ringing, reject
        if (sendSignaling && (State is CallState.Incoming or CallState.Ringing) &&
            !ActiveCall.IsOutgoing && !_incomingAnswerAwaitingAck)
        {
            return await RejectAsync().ConfigureAwait(false);
        }

        // Cancel if outgoing and ringing, BYE if active
        if (sendSignaling && outgoingInvitePending &&
            Volatile.Read(ref _outgoingInviteProvisionalReceived) != 0 &&
            _callTransport != null)
        {
            // Network cleanup must not hold the desktop in Dialing/Ringing.
            _ = SendPendingCancelAsync(_callTransport);
        }
        else if (sendSignaling && (State is CallState.Active or CallState.Held) && _callTransport != null && _currentCallId != null)
        {
            try
            {
                var bye = BuildByeRequest();
                await _callTransport.SendAsync(bye).ConfigureAwait(false);
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
            var pcmToSave = _audio.TakeRecording();
            _ = Task.Run(() => SaveRecordingAndPrune(wavPath, rtpToSave, pcmToSave));
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
        _incomingAnswerAwaitingAck = false;
        _incomingQosEarlyAnswer = false;
        _incomingReliableOutstandingRseq = 0;
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
                isVoWifi: _currentHostIms is null,
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
        // server port, distinct from the client source port on the wire.
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
            _dialogRoutes = ParseRecordRoute(recordRoutes, reverse: true);

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
        if (!string.IsNullOrWhiteSpace(_currentAccessNetworkInfo))
            prack.SetHeader("P-Access-Network-Info", _currentAccessNetworkInfo);
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

    /// <summary>Returns negotiated media metadata without exposing SDP or audio contents.</summary>
    public RtpMediaConfiguration? GetMediaConfiguration() => _rtp?.GetMediaConfiguration();

    /// <summary>Sends one 8 kHz mono PCM frame to the active carrier RTP leg.</summary>
    public void SendExternalPcm(short[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (State == CallState.Active) _rtp?.SendAudioPcm(samples);
    }

    /// <summary>Places the active dialog on hold with an SDP re-INVITE.</summary>
    public Task<CallInfo> HoldAsync(CancellationToken ct = default) =>
        ChangeHoldStateAsync(hold: true, ct);

    /// <summary>Resumes a locally held dialog with an SDP re-INVITE.</summary>
    public Task<CallInfo> ResumeAsync(CancellationToken ct = default) =>
        ChangeHoldStateAsync(hold: false, ct);

    private async Task<CallInfo> ChangeHoldStateAsync(bool hold, CancellationToken ct)
    {
        var requiredState = hold ? CallState.Active : CallState.Held;
        if (State != requiredState || ActiveCall == null || _callTransport == null)
            throw new InvalidOperationException(hold
                ? "Only an active IMS call can be placed on hold."
                : "Only a held IMS call can be resumed.");

        var request = BuildInDialogRequest("INVITE");
        var offer = BuildLocalSdpAnswer(hold ? "sendonly" : "sendrecv");
        request.Body = offer;
        request.SetHeader("Content-Type", "application/sdp");
        request.SetHeader("Content-Length", Encoding.UTF8.GetByteCount(offer).ToString());
        request.SetHeader("Supported", "100rel, timer, replaces");
        request.SetHeader("Session-Expires", $"{_sessionExpiresSeconds};refresher={(_localSessionRefresher ? "uac" : "uas")}");
        request.SetHeader("Min-SE", MinimumSessionExpiresSeconds.ToString());

        var response = await _callTransport.SendAndReceiveFinalAsync(request, timeoutMs: 15000, ct: ct)
            .ConfigureAwait(false);
        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"IMS {(hold ? "hold" : "resume")} rejected: {response.StatusCode} {response.ReasonPhrase}");

        await SendAckAsync(_callTransport, request, response, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.Body))
            await SetRtpFromSdpAnswerAsync(response.Body, ct,
                new HashSet<byte> { _rtp?.PayloadType ?? 8 }).ConfigureAwait(false);

        var old = State;
        State = hold ? CallState.Held : CallState.Active;
        ActiveCall = ActiveCall with { State = State };
        NotifyCallStateChanged(old, State);
        EventBus?.Publish(EventTopics.CallState, "ImsCallManager", hold ? "HELD" : "ACTIVE");
        StartSessionTimer(response.GetHeader("Session-Expires"), localIsUac: true);
        return ActiveCall;
    }

    private SipMessage BuildInDialogRequest(string method)
    {
        var localIp = _currentLocalIp ?? "127.0.0.1";
        var request = new SipMessage
        {
            IsRequest = true,
            Method = method,
            RequestUri = _dialogTargetUri ?? _targetUri ?? ActiveCall?.TargetNumber ?? string.Empty,
            SipVersion = "SIP/2.0"
        };
        request.SetHeader("Via", $"SIP/2.0/UDP {ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("Max-Forwards", "70");
        request.SetHeader("From", _dialogFrom ?? string.Empty);
        request.SetHeader("To", _dialogTo ?? string.Empty);
        request.SetHeader("Call-ID", _currentCallId ?? string.Empty);
        request.SetHeader("CSeq", $"{_cseq++} {method}");
        if (_dialogRoutes is { Count: > 0 })
            request.SetHeader("Route", string.Join(", ", _dialogRoutes));
        request.SetHeader("Contact",
            $"<sip:{ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};transport=udp>;audio");
        request.SetHeader("User-Agent", "iPhone Pro/17");
        request.SetHeader("Content-Length", "0");
        return request;
    }

    private void StartSessionTimer(string? header, bool localIsUac)
    {
        if (!TryParseSessionExpires(header, out var seconds, out var refresher)) return;

        CancellationTokenSource cts;
        long generation;
        lock (_sessionTimerGate)
        {
            _sessionTimerCts?.Cancel();
            _sessionTimerCts?.Dispose();
            _sessionExpiresSeconds = seconds;
            _localSessionRefresher = refresher.Equals(localIsUac ? "uac" : "uas", StringComparison.OrdinalIgnoreCase);
            cts = new CancellationTokenSource();
            _sessionTimerCts = cts;
            generation = ++_sessionTimerGeneration;
            _sessionTimerTask = Task.Run(() => SessionTimerLoopAsync(generation, cts.Token));
        }
    }

    private void CancelSessionTimer()
    {
        lock (_sessionTimerGate)
        {
            ++_sessionTimerGeneration;
            _sessionTimerCts?.Cancel();
            _sessionTimerCts?.Dispose();
            _sessionTimerCts = null;
            _sessionTimerTask = null;
        }
    }

    private async Task SessionTimerLoopAsync(long generation, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int expires;
                bool localRefresher;
                lock (_sessionTimerGate)
                {
                    if (generation != _sessionTimerGeneration) return;
                    expires = _sessionExpiresSeconds;
                    localRefresher = _localSessionRefresher;
                }

                var delay = localRefresher
                    ? TimeSpan.FromSeconds(Math.Max(MinimumSessionExpiresSeconds / 2, expires / 2))
                    : TimeSpan.FromSeconds(expires + 5);
                await Task.Delay(delay, ct).ConfigureAwait(false);

                lock (_sessionTimerGate)
                    if (generation != _sessionTimerGeneration) return;

                if (!localRefresher || !await RefreshSessionAsync(ct).ConfigureAwait(false))
                {
                    EventBus?.Publish("call.session_timer_expired", "ImsCallManager",
                        localRefresher ? "Session refresh failed." : "Remote refresher did not refresh the dialog.");
                    await HangupAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            EventBus?.Publish(EventTopics.SystemError, "ImsCallManager", $"Session timer failed: {ex.Message}");
            if (!ct.IsCancellationRequested)
                await HangupAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> RefreshSessionAsync(CancellationToken ct)
    {
        if (_callTransport == null || State is not (CallState.Active or CallState.Held)) return false;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = BuildInDialogRequest("UPDATE");
            request.SetHeader("Supported", "timer");
            request.SetHeader("Session-Expires", $"{_sessionExpiresSeconds};refresher=uac");
            request.SetHeader("Min-SE", MinimumSessionExpiresSeconds.ToString());
            SipMessage response;
            try
            {
                response = await _callTransport.SendAndReceiveFinalAsync(request, timeoutMs: 15000, ct: ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }

            if (response.StatusCode is >= 200 and < 300)
            {
                if (TryParseSessionExpires(response.GetHeader("Session-Expires"), out var seconds, out _))
                {
                    lock (_sessionTimerGate) _sessionExpiresSeconds = seconds;
                }
                return true;
            }

            if (response.StatusCode != 422 ||
                !int.TryParse(response.GetHeader("Min-SE"), out var minimum) ||
                minimum <= _sessionExpiresSeconds)
                return false;

            lock (_sessionTimerGate)
                _sessionExpiresSeconds = Math.Max(MinimumSessionExpiresSeconds, minimum);
        }
        return false;
    }

    private static bool TryParseSessionExpires(string? header, out int seconds, out string refresher)
    {
        seconds = 0;
        refresher = "uac";
        if (string.IsNullOrWhiteSpace(header)) return false;
        var interval = Regex.Match(header, @"^\s*(\d+)");
        if (!interval.Success || !int.TryParse(interval.Groups[1].Value, out seconds)) return false;
        seconds = Math.Max(MinimumSessionExpiresSeconds, seconds);
        var parameter = Regex.Match(header, @"(?:^|;)\s*refresher\s*=\s*(uac|uas)(?:\s*;|\s*$)", RegexOptions.IgnoreCase);
        if (parameter.Success) refresher = parameter.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    private void ApplyRemoteMediaDirection(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp) || ActiveCall == null) return;
        var held = Regex.IsMatch(sdp, @"(?im)^a=(sendonly|recvonly|inactive)\s*$");
        var next = held ? CallState.Held : CallState.Active;
        if (State == next) return;
        var old = State;
        State = next;
        ActiveCall = ActiveCall with { State = next };
        NotifyCallStateChanged(old, next);
        EventBus?.Publish(EventTopics.CallState, "ImsCallManager", held ? "REMOTE_HELD" : "ACTIVE");
    }

    private static string GetAnswerDirection(string? offer)
    {
        if (Regex.IsMatch(offer ?? string.Empty, @"(?im)^a=sendonly\s*$")) return "recvonly";
        if (Regex.IsMatch(offer ?? string.Empty, @"(?im)^a=recvonly\s*$")) return "sendonly";
        if (Regex.IsMatch(offer ?? string.Empty, @"(?im)^a=inactive\s*$")) return "inactive";
        return "sendrecv";
    }

    private bool MatchesCurrentDialog(SipMessage request)
    {
        if (!string.Equals(_currentCallId, request.GetHeader("Call-ID"), StringComparison.OrdinalIgnoreCase))
            return false;
        var expectedRemote = ExtractTag(_dialogTo);
        var expectedLocal = ExtractTag(_dialogFrom);
        var actualRemote = ExtractTag(request.GetHeader("From"));
        var actualLocal = ExtractTag(request.GetHeader("To"));
        return !string.IsNullOrWhiteSpace(expectedRemote) && !string.IsNullOrWhiteSpace(expectedLocal) &&
               expectedRemote.Equals(actualRemote, StringComparison.Ordinal) &&
               expectedLocal.Equals(actualLocal, StringComparison.Ordinal);
    }

    private bool TryAdvanceRemoteDialogCseq(SipMessage request)
    {
        var cseq = ParseCseqNumber(request);
        while (true)
        {
            var current = Volatile.Read(ref _remoteDialogCseq);
            if (cseq <= current) return false;
            if (Interlocked.CompareExchange(ref _remoteDialogCseq, cseq, current) == current)
                return true;
        }
    }

    private static long ParseCseqNumber(SipMessage message)
    {
        var number = message.GetHeader("CSeq")?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return long.TryParse(number, out var cseq) && cseq >= 0 ? cseq : -1;
    }

    private static string ExtractTag(string? header)
    {
        var match = Regex.Match(header ?? string.Empty, @"(?:^|;)\s*tag\s*=\s*([^;>\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string EnsureTag(string header, string tag)
    {
        var withoutTag = Regex.Replace(header, @";\s*tag\s*=\s*[^;>\s]+", string.Empty, RegexOptions.IgnoreCase).Trim();
        return $"{withoutTag};tag={tag}";
    }

    public void Dispose()
    {
        CancelSessionTimer();
        _incomingReliableOutstandingRseq = 0;
        ReleaseRtpSession();
        _audio.Dispose();
    }

    private void ReleaseRtpSession()
    {
        var rtp = _rtp;
        var hostIms = _currentHostIms;
        _rtp = null;

        if (rtp != null)
            _currentVoWifi?.UnregisterRtpSession(rtp);
        _currentVoWifi = null;
        _currentHostIms = null;
        _currentAccessNetworkInfo = null;
        rtp?.Dispose();
        if (hostIms is not null)
            _ = ClearHostImsMediaRoutesAfterCallAsync(hostIms);
    }

    private static async Task ClearHostImsMediaRoutesAfterCallAsync(HostImsRegistrationClient hostIms)
    {
        try { await hostIms.ClearMediaRoutesAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ImsCallManager] Host IMS media route cleanup failed: {ex.Message}");
        }
    }

    private void AttachExternalMediaSink(RtpSession session)
    {
        session.OnAudioDecoded = samples =>
        {
            try { RemotePcmReceived?.Invoke(samples); } catch { }
        };
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

    private static void CopyRecordRoute(SipMessage request, SipMessage response)
    {
        if (request.Headers.TryGetValue("Record-Route", out var values))
            foreach (var value in values) response.AddHeader("Record-Route", value);
    }

    private static List<string> ParseRecordRoute(List<string> rawHeaders, bool reverse)
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
        if (reverse) entries.Reverse();
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
        var original = _pendingOutgoingInvite
            ?? throw new InvalidOperationException("No pending IMS INVITE can be cancelled.");
        var cancel = new SipMessage
        {
            IsRequest  = true,
            Method     = "CANCEL",
            RequestUri = original.RequestUri,
            SipVersion = "SIP/2.0"
        };

        cancel.SetHeader("Via",            original.GetHeader("Via") ?? string.Empty);
        cancel.SetHeader("Max-Forwards",   "70");
        cancel.SetHeader("From",           original.GetHeader("From") ?? string.Empty);
        cancel.SetHeader("To",             original.GetHeader("To") ?? string.Empty);
        cancel.SetHeader("Call-ID",        original.GetHeader("Call-ID") ?? string.Empty);
        cancel.SetHeader("CSeq",           $"{original.GetHeader("CSeq")?.Split(' ')[0] ?? "1"} CANCEL");
        if (original.Headers.TryGetValue("Route", out var routes))
            foreach (var route in routes) cancel.AddHeader("Route", route);
        cancel.SetHeader("Content-Length", "0");
        return cancel;
    }

    private async Task SendPendingCancelAsync(SipTransport transport)
    {
        if (Volatile.Read(ref _outgoingInviteFinalReceived) != 0 ||
            Volatile.Read(ref _outgoingInviteProvisionalReceived) == 0 ||
            Interlocked.Exchange(ref _outgoingInviteCancelSent, 1) != 0)
            return;
        try
        {
            // CANCEL is a separate client transaction and needs retransmission
            // on UDP; a one-shot send can leave the remote phone ringing.
            _ = await transport.SendAndReceiveFinalAsync(BuildCancelRequest(), timeoutMs: 4000,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* A late final INVITE response is still handled by the caller. */ }
    }

    private static Task SendRejectedInviteAckAsync(SipTransport transport, SipMessage invite, SipMessage response)
    {
        var ack = new SipMessage { IsRequest = true, Method = "ACK", RequestUri = invite.RequestUri };
        foreach (var name in new[] { "Via", "From", "Call-ID", "Route", "Max-Forwards" })
            if (invite.GetHeader(name) is { } value) ack.SetHeader(name, value);
        ack.SetHeader("To", response.GetHeader("To") ?? invite.GetHeader("To") ?? string.Empty);
        ack.SetHeader("CSeq", $"{invite.GetHeader("CSeq")?.Split(' ')[0]} ACK");
        ack.SetHeader("Content-Length", "0");
        return transport.SendAsync(ack, CancellationToken.None);
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

    private void SaveRecordingAndPrune(string wavPath, RtpSession? rtpToSave, byte[] pcmRecording)
    {
        try
        {
            if (rtpToSave != null)
                rtpToSave.SaveAudioRecording(wavPath, pcmRecording);
            else
                _audio.SavePcmToWavFile(wavPath, pcmRecording);

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

    private async Task SetRtpFromSdpAnswerAsync(string sdpBody, CancellationToken ct,
        IReadOnlySet<byte>? locallyOfferedPayloads = null, bool startMediaSenders = true)
    {
        var mediaSession = _rtp
            ?? throw new InvalidOperationException("IMS audio media session is unavailable.");
        // SDP media-level values override the session defaults. Keep codec and
        // RTCP attributes inside the selected audio block; a video block (or
        // another audio block) must not change this call's RTP route.
        var lines = sdpBody.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var mediaStarts = Enumerable.Range(0, lines.Length)
            .Where(index => lines[index].StartsWith("m=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sessionBody = string.Join('\n', lines[..(mediaStarts.Length == 0 ? lines.Length : mediaStarts[0])]);
        var audioStart = -1;
        Match mediaMatch = Match.Empty;
        foreach (var start in mediaStarts)
        {
            var candidate = Regex.Match(lines[start], @"^m=audio\s+(\d+)\s+(\S+)\s+(.+)$",
                RegexOptions.IgnoreCase);
            if (!candidate.Success || !int.TryParse(candidate.Groups[1].Value, out var port) || port == 0)
                continue;
            audioStart = start;
            mediaMatch = candidate;
            break;
        }
        var audioEnd = mediaStarts.FirstOrDefault(start => start > audioStart, lines.Length);
        var audioBody = audioStart >= 0 ? string.Join('\n', lines[audioStart..audioEnd]) : string.Empty;
        var mediaConnection = Regex.Match(audioBody, @"(?im)^c=IN\s+(IP[46])\s+(\S+)");
        var connectionMatch = mediaConnection.Success ? mediaConnection :
            Regex.Match(sessionBody, @"(?im)^c=IN\s+(IP[46])\s+(\S+)");
        if (!connectionMatch.Success || !mediaMatch.Success ||
            (Regex.IsMatch(audioBody, @"(?im)^c=") && !mediaConnection.Success) ||
            !mediaMatch.Groups[2].Value.Equals("RTP/AVP", StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(connectionMatch.Groups[2].Value, out var remoteIp) ||
            !int.TryParse(mediaMatch.Groups[1].Value, out var remotePort) ||
            remotePort is < 1 or > ushort.MaxValue ||
            (connectionMatch.Groups[1].Value == "IP4") !=
                (remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ||
            remoteIp.Equals(IPAddress.Any) || remoteIp.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException("IMS peer supplied no usable RTP/AVP audio endpoint.");

        var rtpMappings = new Dictionary<byte, (string Codec, int ClockRate, int Channels)>();
        foreach (var line in audioBody.Split('\n').Where(line => line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase)))
        {
            var match = Regex.Match(line, @"^a=rtpmap:(\d+)[ \t]+([A-Za-z0-9\-]+)/(\d+)(?:/(\d+))?[ \t]*$", RegexOptions.IgnoreCase);
            if (!match.Success || !byte.TryParse(match.Groups[1].Value, out var payload) || payload > 127 ||
                !int.TryParse(match.Groups[3].Value, out var rate) || rate <= 0)
                throw new InvalidOperationException("IMS peer supplied an invalid RTP codec mapping.");
            var channels = 1;
            if (match.Groups[4].Success && (!int.TryParse(match.Groups[4].Value, out channels) || channels <= 0))
                throw new InvalidOperationException("IMS peer supplied an invalid RTP channel count.");
            var mapping = (match.Groups[2].Value.ToUpperInvariant(), rate, channels);
            if (rtpMappings.TryGetValue(payload, out var previous) && previous != mapping)
                throw new InvalidOperationException("IMS peer supplied conflicting RTP codec mappings.");
            rtpMappings[payload] = mapping;
        }
        var offeredPayloads = mediaMatch.Groups[3].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => byte.TryParse(value, out var parsed) ? (byte?)parsed : null)
            .OfType<byte>()
            .ToArray();
        var selectedPayload = offeredPayloads.Cast<byte?>().FirstOrDefault(candidate => candidate is { } value &&
            (locallyOfferedPayloads is null || locallyOfferedPayloads.Contains(value)) &&
            (value switch
            {
                8 => !rtpMappings.TryGetValue(8, out var mapping) ||
                    mapping is { Codec: "PCMA", ClockRate: 8000, Channels: 1 },
                0 => !rtpMappings.TryGetValue(0, out var mapping) ||
                    mapping is { Codec: "PCMU", ClockRate: 8000, Channels: 1 },
                _ => value >= 96 && rtpMappings.TryGetValue(value, out var mapping) &&
                    mapping is { Codec: "AMR" or "PCMA" or "PCMU", ClockRate: 8000, Channels: 1 }
            }));
        if (selectedPayload is not { } pt)
            throw new InvalidOperationException("IMS peer offered no supported two-way audio codec.");
        var codec = rtpMappings.TryGetValue(pt, out var selectedMapping)
            ? selectedMapping.Codec : pt == 0 ? "PCMU" : "PCMA";
        var fmtp = Regex.Match(audioBody, $@"(?im)^a=fmtp:{pt}\s+([^\r\n]+)").Groups[1].Value;
        var octetAligned = Regex.IsMatch(fmtp, @"(?:^|;)\s*octet-align\s*=\s*1(?:;|$)", RegexOptions.IgnoreCase);
        string? amrModeSet = null;
        if (codec == "AMR")
        {
            foreach (var parameter in fmtp.Split(';', StringSplitOptions.TrimEntries))
            {
                if (!Regex.IsMatch(parameter, @"^mode-set\b", RegexOptions.IgnoreCase)) continue;
                var modeSetMatch = Regex.Match(parameter, @"^mode-set\s*=\s*(.*)$", RegexOptions.IgnoreCase);
                if (!modeSetMatch.Success || amrModeSet is not null)
                    throw new InvalidOperationException("IMS peer supplied an invalid AMR mode-set.");
                amrModeSet = modeSetMatch.Groups[1].Value.Trim();
            }
        }
        var rtcpMatch = Regex.Match(audioBody,
            @"(?im)^a=rtcp:(\d+)(?:\s+IN\s+(IP[46])\s+(\S+))?\s*$");
        if (Regex.IsMatch(audioBody, @"(?im)^a=rtcp:") && !rtcpMatch.Success)
            throw new InvalidOperationException("IMS peer supplied an invalid RTCP endpoint.");
        var remoteRtcpPort = rtcpMatch.Success && int.TryParse(rtcpMatch.Groups[1].Value, out var advertisedRtcpPort)
            ? advertisedRtcpPort : remotePort + 1;
        var remoteRtcpIp = remoteIp;
        if (rtcpMatch.Success && rtcpMatch.Groups[3].Success)
        {
            if (!IPAddress.TryParse(rtcpMatch.Groups[3].Value, out remoteRtcpIp) ||
                (rtcpMatch.Groups[2].Value == "IP4") !=
                    (remoteRtcpIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                throw new InvalidOperationException("IMS peer supplied an invalid RTCP address family.");
        }
        if (_currentHostIms is { } hostIms)
        {
            await hostIms.EnsureMediaRouteAsync(remoteIp, ct).ConfigureAwait(false);
            if (!remoteRtcpIp.Equals(remoteIp))
                await hostIms.EnsureMediaRouteAsync(remoteRtcpIp, ct).ConfigureAwait(false);
        }
        Func<byte[], Task>? protectedRtpSender = null;
        Func<byte[], Task>? protectedRtcpSender = null;
        // Disconnected managers are used by local SIP-only simulations. Once
        // a real VoWiFi session has begun, never fall back to plaintext UDP.
        if (_currentVoWifi is { State: not VoWifiState.Disconnected } voWifi)
        {
            var esp = voWifi.EspTunnel
                ?? throw new InvalidOperationException("VoWiFi media ESP tunnel is unavailable.");
            var tr = voWifi.Transport
                ?? throw new InvalidOperationException("VoWiFi media transport is unavailable.");
            if (!IPAddress.TryParse(voWifi.AssignedIp, out var localIp))
                throw new InvalidOperationException("VoWiFi media address is unavailable.");
            protectedRtpSender = async rtpBytes =>
            {
                var inner = VoWifi.IpPacketUtils.BuildUdpPacket(localIp, remoteIp,
                    (ushort)mediaSession.LocalPort, (ushort)remotePort, rtpBytes);
                var nextHeader = localIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? (byte)41 : (byte)4;
                var sealedEsp = esp.Seal(inner, nextHeader);
                await tr.SendEspAsync(sealedEsp, CancellationToken.None).ConfigureAwait(false);
            };
            protectedRtcpSender = async rtcpBytes =>
            {
                var inner = VoWifi.IpPacketUtils.BuildUdpPacket(localIp, remoteRtcpIp,
                    (ushort)mediaSession.LocalRtcpPort, (ushort)remoteRtcpPort, rtcpBytes);
                var nextHeader = localIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? (byte)41 : (byte)4;
                var sealedEsp = esp.Seal(inner, nextHeader);
                await tr.SendEspAsync(sealedEsp, CancellationToken.None).ConfigureAwait(false);
            };
        }
        try
        {
            mediaSession.SetRemoteEndpoint(remoteIp, remotePort, pt, codec, octetAligned,
                remoteRtcpPort, remoteRtcpIp, amrModeSet, protectedRtpSender, protectedRtcpSender,
                startSenders: startMediaSenders);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("IMS peer supplied invalid RTP/RTCP or AMR parameters.", ex);
        }
        if (startMediaSenders)
        {
            try
            {
                AudioStreamStateChanged?.Invoke(this, new AudioStreamStateChangedEventArgs(
                    true, true, 8000, codec));
            }
            catch { }
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
