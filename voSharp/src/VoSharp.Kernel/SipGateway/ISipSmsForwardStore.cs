using System.Security.Cryptography;
using System.Text;

namespace VoSharp.Kernel.SipGateway;

/// <summary>Only Queued work is eligible; an uncertain/partial attempt stays Unknown and is never replayed.</summary>
public enum SipSmsForwardState { Queued, Unknown, Accepted, Cancelled }
public enum SipSmsForwardRecipientKind { Local, Peer }

/// <summary>The identity captured when the SMS is committed, not whoever owns this username later.</summary>
public sealed record SipSmsForwardRecipient(SipSmsForwardRecipientKind Kind, string User, string Authority)
{
    public override string ToString() => "SIP SMS recipient (identity omitted)";
}

public sealed record SipSmsForwardPayload(string LineId, string? SimBinding, string Sender, string Text)
{
    /// <summary>First-admission canonical destination. Null is legacy v1; empty is explicitly read-only.</summary>
    public string? ReplyNumber { get; init; }

    /// <summary>Length framing prevents delimiter-containing message text from changing field boundaries.</summary>
    public string Fingerprint()
    {
        if (ReplyNumber is { Length: > 0 } && !SipGateway.IsCanonicalSmsReplyNumber(ReplyNumber))
            throw new ArgumentException("Invalid canonical SMS reply snapshot.");
        // Preserve old hashes exactly; null must never be promoted using a
        // later listener's country policy. Explicit read-only is a v2 decision.
        var values = ReplyNumber is null
            ? new[] { "sip-sms-forward-payload-v1", LineId, SimBinding ?? "", Sender, Text }
            : new[] { "sip-sms-forward-payload-v2", LineId, SimBinding ?? "", Sender, Text, ReplyNumber };
        var framed = string.Concat(values.Select(value => Encoding.UTF8.GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(framed))).ToLowerInvariant();
    }
    public override string ToString() => "SIP SMS payload (content and source omitted)";
}

public sealed record SipSmsForwardItem(string Id, SipSmsForwardRecipient Recipient, SipSmsForwardPayload Payload,
    string PayloadFingerprint, DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => "Queued SIP SMS forward (content omitted)";
}

public sealed record SipSmsForwardClaim(string Id, string AttemptToken, SipSmsForwardRecipient Recipient,
    SipSmsForwardPayload Payload, string PayloadFingerprint)
{
    /// <summary>The original actual Inbox reference captured by the FULL claim, not a later replacement row.</summary>
    public string? InboxId { get; init; }
    public override string ToString() => "Claimed SIP SMS forward (content and token omitted)";
}

/// <summary>
/// Durable MT delivery intents saved atomically with the incoming inbox row.
/// GetPending returns only unexpired Queued work, never Unknown. TryClaim must
/// verify the current inbox fingerprint/expiry and FULL-commit Unknown plus a
/// new random attempt token before returning a claim. An absent/edited source
/// is cancelled rather than sent. Completion is a token-checked CAS from
/// Unknown; stale tokens cannot complete replacement work. Storage errors fail
/// closed. Retain the same intent for 24 hours; do not synthesize historical
/// recipients or automatically retry a crash, timeout, rejection or partial send.
/// </summary>
public interface ISipSmsForwardStore
{
    Task<IReadOnlyList<SipSmsForwardItem>> GetPendingAsync(DateTimeOffset now, int limit, CancellationToken ct);
    Task<SipSmsForwardClaim?> TryClaimAsync(string id, string expectedFingerprint, DateTimeOffset now, CancellationToken ct);
    /// <summary>
    /// Read-only, fail-closed revalidation immediately before each MT wire send.
    /// Verify the same Unknown claim/token, recipient, unexpired fingerprint and
    /// original actual Inbox/source. Never renew expiry or change its state.
    /// </summary>
    Task<bool> IsClaimSourceCurrentAsync(SipSmsForwardClaim claim, CancellationToken ct);
    Task CompleteAsync(string id, string attemptToken, SipSmsForwardState state, CancellationToken ct);
    Task CancelQueuedAsync(string id, CancellationToken ct);
}
