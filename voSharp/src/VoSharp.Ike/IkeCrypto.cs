using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace VoSharp.Ike;

/// <summary>
/// Key material derived for an IKE SA session (RFC 7296 §2.14).
/// </summary>
public sealed record IkeKeys(
    byte[] SkD,
    byte[] SkAi,
    byte[] SkAr,
    byte[] SkEi,
    byte[] SkEr,
    byte[] SkPi,
    byte[] SkPr
);

/// <summary>
/// Cryptographic operations for IKEv2: PRF, PRF+, key derivation,
/// payload encryption/decryption, and NAT detection hashes (RFC 7296).
/// </summary>
public static class IkeCrypto
{
    /// <summary>
    /// Computes the PRF of data using the negotiated suite's PRF algorithm.
    /// </summary>
    public static byte[] Prf(IkeSuite suite, byte[] key, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);

        return suite.PrfId switch
        {
            IkePrfId.HmacSha1 => HMACSHA1.HashData(key, data),
            IkePrfId.HmacSha2_256 => HMACSHA256.HashData(key, data),
            IkePrfId.HmacSha2_384 => HMACSHA384.HashData(key, data),
            IkePrfId.HmacSha2_512 => HMACSHA512.HashData(key, data),
            _ => throw new IkeUnsupportedSuiteException($"Unsupported PRF ID: {suite.PrfId}")
        };
    }

    /// <summary>
    /// PRF+ key stream generator (RFC 7296 §2.14).
    /// T1 = prf(K, S | 0x01)
    /// T2 = prf(K, T1 | S | 0x02)
    /// ...
    /// </summary>
    public static byte[] PrfPlus(IkeSuite suite, byte[] key, byte[] seed, int length)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(seed);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
        if (length == 0)
            return Array.Empty<byte>();
        if (length > 255 * suite.PrfLength)
            throw new InvalidOperationException("IKE PRF+ output length exceeds maximum block count.");

        var result = new byte[length];
        var offset = 0;
        byte[]? previous = null;

        try
        {
            for (byte counter = 1; offset < length; counter++)
            {
                var inputLength = (previous?.Length ?? 0) + seed.Length + 1;
                var input = new byte[inputLength];
                byte[] block;
                try
                {
                    if (previous != null) Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
                    Buffer.BlockCopy(seed, 0, input, previous?.Length ?? 0, seed.Length);
                    input[^1] = counter;
                    block = Prf(suite, key, input);
                }
                finally { CryptographicOperations.ZeroMemory(input); }
                var toCopy = Math.Min(block.Length, length - offset);
                Buffer.BlockCopy(block, 0, result, offset, toCopy);
                offset += toCopy;
                if (previous != null) CryptographicOperations.ZeroMemory(previous);
                previous = block;
            }
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
        finally { if (previous != null) CryptographicOperations.ZeroMemory(previous); }
    }

    /// <summary>
    /// Derives the 7 IKE SA keys from DH shared secret and nonces (RFC 7296 §2.14).
    /// SKEYSEED = prf(Ni | Nr, g^ir)
    /// {SK_d | SK_ai | SK_ar | SK_ei | SK_er | SK_pi | SK_pr} = prf+(SKEYSEED, Ni | Nr | SPIi | SPIr)
    /// </summary>
    public static IkeKeys DeriveIkeKeys(
        IkeSuite suite,
        byte[] sharedSecret,
        byte[] initiatorNonce,
        byte[] responderNonce,
        ulong initiatorSpi,
        ulong responderSpi)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(sharedSecret);
        ArgumentNullException.ThrowIfNull(initiatorNonce);
        ArgumentNullException.ThrowIfNull(responderNonce);

        var prfLength = suite.PrfLength;
        var encKeyLength = suite.EncryptionKeyLength;
        var integKeyLength = suite.IntegrityKeyLength;

        // Nonce key: Ni | Nr
        var nonceKey = new byte[initiatorNonce.Length + responderNonce.Length];
        Buffer.BlockCopy(initiatorNonce, 0, nonceKey, 0, initiatorNonce.Length);
        Buffer.BlockCopy(responderNonce, 0, nonceKey, initiatorNonce.Length, responderNonce.Length);

        var skeyseed = Prf(suite, nonceKey, sharedSecret);

        // Seed: Ni | Nr | SPIi | SPIr
        var seed = new byte[initiatorNonce.Length + responderNonce.Length + 16];
        Buffer.BlockCopy(initiatorNonce, 0, seed, 0, initiatorNonce.Length);
        Buffer.BlockCopy(responderNonce, 0, seed, initiatorNonce.Length, responderNonce.Length);
        BinaryPrimitives.WriteUInt64BigEndian(seed.AsSpan(initiatorNonce.Length + responderNonce.Length, 8), initiatorSpi);
        BinaryPrimitives.WriteUInt64BigEndian(seed.AsSpan(initiatorNonce.Length + responderNonce.Length + 8, 8), responderSpi);

        var total = prfLength + integKeyLength * 2 + encKeyLength * 2 + prfLength * 2;
        byte[] stream = [];
        try
        {
            stream = PrfPlus(suite, skeyseed, seed, total);
            var at = 0;
            byte[] Take(int len)
            {
                var buf = new byte[len];
                Buffer.BlockCopy(stream, at, buf, 0, len);
                at += len;
                return buf;
            }
            return new IkeKeys(
                SkD: Take(prfLength), SkAi: Take(integKeyLength), SkAr: Take(integKeyLength),
                SkEi: Take(encKeyLength), SkEr: Take(encKeyLength), SkPi: Take(prfLength), SkPr: Take(prfLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonceKey);
            CryptographicOperations.ZeroMemory(skeyseed);
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(stream);
        }
    }

    /// <summary>
    /// Derives CHILD SA (ESP) encryption and integrity keys (RFC 7296 §2.17).
    /// KEYMAT = prf+(SK_d, Ni | Nr)
    /// OutboundEnc, OutboundAuth, InboundEnc, InboundAuth
    /// </summary>
    public static (byte[] OutboundEnc, byte[] OutboundAuth, byte[] InboundEnc, byte[] InboundAuth) DeriveChildSaKeys(
        IkeSuite ikeSuite,
        EspSuite childSuite,
        byte[] skD,
        byte[] initiatorNonce,
        byte[] responderNonce)
    {
        ArgumentNullException.ThrowIfNull(ikeSuite);
        ArgumentNullException.ThrowIfNull(childSuite);
        ArgumentNullException.ThrowIfNull(skD);
        ArgumentNullException.ThrowIfNull(initiatorNonce);
        ArgumentNullException.ThrowIfNull(responderNonce);

        if (skD.Length != ikeSuite.PrfLength) throw new ArgumentException("Invalid SK_d length.");
        if (childSuite.EncryptionId != IkeEncryptionId.AesCbc) throw new IkeUnsupportedSuiteException("Unsupported CHILD_SA cipher.");
        IkeSession.ValidateInitNonce(initiatorNonce, ikeSuite); IkeSession.ValidateInitNonce(responderNonce, ikeSuite);
        var encKeyLength = childSuite.EncryptionKeyLength;
        var integKeyLength = childSuite.IntegrityKeyLength;

        var seed = new byte[initiatorNonce.Length + responderNonce.Length];
        Buffer.BlockCopy(initiatorNonce, 0, seed, 0, initiatorNonce.Length);
        Buffer.BlockCopy(responderNonce, 0, seed, initiatorNonce.Length, responderNonce.Length);

        var total = 2 * (encKeyLength + integKeyLength);
        var stream = PrfPlus(ikeSuite, skD, seed, total);

        try
        {
            var at = 0;
            byte[] Take(int len)
            {
                var buf = new byte[len];
                Buffer.BlockCopy(stream, at, buf, 0, len);
                at += len;
                return buf;
            }

            return (
                OutboundEnc: Take(encKeyLength),
                OutboundAuth: Take(integKeyLength),
                InboundEnc: Take(encKeyLength),
                InboundAuth: Take(integKeyLength)
            );
        }
        finally { CryptographicOperations.ZeroMemory(seed); CryptographicOperations.ZeroMemory(stream); }
    }

    /// <summary>
    /// Computes truncated integrity ICV checksum over message data.
    /// </summary>
    public static byte[] IntegrityMac(IkeSuite suite, byte[] key, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(key);

        var fullChecksum = suite.IntegrityId switch
        {
            IkeIntegrityId.HmacSha1_96 => HMACSHA1.HashData(key, data),
            IkeIntegrityId.HmacSha2_256_128 => HMACSHA256.HashData(key, data),
            IkeIntegrityId.HmacSha2_384_192 => HMACSHA384.HashData(key, data),
            IkeIntegrityId.HmacSha2_512_256 => HMACSHA512.HashData(key, data),
            _ => throw new IkeUnsupportedSuiteException($"Unsupported integrity algorithm: {suite.IntegrityId}")
        };

        var icvLength = suite.ChecksumLength;
        var truncated = new byte[icvLength];
        Buffer.BlockCopy(fullChecksum, 0, truncated, 0, icvLength);
        CryptographicOperations.ZeroMemory(fullChecksum);
        return truncated;
    }

    /// <summary>
    /// Encrypts an inner payload chain into an Encrypted (SK) payload wrapped in an IKE message.
    /// </summary>
    public static byte[] EncryptPayloads(
        IkeWire.IkeMessage header, IReadOnlyList<IkePayload> innerPayloads, IkeSuite suite,
        byte[] encryptionKey, byte[] integrityKey)
        => IkeProtectedMessage.Encrypt(header, innerPayloads, suite, encryptionKey, integrityKey, false, 65507)[0];

    /// <summary>Verifies and decrypts one unfragmented SK message.</summary>
    public static (IkeWire.IkeMessage Header, List<IkePayload> Payloads) DecryptPayloads(
        ReadOnlySpan<byte> packet, IkeSuite suite, byte[] encryptionKey, byte[] integrityKey)
    {
        var expected = IkeWire.ParseMessage(packet);
        using var receiver = new IkeEncryptedMessageReceiver(expected, suite, encryptionKey, integrityKey, false, TimeSpan.FromSeconds(30));
        var result = receiver.Accept(packet) ?? throw new IkeFormatException("Incomplete encrypted message.");
        return (result.Header, result.Payloads);
    }

    /// <summary>
    /// Computes NAT-D (NAT detection) hash: SHA-1(Initiator SPI | Responder SPI | IP | Port).
    /// </summary>
    public static byte[] ComputeNatDetectionHash(ulong initiatorSpi, ulong responderSpi, IPAddress ip, ushort port)
    {
        ArgumentNullException.ThrowIfNull(ip);

        var ipBytes = ip.GetAddressBytes();
        var buffer = new byte[16 + ipBytes.Length + 2];
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(0, 8), initiatorSpi);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(8, 8), responderSpi);
        Buffer.BlockCopy(ipBytes, 0, buffer, 16, ipBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(16 + ipBytes.Length, 2), port);

        return SHA1.HashData(buffer);
    }
}
