using System.Security.Cryptography;

namespace VoSharp.Ike;

/// <summary>Raw Diffie-Hellman key exchange used by IKE_SA_INIT.</summary>
public interface IIkeDhKeyExchange : IDisposable
{
    ushort GroupId { get; }
    byte[] Public { get; }
    byte[] ComputeSharedSecret(ReadOnlySpan<byte> peerPublic);
}

/// <summary>
/// RFC 5903 group 19 (NIST P-256). IKE carries an EC public key as X || Y,
/// each coordinate padded to 32 bytes; it does not use ASN.1/SEC1 point encoding.
/// </summary>
public sealed class Ecp256Group : IIkeDhKeyExchange
{
    private const int CoordinateLength = 32;
    private readonly ECDiffieHellman _key;
    private bool _disposed;

    public ushort GroupId => IkeDhGroupId.Ecp256;
    public byte[] Public { get; }

    private Ecp256Group(ECDiffieHellman key)
    {
        _key = key;
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (parameters.Q.X is not { Length: CoordinateLength } x ||
            parameters.Q.Y is not { Length: CoordinateLength } y)
            throw new CryptographicException("P-256 key export did not contain 32-byte coordinates.");

        Public = new byte[CoordinateLength * 2];
        Buffer.BlockCopy(x, 0, Public, 0, CoordinateLength);
        Buffer.BlockCopy(y, 0, Public, CoordinateLength, CoordinateLength);
    }

    public static Ecp256Group Create() =>
        new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> peerPublic)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (peerPublic.Length != CoordinateLength * 2)
            throw new IkeFormatException($"ECP-256 peer public key must be 64 bytes, got {peerPublic.Length}.");

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = peerPublic[..CoordinateLength].ToArray(),
                Y = peerPublic[CoordinateLength..].ToArray()
            }
        };

        using var peer = ECDiffieHellman.Create(parameters);
        // IKE key derivation needs Z itself. DeriveKeyMaterial would hash it and
        // therefore produce different SK_* values than the ePDG.
        return _key.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _key.Dispose();
        CryptographicOperations.ZeroMemory(Public);
    }
}

public static class IkeDhKeyExchange
{
    public static IIkeDhKeyExchange Create(ushort groupId) => groupId switch
    {
        IkeDhGroupId.Ecp256 => Ecp256Group.Create(),
        IkeDhGroupId.Modp1024 or IkeDhGroupId.Modp2048 => ModpGroup.Create(groupId),
        _ => throw new IkeUnsupportedSuiteException($"Diffie-Hellman group {groupId} is not implemented.")
    };
}
