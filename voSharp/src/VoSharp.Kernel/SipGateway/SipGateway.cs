using System.Collections.Concurrent;
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
public sealed class SipGateway : IAsyncDisposable
{
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
        public string? RemoteTarget { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public SipMessage? OriginalInvite { get; set; }
        public int InviteFinalSent;
        public int CarrierHangupStarted;
        public TaskCompletionSource InviteAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LocalCSeq { get; set; } = 2;
        public Action<short[]>? DownlinkHandler { get; set; }

        public void Dispose()
        {
            try { Cancellation.Cancel(); } catch { }
            if (CarrierMedia != null && DownlinkHandler != null)
                CarrierMedia.RemotePcmReceived -= DownlinkHandler;
            if (CarrierMedia is ImsCallManager ims) ims.ExternalMediaBridgeEnabled = false;
            Media.Dispose();
            Cancellation.Dispose();
        }
    }

    private sealed record PendingTransaction(TaskCompletionSource<SipMessage> Completion, Action<SipMessage>? Provisional, IPEndPoint Remote, string ViaBranch)
    {
        public volatile bool ReceivedProvisional;
    }
    private sealed record CachedServerResponse(SipMessage Response, IPEndPoint Remote, DateTimeOffset CreatedAt);
    private sealed class LateInvite(SipMessage invite, IPEndPoint remote)
    {
        public SipMessage Invite { get; } = invite;
        public IPEndPoint Remote { get; } = remote;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public SipMessage? FinalResponse { get; set; }
        public bool Abandoned { get; set; }
        public bool ByeSent { get; set; }
    }

    private readonly SipGatewayOptions _options;
    private readonly ISipGatewayCallController _controller;
    private IDisposable? _ownedController;
    private readonly SipDigestAuthenticator _authenticator;
    private readonly ConcurrentDictionary<string, SipGatewayRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingTransaction> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LateInvite> _lateInvites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _serverProcessing = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedServerResponse> _serverResponses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dialog> _dialogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private UdpClient? _udp;
    private Task? _receiveTask;
    private int _started;
    private int _disposed;

    internal TimeSpan InviteTransactionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan InviteAckTimeout { get; set; } = TimeSpan.FromSeconds(32);

    public SipGateway(SipGatewayOptions options, ISipGatewayCallController controller)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _authenticator = new SipDigestAuthenticator(options.Realm, options.Accounts);
    }

    public SipGateway(SipGatewayOptions options, IVoKernel kernel)
        : this(options, new KernelSipGatewayCallController(kernel))
    {
        _ownedController = (IDisposable)_controller;
    }

    public event EventHandler<SipGatewayStatus>? StatusChanged;
    public event EventHandler<string>? Diagnostic;

    public SipGatewayStatus Status => new(
        _udp != null,
        _udp?.Client.LocalEndPoint as IPEndPoint,
        GetActiveRegistrations(),
        _dialogs.Count);

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        try
        {
            _udp = new UdpClient(new IPEndPoint(_options.BindAddress, _options.SipPort));
            _controller.IncomingCall += OnCarrierIncomingCall;
            _controller.CallEnded += OnCarrierCallEnded;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));
            RaiseStatus();
            Trace($"SIP gateway listening on {_options.BindAddress}:{_options.SipPort} (private/WireGuard mode).");
            return Task.CompletedTask;
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            _udp?.Dispose();
            _udp = null;
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
                SipMessage message;
                try { message = SipMessage.Parse(packet.Buffer); }
                catch (Exception ex) { Trace($"Dropped malformed SIP datagram from {packet.RemoteEndPoint}: {ex.Message}"); continue; }

                if (!message.IsRequest)
                {
                    var transactionKey = TransactionKey(message);
                    if (_lateInvites.TryGetValue(transactionKey, out var late) &&
                        late.Remote.Equals(packet.RemoteEndPoint) &&
                        string.Equals(ViaBranch(late.Invite), ViaBranch(message), StringComparison.Ordinal) &&
                        message.StatusCode >= 200)
                    {
                        bool acknowledge;
                        bool sendBye;
                        lock (late)
                        {
                            late.FinalResponse = message;
                            acknowledge = late.Abandoned && message.StatusCode >= 200;
                            sendBye = acknowledge && message.StatusCode < 300 && !late.ByeSent;
                            if (sendBye) late.ByeSent = true;
                        }
                        if (acknowledge)
                            _ = Task.Run(() => SendLateInviteAckByeAsync(late, message, sendBye));
                    }
                    if (_pending.TryGetValue(transactionKey, out var pending) &&
                        pending.Remote.Equals(packet.RemoteEndPoint) &&
                        string.Equals(pending.ViaBranch, ViaBranch(message), StringComparison.Ordinal))
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
        switch (request.Method.ToUpperInvariant())
        {
            case "REGISTER": await HandleRegisterAsync(request, remote, ct).ConfigureAwait(false); break;
            case "OPTIONS": await SendResponseAsync(request, remote, 200, "OK", ct).ConfigureAwait(false); break;
            case "INVITE": await HandlePhoneInviteAsync(request, remote, ct).ConfigureAwait(false); break;
            case "ACK": HandleAck(request, remote); break;
            case "CANCEL": await HandleCancelAsync(request, remote, ct).ConfigureAwait(false); break;
            case "BYE": await HandleByeAsync(request, remote, ct).ConfigureAwait(false); break;
            case "INFO": await HandleInfoAsync(request, remote, ct).ConfigureAwait(false); break;
            default:
                await SendResponseAsync(request, remote, 405, "Method Not Allowed", ct, response =>
                    response.SetHeader("Allow", "REGISTER, INVITE, ACK, CANCEL, BYE, OPTIONS, INFO")).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleRegisterAsync(SipMessage request, IPEndPoint remote, CancellationToken ct)
    {
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
            await SendResponseAsync(request, remote, 400, "Missing Contact", ct).ConfigureAwait(false);
            return;
        }
        var expires = ParseExpires(request, contact);
        var key = RegistrationKey(extension, remote);
        if (expires == 0 || contact == "*")
        {
            _registrations.TryRemove(key, out _);
        }
        else
        {
            expires = Math.Clamp(expires, _options.MinimumRegistrationSeconds, _options.MaximumRegistrationSeconds);
            _registrations[key] = new SipGatewayRegistration(extension, contact, remote,
                DateTimeOffset.UtcNow.AddSeconds(expires), request.GetHeader("User-Agent") ?? string.Empty);
        }
        await SendResponseAsync(request, remote, 200, "OK", ct, response =>
        {
            if (expires > 0) response.SetHeader("Contact", $"{contact};expires={expires}");
            response.SetHeader("Expires", expires.ToString());
        }).ConfigureAwait(false);
        RaiseStatus();
    }

    private async Task HandlePhoneInviteAsync(SipMessage invite, IPEndPoint remote, CancellationToken serverCt)
    {
        if (!_authenticator.Validate(invite, out var extension))
        {
            await SendResponseAsync(invite, remote, 407, "Proxy Authentication Required", serverCt, response =>
                response.SetHeader("Proxy-Authenticate", _authenticator.CreateChallenge())).ConfigureAwait(false);
            return;
        }
        if (!SipGatewaySdp.TryParseAudioOffer(invite.Body, remote.Address, _options.TrustClientSdpAddress, out var phoneOffer))
        {
            await SendResponseAsync(invite, remote, 488, "Not Acceptable Here", serverCt).ConfigureAwait(false);
            return;
        }

        var number = ExtractDialedNumber(invite.RequestUri);
        if (string.IsNullOrWhiteSpace(number))
        {
            await SendResponseAsync(invite, remote, 484, "Address Incomplete", serverCt).ConfigureAwait(false);
            return;
        }
        var callId = invite.GetHeader("Call-ID") ?? Guid.NewGuid().ToString("N");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt, _lifetime.Token);
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
            From = invite.GetHeader("From"),
            To = EnsureTag(invite.GetHeader("To") ?? $"<sip:{number}@{_options.Realm}>")
        };
        if (!_dialogs.TryAdd(callId, dialog))
        {
            dialog.Dispose();
            await SendResponseAsync(invite, remote, 482, "Loop Detected", serverCt).ConfigureAwait(false);
            return;
        }

        await SendResponseAsync(invite, remote, 100, "Trying", serverCt).ConfigureAwait(false);
        await SendResponseAsync(invite, remote, 180, "Ringing", serverCt, response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
        try
        {
            var line = invite.GetHeader("X-VoWin-Line");
            var carrier = await _controller.DialCarrierAsync(number, line, linked.Token).ConfigureAwait(false);
            dialog.SlotId = carrier.SlotId;
            dialog.CarrierCallId = carrier.Call.CallId;
            linked.Token.ThrowIfCancellationRequested();
            AttachMedia(dialog, carrier.Media);
            var sdp = SipGatewaySdp.BuildOffer(_options.BindAddress, media.LocalPort, media.LocalRtcpPort, phoneOffer.PayloadType);
            Volatile.Write(ref dialog.InviteFinalSent, 1);
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
            Volatile.Write(ref dialog.InviteFinalSent, 1);
            await SendResponseAsync(invite, remote, 487, "Request Terminated", CancellationToken.None,
                response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
            RemoveDialog(callId);
        }
        catch (Exception ex)
        {
            try { await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false); }
            catch (Exception hangupError) { Trace($"Carrier hangup after bridge failure failed: {hangupError.Message}"); }
            Trace($"Outbound bridge call failed: {ex.Message}");
            Volatile.Write(ref dialog.InviteFinalSent, 1);
            await SendResponseAsync(invite, remote, 503, "Carrier Line Unavailable", CancellationToken.None,
                response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
            RemoveDialog(callId);
        }
    }

    private async Task HandleCancelAsync(SipMessage cancel, IPEndPoint remote, CancellationToken ct)
    {
        var callId = cancel.GetHeader("Call-ID");
        if (callId == null || !_dialogs.TryGetValue(callId, out var dialog) ||
            dialog.OriginalInvite == null ||
            Volatile.Read(ref dialog.InviteFinalSent) != 0 ||
            !MatchesCancelTransaction(dialog.OriginalInvite, dialog.PhoneEndPoint, cancel, remote))
        {
            await SendResponseAsync(cancel, remote, 481, "Call/Transaction Does Not Exist", ct).ConfigureAwait(false);
            return;
        }

        await SendResponseAsync(cancel, remote, 200, "OK", ct).ConfigureAwait(false);
        dialog.Cancellation.Cancel();
        // Before DialCarrierAsync returns, SlotId is unknown. Its cancellation
        // cleanup owns that carrier leg; null must never fall back to another slot.
        try { await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false); }
        catch (Exception ex) { Trace($"Carrier hangup after SIP CANCEL failed: {ex.Message}"); }
    }

    private void HandleAck(SipMessage ack, IPEndPoint remote)
    {
        var callId = ack.GetHeader("Call-ID");
        if (callId == null || !_dialogs.TryGetValue(callId, out var dialog) ||
            dialog.OriginalInvite == null || !dialog.PhoneEndPoint.Equals(remote) ||
            Volatile.Read(ref dialog.InviteFinalSent) == 0 ||
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
        try { await HangupDialogCarrierOnceAsync(dialog).ConfigureAwait(false); }
        catch (Exception ex) { Trace($"Carrier hangup after SIP ACK timeout failed: {ex.Message}"); }
        RemoveDialog(dialog.LocalCallId);
    }

    private async Task HangupDialogCarrierOnceAsync(Dialog dialog)
    {
        if (dialog.CarrierCallId == null || Interlocked.Exchange(ref dialog.CarrierHangupStarted, 1) != 0) return;
        try
        {
            await _controller.HangupAsync(dialog.SlotId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref dialog.CarrierHangupStarted, 0);
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
            !dialog.PhoneEndPoint.Equals(remote) || dialog.CarrierCallId == null ||
            (dialog.OriginalInvite != null && Volatile.Read(ref dialog.InviteFinalSent) == 0) ||
            !MatchesDialogTags(dialog.From, dialog.To, dialog.OriginalInvite != null, bye))
        {
            await SendResponseAsync(bye, remote, 481, "Call/Transaction Does Not Exist", ct).ConfigureAwait(false);
            return;
        }
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
        _ = Task.Run(() => ForkIncomingCallAsync(e, _lifetime.Token));
    }

    private async Task ForkIncomingCallAsync(IncomingCallEventArgs incoming, CancellationToken ct)
    {
        var bindings = GetActiveRegistrations();
        if (bindings.Count == 0) { Trace($"Incoming carrier call from {incoming.CallerNumber}: no SIP extensions are registered."); return; }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var branches = bindings.Select(binding => InviteBindingAsync(binding, incoming, race.Token)).ToList();
        while (branches.Count > 0)
        {
            var completed = await Task.WhenAny(branches).ConfigureAwait(false);
            branches.Remove(completed);
            var winner = await completed.ConfigureAwait(false);
            if (winner == null) continue;
            race.Cancel();
            _ = CleanupLosingBranchesAsync(branches);
            var ackSent = false;
            try
            {
                await SendAckAsync(winner, ct).ConfigureAwait(false);
                ackSent = true;
                var carrier = await _controller.AnswerCarrierAsync(incoming.SlotId, ct).ConfigureAwait(false);
                winner.SlotId = carrier.SlotId;
                winner.CarrierCallId = carrier.Call.CallId;
                AttachMedia(winner, carrier.Media);
                _dialogs[winner.LocalCallId] = winner;
                RaiseStatus();
                return;
            }
            catch (Exception ex)
            {
                Trace($"Could not answer incoming carrier call through SIP endpoint: {ex.Message}");
                if (ackSent)
                    try { await SendByeAsync(winner, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception byeError) { Trace($"Could not terminate accepted SIP fork: {byeError.Message}"); }
                winner.Dispose();
                await _controller.HangupAsync(incoming.SlotId, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }
        await _controller.HangupAsync(incoming.SlotId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Dialog?> InviteBindingAsync(SipGatewayRegistration binding, IncomingCallEventArgs incoming, CancellationToken ct)
    {
        var media = new RtpSession(null, localAddress: _options.BindAddress);
        var callId = Guid.NewGuid().ToString("N") + "@" + _options.Realm;
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
        invite.SetHeader("From", $"\"{incoming.DisplayName}\" <sip:{SanitizeUser(incoming.CallerNumber)}@{_options.Realm}>;tag={fromTag}");
        invite.SetHeader("To", $"<sip:{binding.Extension}@{_options.Realm}>");
        invite.SetHeader("Call-ID", callId);
        invite.SetHeader("CSeq", "1 INVITE");
        invite.SetHeader("Contact", $"<sip:gateway@{FormatHost(_options.BindAddress)}:{_options.SipPort};transport=udp>");
        invite.SetHeader("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, INFO");
        invite.SetHeader("Content-Type", "application/sdp");
        invite.Body = SipGatewaySdp.BuildOffer(_options.BindAddress, media.LocalPort, media.LocalRtcpPort);
        var transactionKey = TransactionKey(invite);
        var late = new LateInvite(invite, binding.RemoteEndPoint);
        _lateInvites[transactionKey] = late;

        try
        {
            var response = await SendRequestFinalAsync(invite, binding.RemoteEndPoint, ct, InviteTransactionTimeout).ConfigureAwait(false);
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
                To = response.GetHeader("To") ?? invite.GetHeader("To")
            };
            _lateInvites.TryRemove(transactionKey, out _);
            return dialog;
        }
        catch (OperationCanceledException)
        {
            AbandonLateInvite(transactionKey, late);
            await SendCancelForInviteAsync(invite, binding.RemoteEndPoint).ConfigureAwait(false);
            media.Dispose();
            return null;
        }
        catch (TimeoutException)
        {
            AbandonLateInvite(transactionKey, late);
            await SendCancelForInviteAsync(invite, binding.RemoteEndPoint).ConfigureAwait(false);
            media.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            AbandonLateInvite(transactionKey, late);
            Trace($"SIP fork branch to {binding.RemoteEndPoint} failed: {ex.Message}");
            media.Dispose();
            return null;
        }
    }

    private void AbandonLateInvite(string transactionKey, LateInvite late)
    {
        SipMessage? final;
        bool sendBye;
        lock (late)
        {
            late.Abandoned = true;
            final = late.FinalResponse;
            sendBye = final?.StatusCode is >= 200 and < 300 && !late.ByeSent;
            if (sendBye) late.ByeSent = true;
        }
        if (final?.StatusCode >= 200)
            _ = Task.Run(() => SendLateInviteAckByeAsync(late, final, sendBye));
    }

    private async Task SendLateInviteAckByeAsync(LateInvite late, SipMessage response, bool sendBye)
    {
        try
        {
            var accepted = response.StatusCode is >= 200 and < 300;
            var target = accepted
                ? ExtractContactTarget(response.GetHeader("Contact")) ?? late.Invite.RequestUri
                : late.Invite.RequestUri;
            var inviteCseq = int.TryParse(late.Invite.GetHeader("CSeq")?.Split(' ')[0], out var parsed)
                ? parsed : 1;
            SipMessage MakeRequest(string method, int cseq)
            {
                var request = new SipMessage { IsRequest = true, Method = method, RequestUri = target };
                request.SetHeader("Via", method == "ACK" && !accepted
                    ? late.Invite.GetHeader("Via") ?? string.Empty
                    : $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
                request.SetHeader("Max-Forwards", "70");
                request.SetHeader("From", late.Invite.GetHeader("From") ?? string.Empty);
                request.SetHeader("To", response.GetHeader("To") ?? string.Empty);
                request.SetHeader("Call-ID", late.Invite.GetHeader("Call-ID") ?? string.Empty);
                request.SetHeader("CSeq", $"{cseq} {method}");
                request.SetHeader("Content-Length", "0");
                return request;
            }

            await SendAsync(MakeRequest("ACK", inviteCseq), late.Remote, CancellationToken.None).ConfigureAwait(false);
            if (sendBye)
                _ = await SendRequestFinalAsync(MakeRequest("BYE", inviteCseq + 1), late.Remote,
                    CancellationToken.None, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (sendBye)
                lock (late) late.ByeSent = false;
            Trace($"Late SIP INVITE cleanup failed: {ex.Message}");
        }
    }

    private void CleanupLateInvites()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        foreach (var entry in _lateInvites)
            if (entry.Value.CreatedAt < cutoff) _lateInvites.TryRemove(entry.Key, out _);
    }

    private async Task CleanupLosingBranchesAsync(IEnumerable<Task<Dialog?>> branches)
    {
        foreach (var task in branches)
        {
            try
            {
                var dialog = await task.ConfigureAwait(false);
                if (dialog == null) continue;
                // A simultaneous 200 OK must be ACKed before the losing dialog is
                // terminated; CANCEL cannot tear down an already accepted branch.
                await SendAckAsync(dialog, CancellationToken.None).ConfigureAwait(false);
                await SendByeAsync(dialog, CancellationToken.None).ConfigureAwait(false);
                dialog.Dispose();
            }
            catch { }
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

    private async Task SendAckAsync(Dialog dialog, CancellationToken ct)
    {
        var ack = new SipMessage { IsRequest = true, Method = "ACK", RequestUri = dialog.RemoteTarget ?? $"sip:{dialog.Extension}@{_options.Realm}" };
        FillDialogHeaders(ack, dialog, 1, "ACK");
        await SendAsync(ack, dialog.PhoneEndPoint, ct).ConfigureAwait(false);
    }

    private async Task SendByeAsync(Dialog dialog, CancellationToken ct)
    {
        var bye = new SipMessage { IsRequest = true, Method = "BYE", RequestUri = dialog.RemoteTarget ?? $"sip:{dialog.Extension}@{_options.Realm}" };
        FillDialogHeaders(bye, dialog, dialog.LocalCSeq++, "BYE");
        try { _ = await SendRequestFinalAsync(bye, dialog.PhoneEndPoint, ct, TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
        catch { }
    }

    private void FillDialogHeaders(SipMessage request, Dialog dialog, int cseq, string method)
    {
        request.SetHeader("Via", $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("Max-Forwards", "70");
        request.SetHeader("From", dialog.From ?? $"<sip:gateway@{_options.Realm}>");
        request.SetHeader("To", dialog.To ?? $"<sip:{dialog.Extension}@{_options.Realm}>");
        request.SetHeader("Call-ID", dialog.LocalCallId);
        request.SetHeader("CSeq", $"{cseq} {method}");
        request.SetHeader("Content-Length", "0");
    }

    private void OnCarrierCallEnded(object? sender, CallEndedEventArgs e)
    {
        foreach (var entry in _dialogs.Where(pair =>
                     MatchesCarrierCallEnd(pair.Value.CarrierCallId, pair.Value.SlotId, e)).ToArray())
        {
            _ = Task.Run(async () =>
            {
                await SendByeAsync(entry.Value, CancellationToken.None).ConfigureAwait(false);
                RemoveDialog(entry.Key);
            });
        }
    }

    internal static bool MatchesCarrierCallEnd(string? carrierCallId, string? slotId, CallEndedEventArgs ended) =>
        !string.IsNullOrWhiteSpace(carrierCallId) &&
        string.Equals(carrierCallId, ended.CallId, StringComparison.OrdinalIgnoreCase) &&
        (slotId == null || ended.SlotId == null || string.Equals(slotId, ended.SlotId, StringComparison.OrdinalIgnoreCase));

    private async Task<SipMessage> SendRequestFinalAsync(SipMessage request, IPEndPoint remote, CancellationToken ct, TimeSpan? timeout = null)
    {
        var key = TransactionKey(request);
        var tcs = new TaskCompletionSource<SipMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingTransaction(tcs, null, remote, ViaBranch(request));
        if (!_pending.TryAdd(key, pending)) throw new InvalidOperationException("Duplicate SIP client transaction.");
        try
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            var delays = new[] { 0, 500, 1000, 2000, 4000 };
            foreach (var delay in delays)
            {
                if (delay > 0) await Task.Delay(delay, timer.Token).ConfigureAwait(false);
                if (tcs.Task.IsCompleted) return await tcs.Task.ConfigureAwait(false);
                if (!pending.ReceivedProvisional || !request.Method.Equals("INVITE", StringComparison.OrdinalIgnoreCase))
                    await SendAsync(request, remote, timer.Token).ConfigureAwait(false);
                var won = await Task.WhenAny(tcs.Task, Task.Delay(delay == 0 ? 500 : delay, timer.Token)).ConfigureAwait(false);
                if (won == tcs.Task) return await tcs.Task.ConfigureAwait(false);
            }
            return await tcs.Task.WaitAsync(timer.Token).ConfigureAwait(false);
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
        return $"{message.GetHeader("Call-ID")}|{(cseq.Length > 0 ? cseq[0] : "0")}|{(cseq.Length > 1 ? cseq[1] : message.Method)}";
    }

    private static string ViaBranch(SipMessage message) => Regex.Match(
        message.GetHeader("Via") ?? string.Empty,
        @"(?:^|;)\s*branch\s*=\s*([^;\s,]+)", RegexOptions.IgnoreCase).Groups[1].Value;

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

    private static string ExtractDialedNumber(string value)
    {
        var user = ExtractUriUser(value).Trim();
        return Regex.IsMatch(user, @"^\+?[0-9*#]{2,20}$") ? user : string.Empty;
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
        _lifetime.Cancel();
        _udp?.Dispose();
        if (_receiveTask != null) try { await _receiveTask.ConfigureAwait(false); } catch { }
        foreach (var callId in _dialogs.Keys.ToArray()) RemoveDialog(callId);
        _pending.Clear();
        _lateInvites.Clear();
        _serverProcessing.Clear();
        _serverResponses.Clear();
        _registrations.Clear();
        _udp = null;
        Interlocked.Exchange(ref _ownedController, null)?.Dispose();
        _lifetime.Dispose();
        RaiseStatus();
    }
}
