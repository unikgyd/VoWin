using VoSharp.Sip;

namespace VoSharp.Kernel.SipGateway;

public sealed partial class SipGateway
{
    private readonly SemaphoreSlim _smsForwardWake = new(0, 1);
    private Task? _smsForwardTask;
    private readonly object _smsPeerProbeGate = new();
    private Task<bool>? _smsPeerProbeTask;
    private DateTimeOffset _smsPeerProbeUntil;

    /// <summary>
    /// Capture recipients synchronously when receiving an SMS, including offline
    /// accounts. A host must commit these intents atomically with its inbox row;
    /// this method never scans/backfills historical messages or writes a store.
    /// </summary>
    public IReadOnlyList<SipSmsForwardRecipient> CaptureSmsForwardRecipients(string? lineId)
    {
        if (SmsForwardStopping) return [];
        return ConfiguredSmsForwardRecipients(lineId);
    }

    private bool SmsForwardStopping => Volatile.Read(ref _started) == 0 || Volatile.Read(ref _disposed) != 0;

    private IReadOnlyList<SipSmsForwardRecipient> ConfiguredSmsForwardRecipients(string? lineId)
    {
        // Stopping is not revocation: a worker observing a shutdown must leave
        // valid Queued intents for restart, not cancel the recipient identity.
        if (_options.SmsForwardStore == null || !_options.MessagesEnabled || !_options.ForwardIncomingSms || lineId == null ||
            !_options.LineRoutes.TryGetValue(lineId, out var route) || !route.Enabled || !route.ForwardIncomingSms) return [];
        var result = new List<SipSmsForwardRecipient>();
        // AllowSms is the existing PAID SEND permission, not an MT receive grant.
        foreach (var user in _options.Accounts.Keys)
            if (IsLocalIncomingRecipient(route, user, sms: true) &&
                SipSmsRecipientAuthority.Local(_options, user) is { } authority)
                result.Add(new(SipSmsForwardRecipientKind.Local, user, authority));
        if (TryGetIncomingPeerTarget(route, sms: true, out var target))
        {
            if (SipSmsRecipientAuthority.Peer(_options, target) is { } authority)
                result.Add(new(SipSmsForwardRecipientKind.Peer, target, authority));
        }
        return result.AsReadOnly();
    }

    /// <summary>Only a successful atomic inbox/intent commit should notify this worker.</summary>
    public void NotifySmsInboxCommitted()
    {
        try { _smsForwardWake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private bool IsCurrentForwardRecipient(string line, SipSmsForwardRecipient recipient) =>
        ConfiguredSmsForwardRecipients(line).Any(current => current.Kind == recipient.Kind &&
            current.User.Equals(recipient.User, StringComparison.OrdinalIgnoreCase) && current.Authority == recipient.Authority);

    private IReadOnlyList<SipGatewayRegistration> GetForwardBindings(SipSmsForwardRecipient recipient)
    {
        if (recipient.Kind == SipSmsForwardRecipientKind.Local)
            return GetActiveRegistrations().Where(value => value.Extension.Equals(recipient.User, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (recipient.Kind != SipSmsForwardRecipientKind.Peer || _options.Peer is not { } peer) return [];
        return [new(recipient.User, $"<sip:{recipient.User}@{FormatHost(peer.EndPoint.Address)}:{peer.EndPoint.Port}>",
            peer.EndPoint, DateTimeOffset.MaxValue, "Fixed SMS peer")];
    }

    private void StartSmsForwardWorker()
    {
        if (_options.SmsForwardStore != null && _options.MessagesEnabled && _options.ForwardIncomingSms)
            _smsForwardTask = Task.Run(() => SmsForwardLoopAsync(_lifetime.Token));
    }

    private async Task SmsForwardLoopAsync(CancellationToken ct)
    {
        var store = _options.SmsForwardStore!;
        var running = new List<(string Recipient, SipSmsForwardRecipientKind Kind, Task Work)>();
        try
        {
            while (!ct.IsCancellationRequested && !SmsForwardStopping)
            {
                running.RemoveAll(value => value.Work.IsCompleted);
                try
                {
                    // Inspect the whole bounded queue. A fixed oldest-N window
                    // would starve a later online user behind offline accounts.
                    var candidates = await store.GetPendingAsync(DateTimeOffset.UtcNow, 2048, ct).ConfigureAwait(false);
                    foreach (var item in candidates)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (SmsForwardStopping) break;
                        if (running.Count >= 4) break;
                        // An offline fixed peer may occupy one short readiness
                        // check, never all slots needed by local registrants.
                        if (item.Recipient.Kind == SipSmsForwardRecipientKind.Peer &&
                            running.Any(value => value.Kind == SipSmsForwardRecipientKind.Peer)) continue;
                        if (!IsCurrentForwardRecipient(item.Payload.LineId, item.Recipient))
                        {
                            await store.CancelQueuedAsync(item.Id, ct).ConfigureAwait(false);
                            continue;
                        }
                        var key = item.Recipient.Kind + "\0" + item.Recipient.User.ToLowerInvariant() + "\0" + item.Recipient.Authority;
                        if (running.Any(value => value.Recipient == key)) continue;
                        var bindings = GetForwardBindings(item.Recipient);
                        if (bindings.Count == 0) continue; // Offline is NOT an attempted/Unknown delivery.
                        running.Add((key, item.Recipient.Kind, ForwardQueuedSmsAsync(item, bindings, ct)));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Trace($"SIP SMS forwarding store unavailable; category={ex.GetType().Name}; no queued message retried as unknown.");
                }
                await _smsForwardWake.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            // Disposal keeps the receive socket alive until every owned send
            // has observed cancellation and finished its store completion.
            await Task.WhenAll(running.Select(value => value.Work)).ConfigureAwait(false);
            Task<bool>? probe;
            lock (_smsPeerProbeGate) probe = _smsPeerProbeTask;
            if (probe != null) await probe.ConfigureAwait(false);
        }
    }

    private async Task ForwardQueuedSmsAsync(SipSmsForwardItem item, IReadOnlyList<SipGatewayRegistration> bindings, CancellationToken ct)
    {
        var wakeOnFinish = true;
        var attempted = false;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(MessageTransactionTimeout);
            var token = budget.Token;
            if (SmsForwardStopping || !IsCurrentForwardRecipient(item.Payload.LineId, item.Recipient)) return;
            if (item.Recipient.Kind == SipSmsForwardRecipientKind.Peer && !await SmsPeerReadyAsync(token).ConfigureAwait(false))
            {
                wakeOnFinish = false; // Negative-cache polling must not busy-loop.
                return;
            }
            bindings = GetForwardBindings(item.Recipient);
            if (bindings.Count == 0) { wakeOnFinish = false; return; }
            // FULL durable Unknown must exist BEFORE any MT payload chunk or
            // MESSAGE authentication request is emitted. A crash after this point is
            // intentionally uncertain, even if no datagram actually left.
            var claim = await _options.SmsForwardStore!.TryClaimAsync(item.Id, item.PayloadFingerprint,
                DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            if (claim == null) return;
            attempted = true;
            if (claim.Id != item.Id || claim.Recipient != item.Recipient || claim.PayloadFingerprint != item.PayloadFingerprint ||
                claim.Payload.Fingerprint() != claim.PayloadFingerprint || string.IsNullOrEmpty(claim.AttemptToken))
                throw new InvalidOperationException("Invalid durable SMS forward claim; no wire send performed.");
            token.ThrowIfCancellationRequested();
            if (SmsForwardStopping || !IsCurrentForwardRecipient(claim.Payload.LineId, claim.Recipient)) return;
            bindings = GetForwardBindings(claim.Recipient);
            if (bindings.Count == 0) return;
            var delivered = await Task.WhenAll(bindings.Select(binding => SendTextForSimToBindingAsync(binding,
                claim.Payload.Sender, claim.Payload.Text, claim.Payload.LineId, claim.Payload.SimBinding, token,
                () => !SmsForwardStopping && IsCurrentForwardRecipient(claim.Payload.LineId, claim.Recipient) &&
                    (claim.Recipient.Kind == SipSmsForwardRecipientKind.Peer ||
                     GetForwardBindings(claim.Recipient).Any(current => current.RemoteEndPoint.Equals(binding.RemoteEndPoint) &&
                         current.Contact == binding.Contact)),
                guardToken => _options.SmsForwardStore!.IsClaimSourceCurrentAsync(claim, guardToken),
                claim.Payload.ReplyNumber))).ConfigureAwait(false);
            // One contact must accept EVERY chunk. Splitting acceptance across
            // contacts cannot falsely complete a logical message.
            var outcome = delivered.Any(value => value) ? SipSmsForwardState.Accepted : SipSmsForwardState.Unknown;
            // A bounded independent completion budget permits a final commit on
            // shutdown without reviving the cancelled network operation.
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _options.SmsForwardStore.CompleteAsync(claim.Id, claim.AttemptToken, outcome, completion.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { wakeOnFinish = attempted; /* The already-claimed intent remains Unknown. */ }
        catch (Exception ex)
        {
            wakeOnFinish = attempted;
            Trace($"SIP SMS forward attempt ended; category={ex.GetType().Name}; uncertain delivery is not retried.");
        }
        finally { if (wakeOnFinish) NotifySmsInboxCommitted(); }
    }

    private async Task<bool> SmsPeerReadyAsync(CancellationToken ct)
    {
        Task<bool> probe;
        lock (_smsPeerProbeGate)
        {
            if (_smsPeerProbeTask is not { IsCompletedSuccessfully: true } || DateTimeOffset.UtcNow >= _smsPeerProbeUntil)
            {
                if (_smsPeerProbeTask == null || _smsPeerProbeTask.IsCompleted)
                    _smsPeerProbeTask = ProbeSmsPeerAsync(_lifetime.Token);
            }
            probe = _smsPeerProbeTask!;
        }
        return await probe.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> ProbeSmsPeerAsync(CancellationToken ct)
    {
        try
        {
            if (_options.Peer is not { } peer || SmsForwardStopping) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            // Readiness contains no SMS body, sender, recipient number or SIM
            // binding. It never sends Authorization; a usable pinned challenge
            // only proves reachability, not that a later MESSAGE will succeed.
            var request = new SipMessage { IsRequest = true, Method = "OPTIONS",
                RequestUri = $"sip:keepalive@{FormatHost(peer.EndPoint.Address)}:{peer.EndPoint.Port}" };
            request.SetHeader("Via", $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
            request.SetHeader("From", $"<sip:gateway@{_options.Realm}>;tag={Guid.NewGuid():N}");
            request.SetHeader("To", $"<sip:keepalive@{FormatHost(peer.EndPoint.Address)}:{peer.EndPoint.Port}>");
            request.SetHeader("Call-ID", Guid.NewGuid().ToString("N") + "@" + _options.Realm);
            request.SetHeader("CSeq", "1 OPTIONS");
            request.SetHeader("Max-Forwards", "20");
            var response = await SendRequestFinalAsync(request, peer.EndPoint, deadline.Token, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return response.StatusCode == 200 || response.StatusCode is 401 or 407 && SipPeerDigest.CreatePreferred(response, request, peer) != null;
        }
        catch (Exception) { return false; }
        finally
        {
            lock (_smsPeerProbeGate) _smsPeerProbeUntil = DateTimeOffset.UtcNow.AddSeconds(1);
            NotifySmsInboxCommitted();
        }
    }
}
