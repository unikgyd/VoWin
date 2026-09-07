using System.Security.Cryptography;
using VoSharp.Common.Aka;
using VoSharp.Crypto;

namespace VoSharp.Sim;

/// <summary>
/// Software Milenage AKA. Requires the long-term key K and OPc, so it only works with test cards —
/// a real USIM never discloses K.
/// </summary>
/// <remarks>
/// This provider cannot verify AUTN (it holds no SQN state), so it accepts any challenge. It exists
/// to exercise the IKEv2 / EAP-AKA and IMS code paths without hardware.
/// </remarks>
public sealed class SoftwareAkaProvider : IAkaProvider
{
    private readonly byte[] _k;
    private readonly byte[] _opc;

    public SoftwareAkaProvider(byte[] k, byte[] opc, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(k);
        ArgumentNullException.ThrowIfNull(opc);
        if (k.Length != 16 || opc.Length != 16)
            throw new ArgumentException("K and OPc must each be 16 bytes.");

        _k = k.ToArray();
        _opc = opc.ToArray();
        Description = description ?? "Software Milenage (test vectors only)";
    }

    /// <summary>3GPP TS 35.208 §4.3 test values. Do not use against a live network.</summary>
    public static SoftwareAkaProvider FromTestVectors(string? description = null) => new(
        Convert.FromHexString("465b5ce8b199b49faa5f0a2ee238a6bc"),
        Convert.FromHexString("cd63cb71954a9f4e48a5994e37a02baf"),
        description ?? "Software Milenage (3GPP TS 35.208 §4.3 test vectors)");

    public string Description { get; }

    public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var (res, ck, ik, _, _) = Milenage.ComputeF2345(_opc, _k, challenge.Rand);

        // MILENAGE yields a 16-byte RES by default; profiles without extended RES use 8.
        var trimmed = res.Length > 8 ? res[..8] : res;
        return Task.FromResult(AkaResult.Succeeded(trimmed, ck, ik));
    }

    /// <summary>Wipes the stored K and OPc.</summary>
    public void Clear()
    {
        CryptographicOperations.ZeroMemory(_k);
        CryptographicOperations.ZeroMemory(_opc);
    }
}
