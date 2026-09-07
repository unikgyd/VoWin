using System.Numerics;
using System.Security.Cryptography;

namespace VoSharp.Ike;

/// <summary>
/// Modular exponential (MODP) Diffie-Hellman groups from RFC 3526.
/// </summary>
/// <remarks>
/// Group 2 (MODP-1024) and group 14 (MODP-2048) are the two groups carrier ePDGs
/// actually accept; voCore offers MODP-2048 first because the KE payload is taken from
/// the first proposal, and falls back to 1024 only for legacy deployments.
/// </remarks>
public sealed class ModpGroup : IIkeDhKeyExchange
{

    private readonly BigInteger _prime;
    private readonly BigInteger _generator;
    private readonly BigInteger _private;
    private readonly int _keyLength;
    private bool _disposed;

    public ushort GroupId { get; }

    /// <summary>Public value to place in the KE payload, left-padded to the group size.</summary>
    public byte[] Public { get; }

    private ModpGroup(ushort groupId, BigInteger prime, BigInteger generator, int keyLength)
    {
        GroupId = groupId;
        _prime = prime;
        _generator = generator;
        _keyLength = keyLength;

        _private = SamplePrivate(prime);
        Public = ToFixedLength(BigInteger.ModPow(generator, _private, prime), keyLength);
    }

    /// <summary>Creates a keypair for group 2 (MODP-1024, 128 bytes).</summary>
    public static ModpGroup Create1024() =>
        new(IkeDhGroupId.Modp1024, Prime1024, Generator, 128);

    /// <summary>Creates a keypair for group 14 (MODP-2048, 256 bytes). Preferred.</summary>
    public static ModpGroup Create2048() =>
        new(IkeDhGroupId.Modp2048, Prime2048, Generator, 256);

    /// <summary>Creates a keypair for the requested group.</summary>
    public static ModpGroup Create(ushort groupId) => groupId switch
    {
        IkeDhGroupId.Modp1024 => Create1024(),
        IkeDhGroupId.Modp2048 => Create2048(),
        _ => throw new NotSupportedException(
            $"MODP group {groupId} is not implemented. VoWiFi ePDGs use 2 (MODP-1024) or 14 (MODP-2048).")
    };

    /// <summary>
    /// Computes the shared secret from the peer's public value.
    /// </summary>
    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> peerPublic)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var peer = new BigInteger(peerPublic, isUnsigned: true, isBigEndian: true);

        if (peer <= BigInteger.One || peer >= _prime - 1)
            throw new IkeFormatException("Peer DH public value is outside the valid range.");

        return ToFixedLength(BigInteger.ModPow(peer, _private, _prime), _keyLength);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(Public);
    }

    private static BigInteger SamplePrivate(BigInteger prime)
    {
        // Rejection-sample uniformly from [2, prime-2].
        //
        // The sample must be drawn with the *prime's* bit length, not more: a wider buffer
        // yields candidates that are always larger than the prime, so the acceptance test
        // never passes and the loop never terminates. Excess high bits are masked off so
        // the candidate is < 2^bitLength, keeping the rejection rate near 50%.
        var bitLength = (int)prime.GetBitLength();
        var byteLength = (bitLength + 7) / 8;
        var bytes = new byte[byteLength];
        var excessBits = byteLength * 8 - bitLength;

        while (true)
        {
            RandomNumberGenerator.Fill(bytes);

            if (excessBits > 0)
                bytes[0] &= (byte)(0xFF >> excessBits);

            var candidate = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            if (candidate > 1 && candidate < prime - 1)
                return candidate;
        }
    }

    /// <summary>
    /// Converts to a fixed-length big-endian buffer. Leading zeros matter: the KE payload
    /// must be exactly the group size or the ePDG rejects the proposal.
    /// </summary>
    private static byte[] ToFixedLength(BigInteger value, int length)
    {
        var buffer = new byte[length];
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > length)
            throw new IkeFormatException($"DH value does not fit in {length} bytes.");

        Buffer.BlockCopy(bytes, 0, buffer, length - bytes.Length, bytes.Length);
        return buffer;
    }

    private static readonly BigInteger Generator = 2;

    /// <summary>RFC 3526 §2 — 1024-bit MODP group ("group 2").</summary>
    private static readonly BigInteger Prime1024 = ParseHex(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381" +
        "FFFFFFFFFFFFFFFF");

    /// <summary>RFC 3526 §3 — 2048-bit MODP group ("group 14").</summary>
    private static readonly BigInteger Prime2048 = ParseHex(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3D" +
        "C2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D" +
        "670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9" +
        "DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AACAA68FFFFFFFFFFFFFFFF");

    private static BigInteger ParseHex(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);
}
