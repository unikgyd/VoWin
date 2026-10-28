using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;

namespace VoSharp.Kernel.SipGateway;

/// <summary>
/// Small private-network SIP registrar and B2BUA.  The public/operator dialog
/// remains owned by <see cref="ImsCallManager"/>; this class terminates a second,
/// ordinary SIP/RTP dialog for phones reachable through WireGuard.
/// </summary>
public sealed partial class SipGateway : IAsyncDisposable
{
    private sealed class CarrierRelease { public int Started; }
    private sealed class Dialog : IDisposable
    {
        public required string LocalCallId { get; init; }
        public required string Extension { get; init; }
        public required IPEndPoint PhoneEndPoint { get; init; }
        public required RtpSession Media { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public string? SlotId { get; set; }
        public ICallPcmMedia? CarrierMedia { get; set; }
        public string? CarrierCallId { get; set; }
        public string? PairedDialogId { get; set; }
        public string? RemoteTarget { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public SipMessage? OriginalInvite { get; set; }
        public int InviteFinalSent; // 0 pending, 1 accepted, 2 terminated/rejected
        public readonly object InviteFinalGate = new();
        public CarrierRelease CarrierRelease { get; set; } = new();
        public int ByeStarted;
        public int PeerEnded;
        public TaskCompletionSource InviteAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LocalCSeq { get; set; } = 2;
        public int InviteCSeq { get; set; } = 1;
        public Action<short[]>? DownlinkHandler { get; set; }
        private int _disposed;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (this)
            {
                try { Cancellation.Cancel(); } catch { }
                if (CarrierMedia != null && DownlinkHandler != null)
                    CarrierMedia.RemotePcmReceived -= DownlinkHandler;
                if (CarrierMedia is ImsCallManager ims) ims.ExternalMediaBridgeEnabled = false;
                Media.Dispose();
                Cancellation.Dispose();
            }
        }
    }

    private sealed record PendingTransaction(TaskCompletionSource<SipMessage> Completion, Action<SipMessage>? Provisional,
        IPEndPoint Remote, string ViaBranch, string CallId, string FromTag, string ToTag)
    {
        public volatile bool ReceivedProvisional;
    }
    private sealed record CachedServerResponse(SipMessage Response, IPEndPoint Remote, DateTimeOffset CreatedAt);
    private readonly SipGatewayOptions _options;
    private readonly ISipGatewayCallController _controller;
    private IDisposable? _ownedController;
    private readonly SipDigestAuthenticator _authenticator;
    private readonly SipDigestAuthenticator? _peerAuthenticator;
    private readonly ConcurrentDictionary<string, SipGatewayRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingTransaction> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LateInvite> _lateInvites = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _serverProcessing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedServerResponse> _serverResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dialog> _dialogs = new(StringComparer.Ordinal);
    private sealed class IncomingFork(IncomingCallEventArgs call, CancellationToken lifetime)
    {
        public IncomingCallEventArgs Call { get; } = call;
        public CarrierRelease CarrierRelease { get; } = new();
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public Task Task { get; set; } = Task.CompletedTask;
        // Accessed under _forkGate: 0 = ringing, 1 = desktop, 2 = SIP, 3 = rejected.
        public int PickupOwner;
        public bool LocalPickupEverClaimed;
        public TaskCompletionSource LocalPickup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string, IncomingFork> _incomingForks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _forkGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _receiveLifetime = new();
    private UdpClient? _udp;
    private IPEndPoint? _localEndPoint;
    private Task? _receiveTask;
    private int _started;
    private int _disposed;

    internal TimeSpan InviteTransactionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan InviteAckTimeout { get; set; } = TimeSpan.FromSeconds(32);
    internal TimeSpan MessageTransactionTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public SipGateway(SipGatewayOptions options, ISipGatewayCallController controller)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = SipSmsRecipientAuthority.WithRuntimeEpochs(options.DeepClone());
        _options.Validate();
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _authenticator = new SipDigestAuthenticator(_options.Realm, _options.Accounts, _options.LocalRegistrarAlgorithm);
        if (_options.Peer?.InboundAuth is { } inbound)
            _peerAuthenticator = new SipDigestAuthenticator(_options.Realm,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [inbound.Username] = inbound.Password },
                inbound.Algorithm, strictHeaders: true);
    }

    public SipGateway(SipGatewayOptions options, IVoKernel kernel)
        : this(options, new KernelSipGatewayCallController(kernel))
    {
        _ownedController = (IDisposable)_controller;
    }

    public event EventHandler<SipGatewayStatus>? StatusChanged;
    public event EventHandler<string>? Diagnostic;
    public event EventHandler<SipIncomingCallClaimed>? IncomingCallClaimed;

    public SipGatewayStatus Status => new(
        Volatile.Read(ref _udp) != null && Volatile.Read(ref _disposed) == 0,
        Volatile.Read(ref _localEndPoint), // UI polling must never touch a socket being disposed.
        GetActiveRegistrations(),
        _dialogs.Values.Count(dialog => dialog.PairedDialogId == null ||
            string.CompareOrdinal(dialog.LocalCallId, dialog.PairedDialogId) < 0))
    {
        PeerEndPoint = _options.Peer?.EndPoint,
        IncomingTarget = _options.Peer?.IncomingTarget,
        RequiresConfiguredLines = _options.UsesConfiguredLines,
        PeerAllowsOutboundCalls = _options.Peer?.AllowOutboundCalls == true,
        LocalIncomingEnabled = _options.LocalIncomingEnabled,
        MessagesEnabled = _options.MessagesEnabled, OutgoingSmsEnabled = _options.MessagesEnabled && _options.AllowOutgoingSms,
        MessagesForwarded = Interlocked.Read(ref _messagesDelivered), MessageFailures = Interlocked.Read(ref _messagesFailed),
        LineRoutes = _options.LineRoutes.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone(), StringComparer.OrdinalIgnoreCase)
    };

    // This can be queried synchronously by desktop alerting, before event subscribers run.
    public bool RoutesIncomingCalls => _options.UsesConfiguredLines
        ? _options.LineRoutes.Keys.Any(RoutesIncomingCall) : RoutesIncomingCall(null);

    public bool RoutesIncomingCall(string? slotId) => _udp != null && Volatile.Read(ref _disposed) == 0 &&
        GetIncomingBindings(slotId).Count > 0;

    private IReadOnlyList<SipGatewayRegistration> GetIncomingBindings(string? slotId)
    {
        if (_options.IncomingRoute(slotId) is not { } route) return [];
        var bindings = GetActiveRegistrations().Where(binding => IsLocalIncomingRecipient(route, binding.Extension, sms: false)).ToList();
        if (_options.Peer is { } peer && TryGetIncomingPeerTarget(route, sms: false, out var target))
        {
            bindings.Add(new(target, $"<sip:{target}@{FormatHost(peer.EndPoint.Address)}:{peer.EndPoint.Port}>",
                peer.EndPoint, DateTimeOffset.MaxValue, "Fixed SIP peer"));
        }
        return bindings;
    }

    private bool IsLocalIncomingRecipient(SipGatewayLineRoute route, string username, bool sms)
    {
        var targets = sms ? route.LocalSmsRecipients : route.LocalIncomingExtensions;
        if (targets != null) return targets.Contains(username, StringComparer.OrdinalIgnoreCase);
        var scalar = sms ? route.LocalSmsTarget : route.LocalIncomingTarget;
        scalar ??= _options.Peer == null ? (sms ? route.SmsTarget : route.IncomingTarget) : null;
        return string.IsNullOrWhiteSpace(scalar) || username.Equals(scalar, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetIncomingPeerTarget(SipGatewayLineRoute route, bool sms, out string target)
    {
        target = "";
        if (_options.Peer is not { } peer ||
            (sms ? peer.AllowIncomingMessages == false || route.ForwardSmsToPeer == false :
                peer.AllowIncomingCalls == false || route.ForwardCallsToPeer == false)) return false;
        var explicitTarget = sms ? route.SmsTarget : route.IncomingTarget;
        target = string.IsNullOrWhiteSpace(explicitTarget) ? peer.IncomingTarget : explicitTarget;
        return true;
    }

    public bool HasBridgedCalls
    {
        get { lock (_forkGate) return _incomingForks.Count > 0 || !_dialogs.IsEmpty; }
    }

    /// <summary>Claims only this carrier call; the caller then uses normal local AnswerAsync.</summary>
    public bool TryClaimLocalIncomingCall(string callId, string? slotId)
    {
        lock (_forkGate)
        {
            if (!_options.LocalIncomingEnabled || Volatile.Read(ref _disposed) != 0 ||
                !_incomingForks.TryGetValue(IncomingKey(callId, slotId), out var fork) ||
                fork.Cancellation.IsCancellationRequested || fork.PickupOwner != 0) return false;
            fork.PickupOwner = 1;
            fork.LocalPickupEverClaimed = true;
            fork.LocalPickup.TrySetResult();
        }
        RaiseIncomingClaimed(callId, slotId, isLocal: true);
        return true;
    }

    private void RaiseIncomingClaimed(string callId, string? slotId, bool isLocal)
    {
        try { IncomingCallClaimed?.Invoke(this, new(callId, slotId, isLocal)); } catch { }
    }

    /// <summary>Allows a failed local answer attempt to be retried; cancelled SIP legs stay cancelled.</summary>
    public void ReleaseLocalIncomingCallClaim(string callId, string? slotId)
    {
        lock (_forkGate)
            if (_incomingForks.TryGetValue(IncomingKey(callId, slotId), out var fork) && fork.PickupOwner == 1)
                fork.PickupOwner = 0;
    }

    /// <summary>Stops every ringing branch before the desktop rejects the carrier leg.</summary>
    public void CancelIncomingCallRinging(string callId, string? slotId)
    {
        lock (_forkGate)
            if (_incomingForks.TryGetValue(IncomingKey(callId, slotId), out var fork))
            {
                fork.PickupOwner = 3;
                fork.LocalPickup.TrySetResult();
            }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        try
        {
            _udp = new UdpClient(new IPEndPoint(_options.BindAddress, _options.SipPort));
            _localEndPoint = _udp.Client.LocalEndPoint as IPEndPoint;
            _controller.IncomingCall += OnCarrierIncomingCall;
            _controller.CallEnded += OnCarrierCallEnded;
            if (_controller is ISipGatewayMessageController messages) messages.SmsReceived += OnCarrierSms;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveLifetime.Token));
            StartSmsForwardWorker();
            RaiseStatus();
            Trace($"SIP gateway listening on {_options.BindAddress}:{_options.SipPort} (private/WireGuard mode).");
            return Task.CompletedTask;
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            _udp?.Dispose();
            _udp = null;
            _localEndPoint = null;
            throw;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                // Apply source policy before parsing, transaction-cache replies,
                // challenges or unauthenticated OPTIONS can amplify a datagram.
                if (!_options.IsTrustedSource(packet.RemoteEndPoint.Address)) continue;
                SipMessage message;
                try { message = SipMessage.Parse(packet.Buffer); }
                catch (Exception ex) { Trace($"Dropped malformed SIP datagram from {packet.RemoteEndPoint}: {ex.Message}"); continue; }

                if (!message.IsRequest)
                {
                    var transactionKey = TransactionKey(message);
                    if (_lateInvites.TryGetValue(transactionKey, out var late) &&
                        late.Remote.Equals(packet.RemoteEndPoint) && ValidLateInviteResponse(late, message) &&
                        ObserveLateInviteResponse(late, message, out var sendBye))
                        // ACK sends do not wait for a peer response. BYE work is
                        // separately bounded and never blocks this receive loop.
                        await SendLateInviteAckByeAsync(late, message, sendBye).ConfigureAwait(false);
                    if (_pending.TryGetValue(transactionKey, out var pending) &&
                        pending.Remote.Equals(packet.RemoteEndPoint) &&
                        string.Equals(pending.ViaBranch, ViaBranch(message), StringComparison.Ordinal) &&
                        ResponseMatchesIdentity(message, pending.CallId, pending.FromTag, pending.ToTag))
                    {
                        if (message.StatusCode < 200)
                        {
                            pending.ReceivedProvisional = true;
                            pending.Provisional?.Invoke(message);
                        }
                        else pending.Completion.TrySetResult(message);
                    }
                    CleanupLateInvites();
                    continue;
                }
                var serverKey = ServerTransactionKey(message, packet.RemoteEndPoint);
                if (_serverResponses.TryGetValue(serverKey, out var cached))
                {
                    await SendAsync(cached.Response, packet.RemoteEndPoint, ct).ConfigureAwait(false);
                    continue;
                }
                if (!_serverProcessing.TryAdd(serverKey, 0)) continue;
                _ = Task.Run(async () =>
                {
                    try { await HandleRequestAsync(message, packet.RemoteEndPoint, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Trace($"SIP request processing failed: {ex.Message}");
                        if (message.Method.Equals("ACK", StringComparison.OrdinalIgnoreCase) || ct.IsCancellationRequested)
                            return;
                        if (_serverResponses.TryGetValue(serverKey, out var response) && response.Response.StatusCode >= 200)
                            return;
                        try
                        {
                            await SendResponseAsync(message, packet.RemoteEndPoint, 500, "Server Internal Error",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception replyError) { Trace($"SIP error response failed: {replyError.Message}"); }
                    }
                    finally { _serverProcessing.TryRemove(serverKey, out _); }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Trace($"SIP receive error: {ex.Message}"); }
        }
    }

    private async Task HandleRequestAsync(SipMessage request, IPEndPoint remote, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 && request.Method.ToUpperInvariant() is "INVITE" or "REGISTER")
        {
            await SendResponseAsync(request, remote, 503, "Gateway Stopping", ct).ConfigureAwait(false);
            return;
        }
        switch (request.Method.ToUpperInvariant())
        {
            case "REGISTER": await HandleRegisterAsync(request, remote, ct).ConfigureAwait(false); break;
            case "OPTIONS": await SendResponseAsync(request, remote, 200, "OK", ct, response =>
            {
                response.SetHeader("Allow", "REGISTER, INVITE, ACK, CANCEL, BYE, OPTIONS, INFO" + (_options.MessagesEnabled ? ", MESSAGE" : ""));
                response.SetHeader("Accept", "application/sdp" + (_options.MessagesEnabled ? ", text/plain" : ""));
            }).ConfigureAwait(false); break;
            case "INVITE": await HandlePhoneInviteAsync(request, remote, ct).ConfigureAwait(false); break;
            case "ACK": HandleAck(request, remote); break;
            case "CANCEL": await HandleCancelAsync(request, remote, ct).ConfigureAwait(false); break;
            case "BYE": await HandleByeAsync(request, remote, ct).ConfigureAwait(false); break;
            case "INFO": await HandleInfoAsync(request, remote, ct).ConfigureAwait(false); break;
            case "MESSAGE": await HandleMessageAsync(request, remote, ct).ConfigureAwait(false); break;
            default:
                await SendResponseAsync(request, remote, 405, "Method Not Allowed", ct, response =>
                    response.SetHeader("Allow", "REGISTER, INVITE, ACK, CANCEL, BYE, OPTIONS, INFO, MESSAGE")).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleRegisterAsync(SipMessage request, IPEndPoint remote, CancellationToken ct)
    {
        if (IsPeerSource(remote) || _options.Accounts.Count == 0)
        {
            await SendResponseAsync(request, remote, 403, "Fixed Trunk Does Not Accept Registrations", ct).ConfigureAwait(false);
            return;
        }
        if (!_authenticator.Validate(request, out var username))
        {
            await SendResponseAsync(request, remote, 401, "Unauthorized", ct, response =>
                response.SetHeader("WWW-Authenticate", _authenticator.CreateChallenge())).ConfigureAwait(false);
            return;
        }

        var extension = ExtractUriUser(request.GetHeader("To"));
        if (!string.Equals(extension, username, StringComparison.OrdinalIgnoreCase))
        {
            await SendResponseAsync(request, remote, 403, "Forbidden", ct).ConfigureAwait(false);
            return;
        }
        var contact = request.GetHeader("Contact")?.Trim();
        if (string.IsNullOrWhiteSpace(contact))
        {
            var contacts = GetActiveRegistrations().Where(binding => binding.Extension.Equals(username, StringComparison.OrdinalIgnoreCase)).ToArray();
            await SendResponseAsync(request, remote, 200, "OK", ct, response =>
            {
                foreach (var binding in contacts)
                    response.AddHeader("Contact", $"{binding.Contact};expires={Math.Max(0, (int)(binding.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)}");
            }).ConfigureAwait(false);
            return;
        }
        if (contact.Length > 512 || contact != "*" && ExtractContactTarget(contact) == null)
        {
            await SendResponseAsync(request, remote, 400, "Invalid Contact", ct).ConfigureAwait(false);
            return;
        }
        var expires = ParseExpires(request, contact);
        var key = RegistrationKey(extension, remote);
        if (contact == "*")
        {
            if (expires != 0)
            {
                await SendResponseAsync(request, remote, 400, "Wildcard Contact Requires Zero Expiry", ct).ConfigureAwait(false);
                return;
            }
            foreach (var entry in _registrations.Where(pair => pair.Value.Extension.Equals(username, StringComparison.OrdinalIgnoreCase)))
                _registrations.TryRemove(entry.Key, out _);
        }
        else if (expires == 0)
        {
            _registrations.TryRemove(key, out _);
        }
        else
        {
            if (!_registrations.ContainsKey(key) && GetActiveRegistrations().Count(binding =>
                binding.Extension.Equals(username, StringComparison.OrdinalIgnoreCase)) >= _options.MaximumContactsPerAccount)
            {
                await SendResponseAsync(request, remote, 403, "Registration Contact Limit", ct).ConfigureAwait(false);
                return;
            }
            expires = Math.Clamp(expires, _options.MinimumRegistrationSeconds, _options.MaximumRegistrationSeconds);
            _registrations[key] = new SipGatewayRegistration(extension, contact, remote,
                DateTimeOffset.UtcNow.AddSeconds(expires), request.GetHeader("User-Agent") ?? string.Empty);
        }
        await SendResponseAsync(request, remote, 200, "OK", ct, response =>
        {
            if (expires > 0) response.SetHeader("Contact", $"{contact};expires={expires}");
            response.SetHeader("Expires", expires.ToString());
        }).ConfigureAwait(false);
        NotifySmsInboxCommitted();
        RaiseStatus();
    }

    private async Task HandlePhoneInviteAsync(SipMessage invite, IPEndPoint remote, CancellationToken serverCt)
    {
        var fromPeer = IsPeerSource(remote);
        if (fromPeer && _options.Peer?.AllowOutboundCalls != true || !fromPeer && _options.Accounts.Count == 0)
        {
            await SendResponseAsync(invite, remote, 403, "Outbound Carrier Calls Disabled Or Peer Not Allowed", serverCt).ConfigureAwait(false);
            return;
        }
        var authenticator = fromPeer ? _peerAuthenticator! : _authenticator;
        if (!authenticator.Validate(invite, out var extension))
        {
            await SendResponseAsync(invite, remote, 407, "Proxy Authentication Required", serverCt, response =>
                response.SetHeader("Proxy-Authenticate", authenticator.CreateChallenge())).ConfigureAwait(false);
            return;
        }
        if (!SipGatewaySdp.TryParseAudioOffer(invite.Body, remote.Address, _options.TrustClientSdpAddress, out var phoneOffer))
        {
            await SendResponseAsync(invite, remote, 488, "Not Acceptable Here", serverCt).ConfigureAwait(false);
            return;
        }

        var target = ExtractUriUser(invite.RequestUri);
        if (_options.Accounts.ContainsKey(target))
        {
            if (fromPeer)
            {
                await SendResponseAsync(invite, remote, 403, "Peer Cannot Route To Local Account", serverCt).ConfigureAwait(false);
                return;
            }
            if (_options.PolicyFor(extension)?.AllowInternalCalls == false || _options.PolicyFor(target)?.AllowInternalCalls == false)
            {
                await SendResponseAsync(invite, remote, 403, "Internal Calls Not Permitted", serverCt).ConfigureAwait(false);
                return;
            }
            await HandleExtensionInviteAsync(invite, remote, extension, target, serverCt).ConfigureAwait(false);
            return;
        }
        var number = ExtractDialedNumber(invite.RequestUri);
        if (string.IsNullOrWhiteSpace(number))
        {
            await SendResponseAsync(invite, remote, 484, "Address Incomplete", serverCt).ConfigureAwait(false);
            return;
        }
        string? line;
        try { (number, line) = _options.ResolveDestination(number, invite.GetHeader("X-VoWin-Line"), extension, fromPeer: fromPeer); }
        catch (SipGatewayLineRoutingException ex)
        {
            await SendResponseAsync(invite, remote, ex.StatusCode, ex.Message, serverCt).ConfigureAwait(false);
            return;
        }
        var callId = invite.GetHeader("Call-ID") ?? Guid.NewGuid().ToString("N");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt, _lifetime.Token);
        var dialToken = linked.Token;
        var setupClock = Stopwatch.StartNew();
        var media = new RtpSession(null, localAddress: _options.BindAddress);
        media.SetRemoteEndpoint(phoneOffer!.Address, phoneOffer.Port, phoneOffer.PayloadType, phoneOffer.Codec);
        var dialog = new Dialog
        {
            LocalCallId = callId,
            Extension = extension,
            PhoneEndPoint = remote,
            Media = media,
            Cancellation = linked,
            OriginalInvite = invite,
            SlotId = line,
            RemoteTarget = ExtractContactTarget(invite.GetHeader("Contact")) ?? invite.RequestUri,
            From = invite.GetHeader("From"),
            To = EnsureTag(invite.GetHeader("To") ?? $"<sip:{number}@{_options.IdentityHost}>")
        };
        if (!_dialogs.TryAdd(callId, dialog))
        {
            dialog.Dispose();
            await SendResponseAsync(invite, remote, 482, "Loop Detected", serverCt).ConfigureAwait(false);
            return;
        }

        try
        {
            await SendResponseAsync(invite, remote, 100, "Trying", serverCt).ConfigureAwait(false);
            await SendResponseAsync(invite, remote, 180, "Ringing", serverCt, response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
            Trace($"Outbound setup accepted; line={line ?? "default"}; elapsed={setupClock.ElapsedMilliseconds}ms; identities omitted.");
            var carrier = await _controller.DialCarrierAsync(number, line, dialToken, (id, slot) =>
            {
                dialog.CarrierCallId = id;
                dialog.SlotId = slot;
                Trace($"Outbound carrier INVITE ready; line={slot ?? "default"}; elapsed={setupClock.ElapsedMilliseconds}ms.");
            }).ConfigureAwait(false);
            dialog.SlotId = carrier.SlotId;
            dialog.CarrierCallId = carrier.Call.CallId;
            if (line is not null && !string.Equals(line, carrier.SlotId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Carrier controller changed the explicitly selected module.");
            dialToken.ThrowIfCancellationRequested();
            AttachMedia(dialog, carrier.Media);
            var sdp = SipGatewaySdp.BuildOffer(_options.BindAddress, media.LocalPort, media.LocalRtcpPort, phoneOffer.PayloadType);
            lock (dialog.InviteFinalGate)
            {
                dialToken.ThrowIfCancellationRequested();
                if (Interlocked.CompareExchange(ref dialog.InviteFinalSent, 1, 0) != 0)
                    throw new OperationCanceledException("Phone INVITE already terminated.");
            }
            await SendResponseAsync(invite, remote, 200, "OK", serverCt, response =>
            {
                response.SetHeader("To", dialog.To!);
                response.SetHeader("Contact", $"<sip:gateway@{FormatHost(_options.BindAddress)}:{_options.SipPort};transport=udp>");
                response.SetHeader("Content-Type", "application/sdp");
                response.Body = sdp;
            }).ConfigureAwait(false);
            if (_serverResponses.TryGetValue(ServerTransactionKey(invite, remote), out var finalResponse))
                _ = Task.Run(() => RetransmitInviteFinalUntilAckAsync(dialog, finalResponse.Response));
            RaiseStatus();
        }
        catch (OperationCanceledException)
        {
            try { await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false); }
            catch (Exception ex) { Trace($"Carrier hangup after CANCEL failed: {ex.Message}"); }
            try
            {
                if (Interlocked.CompareExchange(ref dialog.InviteFinalSent, 2, 0) == 0)
                    await SendResponseAsync(invite, remote, 487, "Request Terminated", CancellationToken.None,
                        response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
            }
            finally { RemoveDialog(callId); }
        }
        catch (Exception ex)
        {
            try { await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false); }
            catch (Exception hangupError) { Trace($"Carrier hangup after bridge failure failed: {hangupError.Message}"); }
            Trace($"Outbound bridge call failed: {ex.Message}");
            try
            {
                if (Interlocked.CompareExchange(ref dialog.InviteFinalSent, 2, 0) == 0)
                    await SendResponseAsync(invite, remote, 503, "Carrier Line Unavailable", CancellationToken.None,
                        response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
            }
            finally { RemoveDialog(callId); }
        }
    }

    private async Task HandleCancelAsync(SipMessage cancel, IPEndPoint remote, CancellationToken ct)
    {
        var callId = cancel.GetHeader("Call-ID");
        if (callId == null || !_dialogs.TryGetValue(callId, out var dialog) ||
            dialog.OriginalInvite == null ||
            !MatchesCancelTransaction(dialog.OriginalInvite, dialog.PhoneEndPoint, cancel, remote))
        {
            await SendResponseAsync(cancel, remote, 481, "Call/Transaction Does Not Exist", ct).ConfigureAwait(false);
            return;
        }

        await SendResponseAsync(cancel, remote, 200, "OK", ct).ConfigureAwait(false);
        bool terminate;
        lock (dialog.InviteFinalGate)
        {
            terminate = Interlocked.CompareExchange(ref dialog.InviteFinalSent, 2, 0) == 0;
        }
        if (!terminate) return; // A final response won: CANCEL has no effect.
        // End local ownership before publishing the final response. A slow
        // carrier hangup must not leave a dead dialling dialog visible after
        // the phone has received 487. The captured dialog still owns cleanup;
        // a late successful Dial is released through its same once-only guard.
        RemoveDialog(callId);
        // Do not wait for carrier cleanup to terminate the phone transaction.
        try
        {
            await SendResponseAsync(dialog.OriginalInvite, remote, 487, "Request Terminated", ct,
                response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
        }
        finally
        {
            // A failed phone response must not skip carrier cleanup. Before
            // Dial returns, its cancellation path owns any still-unknown leg;
            // null must never fall back to another modem.
            try { await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false); }
            catch (Exception ex) { Trace($"Carrier hangup after SIP CANCEL failed: {ex.Message}"); }
        }
    }

    private void HandleAck(SipMessage ack, IPEndPoint remote)
    {
        var callId = ack.GetHeader("Call-ID");
        if (callId == null || !_dialogs.TryGetValue(callId, out var dialog) ||
            dialog.OriginalInvite == null || !dialog.PhoneEndPoint.Equals(remote) ||
            Volatile.Read(ref dialog.InviteFinalSent) != 1 ||
            !MatchesDialogTags(dialog.From, dialog.To, true, ack)) return;
        var inviteSequence = dialog.OriginalInvite.GetHeader("CSeq")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var ackParts = ack.GetHeader("CSeq")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ackParts is not { Length: 2 } ||
            !string.Equals(ackParts[0], inviteSequence, StringComparison.Ordinal) ||
            !string.Equals(ackParts[1], "ACK", StringComparison.OrdinalIgnoreCase)) return;
        dialog.InviteAcknowledged.TrySetResult();
    }

    private async Task RetransmitInviteFinalUntilAckAsync(Dialog dialog, SipMessage response)
    {
        CancellationToken dialogToken;
        try { dialogToken = dialog.Cancellation.Token; }
        catch (ObjectDisposedException) { return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(dialogToken);
        timeout.CancelAfter(InviteAckTimeout);
        var intervalMs = 500;
        try
        {
            while (!dialog.InviteAcknowledged.Task.IsCompleted)
            {
                var delay = Task.Delay(intervalMs, timeout.Token);
                if (await Task.WhenAny(dialog.InviteAcknowledged.Task, delay).ConfigureAwait(false) == dialog.InviteAcknowledged.Task)
                    return;
                await delay.ConfigureAwait(false);
                if (dialog.InviteAcknowledged.Task.IsCompleted) return;
                await SendAsync(response, dialog.PhoneEndPoint, timeout.Token).ConfigureAwait(false);
                intervalMs = Math.Min(intervalMs * 2, 4000);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        catch (ObjectDisposedException) { return; }
        catch (Exception ex) { Trace($"SIP INVITE final retransmission failed: {ex.Message}"); }

        if (dialogToken.IsCancellationRequested || dialog.InviteAcknowledged.Task.IsCompleted) return;
        if (!_dialogs.TryGetValue(dialog.LocalCallId, out var active) || !ReferenceEquals(active, dialog)) return;
        Trace($"SIP INVITE ACK timed out for {dialog.LocalCallId}; clearing carrier call.");
        // As in the Rust call actor, local state/media must be idle before a
        // slow carrier cleanup can complete or re-enter through CallEnded.
        RemoveDialog(dialog.LocalCallId);
        try { await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false); }
        catch (Exception ex) { Trace($"Carrier hangup after SIP ACK timeout failed: {ex.Message}"); }
    }

    private async Task HangupDialogCarrierOnceAsync(Dialog dialog)
    {
        if (dialog.PairedDialogId is { } pairedId && _dialogs.TryGetValue(pairedId, out var paired))
        {
            try
            {
                if (paired.OriginalInvite == null) await SendAckAsync(paired, CancellationToken.None).ConfigureAwait(false);
                await SendByeAsync(paired, CancellationToken.None).ConfigureAwait(false);
            }
            finally { RemoveDialog(pairedId); }
            return;
        }
        if (dialog.CarrierCallId == null) return;
        await HangupCarrierOnceAsync(dialog.CarrierCallId, dialog.SlotId, dialog.CarrierRelease).ConfigureAwait(false);
    }

    private async Task HangupCarrierOnceAsync(string callId, string? slotId, CarrierRelease release)
    {
        if (Interlocked.Exchange(ref release.Started, 1) != 0) return;
        try
        {
            await _controller.HangupCarrierCallAsync(callId, slotId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref release.Started, 0);
            throw;
        }
    }

    internal static bool MatchesCancelTransaction(SipMessage invite, IPEndPoint inviteRemote, SipMessage cancel, IPEndPoint cancelRemote)
    {
        static string Branch(SipMessage message) => Regex.Match(
            message.GetHeader("Via") ?? string.Empty,
            @"(?:^|;)\s*branch\s*=\s*([^;\s,]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        static string Sequence(SipMessage message) =>
            message.GetHeader("CSeq")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        return inviteRemote.Equals(cancelRemote) &&
            string.Equals(cancel.Method, "CANCEL", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(Branch(invite)) &&
            string.Equals(Branch(invite), Branch(cancel), StringComparison.Ordinal) &&
            string.Equals(Sequence(invite), Sequence(cancel), StringComparison.Ordinal) &&
            string.Equals(invite.GetHeader("Call-ID"), cancel.GetHeader("Call-ID"), StringComparison.Ordinal) &&
            string.Equals(invite.GetHeader("From"), cancel.GetHeader("From"), StringComparison.Ordinal) &&
            string.Equals(invite.GetHeader("To"), cancel.GetHeader("To"), StringComparison.Ordinal);
    }

    private async Task HandleByeAsync(SipMessage bye, IPEndPoint remote, CancellationToken ct)
    {
        var callId = bye.GetHeader("Call-ID");
        if (callId == null || !_dialogs.TryGetValue(callId, out var dialog) ||
            !dialog.PhoneEndPoint.Equals(remote) || dialog.CarrierCallId == null && dialog.PairedDialogId == null ||
            (dialog.OriginalInvite != null && Volatile.Read(ref dialog.InviteFinalSent) == 0) ||
            !MatchesDialogTags(dialog.From, dialog.To, dialog.OriginalInvite != null, bye))
        {
            await SendResponseAsync(bye, remote, 481, "Call/Transaction Does Not Exist", ct).ConfigureAwait(false);
            return;
        }
        Volatile.Write(ref dialog.PeerEnded, 1);
        await SendResponseAsync(bye, remote, 200, "OK", ct).ConfigureAwait(false);
        RemoveDialog(callId);
        await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false);
    }

    private async Task HandleInfoAsync(SipMessage info, IPEndPoint remote, CancellationToken ct)
    {
        var callId = info.GetHeader("Call-ID");
        if (callId == null || !_dialogs.TryGetValue(callId, out var dialog) ||
            !dialog.PhoneEndPoint.Equals(remote) || dialog.CarrierCallId == null ||
            (dialog.OriginalInvite != null && Volatile.Read(ref dialog.InviteFinalSent) == 0) ||
            !MatchesDialogTags(dialog.From, dialog.To, dialog.OriginalInvite != null, info))
        {
            await SendResponseAsync(info, remote, 481, "Call/Transaction Does Not Exist", ct).ConfigureAwait(false);
            return;
        }
        var match = Regex.Match(info.Body ?? string.Empty, @"(?im)^Signal\s*=\s*([0-9A-D#*])");
        if (!match.Success)
        {
            await SendResponseAsync(info, remote, 415, "Unsupported Media Type", ct).ConfigureAwait(false);
            return;
        }
        await _controller.SendDtmfAsync(match.Groups[1].Value[0], dialog.SlotId, ct).ConfigureAwait(false);
        await SendResponseAsync(info, remote, 200, "OK", ct).ConfigureAwait(false);
    }

    internal static bool MatchesDialogTags(string? originalFrom, string? originalTo, bool phoneInitiated, SipMessage request)
    {
        static string Tag(string? header) => Regex.Match(header ?? string.Empty,
            @"(?:^|;)\s*tag\s*=\s*([^;>\s]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        var expectedFrom = Tag(phoneInitiated ? originalFrom : originalTo);
        var expectedTo = Tag(phoneInitiated ? originalTo : originalFrom);
        return expectedFrom.Length > 0 && expectedTo.Length > 0 &&
            string.Equals(expectedFrom, Tag(request.GetHeader("From")), StringComparison.Ordinal) &&
            string.Equals(expectedTo, Tag(request.GetHeader("To")), StringComparison.Ordinal);
    }

    private void OnCarrierIncomingCall(object? sender, IncomingCallEventArgs e)
    {
        if (!RoutesIncomingCall(e.SlotId)) return;
        var key = IncomingKey(e.CallId, e.SlotId);
        lock (_forkGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _incomingForks.ContainsKey(key) ||
                _dialogs.Values.Any(dialog => dialog.CarrierCallId == e.CallId && dialog.SlotId == e.SlotId)) return;
            var fork = new IncomingFork(e, _lifetime.Token);
            _incomingForks.Add(key, fork);
            fork.Task = Task.Run(async () =>
            {
                try { await ForkIncomingCallAsync(fork, fork.Cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Trace($"Incoming SIP bridge failed: {ex.GetType().Name}."); }
                finally
                {
                    lock (_forkGate) { _incomingForks.Remove(key); fork.Cancellation.Dispose(); }
                }
            });
        }
    }

    private static string IncomingKey(string callId, string? slotId) => $"{slotId}\0{callId}";

    private async Task ForkIncomingCallAsync(IncomingFork fork, CancellationToken ct)
    {
        var incoming = fork.Call;
        var bindings = GetIncomingBindings(incoming.SlotId);
        if (bindings.Count == 0) { Trace($"Incoming carrier call from {incoming.CallerNumber}: no SIP extensions are registered."); return; }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var branches = bindings.Select(binding => InviteBindingAsync(binding, incoming, race.Token)).ToList();
        while (branches.Count > 0)
        {
            var completed = await Task.WhenAny(branches.Cast<Task>().Append(fork.LocalPickup.Task)).ConfigureAwait(false);
            if (completed == fork.LocalPickup.Task)
            {
                race.Cancel();
                await CleanupLosingBranchesAsync(branches).ConfigureAwait(false);
                // Keep the ownership record through the local call. A duplicate
                // incoming event must not start a new fork after desktop pickup.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return;
            }
            var completedBranch = (Task<Dialog?>)completed;
            branches.Remove(completedBranch);
            var winner = await completedBranch.ConfigureAwait(false);
            if (winner == null) continue;
            var sipWon = false;
            lock (_forkGate)
            {
                if (fork.PickupOwner == 0 && !fork.LocalPickupEverClaimed && !ct.IsCancellationRequested)
                {
                    fork.PickupOwner = 2;
                    sipWon = true;
                }
            }
            if (!sipWon)
            {
                race.Cancel();
                await CleanupLosingBranchesAsync(branches.Prepend(Task.FromResult<Dialog?>(winner))).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return;
            }
            race.Cancel();
            _ = CleanupLosingBranchesAsync(branches);
            var ackSent = false;
            // A fork and its promoted dialog can coexist during shutdown. Both
            // must share the same carrier-release owner, including failure cleanup.
            winner.CarrierRelease = fork.CarrierRelease;
            winner.SlotId = incoming.SlotId;
            winner.CarrierCallId = incoming.CallId;
            // The peer dialog already exists after its 200. Accept BYE even while
            // the operator-side 200/ACK handshake is still in flight.
            using var answering = CancellationTokenSource.CreateLinkedTokenSource(ct, winner.Cancellation.Token);
            _dialogs[winner.LocalCallId] = winner;
            RaiseIncomingClaimed(incoming.CallId, incoming.SlotId, isLocal: false);
            try
            {
                answering.Token.ThrowIfCancellationRequested();
                await SendAckAsync(winner, answering.Token).ConfigureAwait(false);
                ackSent = true;
                var carrier = await _controller.AnswerCarrierCallAsync(incoming.CallId, incoming.SlotId, answering.Token).ConfigureAwait(false);
                answering.Token.ThrowIfCancellationRequested();
                winner.SlotId = carrier.SlotId;
                winner.CarrierCallId = carrier.Call.CallId;
                AttachMedia(winner, carrier.Media);
                RaiseStatus();
                return;
            }
            catch (Exception ex)
            {
                Trace($"Could not answer incoming carrier call through SIP endpoint: {ex.Message}");
                try
                {
                    if (!ackSent) await SendAckAsync(winner, CancellationToken.None).ConfigureAwait(false);
                    if (Volatile.Read(ref winner.PeerEnded) == 0) await SendByeAsync(winner, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception byeError) { Trace($"Could not terminate accepted SIP fork: {byeError.Message}"); }
                try
                {
                    if (!ct.IsCancellationRequested)
                        await HangupDialogCarrierOnceAsync(winner).ConfigureAwait(false);
                }
                finally { RemoveDialog(winner.LocalCallId); }
                return;
            }
        }
        if (_options.LocalIncomingEnabled && !ct.IsCancellationRequested)
        {
            Trace($"SIP incoming branches ended on line {incoming.SlotId ?? "fallback"}; desktop is still ringing.");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return;
        }
        if (!ct.IsCancellationRequested)
            await HangupCarrierOnceAsync(incoming.CallId, incoming.SlotId, fork.CarrierRelease).ConfigureAwait(false);
    }

    private async Task<Dialog?> InviteBindingAsync(SipGatewayRegistration binding, IncomingCallEventArgs incoming, CancellationToken ct)
    {
        // The whole logical branch has one budget, including challenge ACK and
        // authenticated retry. A peer cannot renew it by returning a late 407.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(InviteTransactionTimeout);
        var branchToken = deadline.Token;
        var media = new RtpSession(null, localAddress: _options.BindAddress);
        var callId = Guid.NewGuid().ToString("N") + "@" + _options.IdentityHost;
        var fromTag = Guid.NewGuid().ToString("N")[..10];
        var invite = new SipMessage
        {
            IsRequest = true,
            Method = "INVITE",
            RequestUri = ExtractContactTarget(binding.Contact) ?? $"sip:{binding.Extension}@{binding.RemoteEndPoint.Address}:{binding.RemoteEndPoint.Port}",
            SipVersion = "SIP/2.0"
        };
        invite.SetHeader("Via", $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
        invite.SetHeader("Max-Forwards", "70");
        invite.SetHeader("From", $"<sip:{SanitizeUser(incoming.CallerNumber)}@{_options.IdentityHost}>;tag={fromTag}");
        invite.SetHeader("To", $"<sip:{binding.Extension}@{_options.IdentityHost}>");
        invite.SetHeader("Call-ID", callId);
        invite.SetHeader("CSeq", "1 INVITE");
        invite.SetHeader("Contact", $"<sip:gateway@{FormatHost(_options.BindAddress)}:{_options.SipPort};transport=udp>");
        invite.SetHeader("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, INFO");
        if (_options.Peer is not null && incoming.SlotId is { } incomingLine &&
            Regex.IsMatch(incomingLine, @"^[A-Za-z0-9_.:-]{1,128}$"))
            invite.SetHeader("X-VoWin-Line", incomingLine);
        invite.SetHeader("Content-Type", "application/sdp");
        invite.Body = SipGatewaySdp.BuildOffer(_options.BindAddress, media.LocalPort, media.LocalRtcpPort, offerBoth: true);
        var transactionKey = TransactionKey(invite);
        LateInvite? late = null;

        try
        {
            branchToken.ThrowIfCancellationRequested();
            late = TrackLateInvite(invite, binding.RemoteEndPoint, previous: null);
            var response = await SendRequestFinalAsync(invite, binding.RemoteEndPoint, branchToken, InviteTransactionTimeout).ConfigureAwait(false);
            // One authenticated retry only. Credentials never follow redirects or another source endpoint.
            if (response.StatusCode is 401 or 407 && _options.Peer is { } authPeer && authPeer.EndPoint.Equals(binding.RemoteEndPoint))
            {
                await SendLateInviteAckByeAsync(late, response, false).ConfigureAwait(false);
                branchToken.ThrowIfCancellationRequested();
                var authorizationName = response.StatusCode == 401 ? "Authorization" : "Proxy-Authorization";
                var authorization = SipPeerDigest.CreatePreferred(response, invite, authPeer);
                if (authorization != null)
                {
                    // Retain the rejected transaction to ACK retransmitted challenges.
                    RetireLateInvite(late);
                    invite = SipMessage.Parse(invite.ToBytes());
                    invite.SetHeader("Via", $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
                    invite.SetHeader("CSeq", "2 INVITE");
                    invite.SetHeader(authorizationName, authorization);
                    transactionKey = TransactionKey(invite);
                    branchToken.ThrowIfCancellationRequested();
                    late = TrackLateInvite(invite, binding.RemoteEndPoint, previous: late);
                    response = await SendRequestFinalAsync(invite, binding.RemoteEndPoint, branchToken, InviteTransactionTimeout).ConfigureAwait(false);
                }
                else Trace("SIP peer returned an unsupported Digest challenge.");
            }
            if (response.StatusCode is < 200 or >= 300 ||
                !SipGatewaySdp.TryParseAudioOffer(response.Body, binding.RemoteEndPoint.Address, _options.TrustClientSdpAddress, out var answer))
            {
                if (response.StatusCode >= 200)
                    AbandonLateInvite(transactionKey, late);
                else
                    _lateInvites.TryRemove(transactionKey, out _);
                media.Dispose();
                return null;
            }
            branchToken.ThrowIfCancellationRequested();
            media.SetRemoteEndpoint(answer!.Address, answer.Port, answer.PayloadType, answer.Codec);
            var dialog = new Dialog
            {
                LocalCallId = callId,
                Extension = binding.Extension,
                PhoneEndPoint = binding.RemoteEndPoint,
                Media = media,
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),
                RemoteTarget = ExtractContactTarget(response.GetHeader("Contact")) ?? invite.RequestUri,
                From = invite.GetHeader("From"),
                To = response.GetHeader("To") ?? invite.GetHeader("To"),
                InviteCSeq = int.Parse(invite.GetHeader("CSeq")!.Split(' ')[0]),
                LocalCSeq = int.Parse(invite.GetHeader("CSeq")!.Split(' ')[0]) + 1
            };
            // Acceptance is irreversible for this dialog family: commit only
            // after all media/dialog construction that can fail has succeeded.
            if (branchToken.IsCancellationRequested || !TryAcceptLateInvite(late, response))
            {
                dialog.Dispose();
                AbandonLateInvite(transactionKey, late);
                return null;
            }
            return dialog;
        }
        catch (OperationCanceledException)
        {
            if (late != null)
            {
                AbandonLateInvite(transactionKey, late);
                await SendCancelForInviteAsync(invite, binding.RemoteEndPoint).ConfigureAwait(false);
            }
            media.Dispose();
            return null;
        }
        catch (TimeoutException)
        {
            if (late != null)
            {
                AbandonLateInvite(transactionKey, late);
                await SendCancelForInviteAsync(invite, binding.RemoteEndPoint).ConfigureAwait(false);
            }
            media.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            if (late != null) AbandonLateInvite(transactionKey, late);
            Trace($"SIP fork branch to {binding.RemoteEndPoint} failed: {ex.Message}");
            media.Dispose();
            return null;
        }
    }

    private async Task CleanupLosingBranchesAsync(IEnumerable<Task<Dialog?>> branches)
    {
        foreach (var task in branches)
        {
            Dialog? dialog = null;
            try
            {
                dialog = await task.ConfigureAwait(false);
                if (dialog == null) continue;
                // A simultaneous 200 OK must be ACKed before the losing dialog is
                // terminated; CANCEL cannot tear down an already accepted branch.
                await SendAckAsync(dialog, CancellationToken.None).ConfigureAwait(false);
                await SendByeAsync(dialog, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            finally { dialog?.Dispose(); }
        }
    }

    private async Task SendCancelForInviteAsync(SipMessage invite, IPEndPoint remote)
    {
        try
        {
            var cancel = new SipMessage { IsRequest = true, Method = "CANCEL", RequestUri = invite.RequestUri };
            cancel.SetHeader("Via", invite.GetHeader("Via") ?? string.Empty);
            cancel.SetHeader("Max-Forwards", invite.GetHeader("Max-Forwards") ?? "70");
            cancel.SetHeader("From", invite.GetHeader("From") ?? string.Empty);
            cancel.SetHeader("To", invite.GetHeader("To") ?? string.Empty);
            cancel.SetHeader("Call-ID", invite.GetHeader("Call-ID") ?? string.Empty);
            var cseq = invite.GetHeader("CSeq")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "1";
            cancel.SetHeader("CSeq", $"{cseq} CANCEL");
            await SendAsync(cancel, remote, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private void AttachMedia(Dialog dialog, ICallPcmMedia carrier)
    {
        lock (dialog)
        {
            ObjectDisposedException.ThrowIf(dialog.IsDisposed, dialog);
            dialog.CarrierMedia = carrier;
            dialog.DownlinkHandler = dialog.Media.SendAudioPcm;
            carrier.RemotePcmReceived += dialog.DownlinkHandler;
            dialog.Media.OnAudioDecoded = carrier.SendExternalPcm;
            dialog.Media.OnDtmfReceived = (digit, durationMs) =>
            {
                _ = durationMs;
                _ = _controller.SendDtmfAsync(digit, dialog.SlotId, CancellationToken.None);
            };
        }
    }

    private async Task SendAckAsync(Dialog dialog, CancellationToken ct)
    {
        var ack = new SipMessage { IsRequest = true, Method = "ACK", RequestUri = dialog.RemoteTarget ?? $"sip:{dialog.Extension}@{_options.IdentityHost}" };
        FillDialogHeaders(ack, dialog, dialog.InviteCSeq, "ACK");
        await SendAsync(ack, dialog.PhoneEndPoint, ct).ConfigureAwait(false);
    }

    private async Task SendByeAsync(Dialog dialog, CancellationToken ct)
    {
        if (Volatile.Read(ref dialog.PeerEnded) != 0 || Interlocked.Exchange(ref dialog.ByeStarted, 1) != 0) return;
        var bye = new SipMessage { IsRequest = true, Method = "BYE", RequestUri = dialog.RemoteTarget ?? $"sip:{dialog.Extension}@{_options.IdentityHost}" };
        FillDialogHeaders(bye, dialog, dialog.LocalCSeq++, "BYE");
        try { await SendByeRequestWithAuthAsync(bye, dialog.PhoneEndPoint, ct).ConfigureAwait(false); }
        catch { }
    }

    private async Task SendByeRequestWithAuthAsync(SipMessage bye, IPEndPoint remote, CancellationToken ct)
    {
        // Keep the original four-second total cleanup budget; an authentication
        // challenge must not double shutdown/hangup latency or replay a call.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var response = await SendRequestFinalAsync(bye, remote, deadline.Token, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        if (_options.Peer is not { } peer || !peer.EndPoint.Equals(remote)) return;
        var authorization = SipPeerDigest.CreatePreferred(response, bye, peer);
        if (authorization == null) return;
        var sequence = int.Parse(bye.GetHeader("CSeq")!.Split(' ')[0]);
        bye = SipMessage.Parse(bye.ToBytes());
        bye.SetHeader("Via", $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
        bye.SetHeader("CSeq", $"{sequence + 1} BYE");
        bye.SetHeader(response.StatusCode == 401 ? "Authorization" : "Proxy-Authorization", authorization);
        _ = await SendRequestFinalAsync(bye, remote, deadline.Token, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
    }

    private void FillDialogHeaders(SipMessage request, Dialog dialog, int cseq, string method)
    {
        request.SetHeader("Via", $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("Max-Forwards", "70");
        // For a peer-originated dialog the local tag is in the original To.
        request.SetHeader("From", (dialog.OriginalInvite == null ? dialog.From : dialog.To) ?? $"<sip:gateway@{_options.IdentityHost}>");
        request.SetHeader("To", (dialog.OriginalInvite == null ? dialog.To : dialog.From) ?? $"<sip:{dialog.Extension}@{_options.IdentityHost}>");
        request.SetHeader("Call-ID", dialog.LocalCallId);
        request.SetHeader("CSeq", $"{cseq} {method}");
        request.SetHeader("Content-Length", "0");
    }

    private void OnCarrierCallEnded(object? sender, CallEndedEventArgs e)
    {
        lock (_forkGate)
            if (_incomingForks.TryGetValue(IncomingKey(e.CallId, e.SlotId), out var fork)) fork.Cancellation.Cancel();
        foreach (var entry in _dialogs.Where(pair =>
                     MatchesCarrierCallEnd(pair.Value.CarrierCallId, pair.Value.SlotId, e)).ToArray())
        {
            _ = Task.Run(async () =>
            {
                var dialog = entry.Value;
                if (dialog.OriginalInvite != null &&
                    Interlocked.CompareExchange(ref dialog.InviteFinalSent, 2, 0) == 0)
                {
                    RemoveDialog(entry.Key);
                    await SendResponseAsync(dialog.OriginalInvite, dialog.PhoneEndPoint, 487, "Request Terminated",
                        CancellationToken.None, response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
                }
                else
                {
                    var needsBye = (dialog.OriginalInvite == null || Volatile.Read(ref dialog.InviteFinalSent) == 1) &&
                        !dialog.Cancellation.IsCancellationRequested;
                    RemoveDialog(entry.Key);
                    if (needsBye) await SendByeAsync(dialog, CancellationToken.None).ConfigureAwait(false);
                }
            });
        }
    }

    internal static bool MatchesCarrierCallEnd(string? carrierCallId, string? slotId, CallEndedEventArgs ended) =>
        !string.IsNullOrWhiteSpace(carrierCallId) &&
        string.Equals(carrierCallId, ended.CallId, StringComparison.OrdinalIgnoreCase) &&
        (slotId == null || ended.SlotId == null || string.Equals(slotId, ended.SlotId, StringComparison.OrdinalIgnoreCase));

    private async Task<SipMessage> SendRequestFinalAsync(SipMessage request, IPEndPoint remote, CancellationToken ct, TimeSpan? timeout = null,
        Func<CancellationToken, Task<bool>>? beforeSend = null)
    {
        ct.ThrowIfCancellationRequested();
        var key = TransactionKey(request);
        var tcs = new TaskCompletionSource<SipMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingTransaction(tcs, null, remote, ViaBranch(request), request.GetHeader("Call-ID") ?? "", HeaderTag(request.GetHeader("From")), HeaderTag(request.GetHeader("To")));
        if (!_pending.TryAdd(key, pending)) throw new InvalidOperationException("Duplicate SIP client transaction.");
        try
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            var delays = new[] { 0, 500, 1000, 2000, 4000 };
            foreach (var delay in delays)
            {
                if (delay > 0) await Task.Delay(delay, timer.Token).ConfigureAwait(false);
                timer.Token.ThrowIfCancellationRequested();
                if (tcs.Task.IsCompleted) return await tcs.Task.ConfigureAwait(false);
                if (!pending.ReceivedProvisional || !request.Method.Equals("INVITE", StringComparison.OrdinalIgnoreCase))
                {
                    // MT payloads supply this guard; OPTIONS/voice/dialog
                    // requests retain their existing transport behavior. Check
                    // every datagram, including after the retransmission wait.
                    if (beforeSend != null)
                    {
                        var allowed = await beforeSend(timer.Token).ConfigureAwait(false);
                        timer.Token.ThrowIfCancellationRequested();
                        // A legitimate final reply may arrive while SQLite was
                        // awaited. Do not retransmit an already completed send.
                        if (tcs.Task.IsCompleted) return await tcs.Task.ConfigureAwait(false);
                        if (!allowed) throw new InvalidOperationException("The original SMS source is no longer available for transmission.");
                    }
                    await SendAsync(request, remote, timer.Token).ConfigureAwait(false);
                }
                var won = await Task.WhenAny(tcs.Task, Task.Delay(delay == 0 ? 500 : delay, timer.Token)).ConfigureAwait(false);
                timer.Token.ThrowIfCancellationRequested();
                if (won == tcs.Task) return await tcs.Task.ConfigureAwait(false);
            }
            var final = await tcs.Task.WaitAsync(timer.Token).ConfigureAwait(false);
            timer.Token.ThrowIfCancellationRequested();
            return final;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("SIP transaction timed out."); }
        finally { _pending.TryRemove(key, out _); }
    }

    private async Task SendResponseAsync(SipMessage request, IPEndPoint remote, int status, string reason, CancellationToken ct, Action<SipMessage>? configure = null)
    {
        var response = request.CreateResponse(status, reason);
        if (status >= 180) response.SetHeader("To", EnsureTag(response.GetHeader("To") ?? string.Empty));
        response.SetHeader("Server", "VoWin-SIP-Gateway/1.0");
        configure?.Invoke(response);
        _serverResponses[ServerTransactionKey(request, remote)] = new CachedServerResponse(response, remote, DateTimeOffset.UtcNow);
        CleanupServerTransactions();
        await SendAsync(response, remote, ct).ConfigureAwait(false);
    }

    private Task SendAsync(SipMessage message, IPEndPoint remote, CancellationToken ct) =>
        _udp!.SendAsync(message.ToBytes(), remote, ct).AsTask();

    private IReadOnlyList<SipGatewayRegistration> GetActiveRegistrations()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _registrations)
            if (entry.Value.ExpiresAt <= now) _registrations.TryRemove(entry.Key, out _);
        return _registrations.Values.OrderBy(item => item.Extension, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void RemoveDialog(string callId)
    {
        if (_dialogs.TryRemove(callId, out var dialog)) dialog.Dispose();
        RaiseStatus();
    }

    private static string TransactionKey(SipMessage message)
    {
        var cseq = message.GetHeader("CSeq")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        // Distinct fork dialogs can legitimately use the same Call-ID/CSeq
        // for their BYEs. A client transaction is also scoped to its Via
        // branch; otherwise one tag's cleanup can suppress another's BYE.
        return $"{message.GetHeader("Call-ID")}|{(cseq.Length > 0 ? cseq[0] : "0")}|{(cseq.Length > 1 ? cseq[1] : message.Method)}|{ViaBranch(message)}";
    }

    private static string ViaBranch(SipMessage message) => Regex.Match(
        message.GetHeader("Via") ?? string.Empty,
        @"(?:^|;)\s*branch\s*=\s*([^;\s,]+)", RegexOptions.IgnoreCase).Groups[1].Value;

    internal static bool ResponseMatchesRequestIdentity(SipMessage request, SipMessage response) =>
        ResponseMatchesIdentity(response, request.GetHeader("Call-ID") ?? "", HeaderTag(request.GetHeader("From")), HeaderTag(request.GetHeader("To")));

    private static bool ResponseMatchesIdentity(SipMessage response, string callId, string fromTag, string toTag)
    {
        if (response.IsRequest || response.StatusCode is < 100 or > 699) return false;
        foreach (var name in new[] { "From", "To", "Call-ID", "CSeq" })
            if (!response.Headers.TryGetValue(name, out var values) || values.Count != 1) return false;
        if (fromTag.Length == 0 || !TryHeaderTag(response.GetHeader("From"), out var responseFromTag) ||
            !TryHeaderTag(response.GetHeader("To"), out var responseToTag)) return false;
        // Call-ID and tags are case-sensitive opaque identifiers, not domains.
        // A matching socket/branch alone must not complete a different dialog.
        return string.Equals(callId, response.GetHeader("Call-ID"), StringComparison.Ordinal) &&
            string.Equals(fromTag, responseFromTag, StringComparison.Ordinal) &&
            (toTag.Length == 0 || string.Equals(toTag, responseToTag, StringComparison.Ordinal));
    }

    private static string HeaderTag(string? value) => TryHeaderTag(value, out var tag) ? tag : "";

    private static bool TryHeaderTag(string? value, out string tag)
    {
        tag = "";
        value ??= "";
        var addressEnd = value.LastIndexOf('>');
        if (addressEnd >= 0) value = value[(addressEnd + 1)..]; // exclude quoted display names and URI parameters
        var count = Regex.Matches(value, @"(?:^|;)\s*tag\s*=", RegexOptions.IgnoreCase).Count;
        if (count == 0) return true; // an initial transaction may establish a new To tag
        if (count != 1) return false;
        tag = Regex.Match(value, @"(?:^|;)\s*tag\s*=\s*([^;\s,]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        return tag.Length != 0;
    }

    private static string ServerTransactionKey(SipMessage request, IPEndPoint remote)
    {
        var via = request.GetHeader("Via") ?? string.Empty;
        var branch = Regex.Match(via, @"(?:^|;)\s*branch\s*=\s*([^;\s,]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        return $"{remote}|{branch}|{request.GetHeader("Call-ID")}|{request.GetHeader("CSeq")}|{request.Method}";
    }

    private void CleanupServerTransactions()
    {
        if (_serverResponses.Count < 256) return;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        foreach (var entry in _serverResponses)
            if (entry.Value.CreatedAt < cutoff) _serverResponses.TryRemove(entry.Key, out _);
    }

    private static int ParseExpires(SipMessage request, string contact)
    {
        var contactExpires = Regex.Match(contact, @"(?:^|;)\s*expires\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        if (contactExpires.Success && int.TryParse(contactExpires.Groups[1].Value, out var parsed)) return parsed;
        return int.TryParse(request.GetHeader("Expires"), out parsed) ? Math.Max(0, parsed) : 600;
    }

    private static string ExtractUriUser(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"(?:sip:|tel:)([^@;>]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private string ExtractDialedNumber(string value)
    {
        var user = ExtractUriUser(value).Trim();
        return _options.IsPlausibleCarrierDestination(user) ? user : string.Empty;
    }

    private static string? ExtractContactTarget(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"<\s*(sip:[^>]+)\s*>", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string EnsureTag(string value) => Regex.IsMatch(value, @"(?:^|;)\s*tag=", RegexOptions.IgnoreCase)
        ? value
        : value + ";tag=" + Guid.NewGuid().ToString("N")[..10];

    private static string RegistrationKey(string extension, IPEndPoint endpoint) => $"{extension}|{endpoint.Address}|{endpoint.Port}";
    private static string FormatHost(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    private static string SanitizeUser(string value) => Regex.Replace(value, @"[^+0-9A-Za-z_.-]", string.Empty);

    private void Trace(string message) { try { Diagnostic?.Invoke(this, message); } catch { } }
    private void RaiseStatus() { try { StatusChanged?.Invoke(this, Status); } catch { } }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _started, 0);
        _controller.IncomingCall -= OnCarrierIncomingCall;
        _controller.CallEnded -= OnCarrierCallEnded;
        if (_controller is ISipGatewayMessageController messages) messages.SmsReceived -= OnCarrierSms;
        IncomingFork[] forks;
        lock (_forkGate) forks = _incomingForks.Values.ToArray();
        _lifetime.Cancel();
        if (_smsForwardTask != null) try { await _smsForwardTask.ConfigureAwait(false); } catch { }
        try { await Task.WhenAll(forks.Select(fork => fork.Task)).ConfigureAwait(false); } catch { }
        foreach (var fork in forks)
            if (!_options.LocalIncomingEnabled || fork.PickupOwner == 2)
                try { await HangupCarrierOnceAsync(fork.Call.CallId, fork.Call.SlotId, fork.CarrierRelease).ConfigureAwait(false); } catch { }
        foreach (var dialog in _dialogs.Values.ToArray())
        {
            await SendByeAsync(dialog, CancellationToken.None).ConfigureAwait(false);
            try { await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false); } catch { }
        }
        _receiveLifetime.Cancel(); // Keep receiving transaction responses through BYE cleanup.
        _udp?.Dispose();
        if (_receiveTask != null) try { await _receiveTask.ConfigureAwait(false); } catch { }
        foreach (var callId in _dialogs.Keys.ToArray()) RemoveDialog(callId);
        _pending.Clear();
        _lateInvites.Clear();
        _serverProcessing.Clear();
        _serverResponses.Clear();
        _registrations.Clear();
        _udp = null;
        _localEndPoint = null;
        Interlocked.Exchange(ref _ownedController, null)?.Dispose();
        _lifetime.Dispose();
        _receiveLifetime.Dispose();
        RaiseStatus();
    }
}
