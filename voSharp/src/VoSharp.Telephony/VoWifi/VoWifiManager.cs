using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using VoSharp.Common.Aka;
using VoSharp.Common.Events;
using VoSharp.Crypto;
using VoSharp.Ike;
using VoSharp.Ike.Transport;
using VoSharp.Modem;
using VoSharp.Sim;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;

namespace VoSharp.Telephony.VoWifi;

public enum VoWifiState
{
    Disconnected = 0,
    ResolvingEpdg = 1,
    ConnectingIkev2 = 2,
    AuthenticatingEapAka = 3,
    IpsecTunnelEstablished = 4,
    ImsRegistering = 5,
    ImsRegistered = 6,
    Failed = 7
}

public record VoWifiIpsecTunnelInfo(
    string AccessStandard,
    uint InboundSpi,
    uint OutboundSpi,
    string EncryptionAlgorithm,
    string IntegrityAlgorithm,
    string AssignedIPv4,
    string? AssignedIPv6,
    string? AssignedDns,
    string PcscfIp,
    int NatPort
);

public record VoWifiImsSessionInfo(
    string RegistrationState,
    string HomeDomain,
    string Impi,
    string Impu,
    string PcscfEndpoint,
    string ContactUri,
    int CSeq,
    int ExpiresSeconds,
    string SecurityAssociation,
    DateTime RegisteredAt,
    string? ServiceRoute = null,
    string? PAssociatedUri = null
);

public record VoWifiDiagnosticInfo(
    VoWifiState State,
    string Standard,
    string MatchedCarrier,
    string EpdgFqdn,
    string EpdgIp,
    int EpdgPort,
    IkeProposalSuite Suite,
    string DhGroup,
    string IkeInitiatorSpi,
    string IkeResponderSpi,
    string EapMethod,
    string AuthVectorStatus,
    VoWifiIpsecTunnelInfo? Tunnel,
    VoWifiImsSessionInfo? Ims,
    DateTime? ConnectedAt,
    string? Uptime,
    string? LastError,
    int SipProbesSent = 0,
    int SipProbesSuccess = 0,
    int SipProbesFailed = 0,
    long LastProbeRttMs = 0,
    DateTime? LastProbeTime = null,
    string? LastProbeResult = null
);

public class VoWifiManager : IDisposable
{
    public VoWifiState State { get; private set; } = VoWifiState.Disconnected;
    public IkeProposalSuite CurrentSuite { get; private set; } = IkeProposalSuite.Auto;
    public AsyncEventBus? EventBus { get; }
    public ModemDriver? Modem { get; set; }
    public EpdgResolutionResult? EpdgInfo { get; private set; }
    public string? AssignedIp { get; private set; }
    public DateTime? ConnectedAt { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler<VoWifiStateChangedEventArgs>? StateChanged;
    public event EventHandler<VoWifiIpsecTunnelInfo>? TunnelEstablished;
    public event EventHandler<VoWifiImsSessionInfo>? ImsRegistered;
    public event EventHandler<string>? VoWifiError;

    private string? _proxyUrl;
    public string? ProxyUrl
    {
        get => _proxyUrl;
        set
        {
            _proxyUrl = value;
            if (_lastStartOptions is { } options)
                _lastStartOptions = options with { ProxyUrl = value };
        }
    }
    public bool KeepOnlineRequested => Volatile.Read(ref _keepOnlineRequested) != 0;
    public ulong IkeInitiatorSpi { get; private set; }
    public ulong IkeResponderSpi { get; private set; }
    public VoWifiIpsecTunnelInfo? TunnelInfo { get; private set; }
    public VoWifiImsSessionInfo? ImsInfo { get; private set; }
    public EspTunnel? EspTunnel => _csharpIkeBackend?.EspTunnel;
    public VoSharp.Ike.IkeTransport? Transport => _csharpIkeBackend?.Transport;
    public Calls.ImsCallManager? Calls { get; set; }
    // SIP and media share one ESP dispatcher.  Keep the media endpoint registry
    // separate from the current-call reference so a concurrent SIP state change
    // cannot make an already negotiated RTP port disappear from the dispatcher.
    private readonly ConcurrentDictionary<int, Calls.RtpSession> _rtpSessions = new();
    private readonly object _rtpSessionLock = new();
    private Calls.RtpSession? _activeRtpSession;
    public Calls.RtpSession? ActiveRtpSession
    {
        get { lock (_rtpSessionLock) return _activeRtpSession; }
    }

    public void RegisterRtpSession(Calls.RtpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_rtpSessionLock)
        {
            if (_activeRtpSession is { } previous && !ReferenceEquals(previous, session))
                _rtpSessions.TryRemove(previous.LocalPort, out _);

            _rtpSessions[session.LocalPort] = session;
            _activeRtpSession = session;
        }
        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi", $"Registered RTP endpoint on UDP {session.LocalPort}.");
    }

    public void UnregisterRtpSession(Calls.RtpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_rtpSessionLock)
        {
            if (_rtpSessions.TryGetValue(session.LocalPort, out var registered) && ReferenceEquals(registered, session))
                _rtpSessions.TryRemove(session.LocalPort, out _);
            if (ReferenceEquals(_activeRtpSession, session))
                _activeRtpSession = null;
        }
    }

    private void ClearRtpSessions()
    {
        lock (_rtpSessionLock)
        {
            _rtpSessions.Clear();
            _activeRtpSession = null;
        }
    }

    private SipTransport? _sipTransport;
    private SipRegisterSession? _registerSession;
    private ImsIpsecTransport? _imsIpsec;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _healthCts;
    private Task? _healthTask;
    private int _healthFailures;
    private int _recoveryQueued;
    private int _keepOnlineRequested;
    private CancellationTokenSource? _recoveryCts;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryWake = new(0, 1);
    private bool _disposed;
    private VoWifiStartOptions? _lastStartOptions;
    private static readonly TimeSpan HealthProbeInterval = TimeSpan.FromSeconds(20);
    private bool _sipOptionsRejectedByNetwork;
    private int _sipProbesSent;
    private int _sipProbesSuccess;
    private int _sipProbesFailed;
    private long _lastProbeRttMs;
    private DateTime? _lastProbeTime;
    private string? _lastProbeResult;
    // Most carrier ePDGs roll the CHILD_SA at or before four hours.  The C# data
    // plane does not yet implement CREATE_CHILD_SA rekeying, so renew the whole
    // session before that hard lifetime instead of leaving a stale "registered"
    // UI state behind.
    private static readonly TimeSpan ProactiveRenewalAge = TimeSpan.FromHours(3.5);
    public bool EnableImsIpsec { get; set; } = true;
    public bool SmsCapabilityConfirmed { get; private set; }
    private Ikev2Transport? _transport;
    private CancellationTokenSource? _ctsEspDispatch;
    private Task? _espDispatchTask;
    private readonly Ipv4FragmentReassembler _innerIpv4Reassembler = new();
    private readonly Ipv6FragmentReassembler _innerIpv6Reassembler = new();
    private readonly SmsReassembler _smsReassembler = new();
    // Inner protocols that are not UDP are logged once each; the tunnel sees the same control
    // packet every few seconds and repeating it would flood the log.
    private readonly ConcurrentDictionary<byte, byte> _ignoredInnerProtocols = new();
    private int _icmpv6EchoLogged;
    private int _nextRpReference;
    private readonly string _smsFromTag = Guid.NewGuid().ToString("N")[..12];
    private sealed record IncomingSmsTransaction(DateTime Created, Lazy<Task> Work);
    private readonly ConcurrentDictionary<string, IncomingSmsTransaction> _incomingSmsTransactions = new();
    private int _sipCseq = 100;

    private sealed record VoWifiStartOptions(
        SimIdentity Sim,
        string? CustomEpdg,
        IkeProposalSuite Suite,
        string? ProxyUrl);

    // IKEv2 Configuration Attribute type codes (RFC 7296 §3.15.1)
    private const ushort CpAttrInternalIp4Address = 1;
    private const ushort CpAttrInternalIp4Dns = 3;
    private const ushort CpAttrPcscfIp4Address = 12;  // 3GPP TS 24.302 §7.2

    // ── C# full-stack IKEv2 backend ─────────────────────────────────────
    private IIkeBackend? _csharpIkeBackend;

    public VoWifiManager(AsyncEventBus? eventBus = null, ModemDriver? modem = null)
    {
        EventBus = eventBus;
        Modem = modem;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable || _disposed || Volatile.Read(ref _keepOnlineRequested) == 0)
            return;

        if (Volatile.Read(ref _recoveryQueued) != 0)
        {
            try { _recoveryWake.Release(); } catch (SemaphoreFullException) { }
            return;
        }

        if (State is VoWifiState.Disconnected or VoWifiState.Failed)
            QueueAutomaticRecovery("Network connectivity became available while VoWiFi was offline.");
    }

    /// <summary>
    /// Detaches the USIM/AT provider without touching an already established
    /// IKE, ESP or IMS session. Those data-plane objects do not require the
    /// module until a fresh AKA challenge is needed.
    /// </summary>
    public void NotifyModemDetached()
    {
        Modem = null;
        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
            "USIM module was removed; preserving the active VoWiFi tunnel for as long as liveness and registration remain valid.");
    }

    /// <summary>
    /// Rebinds a returned USIM and requests a clean IKE/IMS registration. If a
    /// recovery loop is already waiting for hardware, wake it immediately.
    /// </summary>
    public void NotifyModemReattached(ModemDriver modem, SimIdentity sim)
    {
        ArgumentNullException.ThrowIfNull(modem);
        ArgumentNullException.ThrowIfNull(sim);
        Modem = modem;
        if (_lastStartOptions is { } options)
            _lastStartOptions = options with { Sim = sim, ProxyUrl = ProxyUrl };

        if (!KeepOnlineRequested)
            return;

        if (Volatile.Read(ref _recoveryQueued) != 0)
        {
            try { _recoveryWake.Release(); } catch (SemaphoreFullException) { }
        }
        else
        {
            QueueAutomaticRecovery("USIM module was reattached; renewing IKE and IMS registration with the available card.");
        }
    }

    public SipTransport? SipTransport => _sipTransport;


    public async Task<bool> StartVoWifiAsync(
        SimIdentity sim,
        string? customEpdg = null,
        IkeProposalSuite suite = IkeProposalSuite.Auto,
        string? proxyUrl = null,
        CancellationToken ct = default) =>
        await StartVoWifiAsync(new[] { sim }, customEpdg, suite, proxyUrl, ct).ConfigureAwait(false);

    public async Task<bool> StartVoWifiAsync(
        IReadOnlyList<SimIdentity> identityCandidates,
        string? customEpdg = null,
        IkeProposalSuite suite = IkeProposalSuite.Auto,
        string? proxyUrl = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identityCandidates);
        var candidates = identityCandidates
            .Where(candidate => candidate != null)
            .GroupBy(candidate => candidate.Imsi, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException("At least one live or learned SIM identity is required.", nameof(identityCandidates));
        if (candidates.Select(candidate => candidate.Iccid).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new ArgumentException("All identity candidates must belong to the same live ICCID.", nameof(identityCandidates));

        Volatile.Write(ref _keepOnlineRequested, 1);
        var started = false;
        for (var index = 0; index < candidates.Length; index++)
        {
            var options = new VoWifiStartOptions(candidates[index], customEpdg, suite, proxyUrl ?? ProxyUrl);
            _lastStartOptions = options;
            if (candidates.Length > 1)
                EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                    $"Trying learned SIM identity candidate {index + 1}/{candidates.Length} for this ICCID.");

            started = await StartVoWifiAttemptAsync(options, ct).ConfigureAwait(false);
            if (started || !CanTryNextIdentity(LastError))
                break;
        }

        if (!started && Volatile.Read(ref _keepOnlineRequested) != 0)
            QueueAutomaticRecovery(LastError ?? "VoWiFi startup failed.");
        return started;
    }

    private async Task<bool> StartVoWifiAttemptAsync(VoWifiStartOptions options, CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await StartVoWifiCoreAsync(options, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> StartVoWifiCoreAsync(VoWifiStartOptions options, CancellationToken ct)
    {
        try
        {
            var sim = options.Sim;
            var customEpdg = options.CustomEpdg;
            var suite = options.Suite;
            var proxyUrl = options.ProxyUrl;
            _lastStartOptions = options;
            if (_sipTransport != null || _csharpIkeBackend != null)
                await StopVoWifiCoreAsync(ct).ConfigureAwait(false);
            _sessionCts = new CancellationTokenSource();
            _sipOptionsRejectedByNetwork = false;
            SetState(VoWifiState.ResolvingEpdg);
            LastError = null;

            var effectiveProxy = proxyUrl ?? ProxyUrl;
            if (!string.IsNullOrWhiteSpace(effectiveProxy) &&
                !effectiveProxy.Equals("direct", StringComparison.OrdinalIgnoreCase))
            {
                using var parsedProxy = Socks5Client.TryParse(effectiveProxy)
                    ?? throw new ArgumentException(
                        "VoWiFi requires a valid socks5:// or socks5h:// proxy; HTTP proxies cannot relay IKEv2/ESP UDP.",
                        nameof(proxyUrl));
                EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                    $"Using SOCKS5 UDP proxy {parsedProxy.ProxyHost}:{parsedProxy.ProxyPort} for ePDG and ESP.");
            }
            else
            {
                EventBus?.Publish(EventTopics.SystemLog, "VoWiFi", "Using direct UDP route for ePDG and ESP.");
            }

            // ── 1. Resolve 3GPP ePDG FQDN & DNS ─────────────────────────────
            EpdgInfo = await EpdgResolver.ResolveAsync(sim, customEpdg, ct)
                                         .ConfigureAwait(false);
            // TS 24.011 RP-DATA must use the SMSC provisioned by this SIM.  Do
            // not substitute a number from another carrier when the modem has
            // no value: VoCat likewise uses SIM-provided SMSC/PSI routing.
            if (Modem != null)
            {
                var smsc = await Modem.GetSmsCenterAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(smsc))
                    EpdgInfo = EpdgInfo with { SmsCenter = smsc };
            }
            EventBus?.Publish("vowifi.epdg.resolved", "VoWifiManager", EpdgInfo);
            EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                $"Resolved ePDG: carrier={EpdgInfo.MatchedCarrier ?? "unmatched"}, " +
                $"fqdn={EpdgInfo.Fqdn}, apn={EpdgInfo.Apn ?? "ims"}, " +
                $"addresses=[{string.Join(", ", EpdgInfo.IpAddresses.Select(address => address.ToString()))}].");

            var chosenSuite = suite == IkeProposalSuite.Auto ? EpdgInfo.PreferredSuite : suite;

            var targetIps = EpdgInfo.IpAddresses.Distinct().ToArray();
            if (targetIps.Length == 0)
                throw new InvalidOperationException(
                    $"ePDG DNS resolution returned no addresses for '{EpdgInfo.Fqdn}'. " +
                    "Check network connectivity or provide a custom ePDG IP.");

            // ── 2. Pick AKA Provider ─────────────────────────────────────────
            if (Modem == null || !Modem.IsOpen)
                throw new InvalidOperationException(
                    "USIM modem is unavailable. The existing VoWiFi intent is preserved and registration will retry when the module returns.");
            IAkaProvider akaProvider = new Ec25AkaProvider(Modem.Session);
            if (!await akaProvider.CheckReadyAsync(sim.Iccid, ct).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "The live USIM ICCID does not match the identity selected for VoWiFi. Authentication was blocked before EAP-AKA; refresh the SIM identity after switching profiles.");
            var deviceImei = await ResolveImeiAsync(ct).ConfigureAwait(false);

            // ── 3. IKEv2 / EAP-AKA Handshake & Child SA (C# Full Stack) ─────
            string? fallbackPcscf = null;
            var apn = EpdgInfo.Apn ?? "ims";
            SetState(VoWifiState.ConnectingIkev2);

            // A carrier database entry is a useful first choice but it cannot describe every
            // MVNO or a carrier migration. Retry only a rejected IKE_SA_INIT proposal; once
            // EAP-AKA or the CHILD_SA has started, changing algorithms would mask a real
            // authentication/provisioning fault and could consume additional AKA vectors.
            IkeBackendResult? ikeResult = null;
            var attemptedSuites = new List<string>();
            var negotiationErrors = new List<string>();
            var stopEndpointFailover = false;
            var suiteAttempts = GetIkeSuiteAttempts(suite, chosenSuite);
            foreach (var targetIp in targetIps)
            {
                for (var suiteIndex = 0; suiteIndex < suiteAttempts.Count; suiteIndex++)
                {
                    var attemptSuite = suiteAttempts[suiteIndex];
                    ct.ThrowIfCancellationRequested();
                    attemptedSuites.Add($"{targetIp}/{attemptSuite}");
                    CurrentSuite = attemptSuite;
                    EventBus?.Publish("vowifi.ike.attempt", "VoWifiManager",
                        new { Endpoint = targetIp.ToString(), Suite = attemptSuite.ToString(), Attempt = attemptedSuites.Count });

                    _csharpIkeBackend?.Dispose();
                    _csharpIkeBackend = new CSharpIkeBackend();
                    ikeResult = await _csharpIkeBackend.StartTunnelAsync(
                        sim,
                        akaProvider,
                        targetIp.ToString(),
                        apn,
                        fallbackPcscf,
                        attemptSuite,
                        effectiveProxy,
                        deviceImei,
                        ct).ConfigureAwait(false);

                    if (ikeResult.Success && !string.IsNullOrEmpty(ikeResult.AssignedIp) && !string.IsNullOrEmpty(ikeResult.PcscfIp))
                        break;

                    var error = ikeResult.ErrorMessage ?? "Unknown IKE negotiation error";
                    negotiationErrors.Add($"{targetIp}/{attemptSuite}: {error}");
                    _csharpIkeBackend.Dispose();
                    _csharpIkeBackend = null;

                    if (IsInitialIkeTimeout(error))
                    {
                        // A silent IKE_SA_INIT may mean either an unsupported proposal or a
                        // dead anycast/backend node. In Auto mode exhaust this endpoint's safe
                        // pre-EAP proposal ladder, then move to the next DNS address. An explicit
                        // suite has no useful same-endpoint retry and can fail over immediately.
                        if (suiteIndex + 1 < suiteAttempts.Count)
                        {
                            EventBus?.Publish("vowifi.ike.retry", "VoWifiManager",
                                new { Endpoint = targetIp.ToString(), FailedSuite = attemptSuite.ToString(), Reason = error });
                            continue;
                        }

                        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                            $"ePDG {targetIp} did not answer IKE_SA_INIT; trying the next resolved address.");
                        break;
                    }

                    if (IsProposalNegotiationFailure(error))
                    {
                        EventBus?.Publish("vowifi.ike.retry", "VoWifiManager",
                            new { Endpoint = targetIp.ToString(), FailedSuite = attemptSuite.ToString(), Reason = error });
                        continue;
                    }

                    // The peer rejected the permanent identity before it supplied RAND/AUTN.
                    // No USIM vector has been consumed. Different DNS nodes can be backed by
                    // independent AAA pools, so it is safe to try the next resolved address.
                    if (IsPreAkaRejection(error))
                    {
                        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                            $"ePDG {targetIp} rejected EAP before the AKA challenge; trying the next resolved address.");
                        break;
                    }

                    stopEndpointFailover = true;
                    break;
                }

                if (ikeResult is { Success: true, AssignedIp: not null, PcscfIp: not null } || stopEndpointFailover)
                    break;
            }

            if (ikeResult == null || !ikeResult.Success || string.IsNullOrEmpty(ikeResult.AssignedIp) || string.IsNullOrEmpty(ikeResult.PcscfIp))
            {
                throw new InvalidOperationException(
                    $"IKEv2 session failed after {string.Join(" -> ", attemptedSuites)}: " +
                    string.Join(" | ", negotiationErrors));
            }

            IkeInitiatorSpi = ikeResult.InboundSpi;
            IkeResponderSpi = ikeResult.OutboundSpi;

            AssignedIp = ikeResult.AssignedIp;
            var assignedAddress = IPAddress.Parse(ikeResult.AssignedIp);
            TunnelInfo = new VoWifiIpsecTunnelInfo(
                AccessStandard: "3GPP TS 24.302 (Untrusted Non-3GPP / S2b Interface)",
                InboundSpi: ikeResult.InboundSpi,
                OutboundSpi: ikeResult.OutboundSpi,
                EncryptionAlgorithm: $"AES-CBC-{ikeResult.EspSuite?.EncryptionBits ?? 128}",
                IntegrityAlgorithm: ikeResult.EspSuite?.IntegrityId == IkeIntegrityId.HmacSha1_96
                    ? "HMAC-SHA1-96"
                    : "HMAC-SHA256-128",
                AssignedIPv4: assignedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? ikeResult.AssignedIp : string.Empty,
                AssignedIPv6: assignedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? ikeResult.AssignedIp : null,
                AssignedDns: ikeResult.DnsIps.FirstOrDefault() ?? string.Empty,
                PcscfIp: ikeResult.PcscfIp,
                NatPort: Ikev2Protocol.NattPort
            );
            try { TunnelEstablished?.Invoke(this, TunnelInfo); } catch { }
            EventBus?.Publish("vowifi.tunnel.established", "VoWifiManager", TunnelInfo);

            SetState(VoWifiState.IpsecTunnelEstablished);

            // ── 4. IMS SIP REGISTER over IPsec tunnel ─────────────────────────
            SetState(VoWifiState.ImsRegistering);

            var imsProfile = new ImsProfile(
                PrivateIdentity: EpdgInfo.Impi,
                PublicIdentity: EpdgInfo.Impu,
                HomeDomain: EpdgInfo.ImsDomain,
                Imei: deviceImei,
                LocalIp: ikeResult.AssignedIp,
                LocalPort: 5060,
                PAccessNetworkInfo: ImsRegisterBuilder.BuildAccessNetworkInfo(EpdgInfo.Impi)
            );

            _sipTransport?.Dispose();
            _sipTransport = new SipTransport(timeoutMs: 15000);
            _sipTransport.IncomingRequestReceived += OnIncomingSipRequestReceived;
            _sipTransport.ReceiveError += (_, error) => EventBus?.Publish(EventTopics.SystemError, "VoWiFi", $"SIP receive: {error}");

            if (!IPAddress.TryParse(ikeResult.PcscfIp, out var pcscfAddr))
                throw new InvalidOperationException($"P-CSCF IP '{ikeResult.PcscfIp}' is invalid.");
            if (assignedAddress.AddressFamily != pcscfAddr.AddressFamily)
                throw new InvalidOperationException(
                    $"The ePDG assigned {assignedAddress} but returned P-CSCF {pcscfAddr}. IMS signalling cannot mix " +
                    "IPv4 and IPv6; the address family requested from this ePDG does not match its IMS PDN.");

            if (ikeResult.EspTunnel != null && ikeResult.Transport != null)
            {
                var esp = ikeResult.EspTunnel;
                var tr = ikeResult.Transport;
                var assignedIp = IPAddress.Parse(ikeResult.AssignedIp);

                if (EnableImsIpsec) _imsIpsec = new ImsIpsecTransport(assignedIp, pcscfAddr);
                _innerIpv4Reassembler.Clear();
                _innerIpv6Reassembler.Clear();
                EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                    $"Tunnel data plane ready: assigned={assignedIp}, P-CSCF={pcscfAddr}, family={assignedIp.AddressFamily}.");
                var sipChannel = Channel.CreateUnbounded<SipDatagram>();
                _ctsEspDispatch?.Cancel();
                _ctsEspDispatch = new CancellationTokenSource();
                var dispatchToken = _ctsEspDispatch.Token;

                _espDispatchTask = Task.Run(async () =>
                {
                    while (!dispatchToken.IsCancellationRequested)
                    {
                        try
                        {
                            var espBytes = await tr.EspPackets.ReadAsync(dispatchToken).ConfigureAwait(false);
                            var inner = esp.Open(espBytes, out var innerNextHeader);
                            if (inner == null)
                            {
                                Console.WriteLine($"[VoWifiManager RX ESP] Decrypt failed! len={espBytes.Length}");
                                continue;
                            }
                            if (innerNextHeader is not (4 or 41))
                                throw new FormatException($"Unsupported ESP inner protocol {innerNextHeader}; expected IPv4 or IPv6.");
                            if (innerNextHeader == 4)
                            {
                                bool wasFragmented = inner.Length >= 8 &&
                                    (BinaryPrimitives.ReadUInt16BigEndian(inner.AsSpan(6, 2)) & 0x3fff) != 0;
                                inner = _innerIpv4Reassembler.Process(inner);
                                if (inner == null)
                                    continue;
                                if (wasFragmented)
                                    EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                                        $"Reassembled fragmented inner IPv4 packet ({inner.Length} bytes).");
                            }
                            else
                            {
                                var reassembled = _innerIpv6Reassembler.Process(inner);
                                if (reassembled == null)
                                    continue;
                                if (!ReferenceEquals(reassembled, inner))
                                    EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                                        $"Reassembled fragmented inner IPv6 packet ({reassembled.Length} bytes).");
                                inner = reassembled;
                            }
                            if (innerNextHeader == 41 &&
                                IpPacketUtils.TryGetInnerTransport(inner, out var ipv6Protocol) && ipv6Protocol == 58)
                            {
                                // A kernel tunnel answers ICMPv6 in the host IPv6 stack. This one
                                // is user space, so it must answer itself: the ePDG pings the
                                // assigned address and treats silence as an unreachable UE.
                                if (IpPacketUtils.TryBuildIcmpv6EchoReply(inner, out var echoReply))
                                {
                                    if (Interlocked.Exchange(ref _icmpv6EchoLogged, 1) == 0)
                                        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                                            "Answering ePDG ICMPv6 echo requests inside the IPsec tunnel.");
                                    var outerReply = esp.Seal(echoReply, nextHeader: 41);
                                    await tr.SendEspAsync(outerReply, dispatchToken).ConfigureAwait(false);
                                }
                                // ICMPv6 is control traffic, not a SIP/RTP datagram. Other
                                // types (errors/ND/status) are consumed without UDP parsing.
                                continue;
                            }
                            if (_imsIpsec != null) inner = _imsIpsec.Unprotect(inner);
                            if (!IpPacketUtils.IsUdpInnerPacket(inner))
                            {
                                // ICMPv6/ND/MLD, TCP, ESP and other non-UDP traffic is operator
                                // control traffic. A full OS stack consumes it silently; raising
                                // an error for every one drowns the real faults in the log.
                                IpPacketUtils.TryGetInnerTransport(inner, out var ignoredProtocol);
                                NoteIgnoredInnerProtocol(ignoredProtocol);
                                continue;
                            }
                            var packet = IpPacketUtils.ParseUdpPacket(inner);
                            if (!packet.LocalEndPoint.Address.Equals(assignedIp))
                                throw new FormatException("Inner packet destination does not match assigned IP.");
                            if (_imsIpsec?.AcceptsPort(packet.LocalEndPoint.Port) ?? packet.LocalEndPoint.Port == imsProfile.LocalPort)
                            {
                                if (!packet.RemoteEndPoint.Address.Equals(pcscfAddr))
                                    throw new FormatException("SIP source is not the registered P-CSCF.");
                                sipChannel.Writer.TryWrite(packet);
                            }
                            else if (_rtpSessions.TryGetValue(packet.LocalEndPoint.Port, out var rtpSession))
                                rtpSession.ProcessRtpPacket(packet.Payload);
                            else
                            {
                                var ports = _rtpSessions.IsEmpty ? "none" : string.Join(",", _rtpSessions.Keys.Order());
                                EventBus?.Publish(EventTopics.SystemLog, "VoWiFi", $"Unrouted inner UDP {packet.RemoteEndPoint} -> {packet.LocalEndPoint} ({packet.Payload.Length} bytes; registered RTP ports: {ports})");
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { EventBus?.Publish(EventTopics.SystemError, "VoWiFi", $"Inner packet rejected: {ex.Message}"); }
                    }
                });

                _sipTransport.ConnectDatagrams(
                    new IPEndPoint(assignedIp, imsProfile.LocalPort), new IPEndPoint(pcscfAddr, 5060),
                    sender: async (packet, sendCt) =>
                    {
                        var inner = _imsIpsec?.Protect(packet) ?? IpPacketUtils.BuildUdpPacket(
                            packet.LocalEndPoint.Address, packet.RemoteEndPoint.Address,
                            (ushort)packet.LocalEndPoint.Port, (ushort)packet.RemoteEndPoint.Port, packet.Payload);
                        var nextHeader = packet.LocalEndPoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? (byte)41 : (byte)4;
                        var fragments = IpPacketUtils.FragmentForTunnel(inner);
                        var outerLengths = new List<int>(fragments.Count);
                        foreach (var fragment in fragments)
                        {
                            var outerEsp = esp.Seal(fragment, nextHeader);
                            outerLengths.Add(outerEsp.Length);
                            await tr.SendEspAsync(outerEsp, sendCt).ConfigureAwait(false);
                        }
                        var detail = $"SIP TX {packet.Payload.Length} bytes {packet.LocalEndPoint} -> {packet.RemoteEndPoint}; " +
                                     $"inner={inner.Length}, fragments={fragments.Count}, outer-ESP=[{string.Join(',', outerLengths)}]";
                        Console.WriteLine($"[VoWiFi SIP TX] {detail}");
                        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi", detail);
                    },
                    receiver: readCt => sipChannel.Reader.ReadAsync(readCt).AsTask(),
                    timeoutMs: 30000
                );
            }
            else
            {
                _sipTransport.Connect(pcscfAddr, remotePort: 5060, localPort: imsProfile.LocalPort);
            }

            var session = new SipRegisterSession(_sipTransport, imsProfile, _imsIpsec?.Proposal,
                _imsIpsec == null ? null : (agreement, ck, ik) =>
                {
                    _imsIpsec.Activate(agreement, ck, ik);
                    _sipTransport.SetEndpoints(new IPEndPoint(IPAddress.Parse(AssignedIp!), agreement.Selected.PortClient),
                        new IPEndPoint(pcscfAddr, agreement.PcscfServerPort));
                });
            _registerSession = session;
            async Task<(byte[] Res, byte[] Ck, byte[] Ik)> Authenticate(byte[] rand, byte[] autn, CancellationToken token)
            {
                var akaRes = await akaProvider.AuthenticateAsync(AkaChallenge.Create(rand, autn), token).ConfigureAwait(false);
                return (akaRes.Res!, akaRes.Ck!, akaRes.Ik!);
            }
            var regResult = await session.RegisterAsync(Authenticate, ct).ConfigureAwait(false);
            SmsCapabilityConfirmed = regResult.SmsCapabilityConfirmed;
            ImsInfo = new VoWifiImsSessionInfo(
                RegistrationState: "200 OK (Registered)",
                HomeDomain: EpdgInfo!.ImsDomain,
                Impi: EpdgInfo!.Impi,
                Impu: EpdgInfo!.Impu,
                PcscfEndpoint: $"sip:{_sipTransport.RemoteEndPoint};transport=udp",
                ContactUri: regResult?.ContactUri ?? imsProfile.PrivateIdentity,
                CSeq: 1,
                ExpiresSeconds: regResult?.ExpiresSeconds ?? 3600,
                SecurityAssociation: session.Agreement == null ? "ePDG ESP (S2b); IMS IPsec not negotiated" : "ePDG ESP + IMS ipsec-3gpp",
                RegisteredAt: regResult?.RegisteredAt ?? DateTime.UtcNow,
                ServiceRoute: regResult?.ServiceRoute,
                PAssociatedUri: regResult?.PAssociatedUri
            );

            session.RegistrationRefreshed += (_, result) =>
            {
                SmsCapabilityConfirmed = result.SmsCapabilityConfirmed;
                if (ImsInfo != null) ImsInfo = ImsInfo with
                {
                    ContactUri = result.ContactUri,
                    ExpiresSeconds = result.ExpiresSeconds,
                    RegisteredAt = result.RegisteredAt,
                    ServiceRoute = result.ServiceRoute,
                    PAssociatedUri = result.PAssociatedUri
                };
            };
            session.RegistrationFailed += (_, error) =>
            {
                QueueAutomaticRecovery($"IMS registration refresh failed after retries: {error}");
            };
            if (!SmsCapabilityConfirmed)
                EventBus?.Publish(EventTopics.SystemLog, "VoWiFi", "IMS registered, but this Contact's SMS capability was not confirmed by the registrar.");
            try { ImsRegistered?.Invoke(this, ImsInfo); } catch { }
            SetState(VoWifiState.ImsRegistered);
            ConnectedAt = DateTime.UtcNow;
            session.StartRefreshing(Authenticate);
            StartHealthMonitor();

            return true;
        }
        catch (Exception ex)
        {
            var failure = ex.Message;
            try { await StopVoWifiCoreAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            LastError = failure;
            SetState(VoWifiState.Failed);
            try { VoWifiError?.Invoke(this, failure); } catch { }
            EventBus?.Publish("vowifi.error", "VoWifiManager", failure);
            return false;
        }
    }

    public async Task<(bool Success, long RttMs, string Status)> ProbeLivenessAsync(CancellationToken token = default)
    {
        if (State != VoWifiState.ImsRegistered || _sipTransport == null)
            return (false, 0, "IMS未就绪");

        Interlocked.Increment(ref _sipProbesSent);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_csharpIkeBackend != null)
                await _csharpIkeBackend.ProbeLivenessAsync(token).ConfigureAwait(false);

            var sipStatus = _sipOptionsRejectedByNetwork
                ? "IKE DPD OK; IMS OPTIONS disabled by network policy"
                : await ProbeImsSignalingAsync(token).ConfigureAwait(false);
            sw.Stop();
            _lastProbeRttMs = sw.ElapsedMilliseconds;
            _lastProbeTime = DateTime.Now;
            _lastProbeResult = sipStatus;
            Interlocked.Increment(ref _sipProbesSuccess);
            _healthFailures = 0;
            EventBus?.Publish(EventTopics.SystemLog, "SIP", $"VoWiFi 心跳探针 {sipStatus} (RTT: {_lastProbeRttMs} ms)");
            return (true, _lastProbeRttMs, sipStatus);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _lastProbeRttMs = sw.ElapsedMilliseconds;
            _lastProbeTime = DateTime.Now;
            _lastProbeResult = ex.Message;
            Interlocked.Increment(ref _sipProbesFailed);
            EventBus?.Publish(EventTopics.SystemLog, "SIP", $"VoWiFi 心跳探针失败: {ex.Message}");
            return (false, _lastProbeRttMs, ex.Message);
        }
    }

    private void StartHealthMonitor()
    {
        _healthCts?.Cancel();
        _healthCts?.Dispose();
        _healthCts = new CancellationTokenSource();
        var token = _healthCts.Token;
        _healthFailures = 0;
        _healthTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                if (State == VoWifiState.ImsRegistered)
                {
                    await ProbeLivenessAsync(token).ConfigureAwait(false);
                }
            }
            catch { }
            await RunHealthMonitorAsync(token).ConfigureAwait(false);
        });
    }

    private async Task RunHealthMonitorAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthProbeInterval, token).ConfigureAwait(false);
                if (State != VoWifiState.ImsRegistered || _sipTransport == null)
                    continue;

                if (ConnectedAt is { } connectedAt && DateTime.UtcNow - connectedAt >= ProactiveRenewalAge)
                {
                    QueueAutomaticRecovery("VoWiFi session reached its scheduled renewal age.");
                    return;
                }

                var (success, _, status) = await ProbeLivenessAsync(token).ConfigureAwait(false);
                if (success)
                {
                    // Recovery is based on consecutive failures, not two unrelated failures over
                    // the lifetime of the session.
                    Interlocked.Exchange(ref _healthFailures, 0);
                }
                else
                {
                    var failures = Interlocked.Increment(ref _healthFailures);
                    if (failures >= 2)
                    {
                        QueueAutomaticRecovery($"VoWiFi liveness probes failed twice: {status}");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var failures = Interlocked.Increment(ref _healthFailures);
                EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                    $"VoWiFi health probe failed ({failures}/2): {ex.Message}");
                if (failures >= 2)
                {
                    QueueAutomaticRecovery($"VoWiFi liveness probes failed twice: {ex.Message}");
                    return;
                }
            }
        }
    }

    private async Task<string> ProbeImsSignalingAsync(CancellationToken token)
    {
        var transport = _sipTransport ?? throw new InvalidOperationException("IMS SIP transport is unavailable.");
        var ims = ImsInfo ?? throw new InvalidOperationException("IMS registration metadata is unavailable.");
        var local = transport.LocalEndPoint ?? throw new InvalidOperationException("IMS local endpoint is unavailable.");

        var request = new SipMessage
        {
            IsRequest = true,
            Method = "OPTIONS",
            RequestUri = $"sip:{ims.HomeDomain}"
        };
        var cseq = Interlocked.Increment(ref _sipCseq);
        request.SetHeader("Via", $"SIP/2.0/UDP {ImsRegisterBuilder.FormatHost(local.Address.ToString())}:{local.Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("Max-Forwards", "70");
        request.SetHeader("From", $"<{ims.Impu}>;tag={Guid.NewGuid().ToString("N")[..8]}");
        request.SetHeader("To", $"<{ims.Impu}>");
        request.SetHeader("Call-ID", $"{Guid.NewGuid():N}@{local.Address}");
        request.SetHeader("CSeq", $"{cseq} OPTIONS");
        var privateUser = ims.Impi.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
            ? ims.Impi[4..].Split('@')[0]
            : ims.Impi.Split('@')[0];
        request.SetHeader("Contact", $"<sip:{privateUser}@{ImsRegisterBuilder.FormatHost(local.Address.ToString())}:{local.Port};transport=udp>");
        request.SetHeader("Content-Length", "0");

        var response = await transport.SendAndReceiveFinalAsync(request, timeoutMs: 12000, ct: token).ConfigureAwait(false);
        // OPTIONS is only a transport/liveness probe. Several IMS deployments
        // intentionally reject it with 403 while still accepting REGISTER,
        // calls, and SMS. A valid final SIP response proves the P-CSCF route
        // and the encrypted tunnel are alive; registration renewal remains the
        // authority for whether the IMS identity is usable.
        if (response.StatusCode is < 200 or > 699)
            throw new InvalidOperationException($"IMS OPTIONS returned an invalid final status {response.StatusCode}.");

        var status = $"{response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        if (response.StatusCode is 403 or 405 or 501)
        {
            _sipOptionsRejectedByNetwork = true;
            EventBus?.Publish(EventTopics.SystemLog, "SIP",
                $"IMS returned {status} for OPTIONS. Future health checks will use IKE DPD and REGISTER refresh for this tunnel.");
        }
        return response.StatusCode is >= 200 and < 300
            ? status
            : $"{status} (IMS reachable; OPTIONS blocked by policy)";
    }

    private void QueueAutomaticRecovery(string reason)
    {
        if (Volatile.Read(ref _keepOnlineRequested) == 0 || _lastStartOptions == null)
            return;
        if (IsNonRetryableEpdgRejection(reason))
        {
            LastError = reason;
            EventBus?.Publish(EventTopics.SystemError, "VoWiFi",
                reason + " Automatic retries are paused; retry manually after the network backoff or after correcting the SIM/APN authorization.");
            return;
        }
        if (Interlocked.Exchange(ref _recoveryQueued, 1) != 0)
            return;

        var recoveryCts = new CancellationTokenSource();
        _recoveryCts = recoveryCts;
        var token = recoveryCts.Token;
        LastError = reason;
        SmsCapabilityConfirmed = false;
        SetState(VoWifiState.Failed);
        EventBus?.Publish(EventTopics.SystemError, "VoWiFi", reason + " Reconnecting automatically.");

        _ = Task.Run(async () =>
        {
            try
            {
                await StopVoWifiAttemptAsync(token).ConfigureAwait(false);
                var attempt = 0;
                var waitingForModemLogged = false;
                while (!token.IsCancellationRequested && Volatile.Read(ref _keepOnlineRequested) != 0)
                {
                    if (State == VoWifiState.ImsRegistered)
                        return;

                    if (Modem == null || !Modem.IsOpen)
                    {
                        if (!waitingForModemLogged)
                        {
                            waitingForModemLogged = true;
                            LastError = "VoWiFi registration is waiting for the USIM module to return.";
                            EventBus?.Publish(EventTopics.SystemLog, "VoWiFi", LastError);
                        }
                        await _recoveryWake.WaitAsync(token).ConfigureAwait(false);
                        attempt = 0;
                        continue;
                    }
                    waitingForModemLogged = false;

                    var delay = GetRecoveryDelay(attempt);
                    // A Windows network-availability event wakes a backed-off retry
                    // immediately after Wi-Fi/Ethernet returns from an outage or sleep.
                    await _recoveryWake.WaitAsync(delay, token).ConfigureAwait(false);
                    if (State == VoWifiState.ImsRegistered)
                        return;
                    if (Modem == null || !Modem.IsOpen)
                        continue;
                    var options = _lastStartOptions;
                    if (options == null || Volatile.Read(ref _keepOnlineRequested) == 0)
                        return;

                    attempt++;
                    EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                        $"Starting automatic VoWiFi recovery attempt {attempt}.");
                    if (await StartVoWifiAttemptAsync(options, token).ConfigureAwait(false))
                    {
                        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                            $"Automatic VoWiFi recovery succeeded on attempt {attempt}.");
                        return;
                    }

                    EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
                        $"Automatic VoWiFi recovery attempt {attempt} failed; retrying while VoWiFi remains enabled.");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LastError = $"Automatic VoWiFi recovery failed: {ex.Message}";
                SetState(VoWifiState.Failed);
                EventBus?.Publish(EventTopics.SystemError, "VoWiFi", LastError);
            }
            finally
            {
                Interlocked.CompareExchange(ref _recoveryCts, null, recoveryCts);
                recoveryCts.Dispose();
                Interlocked.Exchange(ref _recoveryQueued, 0);
            }
        });
    }

    private static TimeSpan GetRecoveryDelay(int failedAttempts) => failedAttempts switch
    {
        0 => TimeSpan.FromSeconds(2),
        1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(10),
        3 => TimeSpan.FromSeconds(20),
        4 => TimeSpan.FromSeconds(30),
        5 => TimeSpan.FromMinutes(1),
        6 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5)
    };

    private static bool IsNonRetryableEpdgRejection(string? reason) => reason != null &&
        (reason.Contains("NON_3GPP_ACCESS_TO_EPC_NOT_ALLOWED", StringComparison.Ordinal) ||
         reason.Contains("USER_UNKNOWN", StringComparison.Ordinal) ||
         reason.Contains("NO_APN_SUBSCRIPTION", StringComparison.Ordinal) ||
         reason.Contains("AUTHORIZATION_REJECTED", StringComparison.Ordinal) ||
         reason.Contains("ILLEGAL_ME", StringComparison.Ordinal) ||
         reason.Contains("RAT_TYPE_NOT_ALLOWED", StringComparison.Ordinal) ||
         reason.Contains("IMEI_NOT_ACCEPTED", StringComparison.Ordinal) ||
         reason.Contains("PLMN_NOT_ALLOWED", StringComparison.Ordinal) ||
         reason.Contains("UNAUTHENTICATED_EMERGENCY_NOT_SUPPORTED", StringComparison.Ordinal));

    /// <summary>
    /// Records the first sighting of a non-UDP inner protocol. Only SIP (and RTP/RTCP) is UDP;
    /// everything else on the S2b tunnel is operator control traffic that a real IPv6 stack would
    /// consume without bothering the application.
    /// </summary>
    private void NoteIgnoredInnerProtocol(byte protocol)
    {
        if (!_ignoredInnerProtocols.TryAdd(protocol, 0)) return;
        var name = protocol switch
        {
            1 => "ICMP",
            6 => "TCP",
            50 => "ESP",
            58 => "ICMPv6",
            _ => $"protocol {protocol}"
        };
        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
            $"Ignoring non-UDP inner {name} traffic; only SIP/RTP is delivered to the IMS stack.");
    }

    public async Task StopVoWifiAsync(CancellationToken ct = default)
    {
        Volatile.Write(ref _keepOnlineRequested, 0);
        _lastStartOptions = null;
        _recoveryCts?.Cancel();
        await StopVoWifiAttemptAsync(ct).ConfigureAwait(false);
    }

    private async Task StopVoWifiAttemptAsync(CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopVoWifiCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopVoWifiCoreAsync(CancellationToken ct)
    {
        var healthCts = _healthCts;
        var healthTask = _healthTask;
        _healthCts = null;
        _healthTask = null;
        healthCts?.Cancel();
        if (healthTask != null)
        {
            try { await healthTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        healthCts?.Dispose();
        _sessionCts?.Cancel();
        ClearRtpSessions();
        if (_registerSession != null) await _registerSession.StopRefreshingAsync().ConfigureAwait(false);
        _registerSession = null;
        SmsCapabilityConfirmed = false;
        _incomingSmsTransactions.Clear();

        _ctsEspDispatch?.Cancel();
        if (_espDispatchTask != null)
        {
            try { await _espDispatchTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _espDispatchTask = null;
        _innerIpv4Reassembler.Clear();
        _innerIpv6Reassembler.Clear();
        _ctsEspDispatch?.Dispose();
        _ctsEspDispatch = null;
        _imsIpsec?.Dispose();
        _imsIpsec = null;
        _sessionCts?.Dispose();
        _sessionCts = null;
        _sipTransport?.Dispose();
        _sipTransport = null;
        _transport?.Dispose();
        _transport = null;

        if (_csharpIkeBackend != null)
        {
            try { await _csharpIkeBackend.StopTunnelAsync(ct).ConfigureAwait(false); }
            catch { }
            _csharpIkeBackend.Dispose();
            _csharpIkeBackend = null;
        }

        SetState(VoWifiState.Disconnected);
        ConnectedAt = null;
        AssignedIp = null;
        TunnelInfo = null;
        ImsInfo = null;
        _sipProbesSent = 0;
        _sipProbesSuccess = 0;
        _sipProbesFailed = 0;
        _lastProbeRttMs = 0;
        _lastProbeTime = null;
        _lastProbeResult = null;
        _sipOptionsRejectedByNetwork = false;
        EventBus?.Publish("vowifi.disconnected", "VoWifiManager", "Disconnected");
    }

    public VoWifiDiagnosticInfo GetDiagnosticInfo()
    {
        var connectedAt = ConnectedAt;
        var uptimeStr = connectedAt is { } conn
            ? (DateTime.UtcNow - conn).ToString(@"hh\:mm\:ss")
            : "00:00:00";

        var epdgInfo = EpdgInfo;
        return new VoWifiDiagnosticInfo(
            State: State,
            Standard: "3GPP TS 23.402 / TS 24.302 (Untrusted Non-3GPP Wi-Fi Access)",
            MatchedCarrier: epdgInfo?.MatchedCarrier ?? "Generic 3GPP",
            EpdgFqdn: epdgInfo?.Fqdn ?? "N/A",
            EpdgIp: epdgInfo?.IpAddresses?.FirstOrDefault()?.ToString() ?? "N/A",
            EpdgPort: Ikev2Protocol.DefaultIkev2Port,
            Suite: CurrentSuite,
            DhGroup: CurrentSuite switch
            {
                IkeProposalSuite.Modern => "Group 19 (ECP-256 / NIST P-256)",
                IkeProposalSuite.Legacy => "Group 2 (MODP-1024)",
                _ => "Group 14 (MODP-2048)"
            },
            IkeInitiatorSpi: $"0x{IkeInitiatorSpi:X16}",
            IkeResponderSpi: $"0x{IkeResponderSpi:X16}",
            EapMethod: "EAP-AKA (RFC 4187 / 3GPP TS 33.402)",
            AuthVectorStatus: State == VoWifiState.ImsRegistered
                ? "Authenticated (Mutual 3GPP USIM AKA verified)"
                : "Pending",
            Tunnel: TunnelInfo,
            Ims: ImsInfo,
            ConnectedAt: connectedAt,
            Uptime: uptimeStr,
            LastError: LastError,
            SipProbesSent: _sipProbesSent,
            SipProbesSuccess: _sipProbesSuccess,
            SipProbesFailed: _sipProbesFailed,
            LastProbeRttMs: _lastProbeRttMs,
            LastProbeTime: _lastProbeTime,
            LastProbeResult: _lastProbeResult
        );
    }

    public VoWifiDiagnosticInfo GetStatusInfo() => GetDiagnosticInfo();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<IkeProposalSuite> GetIkeSuiteAttempts(
        IkeProposalSuite requestedSuite,
        IkeProposalSuite preferredSuite)
    {
        // An explicit CLI/UI suite is a diagnostic override and must remain deterministic.
        if (requestedSuite != IkeProposalSuite.Auto)
            return new[] { requestedSuite };

        var attempts = new List<IkeProposalSuite> { preferredSuite };
        foreach (var candidate in new[]
        {
            IkeProposalSuite.Modern, // ECP-256 / SHA-256 / AES-256
            IkeProposalSuite.Standard, // MODP-2048 / SHA-256 / AES-128
            IkeProposalSuite.Legacy // MODP-1024 / SHA-1 / AES-128
        })
        {
            if (!attempts.Contains(candidate)) attempts.Add(candidate);
        }
        return attempts;
    }

    private static bool IsProposalNegotiationFailure(string message)
    {
        // This intentionally excludes EAP, AUTH, CHILD_SA and CP errors. Only an
        // early proposal rejection is a reason to offer another algorithm family.
        // Some ePDGs silently discard an unsupported IKE_SA_INIT instead of
        // returning NO_PROPOSAL_CHOSEN. Message ID 0 is before EAP-AKA, so it is
        // safe to retry that timeout with another suite without consuming an AKA
        // vector or hiding an authentication failure.
        return message.Contains("no proposal", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("NO_PROPOSAL_CHOSEN", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("missing SA payload", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("invalid_ke", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("invalid ke", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("DH group", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Diffie-Hellman group", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unsupported suite", StringComparison.OrdinalIgnoreCase) ||
               (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("Message ID 0", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("IKE_SA_INIT", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsInitialIkeTimeout(string message) =>
        message.Contains("timed out", StringComparison.OrdinalIgnoreCase) &&
        (message.Contains("Message ID 0", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("IKE_SA_INIT", StringComparison.OrdinalIgnoreCase));

    private static bool IsPreAkaRejection(string message) =>
        message.Contains("EAP-AKA authentication rejected", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("before AKA challenge response", StringComparison.OrdinalIgnoreCase);

    private static bool CanTryNextIdentity(string? message) => message != null &&
        (message.Contains("ePDG DNS resolution returned no addresses", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("IKEv2 session failed", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the device IMEI from the modem. IMS registration sends this in the instance ID,
    /// and a fabricated value is rejected by most IMS cores.
    /// </summary>
    private async Task<string> ResolveImeiAsync(CancellationToken ct)
    {
        if (Modem != null)
        {
            try
            {
                var imei = await Modem.GetImeiAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(imei))
                    return imei.Trim();
            }
            catch (Exception ex)
            {
                EventBus?.Publish("vowifi.imei.unavailable", "VoWifiManager", ex.Message);
            }
        }

        throw new InvalidOperationException(
            "No IMEI available: attach a modem (AT+CGSN) or supply one explicitly. " +
            "A placeholder IMEI is rejected by IMS cores during registration.");
    }

    private static (byte[] Res, byte[] Ck, byte[] Ik, string Status) ComputeSoftwareAka(string imsi)
    {
        // Test vectors from 3GPP TS 35.208 §4.3 — used only when no USIM hardware is available
        var k = Convert.FromHexString("465b5ce8b199b49faa5f0a2ee238a6bc");
        var opc = Convert.FromHexString("cd63cb71954a9f4e48a5994e37a02baf");
        var rand = new byte[16];
        RandomNumberGenerator.Fill(rand);
        var (res, ck, ik, _, _) = Milenage.ComputeF2345(opc, k, rand);
        return (res, ck, ik, "Milenage-Software-TestVectors (No USIM Hardware)");
    }

    /// <summary>
    /// Attempts to parse an IKEv2 Configuration Payload from an IKE response.
    /// BUG-07 FIX: replaces Random.Shared.Next() for tunnel address assignment.
    /// Returns null if no CP payload is found (requires full IKE_AUTH exchange).
    /// </summary>
    private static (string AssignedIp, string PcscfIp, string? Dns, uint InboundSpi, uint OutboundSpi)?
        TryParseConfigurationPayload(byte[] ikeResponse)
    {
        if (ikeResponse.Length < 28) return null;

        byte nextPayload = ikeResponse[16];
        int offset = 28;
        string? assignedIp = null;
        string? pcscfIp = null;
        string? dns = null;

        while (offset + 4 <= ikeResponse.Length && nextPayload != (byte)IkePayloadType.None)
        {
            byte next = ikeResponse[offset];
            ushort pLen = BinaryPrimitives.ReadUInt16BigEndian(ikeResponse.AsSpan(offset + 2, 2));
            if (pLen < 4 || offset + pLen > ikeResponse.Length) break;

            if (nextPayload == (byte)IkePayloadType.Configuration)
            {
                // CP Type (1B) | Reserved (3B) | Attributes...
                int attrOffset = offset + 8; // skip payload header(4) + CP type(1) + reserved(3)
                while (attrOffset + 4 <= offset + pLen)
                {
                    ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(
                        ikeResponse.AsSpan(attrOffset, 2));
                    ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(
                        ikeResponse.AsSpan(attrOffset + 2, 2));

                    if (attrType == CpAttrInternalIp4Address && attrLen == 4)
                    {
                        var ip = new IPAddress(ikeResponse.AsSpan(attrOffset + 4, 4).ToArray());
                        assignedIp = ip.ToString();
                    }
                    else if (attrType == CpAttrPcscfIp4Address && attrLen == 4)
                    {
                        var ip = new IPAddress(ikeResponse.AsSpan(attrOffset + 4, 4).ToArray());
                        pcscfIp = ip.ToString();
                    }
                    else if (attrType == CpAttrInternalIp4Dns && attrLen == 4)
                    {
                        var ip = new IPAddress(ikeResponse.AsSpan(attrOffset + 4, 4).ToArray());
                        dns = ip.ToString();
                    }

                    attrOffset += 4 + attrLen;
                }
            }

            nextPayload = next;
            offset += pLen;
        }

        if (assignedIp == null) return null;

        // SPIs from a real IKE_AUTH response would come from the Child SA payload;
        // use cryptographically random SPIs as a placeholder until CREATE_CHILD_SA is implemented.
        var spiBytes = new byte[8];
        RandomNumberGenerator.Fill(spiBytes);
        uint inSpi = BinaryPrimitives.ReadUInt32BigEndian(spiBytes.AsSpan(0, 4));
        uint outSpi = BinaryPrimitives.ReadUInt32BigEndian(spiBytes.AsSpan(4, 4));

        return (assignedIp, pcscfIp ?? "0.0.0.0", dns, inSpi, outSpi);
    }

    /// <summary>
    /// Handles an incoming SIP MESSAGE containing 3GPP TS 24.341 / TS 24.011 RPDU payload.
    /// Acknowledges delivery with 3GPP RP-ACK and dispatches SMS or delivery status report.
    /// </summary>
    private void OnIncomingSipRequestReceived(object? sender, SipMessage request)
    {
        _ = RouteIncomingSipRequestAsync(request);
    }

    private async Task RouteIncomingSipRequestAsync(SipMessage request)
    {
        var transport = _sipTransport;
        if (transport == null) return;

        Task Reply(SipMessage response) => transport.SendAsync(response);

        try
        {
            if (request.Method.Equals("INVITE", StringComparison.OrdinalIgnoreCase))
            {
                if (Calls != null) await Calls.HandleIncomingInviteAsync(request, this, Reply).ConfigureAwait(false);
                return;
            }
            if (request.Method.Equals("CANCEL", StringComparison.OrdinalIgnoreCase))
            {
                if (Calls != null) await Calls.HandleIncomingCancelAsync(request, Reply).ConfigureAwait(false);
                return;
            }
            if (request.Method.Equals("BYE", StringComparison.OrdinalIgnoreCase))
            {
                if (Calls != null && (Calls.State == CallState.Active || Calls.State == CallState.Ringing))
                    await Calls.HandleIncomingByeAsync(request, Reply).ConfigureAwait(false);
                else
                    await Reply(request.CreateResponse(200, "OK")).ConfigureAwait(false);
                return;
            }
            if (request.Method.Equals("MESSAGE", StringComparison.OrdinalIgnoreCase))
            {
                await HandleIncomingSipMessageAsync(request, Reply, ct: _sessionCts?.Token ?? default).ConfigureAwait(false);
                return;
            }
            if (request.Method.Equals("ACK", StringComparison.OrdinalIgnoreCase)) return;

            var status = request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) ? 200 : 405;
            var reason = status == 200 ? "OK" : "Method Not Allowed";
            await Reply(request.CreateResponse(status, reason)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EventBus?.Publish(EventTopics.SystemError, "VoWiFi", $"Incoming SIP {request.Method} failed: {ex.Message}");
        }
    }

    public async Task HandleIncomingSipMessageAsync(
        SipMessage request, Func<SipMessage, Task> replySender,
        Func<SipMessage, Task<SipMessage>>? reportSender = null, CancellationToken ct = default)
    {
        var contentType = request.GetHeader("Content-Type");
        EventBus?.Publish(EventTopics.SystemLog, "VoWiFi",
            $"Incoming SIP {request.Method}: Call-ID={request.GetHeader("Call-ID")}, CSeq={request.GetHeader("CSeq")}, Content-Type={contentType}");
        if (!ImsSmsHandler.SupportsContentType(contentType))
        {
            await replySender(ImsSmsHandler.BuildSipResponse(request, 415, "vowifi-sms")).ConfigureAwait(false);
            return;
        }

        // SIP acceptance and the RP delivery report are two separate transactions.
        await replySender(ImsSmsHandler.BuildSipResponse(request, 200, "vowifi-sms")).ConfigureAwait(false);
        var key = $"{request.GetHeader("Call-ID")}|{request.GetHeader("CSeq")}|{request.GetHeader("Via")}";
        var now = DateTime.UtcNow;
        foreach (var entry in _incomingSmsTransactions)
            if (entry.Value.Work.IsValueCreated && entry.Value.Work.Value.IsCompleted && now - entry.Value.Created > TimeSpan.FromMinutes(2))
                _incomingSmsTransactions.TryRemove(entry.Key, out _);
        var transaction = _incomingSmsTransactions.GetOrAdd(key, _ => new IncomingSmsTransaction(now,
            new Lazy<Task>(() => ProcessIncomingSmsAsync(request, reportSender, ct), LazyThreadSafetyMode.ExecutionAndPublication)));
        await transaction.Work.Value.ConfigureAwait(false);
    }

    private async Task ProcessIncomingSmsAsync(SipMessage request,
        Func<SipMessage, Task<SipMessage>>? reportSender, CancellationToken ct)
    {
        byte reference = 0;
        byte[] report;
        try
        {
            var payload = ImsSmsHandler.ExtractSmsPayload(request, out _)
                ?? throw new FormatException("No SMS payload.");
            if (payload.Length > 1) reference = payload[1];
            var rpdu = ImsSmsHandler.ParseRpdu(payload);
            if (rpdu.MessageType is 3 or 5) return; // Network RP-ACK / RP-ERROR: no ACK of an ACK.
            if (rpdu.MessageType != 1 || rpdu.Tpdu == null)
                throw new FormatException("Expected network RP-DATA.");
            ImsSmsHandler.ValidateIncomingTpdu(rpdu.Tpdu);
            var decoded = SmsPdu.DecodePdu("00" + Convert.ToHexString(rpdu.Tpdu));
            if (decoded.IsStatusReport && decoded.StatusReport != null)
            {
                EventBus?.Publish(EventTopics.SmsStatusReport, "VoWifiManager", decoded.StatusReport);
                EventBus?.Publish("vowifi.sms.status_report", "VoWifiManager", decoded.StatusReport);
            }
            else
            {
                var fullSms = _smsReassembler.ProcessIncomingPart(decoded);
                if (fullSms != null)
                {
                    EventBus?.Publish(EventTopics.SmsIncoming, "VoWifiManager", fullSms);
                    EventBus?.Publish(EventTopics.SmsReceived, "VoWifiManager", fullSms);
                    EventBus?.Publish("vowifi.sms.received", "VoWifiManager", fullSms);
                }
            }
            // Each accepted fragment is acknowledged, even before the full SMS is assembled.
            report = ImsSmsHandler.BuildRpAck(reference, includeUserData: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EventBus?.Publish(EventTopics.SystemError, "VoWiFi", $"SMS decode failed: {ex.Message}");
            report = ImsSmsHandler.BuildRpError(reference, 95);
        }

        var target = FirstSipUri(request.GetHeader("P-Asserted-Identity")) ?? FirstSipUri(request.GetHeader("From"))
            ?? throw new FormatException("SMS delivery report has no IP-SM-GW target.");
        var message = BuildSmsMessage(target, report, request.GetHeader("Call-ID"), FirstSipUri(request.GetHeader("To")));
        var response = reportSender != null
            ? await reportSender(message).ConfigureAwait(false)
            : await (_sipTransport ?? throw new InvalidOperationException("SIP transport is unavailable."))
                .SendAndReceiveFinalAsync(message, ct: ct).ConfigureAwait(false);
        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"SMS delivery report rejected: SIP {response.StatusCode}.");
    }

    private static string? FirstSipUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = SecurityAgreementBuilder.SplitHeaderValues(value).FirstOrDefault();
        if (first == null) return null;
        var match = Regex.Match(first, @"(?:<|^)[ \t]*((?:sips?|tel):[^>\s]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return match.Groups[1].Value.Split(";tag=", StringSplitOptions.None)[0];
    }

    private SipMessage BuildSmsMessage(string target, byte[] body, string? inReplyTo = null, string? fallbackIdentity = null)
    {
        var local = _sipTransport?.LocalEndPoint ?? new IPEndPoint(IPAddress.Parse(AssignedIp ?? "127.0.0.1"), 5060);
        var publicIdentity = FirstSipUri(ImsInfo?.PAssociatedUri) ?? FirstSipUri(ImsInfo?.Impu)
            ?? (ImsInfo != null ? "sip:" + ImsInfo.Impu : fallbackIdentity)
            ?? throw new InvalidOperationException("No IMS public identity.");
        var message = new SipMessage { IsRequest = true, Method = "MESSAGE", RequestUri = target, RawBody = body };
        message.SetHeader("Via", $"SIP/2.0/UDP {local};branch=z9hG4bK{Guid.NewGuid():N};rport");
        message.SetHeader("Max-Forwards", "70");
        message.SetHeader("From", $"<{publicIdentity}>;tag={_smsFromTag}");
        message.SetHeader("To", $"<{target}>");
        message.SetHeader("Call-ID", $"{Guid.NewGuid():N}@{local.Address}");
        message.SetHeader("CSeq", $"{Interlocked.Increment(ref _sipCseq)} MESSAGE");
        message.SetHeader("P-Preferred-Identity", $"<{publicIdentity}>");
        message.SetHeader("Accept-Contact", "*;+g.3gpp.smsip");
        message.SetHeader("Request-Disposition", "no-fork");
        message.SetHeader("Allow", "MESSAGE");
        if (inReplyTo != null) message.SetHeader("In-Reply-To", inReplyTo);
        if (!string.IsNullOrWhiteSpace(ImsInfo?.ServiceRoute)) message.SetHeader("Route", ImsInfo.ServiceRoute);
        else if (_sipTransport?.RemoteEndPoint != null)
            message.SetHeader("Route", $"<sip:{_sipTransport.RemoteEndPoint};transport=udp;lr>");
        message.SetHeader("Content-Type", ImsSmsHandler.SmsContentType);
        message.SetHeader("Content-Transfer-Encoding", "binary");
        message.SetHeader("Content-Length", body.Length.ToString());
        _registerSession?.AddSecurityHeaders(message);
        return message;
    }

    /// <summary>
    /// Submits an SMS message over VoWiFi / IMS (3GPP TS 24.341 / TS 24.011) via SIP MESSAGE.
    /// Requests delivery receipts by default and assigns unique Message References.
    /// </summary>
    public async Task<SmsSubmitResult> SendSmsOverImsAsync(
        string recipient,
        string text,
        bool requestStatusReport = true,
        CancellationToken ct = default)
    {
        if (State != VoWifiState.ImsRegistered || ImsInfo == null || _sipTransport == null)
            throw new InvalidOperationException("VoWiFi is not registered. Cannot send SMS over IMS.");

        var smsc = EpdgInfo?.SmsCenter;
        if (string.IsNullOrWhiteSpace(smsc))
            throw new InvalidOperationException(
                "The SIM did not provide an SMSC (AT+CSCA). Refusing to route IMS SMS to an unverified service centre.");

        var parts = SmsPdu.PrepareSubmitParts(recipient, text, requestStatusReport: requestStatusReport, smsc: smsc);
        var now = DateTime.UtcNow;
        var result = new SmsSubmitResult(
            Recipient: recipient,
            Text: text,
            Encoding: parts[0].Encoding,
            ConcatReference: parts[0].ConcatReference,
            PartsTotal: parts.Count,
            PartsAccepted: 0,
            PartsAttempted: 0,
            AllPartsAccepted: false,
            SubmissionStatus: "pending",
            PartResults: new List<SmsSubmitPartStatus>(),
            SubmittedAt: now
        );

        string psi = "tel:" + SmsPdu.NormalizeRecipient(smsc);
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            byte reference = unchecked((byte)Interlocked.Increment(ref _nextRpReference));
            if (part.Tpdu.Length >= 2)
            {
                part.Tpdu[1] = reference;
            }
            var rpData = ImsSmsHandler.BuildRpData(reference, smsc, part.Tpdu);

            var msgReq = BuildSmsMessage(psi, rpData);

            result = result with { PartsAttempted = result.PartsAttempted + 1 };

            var resp = await _sipTransport.SendAndReceiveAsync(msgReq, ct).ConfigureAwait(false);
            var partStatus = new SmsSubmitPartStatus(
                Part: part.PartNumber,
                Total: part.TotalParts,
                Reference: reference,
                Accepted: resp != null && resp.StatusCode is >= 200 and < 300,
                SipCode: resp?.StatusCode,
                SubmissionStatus: resp != null && resp.StatusCode is >= 200 and < 300 ? "accepted_by_ims" : "rejected_by_ims",
                SubmittedAt: DateTime.UtcNow
            );
            result.PartResults.Add(partStatus);

            if (partStatus.Accepted)
            {
                result = result with { PartsAccepted = result.PartsAccepted + 1 };
            }
            else
            {
                result = result with { SubmissionStatus = "rejected" };
                break;
            }
        }

        if (result.PartsAccepted == result.PartsTotal)
        {
            result = result with { AllPartsAccepted = true, SubmissionStatus = "accepted_by_ims" };
            EventBus?.Publish(EventTopics.SmsSent, "VoWifiManager", result);
        }

        return result;
    }

    private void SetState(VoWifiState newState)
    {
        var oldState = State;
        State = newState;
        try
        {
            StateChanged?.Invoke(this, new VoWifiStateChangedEventArgs(
                oldState,
                newState,
                EpdgInfo?.Fqdn,
                AssignedIp,
                TunnelInfo?.PcscfIp,
                LastError
            ));
        }
        catch { }
        EventBus?.Publish("vowifi.state.changed", "VoWifiManager", newState.ToString());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        Volatile.Write(ref _keepOnlineRequested, 0);
        _lastStartOptions = null;
        _recoveryCts?.Cancel();
        _healthCts?.Cancel();
        _sessionCts?.Cancel();
        _ctsEspDispatch?.Cancel();
        _innerIpv4Reassembler.Clear();
        _innerIpv6Reassembler.Clear();
        ClearRtpSessions();
        _registerSession?.Dispose();
        _imsIpsec?.Dispose();
        _csharpIkeBackend?.Dispose();
        _csharpIkeBackend = null;
        _sipTransport?.Dispose();
        _transport?.Dispose();
    }
}
