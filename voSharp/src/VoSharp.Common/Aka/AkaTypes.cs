using System.Security.Cryptography;

namespace VoSharp.Common.Aka;

/// <summary>
/// UMTS AKA challenge as carried by EAP-AKA (RFC 4187) and IMS Digest (RFC 3310).
/// Both RAND and AUTN are fixed 16 bytes; fixed-size prevents a provider from
/// silently accepting truncated input.
/// </summary>
public sealed record AkaChallenge(byte[] Rand, byte[] Autn)
{
    public const int RandLength = 16;
    public const int AutnLength = 16;

    public static AkaChallenge Create(byte[] rand, byte[] autn)
    {
        ArgumentNullException.ThrowIfNull(rand);
        ArgumentNullException.ThrowIfNull(autn);
        if (rand.Length != RandLength)
            throw new ArgumentException($"RAND must be {RandLength} bytes, got {rand.Length}.", nameof(rand));
        if (autn.Length != AutnLength)
            throw new ArgumentException($"AUTN must be {AutnLength} bytes, got {autn.Length}.", nameof(autn));
        return new AkaChallenge(rand, autn);
    }
}

/// <summary>
/// Result of a USIM/ISIM AUTHENTICATE operation.
/// Either a successful vector (RES / CK / IK) or synchronization-failure
/// evidence (AUTS, always 14 bytes).
/// </summary>
/// <remarks>
/// Implementations must never place CK, IK, RAND, AUTN or AUTS into exception
/// messages or logs. <see cref="ToString"/> is overridden to redact them.
/// </remarks>
public sealed class AkaResult
{
    public bool Success { get; }
    public bool SynchronizationFailure { get; }
    public string? ErrorMessage { get; }

    private readonly byte[]? _res;
    private readonly byte[]? _ck;
    private readonly byte[]? _ik;
    private readonly byte[]? _auts;

    private AkaResult(bool success, bool syncFailure, byte[]? res, byte[]? ck, byte[]? ik, byte[]? auts, string? error)
    {
        Success = success;
        SynchronizationFailure = syncFailure;
        _res = res; _ck = ck; _ik = ik; _auts = auts;
        ErrorMessage = error;
    }

    /// <summary>Authentication response (4..16 bytes). Null unless <see cref="Success"/>.</summary>
    public byte[]? Res
    {
        get
        {
            if (!Success) throw new InvalidOperationException("AKA did not succeed; RES is unavailable.");
            return _res;
        }
    }

    /// <summary>Confidentiality key (16 bytes). Null unless <see cref="Success"/>.</summary>
    public byte[]? Ck
    {
        get
        {
            if (!Success) throw new InvalidOperationException("AKA did not succeed; CK is unavailable.");
            return _ck;
        }
    }

    /// <summary>Integrity key (16 bytes). Null unless <see cref="Success"/>.</summary>
    public byte[]? Ik
    {
        get
        {
            if (!Success) throw new InvalidOperationException("AKA did not succeed; IK is unavailable.");
            return _ik;
        }
    }

    /// <summary>Resynchronization token (14 bytes). Null unless <see cref="SynchronizationFailure"/>.</summary>
    public byte[]? Auts
    {
        get
        {
            if (!SynchronizationFailure) throw new InvalidOperationException("AKA did not fail with synchronization error; AUTS is unavailable.");
            return _auts;
        }
    }

    public static AkaResult Succeeded(byte[] res, byte[] ck, byte[] ik) =>
        new(true, false, res, ck, ik, null, null);

    public static AkaResult SyncFailed(byte[] auts) =>
        new(false, true, null, null, null, auts, "USIM reported synchronization failure (AUTS).");

    public static AkaResult Failed(string message) =>
        new(false, false, null, null, null, null, message);

    /// <summary>Wipes key material. Call once CK/IK have been consumed.</summary>
    public void Clear()
    {
        if (_ck != null) CryptographicOperations.ZeroMemory(_ck);
        if (_ik != null) CryptographicOperations.ZeroMemory(_ik);
        if (_res != null) CryptographicOperations.ZeroMemory(_res);
    }

    public override string ToString() => Success
        ? "AkaResult(Success)"
        : $"AkaResult(Failed: {ErrorMessage})";
}

/// <summary>
/// Provides UMTS AKA authentication vectors from physical card hardware.
/// Implementations must never export SIM secrets (Ki / OPc) through this
/// interface — the card performs the Milenage computation internally.
/// </summary>
public interface IAkaProvider
{
    /// <summary>
    /// Verifies the card is present, AKA-capable, and — when <paramref name="expectedIccid"/> is
    /// supplied — that it is the expected card.
    /// </summary>
    /// <remarks>
    /// The ICCID guard matters more than it looks: selecting the wrong UICC produces USIM status
    /// word 0x9862, which is indistinguishable from a genuine network rejection and routinely
    /// sends people chasing the carrier instead of the card.
    /// </remarks>
    Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default);

    /// <summary>Runs USIM/ISIM AUTHENTICATE with the given RAND/AUTN.</summary>
    Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default);
}
