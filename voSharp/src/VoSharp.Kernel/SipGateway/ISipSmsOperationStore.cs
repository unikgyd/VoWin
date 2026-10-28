namespace VoSharp.Kernel.SipGateway;

/// <summary>Submission evidence only, never a handset delivery receipt.</summary>
public enum SipSmsOperationOutcome { Unknown, Submitted, PartiallySubmitted, Failed }

public sealed record SipSmsOperationReservation(bool IsNew, bool ContentMatches, SipSmsOperationOutcome Outcome,
    string? ReservationToken = null);

/// <summary>
/// Durable admission for paid SIP SMS. Reserve must atomically persist Unknown
/// before returning IsNew=true, including across processes sharing the store.
/// Replays never call the transport again. A new reservation has a unique token;
/// Complete must reject an old token after expiry/replacement, even for identical
/// content. Keep reservations for at least 24h;
/// persist hashes and outcomes only, not message text or subscriber identities.
/// Storage errors must fail closed, never act like an empty store.
/// </summary>
public interface ISipSmsOperationStore
{
    Task<SipSmsOperationReservation> ReserveAsync(string operationId, string fingerprint, CancellationToken ct);
    Task CompleteAsync(string operationId, string fingerprint, string reservationToken, SipSmsOperationOutcome outcome, CancellationToken ct);
}
