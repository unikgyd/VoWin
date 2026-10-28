using System.Net;
using VoSharp.Sip;

namespace VoSharp.Kernel.SipGateway;

public sealed partial class SipGateway
{
    private const int LateInviteCapacity = 512, LateDialogCapacity = 32, LateAttemptCapacity = 4;
    private readonly object _lateInviteGate = new();
    private readonly SemaphoreSlim _lateByeSlots = new(64, 64);

    // One dialog identity spans authenticated INVITE attempts. Neither CSeq
    // nor Via is part of the remote dialog's To-tag identity.
    private sealed class LateInviteFamily
    {
        public object Gate { get; } = new();
        public Dictionary<string, LateDialog> Dialogs { get; } = new(StringComparer.Ordinal);
        public string? AcceptedTag;
        public bool Fenced;
        public int Attempts, MaximumInviteCSeq;
    }
    private sealed class LateDialog
    {
        public Dictionary<LateInvite, SipMessage> Responses { get; } = new();
        public bool ByeClaimed, ByeStarted;
        public int ByeCSeq;
    }
    private sealed class LateFailure(SipMessage response)
    {
        public SipMessage Response = response;
        public bool Acknowledged;
    }
    private sealed class LateInvite(SipMessage invite, IPEndPoint remote, LateInviteFamily family)
    {
        public SipMessage Invite { get; } = invite;
        public IPEndPoint Remote { get; } = remote;
        public LateInviteFamily Family { get; } = family;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        // Every mutable field below is guarded by Family.Gate, never by this.
        public Dictionary<string, LateFailure> Failures { get; } = new(StringComparer.Ordinal);
        public bool Abandoned, Retired;
    }
    private sealed record LateCleanup(LateInvite Attempt, SipMessage Response, bool Bye);

    private LateInvite TrackLateInvite(SipMessage invite, IPEndPoint remote, LateInvite? previous = null)
    {
        lock (_lateInviteGate)
        {
            CleanupLateInvitesLocked();
            if (_lateInvites.Count >= LateInviteCapacity || Volatile.Read(ref _disposed) != 0)
                throw new InvalidOperationException("Late INVITE tracking capacity unavailable.");
            bool SameFamily(LateInvite candidate) => candidate.Remote.Equals(remote) &&
                string.Equals(candidate.Invite.GetHeader("Call-ID"), invite.GetHeader("Call-ID"), StringComparison.Ordinal) &&
                string.Equals(HeaderTag(candidate.Invite.GetHeader("From")), HeaderTag(invite.GetHeader("From")), StringComparison.Ordinal);
            if (previous is not null && !SameFamily(previous))
                throw new InvalidOperationException("Late INVITE attempt changed its dialog authority.");
            var family = previous?.Family ?? _lateInvites.Values.FirstOrDefault(SameFamily)?.Family ?? new();
            lock (family.Gate)
            {
                var sequence = LateInviteSequence(invite);
                if (family.Fenced || family.Attempts >= LateAttemptCapacity || family.AcceptedTag is not null || sequence <= family.MaximumInviteCSeq)
                    throw new InvalidOperationException("Late INVITE attempt cannot be admitted.");
                var late = new LateInvite(SnapshotLateMessage(invite), new(remote.Address, remote.Port), family);
                if (!_lateInvites.TryAdd(TransactionKey(invite), late))
                    throw new InvalidOperationException("Duplicate late INVITE transaction.");
                family.Attempts++;
                family.MaximumInviteCSeq = sequence;
                return late;
            }
        }
    }

    private static int LateInviteSequence(SipMessage invite) => int.TryParse(invite.GetHeader("CSeq")?.Split(' ')[0], out var sequence) && sequence is > 0 and < int.MaxValue - 1
        ? sequence : throw new InvalidOperationException("Invalid late INVITE sequence.");
    private static SipMessage SnapshotLateMessage(SipMessage message)
    {
        // Cleanup needs dialog/transaction headers, never SDP, message bodies,
        // authentication proofs or arbitrary peer extension headers.
        var snapshot = new SipMessage { IsRequest = message.IsRequest, Method = message.Method, RequestUri = message.RequestUri,
            StatusCode = message.StatusCode, ReasonPhrase = message.ReasonPhrase, SipVersion = message.SipVersion };
        foreach (var name in new[] { "Via", "From", "To", "Call-ID", "CSeq", "Contact" })
            if (message.GetHeader(name) is { } value) snapshot.SetHeader(name, value);
        return snapshot;
    }
    private static bool ValidLateInviteResponse(LateInvite late, SipMessage response) => response.StatusCode >= 200 &&
        string.Equals(TransactionKey(late.Invite), TransactionKey(response), StringComparison.Ordinal) &&
        string.Equals(ViaBranch(late.Invite), ViaBranch(response), StringComparison.Ordinal) &&
        ResponseMatchesRequestIdentity(late.Invite, response);
    private static string FailureKey(SipMessage response) => response.StatusCode + "|" + HeaderTag(response.GetHeader("To"));
    private static bool DialogTag(SipMessage response, out string tag) => TryHeaderTag(response.GetHeader("To"), out tag) && tag.Length is > 0 and <= 256;

    // True means ACK this exact observed response. A successful final may be
    // retained while the owner validates SDP and commits its accepted To-tag.
    private bool ObserveLateInviteResponse(LateInvite late, SipMessage response, out bool sendBye)
    {
        sendBye = false;
        lock (late.Family.Gate)
        {
            var family = late.Family;
            if (response.StatusCode >= 300)
            {
                var key = FailureKey(response);
                if (!late.Failures.TryGetValue(key, out var failure) && late.Failures.Count < LateDialogCapacity)
                    late.Failures[key] = failure = new(SnapshotLateMessage(response));
                var acknowledge = late.Abandoned || late.Retired || family.AcceptedTag is not null;
                if (acknowledge && failure is not null) failure.Acknowledged = true;
                return acknowledge;
            }
            if (!DialogTag(response, out var tag)) return false;
            if (!family.Dialogs.TryGetValue(tag, out var dialog))
            {
                if (family.Dialogs.Count >= LateDialogCapacity)
                {
                    // Do not evict an old BYE claim and thereby turn a replay
                    // into a new cleanup. Unknown overflow tags get ACK only.
                    family.Fenced = true;
                    late.Abandoned = true;
                    return true;
                }
                family.Dialogs[tag] = dialog = new();
            }
            dialog.Responses[late] = SnapshotLateMessage(response);
            if (family.AcceptedTag == tag) return true;
            if (!late.Abandoned && !late.Retired && family.AcceptedTag is null) return false;
            sendBye = ClaimLateBye(family, dialog, tag);
            return true;
        }
    }

    private static bool ClaimLateBye(LateInviteFamily family, LateDialog dialog, string tag)
    {
        if (family.AcceptedTag == tag || dialog.ByeClaimed) return false;
        dialog.ByeClaimed = true; // Never reset, including unknown/failed I/O.
        dialog.ByeCSeq = family.MaximumInviteCSeq + 1;
        return true;
    }

    private bool TryAcceptLateInvite(LateInvite late, SipMessage response)
    {
        if (!ValidLateInviteResponse(late, response) || response.StatusCode >= 300 || !DialogTag(response, out var tag)) return false;
        List<LateCleanup> cleanups = [];
        lock (late.Family.Gate)
        {
            var family = late.Family;
            if (late.Abandoned || late.Retired || family.Fenced || Volatile.Read(ref _disposed) != 0 ||
                family.AcceptedTag is not null && family.AcceptedTag != tag ||
                family.Dialogs.TryGetValue(tag, out var chosen) && chosen.ByeClaimed) return false;
            family.AcceptedTag = tag;
            // Reserving acceptance and all losing-dialog cleanup is one atomic
            // decision. A retired attempt with this tag is now permanently safe.
            CollectLateCleanups(late, cleanups, allDialogs: true);
        }
        DispatchLateCleanups(cleanups);
        return true;
    }

    private void RetireLateInvite(LateInvite late)
    {
        List<LateCleanup> cleanups = [];
        lock (late.Family.Gate)
        {
            late.Retired = late.Abandoned = true;
            CollectLateCleanups(late, cleanups, allDialogs: false);
        }
        DispatchLateCleanups(cleanups);
    }

    private void AbandonLateInvite(string transactionKey, LateInvite late)
    {
        _ = transactionKey; // Exact owned attempt, not a replaceable key lookup.
        List<LateCleanup> cleanups = [];
        lock (late.Family.Gate)
        {
            late.Abandoned = true;
            CollectLateCleanups(late, cleanups, allDialogs: false);
        }
        DispatchLateCleanups(cleanups);
    }

    private static void CollectLateCleanups(LateInvite late, List<LateCleanup> cleanups, bool allDialogs)
    {
        var family = late.Family;
        foreach (var (tag, dialog) in family.Dialogs)
        {
            if (family.AcceptedTag == tag) continue;
            foreach (var (attempt, response) in dialog.Responses)
                if (allDialogs || ReferenceEquals(attempt, late))
                    cleanups.Add(new(attempt, response, ClaimLateBye(family, dialog, tag)));
        }
        foreach (var failure in late.Failures.Values)
            if (!failure.Acknowledged)
            {
                failure.Acknowledged = true;
                cleanups.Add(new(late, failure.Response, false));
            }
    }

    private void DispatchLateCleanups(List<LateCleanup> cleanups)
    {
        // Bounded by 32 dialog tags x four owned attempts; no per-packet
        // Task.Run or peer-response wait is added to the receive loop.
        foreach (var cleanup in cleanups)
            _ = SendLateInviteAckByeAsync(cleanup.Attempt, cleanup.Response, cleanup.Bye);
    }

    private async Task SendLateInviteAckByeAsync(LateInvite late, SipMessage response, bool sendBye)
    {
        try
        {
            var accepted = response.StatusCode is >= 200 and < 300;
            var target = accepted ? ExtractContactTarget(response.GetHeader("Contact")) ?? late.Invite.RequestUri : late.Invite.RequestUri;
            var inviteCseq = LateInviteSequence(late.Invite);
            int byeCseq = 0;
            lock (late.Family.Gate)
            {
                if (!accepted && late.Failures.TryGetValue(FailureKey(response), out var failure)) failure.Acknowledged = true;
                if (sendBye && DialogTag(response, out var tag) && late.Family.AcceptedTag != tag &&
                    late.Family.Dialogs.TryGetValue(tag, out var dialog) && dialog.ByeClaimed && !dialog.ByeStarted)
                {
                    dialog.ByeStarted = true;
                    byeCseq = dialog.ByeCSeq;
                }
            }
            SipMessage MakeRequest(string method, int sequence)
            {
                var request = new SipMessage { IsRequest = true, Method = method, RequestUri = target };
                request.SetHeader("Via", method == "ACK" && !accepted ? late.Invite.GetHeader("Via") ?? "" :
                    $"SIP/2.0/UDP {FormatHost(_options.BindAddress)}:{_options.SipPort};branch=z9hG4bK{Guid.NewGuid():N};rport");
                request.SetHeader("Max-Forwards", "70");
                request.SetHeader("From", late.Invite.GetHeader("From") ?? "");
                request.SetHeader("To", response.GetHeader("To") ?? "");
                request.SetHeader("Call-ID", late.Invite.GetHeader("Call-ID") ?? "");
                request.SetHeader("CSeq", $"{sequence} {method}");
                request.SetHeader("Content-Length", "0");
                return request;
            }
            using var ackDeadline = CancellationTokenSource.CreateLinkedTokenSource(_receiveLifetime.Token);
            ackDeadline.CancelAfter(TimeSpan.FromSeconds(1));
            await SendAsync(MakeRequest("ACK", inviteCseq), late.Remote, ackDeadline.Token).ConfigureAwait(false);
            if (byeCseq == 0) return;
            if (!_lateByeSlots.Wait(0))
            {
                Trace("Late SIP INVITE cleanup capacity reached; ACK sent, BYE claim retained.");
                return;
            }
            _ = Task.Run(async () =>
            {
                try { await SendByeRequestWithAuthAsync(MakeRequest("BYE", byeCseq), late.Remote, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Trace($"Late SIP INVITE cleanup failed; category={ex.GetType().Name}; BYE claim retained."); }
                finally { _lateByeSlots.Release(); }
            });
        }
        catch (Exception ex) { Trace($"Late SIP INVITE cleanup failed; category={ex.GetType().Name}; cleanup claim retained."); }
    }

    private void CleanupLateInvites()
    {
        lock (_lateInviteGate) CleanupLateInvitesLocked();
    }
    private void CleanupLateInvitesLocked()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        foreach (var entry in _lateInvites)
            if (entry.Value.CreatedAt < cutoff) _lateInvites.TryRemove(entry.Key, out _);
    }
}
