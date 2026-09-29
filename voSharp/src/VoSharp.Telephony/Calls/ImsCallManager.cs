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

public class ImsCallManager : IDisposable, ICallPcmMedia
{
    public CallState State { get; private set; } = CallState.Idle;
    public CallInfo? ActiveCall { get; private set; }
    public AsyncEventBus? EventBus { get; }
    public bool IsIncoming => ActiveCall != null && !ActiveCall.IsOutgoing;
    public bool IsHostImsCall => _currentHostIms is not null && ActiveCall is not null;
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
    public Task<CallInfo> DialAsync(
        string number,
        HostImsRegistrationClient hostIms,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hostIms);
        var result = hostIms.CurrentResult
            ?? throw new InvalidOperationException("Host IMS registration is not active.");
        var transport = hostIms.Transport;
        if (transport.BoundInterfaceIndex is not > 0 ||
            transport.LocalEndPoint?.Address.Equals(hostIms.Endpoint.LocalAddress) != true)
            throw new InvalidOperationException("Host IMS signaling is not bound to a verified cellular interface.");
        var profile = hostIms.Profile;
        var access = new ImsDialAccess(transport, hostIms.Endpoint.LocalAddress.ToString(),
            profile.HomeDomain, profile.PrivateIdentity, profile.PublicIdentity,
            result.ContactUri, result.ServiceRoute, result.PAssociatedUri,
            profile.PAccessNetworkInfo, null, hostIms, transport.BoundInterfaceIndex);
        return DialCoreAsync(number, access, ct);
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
        CancelSessionTimer();

        var startedAt = DateTime.UtcNow;

        // BUG-20 FIX: clear recorded audio buffer before starting a new call
        _audio.ClearRecording();
        ReleaseRtpSession();
        _rtp = access.InterfaceIndex is { } interfaceIndex
            ? new RtpSession(_audio, EventBus, localAddress: IPAddress.Parse(access.LocalIp),
                outgoingInterfaceIndex: interfaceIndex)
            : new RtpSession(_audio, EventBus);
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

        var contactAddress = ExtractRegisteredContactAddress(access.ContactUri)
            ?? $"{signalingHost}:{signalingEndpoint.Port}";
        string contact = $"<sip:{contactUser}@{contactAddress};transport=udp>;+g.3gpp.icsi-ref=\"urn%3Aurn-7%3A3gpp-service.ims.icsi.mmtel\";audio";
        if (!string.IsNullOrWhiteSpace(access.ContactUri))
        {
            var instMatch = Regex.Match(access.ContactUri, @"(\+sip\.instance=""[^""]+"")");
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

        if (!string.IsNullOrEmpty(access.ServiceRoute))
        {
            invite.SetHeader("Route", access.ServiceRoute);
        }

        var branch = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];
        // An IPv6 address must be bracketed or "host:port" becomes ambiguous (RFC 3261 §25.1).
        var inviteVia = $"SIP/2.0/UDP {signalingHost}:{signalingEndpoint.Port};branch={branch};rport";
        _dialogFrom = $"<{fromIdentity}>;tag={_currentFromTag}";
        _dialogTo = $"<{targetUri}>";
        _targetUri = targetUri;
        _currentLocalIp = signalingEndpoint.Address.ToString();
        _currentSignalingPort = signalingEndpoint.Port;

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
        invite.SetHeader("Allow",         "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE, PRACK, UPDATE, INFO");
        invite.SetHeader("Supported",     "100rel, timer, replaces");
        invite.SetHeader("Session-Expires", "1800;refresher=uac");
        invite.SetHeader("Min-SE",        "90");
        invite.SetHeader("Accept",        "application/sdp");
        invite.SetHeader("Content-Type",   "application/sdp");
        invite.SetHeader("Content-Length", sdpStr.Length.ToString());
        invite.SetHeader("User-Agent",    "iPhone Pro/17");
        _pendingOutgoingInvite = invite;

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
        var sipTransport = access.Transport;
        if (sipTransport != null)
        {
            _callTransport = sipTransport; // reuse VoWiFi SIP transport

            try
            {
                var finalResp = await sipTransport.SendAndReceiveFinalAsync(
                    invite,
                    onProvisional: prov =>
                    {
                        Volatile.Write(ref _outgoingInviteProvisionalReceived, 1);
                        if (Volatile.Read(ref _outgoingInviteCancelled) != 0)
                        {
                            if (Volatile.Read(ref _outgoingInviteMaySignal) != 0)
                                _ = SendPendingCancelAsync(sipTransport);
                            return;
                        }
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
                Volatile.Write(ref _outgoingInviteFinalReceived, 1);

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
                    else if (!string.IsNullOrEmpty(access.ServiceRoute))
                    {
                        _dialogRoutes = [access.ServiceRoute];
                    }

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
                    await SendAckAsync(sipTransport, invite, finalResp, ct).ConfigureAwait(false);

                    // Extract remote RTP endpoint from SDP Answer
                    try { SetRtpFromSdpAnswer(finalResp.Body ?? string.Empty); }
                    catch (InvalidOperationException)
                    {
                        try { await sipTransport.SendAsync(BuildByeRequest(), ct).ConfigureAwait(false); }
                        catch { /* The local media session must still fail closed. */ }
                        throw;
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
                CancelSessionTimer();
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
            finally
            {
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

        if (State is CallState.Active or CallState.Held or CallState.Dialing or CallState.Ringing)
        {
            if (string.Equals(_currentCallId, invite.GetHeader("Call-ID"), StringComparison.OrdinalIgnoreCase))
            {
                if (State == CallState.Ringing)
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
            var busyResp = new SipMessage
            {
                IsRequest = false,
                StatusCode = 486,
                ReasonPhrase = "Busy Here",
                SipVersion = "SIP/2.0"
            };
            busyResp.SetHeader("Via", invite.GetHeader("Via") ?? string.Empty);
            busyResp.SetHeader("From", invite.GetHeader("From") ?? string.Empty);
            busyResp.SetHeader("To", EnsureTag(invite.GetHeader("To") ?? string.Empty, Guid.NewGuid().ToString("N")[..8]));
            busyResp.SetHeader("Call-ID", invite.GetHeader("Call-ID") ?? string.Empty);
            busyResp.SetHeader("CSeq", invite.GetHeader("CSeq") ?? "1 INVITE");
            busyResp.SetHeader("Content-Length", "0");
            await replySender(busyResp).ConfigureAwait(false);
            return;
        }

        _incomingInvite = invite;
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
        _currentSignalingPort = incomingEndpoint?.Port ?? 5060;

        var fromHeader = invite.GetHeader("From") ?? "Unknown";
        var callerNumber = ExtractNumberFromUri(fromHeader);

        _dialogFrom = EnsureTag(invite.GetHeader("To") ?? string.Empty, _currentFromTag);
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
        resp180.SetHeader("To", _dialogFrom);
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
        if (State != CallState.Ringing || _incomingInvite == null || _incomingReplySender == null ||
            (_currentVoWifi == null && _currentHostIms?.IsRegistered != true))
            throw new InvalidOperationException("No incoming call in ringing state to answer.");

        _audio.ClearRecording();
        var voWifi = _currentVoWifi;
        var hostIms = _currentHostIms;
        ReleaseRtpSession();
        _rtp = hostIms?.Transport.BoundInterfaceIndex is { } interfaceIndex
            ? new RtpSession(_audio, EventBus, localAddress: hostIms.Endpoint.LocalAddress,
                outgoingInterfaceIndex: interfaceIndex)
            : new RtpSession(_audio, EventBus);
        AttachExternalMediaSink(_rtp);
        _currentVoWifi = voWifi;
        _currentHostIms = hostIms;
        voWifi?.RegisterRtpSession(_rtp);

        try { SetRtpFromSdpAnswer(_remoteOfferSdp ?? string.Empty); }
        catch (InvalidOperationException)
        {
            await RejectAsync(488, "Not Acceptable Here", ct).ConfigureAwait(false);
            ReleaseRtpSession();
            throw;
        }
        var localIp = _currentLocalIp ?? voWifi?.AssignedIp ?? hostIms?.Endpoint.LocalAddress.ToString() ?? "127.0.0.1";
        var sdpStr = BuildLocalSdpAnswer();

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
        resp200.SetHeader("To", _dialogFrom ?? EnsureTag(_incomingInvite.GetHeader("To") ?? string.Empty, _currentFromTag ?? string.Empty));
        resp200.SetHeader("Call-ID", _currentCallId ?? string.Empty);
        resp200.SetHeader("CSeq", _incomingInvite.GetHeader("CSeq") ?? "1 INVITE");
        resp200.SetHeader("Contact", $"<sip:{ImsRegisterBuilder.FormatHost(localIp)}:{_currentSignalingPort};transport=udp>;audio");
        resp200.SetHeader("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE");
        resp200.SetHeader("Supported", "100rel, timer, replaces");
        if (TryParseSessionExpires(_incomingInvite.GetHeader("Session-Expires"), out var sessionExpires, out var refresher))
        {
            resp200.SetHeader("Session-Expires", $"{sessionExpires};refresher={refresher}");
            resp200.SetHeader("Require", "timer");
        }
        resp200.SetHeader("Content-Type", "application/sdp");
        resp200.SetHeader("Content-Length", sdpStr.Length.ToString());

        await _incomingReplySender(resp200).ConfigureAwait(false);

        var codec = _rtp?.Codec ?? ExtractCodecFromSdp(_remoteOfferSdp ?? string.Empty);
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
        StartSessionTimer(_incomingInvite.GetHeader("Session-Expires"), localIsUac: false);
        return ActiveCall!;
    }

    /// <summary>Handles an in-dialog UPDATE without mistaking it for a second call.</summary>
    public async Task HandleIncomingUpdateAsync(SipMessage update, Func<SipMessage, Task> replySender)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(replySender);
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
            try { SetRtpFromSdpAnswer(request.Body); }
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
        response.SetHeader("Contact",
            $"<sip:{ImsRegisterBuilder.FormatHost(_currentLocalIp ?? "127.0.0.1")}:{_currentSignalingPort};transport=udp>;audio");
        response.SetHeader("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, MESSAGE, PRACK, UPDATE, INFO, NOTIFY");
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
        var codecParameters = codec.StartsWith("AMR", StringComparison.Ordinal)
            ? $"a=fmtp:{payloadType} mode-change-capability=2; max-red=0; octet-align={(rtp?.AmrOctetAligned == true ? 1 : 0)}\r\n"
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

        CancelSessionTimer();
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
        var matchesPendingInvite = State == CallState.Ringing && _incomingInvite != null &&
            string.Equals(_incomingInvite.GetHeader("Call-ID"), cancel.GetHeader("Call-ID"), StringComparison.OrdinalIgnoreCase) &&
            ParseCseqNumber(_incomingInvite) == ParseCseqNumber(cancel);
        if (!matchesPendingInvite)
        {
            await replySender(cancel.CreateResponse(481, "Call/Transaction Does Not Exist")).ConfigureAwait(false);
            return;
        }

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

        CancelSessionTimer();
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
        if (ActiveCall == null || State == CallState.Idle)
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
        }

        // If incoming and ringing, reject
        if (sendSignaling && State == CallState.Ringing && !ActiveCall.IsOutgoing)
        {
            return await RejectAsync().ConfigureAwait(false);
        }

        // Cancel if outgoing and ringing, BYE if active
        if (sendSignaling && outgoingInvitePending &&
            Volatile.Read(ref _outgoingInviteProvisionalReceived) != 0 &&
            _callTransport != null)
        {
            await SendPendingCancelAsync(_callTransport).ConfigureAwait(false);
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

    /// <summary>Sends one 8 kHz mono PCM frame to the active carrier RTP leg.</summary>
    public void SendExternalPcm(short[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _rtp?.SendAudioPcm(samples);
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
            SetRtpFromSdpAnswer(response.Body);

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
        _currentHostIms = null;
        _currentAccessNetworkInfo = null;
        rtp?.Dispose();
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
        try { await transport.SendAsync(BuildCancelRequest(), CancellationToken.None).ConfigureAwait(false); }
        catch { /* A late final INVITE response is still handled by the caller. */ }
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
        var mediaSession = _rtp
            ?? throw new InvalidOperationException("IMS audio media session is unavailable.");
        var connectionMatch = Regex.Match(sdpBody, @"(?im)^c=IN\s+(IP[46])\s+(\S+)");
        var mediaMatch = Regex.Match(sdpBody, @"(?im)^m=audio\s+(\d+)\s+(\S+)\s+([^\r\n]+)");
        if (!connectionMatch.Success || !mediaMatch.Success ||
            !mediaMatch.Groups[2].Value.Equals("RTP/AVP", StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(connectionMatch.Groups[2].Value, out var remoteIp) ||
            !int.TryParse(mediaMatch.Groups[1].Value, out var remotePort) ||
            remotePort is < 1 or > ushort.MaxValue ||
            (connectionMatch.Groups[1].Value == "IP4") !=
                (remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ||
            remoteIp.Equals(IPAddress.Any) || remoteIp.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException("IMS peer supplied no usable RTP/AVP audio endpoint.");

        var rtpMappings = Regex.Matches(sdpBody, @"(?im)^a=rtpmap:(\d+)\s+([A-Za-z0-9\-]+)/(\d+)")
            .Cast<Match>()
            .Where(match => byte.TryParse(match.Groups[1].Value, out _) &&
                int.TryParse(match.Groups[3].Value, out _))
            .GroupBy(match => byte.Parse(match.Groups[1].Value))
            .ToDictionary(
                group => group.Key,
                group => (Codec: group.First().Groups[2].Value.ToUpperInvariant(),
                    ClockRate: int.Parse(group.First().Groups[3].Value)));
        var offeredPayloads = mediaMatch.Groups[3].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => byte.TryParse(value, out var parsed) ? (byte?)parsed : null)
            .OfType<byte>()
            .ToArray();
        var selectedPayload = offeredPayloads.Cast<byte?>().FirstOrDefault(candidate => candidate is { } value &&
            (value switch
            {
                8 => !rtpMappings.TryGetValue(8, out var mapping) ||
                    mapping is { Codec: "PCMA", ClockRate: 8000 },
                0 => !rtpMappings.TryGetValue(0, out var mapping) ||
                    mapping is { Codec: "PCMU", ClockRate: 8000 },
                _ => rtpMappings.TryGetValue(value, out var mapping) &&
                    mapping is { Codec: "AMR", ClockRate: 8000 }
            }));
        if (selectedPayload is not { } pt)
            throw new InvalidOperationException("IMS peer offered no supported two-way audio codec.");
        var codec = rtpMappings.TryGetValue(pt, out var selectedMapping)
            ? selectedMapping.Codec : pt == 0 ? "PCMU" : "PCMA";
        var fmtp = Regex.Match(sdpBody, $@"(?im)^a=fmtp:{pt}\s+([^\r\n]+)").Groups[1].Value;
        var octetAligned = Regex.IsMatch(fmtp, @"(?:^|;)\s*octet-align\s*=\s*1(?:;|$)", RegexOptions.IgnoreCase);
        var rtcpMatch = Regex.Match(sdpBody,
            @"(?im)^a=rtcp:(\d+)(?:\s+IN\s+(IP[46])\s+(\S+))?\s*$");
        if (Regex.IsMatch(sdpBody, @"(?im)^a=rtcp:") && !rtcpMatch.Success)
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
        try
        {
            mediaSession.SetRemoteEndpoint(remoteIp, remotePort, pt, codec, octetAligned,
                remoteRtcpPort, remoteRtcpIp);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("IMS peer supplied an RTCP endpoint outside the selected bearer.", ex);
        }

        if (_currentVoWifi?.EspTunnel != null && _currentVoWifi.Transport != null && IPAddress.TryParse(_currentVoWifi.AssignedIp, out var localIp))
        {
            var esp = _currentVoWifi.EspTunnel;
            var tr = _currentVoWifi.Transport;
            mediaSession.CustomSender = async rtpBytes =>
            {
                var inner = VoWifi.IpPacketUtils.BuildUdpPacket(localIp, remoteIp,
                    (ushort)mediaSession.LocalPort, (ushort)remotePort, rtpBytes);
                var nextHeader = localIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? (byte)41 : (byte)4;
                var sealedEsp = esp.Seal(inner, nextHeader);
                await tr.SendEspAsync(sealedEsp, CancellationToken.None).ConfigureAwait(false);
            };
            mediaSession.CustomRtcpSender = async rtcpBytes =>
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
            AudioStreamStateChanged?.Invoke(this, new AudioStreamStateChangedEventArgs(
                true, true, 8000, codec));
        }
        catch { }
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
