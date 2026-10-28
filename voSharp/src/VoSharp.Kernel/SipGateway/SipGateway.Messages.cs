using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Sip;
using VoSharp.Telephony.Sms;

namespace VoSharp.Kernel.SipGateway;

public sealed partial class SipGateway
{
    public event EventHandler<SipSmsSubmission>? SmsSubmitted;
    private sealed record MessageResult(int Status, string Reason);
    private sealed record MessageSubmission(string Fingerprint, DateTimeOffset Created, Lazy<Task<MessageResult>> Work);
    private readonly ConcurrentDictionary<string, MessageSubmission> _messageSubmissions = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _forwardedSms = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _messageDestinations = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _smsLines = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _messageRates = new();
    private readonly SemaphoreSlim _incomingMessageLimit = new(16, 16);
    private long _messagesDelivered, _messagesFailed;

    private async Task HandleMessageAsync(SipMessage request, IPEndPoint remote, CancellationToken ct)
    {
        if (!_options.MessagesEnabled)
        {
            await SendResponseAsync(request, remote, 403, "Messages Disabled", ct).ConfigureAwait(false);
            return;
        }
        var fromPeer = IsPeerSource(remote);
        if (!fromPeer && _options.Accounts.Count == 0)
        {
            await SendResponseAsync(request, remote, 403, "Local Accounts Disabled", ct).ConfigureAwait(false);
            return;
        }
        var authenticator = fromPeer ? _peerAuthenticator! : _authenticator;
        if (!authenticator.Validate(request, out var extension))
        {
            await SendResponseAsync(request, remote, 407, "Proxy Authentication Required", ct,
                response => response.SetHeader("Proxy-Authenticate", authenticator.CreateChallenge())).ConfigureAwait(false);
            return;
        }
        var contentType = request.GetHeader("Content-Type") ?? "";
        if (request.GetHeader("Content-Encoding") is { } encoding && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(contentType, @"^text/plain(?:\s*;\s*charset\s*=\s*""?(?:utf-8|us-ascii)""?)?\s*$", RegexOptions.IgnoreCase))
        {
            await SendResponseAsync(request, remote, 415, "Text Plain UTF8 Required", ct,
                response => response.SetHeader("Accept", "text/plain")).ConfigureAwait(false);
            return;
        }
        var bytes = request.RawBody ?? Encoding.UTF8.GetBytes(request.Body);
        if (bytes.Length is 0 or > 1024 || request.ToBytes().Length > 8192)
        {
            await SendResponseAsync(request, remote, 413, "Message Too Large Or Empty", ct).ConfigureAwait(false);
            return;
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            await SendResponseAsync(request, remote, 400, "Invalid UTF8", ct).ConfigureAwait(false);
            return;
        }
        var target = ExtractUriUser(request.RequestUri);
        var requestedLine = request.GetHeader("X-VoWin-Line");
        string? expectedSimBinding = null;
        if (!_options.Accounts.ContainsKey(target) && IsSmsReplyNamespace(target))
        {
            // A legacy line-only alias cannot prove which card received the
            // original message. Never reinterpret it using today's active SIM.
            if (!TryParseSmsReplyUser(target, out var lineHash, out var binding, out var replyNumber) ||
                !IsCanonicalSmsReplyNumber(replyNumber))
            {
                await SendResponseAsync(request, remote, 403, "SMS Reply Source Is Unproven Or Read Only", ct).ConfigureAwait(false);
                return;
            }
            var originalLines = _options.LineRoutes.Keys.Where(line => SmsReplyLineHash(line) == lineHash).ToArray();
            if (originalLines.Length != 1 || !string.IsNullOrWhiteSpace(requestedLine) &&
                !requestedLine.Equals(originalLines[0], StringComparison.OrdinalIgnoreCase))
            {
                await SendResponseAsync(request, remote, 403, "Original SMS Line Unavailable Or Conflicting", ct).ConfigureAwait(false);
                return;
            }
            requestedLine = originalLines[0];
            expectedSimBinding = binding;
            target = replyNumber;
        }
        var callId = request.GetHeader("Call-ID");
        var sequenceText = Regex.Match(request.GetHeader("CSeq") ?? "", @"^([0-9]{1,10})\s+MESSAGE$", RegexOptions.IgnoreCase).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || string.IsNullOrEmpty(callId) || callId.Length > 256 ||
            !uint.TryParse(sequenceText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence) || sequence > int.MaxValue)
        {
            await SendResponseAsync(request, remote, 400, "Invalid Message", ct).ConfigureAwait(false);
            return;
        }
        CleanupMessages();
        // Logical identity survives UDP retransmissions AND authenticated retry
        // branches. Reusing it with changed content is an error, never a new SMS.
        var operationId = SmsOperationId(_options.Realm, extension, callId, sequence);
        var authority = MessageAuthority(extension, fromPeer);
        var key = operationId;
        // The authenticated user, exact Call-ID and numeric CSeq identify the
        // paid operation. LAN/VPN domain spelling and CSeq leading zeroes must
        // neither create another submission nor make an equivalent retry fail.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            authority + "\0" + target + "\0" + requestedLine + "\0" + expectedSimBinding + "\0" + text)));
        if (_messageSubmissions.Count >= 2048 && !_messageSubmissions.ContainsKey(key))
        {
            await SendResponseAsync(request, remote, 503, "Message Capacity Reached", ct).ConfigureAwait(false);
            return;
        }
        var submission = _messageSubmissions.GetOrAdd(key, _ => new(fingerprint, DateTimeOffset.UtcNow,
            new(() => RouteMessageAsync(extension, target, text, requestedLine, operationId, authority, fromPeer, expectedSimBinding, _lifetime.Token))));
        var result = submission.Fingerprint == fingerprint ? await submission.Work.Value.ConfigureAwait(false)
            : new MessageResult(400, "Message Transaction Content Changed");
        await SendResponseAsync(request, remote, result.Status, result.Reason, ct).ConfigureAwait(false);
        Trace($"SIP MESSAGE completed; status={result.Status}; body and identities omitted.");
        RaiseStatus();
    }

    private async Task<MessageResult> RouteMessageAsync(string extension, string target, string text, string? requestedLine,
        string operationId, string authority, bool fromPeer, string? expectedSimBinding, CancellationToken ct)
    {
        lock (_messageRates)
        {
            var now = DateTimeOffset.UtcNow;
            // The same username may exist in the independent local and peer
            // authorities. Keep each authenticated principal's bounded budget.
            var rate = _messageRates.GetValueOrDefault(authority);
            if (rate.Start.AddMinutes(1) <= now) rate = (now, 0);
            if (rate.Count >= 20) return new(503, "Message Rate Limit");
            _messageRates[authority] = (rate.Start, rate.Count + 1);
        }
        try
        {
            // A decoded bound reply remains paid SIM traffic, even if its
            // numeric destination happens to equal a local account's name.
            if (expectedSimBinding == null && _options.Accounts.ContainsKey(target))
            {
                if (fromPeer) return new(403, "Peer Cannot Route To Local Account");
                if (_options.PolicyFor(extension)?.AllowMessages == false || _options.PolicyFor(target)?.AllowMessages == false)
                    return new(403, "Internal Messages Not Permitted");
                var bindings = GetActiveRegistrations().Where(binding => binding.Extension.Equals(target, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (bindings.Length == 0) return new(480, "Extension Offline");
                var delivered = await Task.WhenAll(bindings.Select(binding => SendTextToBindingAsync(binding, extension, text, null, ct))).ConfigureAwait(false);
                return delivered.Any(ok => ok) ? new(200, "OK") : new(480, "Message Delivery Failed");
            }
            if (!_options.IsPlausibleCarrierDestination(target, sms: true)) return new(404, "Unknown Message Destination");
            if (fromPeer && _options.Peer?.AllowOutboundMessages != true) return new(403, "Peer SMS Disabled");
            var (number, line) = _options.ResolveDestination(target, requestedLine, extension, sms: true, fromPeer: fromPeer);
            if (!Regex.IsMatch(number, @"^\+?[0-9]{2,20}$")) return new(400, "Invalid SMS Number");
            if (_controller is not ISipGatewayMessageController sms) return new(503, "SMS Transport Unavailable");
            if (expectedSimBinding != null && (line == null || sms is not ISipGatewaySimBoundMessageController))
                return new(403, "Original SMS SIM Cannot Be Verified");
            if (line != null && sms is ISipGatewaySimBoundMessageController bound)
            {
                var currentBinding = bound.GetSmsSimBinding(line);
                if (!SmsSubscriptionIdentity.IsValid(currentBinding)) return new(403, "SMS SIM Is Unproven");
                if (expectedSimBinding != null && expectedSimBinding != currentBinding) return new(403, "Original SMS SIM Is Unavailable");
                expectedSimBinding ??= currentBinding;
            }
            // The legacy idempotent interface does not make the independent
            // bound-send entry idempotent. It still needs durable admission.
            if (_options.SmsOperationStore is null && (expectedSimBinding != null || sms is not ISipGatewayIdempotentMessageController))
                return new(503, "Durable SMS Store Required");
            var gate = _smsLines.GetOrAdd(line?.ToLowerInvariant() ?? "default", _ => new(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await SubmitSmsOnceAsync(sms, operationId, extension, number, text, line, authority, expectedSimBinding, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        catch (SipGatewayLineRoutingException ex) { return new(ex.StatusCode, ex.Message); }
        catch (OperationCanceledException) { return new(503, "Gateway Stopping"); }
        catch (Exception ex)
        {
            Trace($"SIP MESSAGE routing failed; category={ex.GetType().Name}; content omitted.");
            return new(503, "Message Delivery Failed");
        }
    }

    private async Task<MessageResult> SubmitSmsOnceAsync(ISipGatewayMessageController sms, string operationId,
        string extension, string number, string text, string? line, string authority, string? expectedSimBinding, CancellationToken ct)
    {
        // Bind the durable attempt to the RESOLVED destination. A changed dial
        // rule/default line after restart must not turn the old operation into
        // a different paid submission. Network-domain spelling is irrelevant.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "sip-sms-attempt-v3\0" + authority + "\0" + line?.ToLowerInvariant() + "\0" + expectedSimBinding + "\0" + number + "\0" + text))).ToLowerInvariant();
        string? reservationToken = null;
        if (_options.SmsOperationStore is { } store)
        {
            SipSmsOperationReservation reservation;
            try { reservation = await store.ReserveAsync(operationId, fingerprint, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Trace($"SMS admission unavailable; category={ex.GetType().Name}; no transport used.");
                return new(503, "SMS State Unavailable No Submission");
            }
            if (!reservation.ContentMatches) return new(400, "Message Transaction Content Changed");
            if (!reservation.IsNew) return SmsOutcomeResult(reservation.Outcome);
            if (reservation.Outcome != SipSmsOperationOutcome.Unknown || string.IsNullOrEmpty(reservation.ReservationToken))
                return new(503, "Invalid SMS Admission No Submission");
            reservationToken = reservation.ReservationToken;
        }
        SmsSubmitResult sent;
        try
        {
            ct.ThrowIfCancellationRequested();
            sent = expectedSimBinding != null && line != null && sms is ISipGatewaySimBoundMessageController bound
                ? await bound.SendSmsForSimAsync(number, text, line, expectedSimBinding, ct).ConfigureAwait(false)
                : sms is ISipGatewayIdempotentMessageController durable
                ? await durable.SendSmsOnceAsync(operationId, number, text, line, ct).ConfigureAwait(false)
                : await sms.SendSmsAsync(number, text, line, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The modem/carrier may have accepted some or all parts. Keep the
            // pre-send Unknown record; cancellation is not proof of no send.
            Trace($"SMS attempt uncertain; category={ex.GetType().Name}; no automatic retry.");
            return SmsOutcomeResult(SipSmsOperationOutcome.Unknown);
        }
        try { SmsSubmitted?.Invoke(this, new(extension, line, sent)); } catch { }
        var outcome = string.Equals(sent.SubmissionStatus, "unknown", StringComparison.OrdinalIgnoreCase)
            ? SipSmsOperationOutcome.Unknown
            : sent.AllPartsAccepted || sent.SubmissionStatus == "submitted_parts_unreported"
                ? SipSmsOperationOutcome.Submitted
                : sent.PartsAccepted > 0 ? SipSmsOperationOutcome.PartiallySubmitted : SipSmsOperationOutcome.Failed;
        if (_options.SmsOperationStore is { } completedStore)
        {
            try { await completedStore.CompleteAsync(operationId, fingerprint, reservationToken!, outcome, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Trace($"SMS completion not persisted; category={ex.GetType().Name}; no automatic retry.");
                return SmsOutcomeResult(SipSmsOperationOutcome.Unknown);
            }
        }
        return SmsOutcomeResult(outcome);
    }

    private static MessageResult SmsOutcomeResult(SipSmsOperationOutcome outcome) => outcome switch
    {
        SipSmsOperationOutcome.Submitted => new(202, "SMS Submitted Not Delivery Confirmed"),
        SipSmsOperationOutcome.PartiallySubmitted => new(503, "SMS Partially Submitted Do Not Retry"),
        SipSmsOperationOutcome.Failed => new(503, "SMS Submission Failed Do Not Retry"),
        _ => new(503, "SMS Result Unknown Do Not Retry")
    };

    private void OnCarrierSms(object? sender, SmsReceivedEventArgs e)
    {
        // The desktop commits inbox+recipient intents together. Its durable
        // worker, not this pre-commit event callback, owns all MT delivery.
        if (_options.SmsForwardStore != null) return;
        if (!_options.MessagesEnabled || !_options.ForwardIncomingSms || Volatile.Read(ref _disposed) != 0 ||
            e.Message.Direction != SmsDirection.Received || string.IsNullOrEmpty(e.Message.Text) ||
            e.SlotId == null || !_options.LineRoutes.TryGetValue(e.SlotId, out var route) ||
            !route.Enabled || !route.ForwardIncomingSms) return;
        CleanupMessages();
        var key = e.SlotId + ":" + e.SimBinding + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            e.Message.Id ?? $"{e.Message.SenderOrRecipient}\0{e.Message.Timestamp:O}\0{e.Message.Text}")));
        if (_forwardedSms.Count >= 4096 || !_forwardedSms.TryAdd(key, DateTimeOffset.UtcNow)) return;
        var replyNumber = CaptureSmsReplyNumber(e.SlotId, e.Message.SenderOrRecipient);
        if (!_incomingMessageLimit.Wait(0))
        {
            Trace("Incoming SMS forwarding capacity reached; SMS remains in the local inbox.");
            Interlocked.Increment(ref _messagesFailed);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var bindings = GetActiveRegistrations().Where(binding => IsLocalIncomingRecipient(route, binding.Extension, sms: true)).ToList();
                if (_options.Peer is { } peer && TryGetIncomingPeerTarget(route, sms: true, out var target))
                {
                    bindings.Add(new(target,
                        $"<sip:{target}@{FormatHost(peer.EndPoint.Address)}:{peer.EndPoint.Port}>", peer.EndPoint, DateTimeOffset.MaxValue, "Fixed peer"));
                }
                if (bindings.Count == 0)
                {
                    Interlocked.Increment(ref _messagesFailed);
                    Trace($"Incoming SMS not forwarded; line={e.SlotId}; no online target; local inbox retained.");
                }
                await Task.WhenAll(bindings.Select(binding => SendTextForSimToBindingAsync(binding,
                    e.Message.SenderOrRecipient, e.Message.Text, e.SlotId, e.SimBinding, _lifetime.Token,
                    replyNumber: replyNumber))).ConfigureAwait(false);
            }
            catch (Exception ex) { Trace($"Incoming SMS forwarding failed; category={ex.GetType().Name}; local inbox retained."); }
            finally { _incomingMessageLimit.Release(); RaiseStatus(); }
        });
    }

    private Task<bool> SendTextToBindingAsync(SipGatewayRegistration binding, string from, string text, string? line, CancellationToken ct) =>
        SendTextForSimToBindingAsync(binding, from, text, line, simBinding: null, ct);

    private async Task<bool> SendTextForSimToBindingAsync(SipGatewayRegistration binding, string from, string text, string? line, string? simBinding, CancellationToken ct,
        Func<bool>? canSend = null, Func<CancellationToken, Task<bool>>? sourceIsCurrent = null, string? replyNumber = null)
    {
        // Queueing, every chunk and the one optional authentication retry share
        // the same logical-message deadline. No renewal on a late challenge.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(MessageTransactionTimeout);
        var messageToken = deadline.Token;
        // RFC 3428: no overlapping out-of-dialog transactions to one URI.
        var key = binding.Extension + "@" + binding.RemoteEndPoint;
        if (_messageDestinations.Count >= 4096 && !_messageDestinations.ContainsKey(key)) return false;
        var gate = _messageDestinations.GetOrAdd(key, _ => new(1, 1));
        var entered = false;
        async Task<bool> BeforeWireAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (canSend?.Invoke() == false) return false;
            // This is the last awaited authority/database check before bytes.
            // The recipient may change while SQLite is awaited, so repeat only
            // the synchronous runtime/contact checks after it returns.
            if (sourceIsCurrent != null && !await sourceIsCurrent(token).ConfigureAwait(false)) return false;
            token.ThrowIfCancellationRequested();
            return canSend?.Invoke() != false;
        }
        Func<CancellationToken, Task<bool>>? beforeWire = sourceIsCurrent == null ? null : BeforeWireAsync;
        try
        {
            await gate.WaitAsync(messageToken).ConfigureAwait(false);
            entered = true;
            var chunks = SplitMessageText(text);
            for (var i = 0; i < chunks.Count; i++)
            {
                messageToken.ThrowIfCancellationRequested();
                if (canSend?.Invoke() == false) throw new InvalidOperationException("The original SMS contact is no longer authorized.");
                var request = new SipMessage { IsRequest = true, Method = "MESSAGE",
                    RequestUri = ExtractContactTarget(binding.Contact) ?? $"sip:{binding.Extension}@{FormatHost(binding.RemoteEndPoint.Address)}:{binding.RemoteEndPoint.Port}",
                    Body = chunks.Count == 1 ? chunks[i] : $"[{i + 1}/{chunks.Count}] {chunks[i]}" };
                request.RawBody = Encoding.UTF8.GetBytes(request.Body);
                request.SetHeader("Via", $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
                request.SetHeader("Max-Forwards", "20");
                var display = SanitizeUser(from);
                var fromUser = line != null ? ForwardSmsReplyUser(line, from, simBinding, replyNumber) : display;
                request.SetHeader("From", $"\"{(string.IsNullOrEmpty(display) ? "unknown" : display)}\" <sip:{(string.IsNullOrEmpty(fromUser) ? "unknown" : fromUser)}@{_options.IdentityHost}>;tag={Guid.NewGuid():N}");
                request.SetHeader("To", $"<sip:{binding.Extension}@{_options.IdentityHost}>");
                request.SetHeader("Call-ID", Guid.NewGuid().ToString("N") + "@" + _options.IdentityHost);
                request.SetHeader("CSeq", "1 MESSAGE");
                request.SetHeader("Content-Type", "text/plain;charset=UTF-8");
                if (line != null) request.SetHeader("X-VoWin-Line", line);
                if (request.ToBytes().Length > 1300) throw new InvalidOperationException("SIP MESSAGE exceeds safe UDP size.");
                var response = await SendRequestFinalAsync(request, binding.RemoteEndPoint, messageToken, MessageTransactionTimeout, beforeWire).ConfigureAwait(false);
                if (_options.Peer is { } peer && binding.RemoteEndPoint.Equals(peer.EndPoint) && response.StatusCode is 401 or 407)
                {
                    messageToken.ThrowIfCancellationRequested();
                    if (canSend?.Invoke() == false) throw new InvalidOperationException("The original SMS contact is no longer authorized.");
                    var authorization = SipPeerDigest.CreatePreferred(response, request, peer);
                    if (authorization != null)
                    {
                        request.SetHeader(response.StatusCode == 401 ? "Authorization" : "Proxy-Authorization", authorization);
                        request.SetHeader("CSeq", "2 MESSAGE");
                        request.SetHeader("Via", $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
                        if (request.ToBytes().Length > 1300) throw new InvalidOperationException("Authenticated MESSAGE exceeds safe UDP size.");
                        response = await SendRequestFinalAsync(request, binding.RemoteEndPoint, messageToken, MessageTransactionTimeout, beforeWire).ConfigureAwait(false);
                    }
                }
                if (response.StatusCode is < 200 or >= 300) throw new InvalidOperationException("MESSAGE target rejected request.");
            }
            Interlocked.Increment(ref _messagesDelivered);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _messagesFailed);
            Trace($"SIP MESSAGE forward failed; category={ex.GetType().Name}; content omitted.");
            return false;
        }
        finally { if (entered) gate.Release(); }
    }

    internal static IReadOnlyList<string> SplitMessageText(string text)
    {
        if (text.Length > 16384) throw new ArgumentException("Message text too long.");
        var chunks = new List<string>();
        var current = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 280) { chunks.Add(current.ToString()); current.Clear(); bytes = 0; }
            current.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    internal static string SmsOperationId(string realm, string extension, string callId, uint sequence) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"sip-sms-v1\0{realm.ToLowerInvariant()}\0{extension.ToLowerInvariant()}\0{callId}\0{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))).ToLowerInvariant();

    internal static string SmsReplyUser(string line, string number) => "sms-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line.ToLowerInvariant())))[..12].ToLowerInvariant() + "-" + number;

    private void CleanupMessages()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var entry in _messageSubmissions)
            if (entry.Value.Created < cutoff && (!entry.Value.Work.IsValueCreated || entry.Value.Work.Value.IsCompleted))
                _messageSubmissions.TryRemove(entry.Key, out _);
        foreach (var entry in _forwardedSms)
            if (entry.Value < cutoff) _forwardedSms.TryRemove(entry.Key, out _);
    }
}

public sealed record SipSmsSubmission(string Extension, string? SlotId, SmsSubmitResult Result)
{
    public override string ToString() => "SIP SMS submission (content omitted)";
}
