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

        var result = new byte[length];
        var offset = 0;
        byte[]? previous = null;

        for (byte counter = 1; offset < length; counter++)
        {
            if (counter == 0)
                throw new InvalidOperationException("IKE PRF+ output length exceeds maximum block count.");

            var inputLength = (previous?.Length ?? 0) + seed.Length + 1;
            var input = new byte[inputLength];
            var inputOffset = 0;

            if (previous != null)
            {
                Buffer.BlockCopy(previous, 0, input, inputOffset, previous.Length);
                inputOffset += previous.Length;
            }

            Buffer.BlockCopy(seed, 0, input, inputOffset, seed.Length);
            inputOffset += seed.Length;
            input[inputOffset] = counter;

            var block = Prf(suite, key, input);
            var toCopy = Math.Min(block.Length, length - offset);
            Buffer.BlockCopy(block, 0, result, offset, toCopy);
            offset += toCopy;
            previous = block;
        }

        return result;
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
        var stream = PrfPlus(suite, skeyseed, seed, total);

        var at = 0;
        byte[] Take(int len)
        {
            var buf = new byte[len];
            Buffer.BlockCopy(stream, at, buf, 0, len);
            at += len;
            return buf;
        }

        return new IkeKeys(
            SkD: Take(prfLength),
            SkAi: Take(integKeyLength),
            SkAr: Take(integKeyLength),
            SkEi: Take(encKeyLength),
            SkEr: Take(encKeyLength),
            SkPi: Take(prfLength),
            SkPr: Take(prfLength)
        );
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

        var encKeyLength = childSuite.EncryptionKeyLength;
        var integKeyLength = childSuite.IntegrityKeyLength;

        var seed = new byte[initiatorNonce.Length + responderNonce.Length];
        Buffer.BlockCopy(initiatorNonce, 0, seed, 0, initiatorNonce.Length);
        Buffer.BlockCopy(responderNonce, 0, seed, initiatorNonce.Length, responderNonce.Length);

        var total = 2 * (encKeyLength + integKeyLength);
        var stream = PrfPlus(ikeSuite, skD, seed, total);

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
        return truncated;
    }

    /// <summary>
    /// Encrypts an inner payload chain into an Encrypted (SK) payload wrapped in an IKE message.
    /// </summary>
    public static byte[] EncryptPayloads(
        IkeWire.IkeMessage header,
        IReadOnlyList<IkePayload> innerPayloads,
        IkeSuite suite,
        byte[] encryptionKey,
        byte[] integrityKey)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(innerPayloads);
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(encryptionKey);
        ArgumentNullException.ThrowIfNull(integrityKey);

        // RFC 7296 §1.4 defines an empty encrypted INFORMATIONAL request as
        // the authenticated dead-peer-detection exchange. Its SK next-payload
        // field is zero and the encrypted plaintext contains only padding.
        // Other exchanges still require at least one inner payload.
        if (innerPayloads.Count == 0 && header.Exchange != IkeExchangeType.Informational)
            throw new ArgumentException("At least one inner payload is required outside an IKE INFORMATIONAL exchange.", nameof(innerPayloads));

        var firstPayloadType = innerPayloads.Count == 0 ? IkePayloadType.None : innerPayloads[0].Type;
        var plaintext = IkeWire.SerializePayloadChain(innerPayloads);

        const int blockSize = IkeSuite.BlockSize; // 16 for AES
        var remainder = (plaintext.Length + 1) % blockSize;
        var paddingLength = remainder == 0 ? 0 : blockSize - remainder;

        var paddedPlaintext = new byte[plaintext.Length + paddingLength + 1];
        Buffer.BlockCopy(plaintext, 0, paddedPlaintext, 0, plaintext.Length);
        if (paddingLength > 0)
        {
            RandomNumberGenerator.Fill(paddedPlaintext.AsSpan(plaintext.Length, paddingLength));
        }
        paddedPlaintext[^1] = (byte)paddingLength;

        var iv = new byte[blockSize];
        RandomNumberGenerator.Fill(iv);

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = encryptionKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var encryptor = aes.CreateEncryptor(encryptionKey, iv);
            ciphertext = encryptor.TransformFinalBlock(paddedPlaintext, 0, paddedPlaintext.Length);
        }

        var checksumLength = suite.ChecksumLength;
        var skLength = IkeDefaults.GenericPayloadHeaderLength + iv.Length + ciphertext.Length + checksumLength;
        if (skLength > 65535)
            throw new InvalidOperationException("Encrypted payload exceeds maximum size of 65535 bytes.");

        var skBody = new byte[skLength];
        skBody[0] = (byte)firstPayloadType;
        skBody[1] = 0; // Critical / Reserved
        BinaryPrimitives.WriteUInt16BigEndian(skBody.AsSpan(2, 2), (ushort)skLength);
        Buffer.BlockCopy(iv, 0, skBody, 4, iv.Length);
        Buffer.BlockCopy(ciphertext, 0, skBody, 4 + iv.Length, ciphertext.Length);

        var totalMessageLength = IkeDefaults.HeaderLength + skLength;
        var messageBuffer = new byte[totalMessageLength];
        var span = messageBuffer.AsSpan();

        BinaryPrimitives.WriteUInt64BigEndian(span[..8], header.InitiatorSpi);
        BinaryPrimitives.WriteUInt64BigEndian(span[8..16], header.ResponderSpi);
        span[16] = (byte)IkePayloadType.Encrypted;
        span[17] = header.Version != 0 ? header.Version : IkeDefaults.Version;
        span[18] = (byte)header.Exchange;
        span[19] = (byte)header.Flags;
        BinaryPrimitives.WriteUInt32BigEndian(span[20..24], header.MessageId);
        BinaryPrimitives.WriteUInt32BigEndian(span[24..28], (uint)totalMessageLength);
        Buffer.BlockCopy(skBody, 0, messageBuffer, IkeDefaults.HeaderLength, skLength);

        // Compute ICV over header + SK payload body up to before the checksum
        var macInput = messageBuffer.AsSpan(0, totalMessageLength - checksumLength);
        var icv = IntegrityMac(suite, integrityKey, macInput);
        Buffer.BlockCopy(icv, 0, messageBuffer, totalMessageLength - checksumLength, checksumLength);

        return messageBuffer;
    }

    /// <summary>
    /// Decrypts an encrypted IKE message and verifies its integrity checksum.
    /// Returns the parsed message header and inner payloads.
    /// </summary>
    public static (IkeWire.IkeMessage Header, List<IkePayload> Payloads) DecryptPayloads(
        ReadOnlySpan<byte> packet,
        IkeSuite suite,
        byte[] encryptionKey,
        byte[] integrityKey)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(encryptionKey);
        ArgumentNullException.ThrowIfNull(integrityKey);

        if (packet.Length < IkeDefaults.HeaderLength)
            throw new IkeFormatException($"Packet too short for IKE header ({packet.Length} bytes).");

        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(24, 4));
        if (declaredLength != packet.Length)
            throw new IkeFormatException($"Declared message length {declaredLength} != actual buffer {packet.Length}.");

        var nextPayload = (IkePayloadType)packet[16];
        if (nextPayload != IkePayloadType.Encrypted)
            throw new IkeFormatException($"Expected Encrypted payload (46), got {nextPayload}.");

        var checksumLength = suite.ChecksumLength;
        const int blockSize = IkeSuite.BlockSize;
        var minLength = IkeDefaults.HeaderLength + IkeDefaults.GenericPayloadHeaderLength + blockSize + blockSize + checksumLength;
        if (packet.Length < minLength)
            throw new IkeFormatException($"Encrypted packet is too short ({packet.Length} bytes, minimum {minLength}).");

        // Verify ICV using constant-time comparison
        var expectedIcv = IntegrityMac(suite, integrityKey, packet[..^checksumLength]);
        var actualIcv = packet[^checksumLength..];
        if (!CryptographicOperations.FixedTimeEquals(expectedIcv, actualIcv))
            throw new CryptographicException("IKE encrypted payload ICV integrity check failed.");

        var skBody = packet[IkeDefaults.HeaderLength..];
        var innerFirstPayloadType = (IkePayloadType)skBody[0];
        var skDeclaredLength = BinaryPrimitives.ReadUInt16BigEndian(skBody.Slice(2, 2));
        if (skDeclaredLength != skBody.Length)
            throw new IkeFormatException($"SK declared length {skDeclaredLength} != actual body length {skBody.Length}.");

        var iv = skBody.Slice(4, blockSize).ToArray();
        var ciphertextLength = skBody.Length - 4 - blockSize - checksumLength;
        if (ciphertextLength <= 0 || ciphertextLength % blockSize != 0)
            throw new IkeFormatException($"Ciphertext length {ciphertextLength} is invalid or not block aligned.");

        var ciphertext = skBody.Slice(4 + blockSize, ciphertextLength).ToArray();

        byte[] plaintext;
        using (var aes = Aes.Create())
        {
            aes.Key = encryptionKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor(encryptionKey, iv);
            plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        }

        var paddingLength = (int)plaintext[^1];
        if (paddingLength + 1 > plaintext.Length)
            throw new IkeFormatException($"Invalid encrypted payload padding length: {paddingLength}.");

        var payloadBytes = plaintext.AsSpan(0, plaintext.Length - paddingLength - 1);
        var payloads = IkeWire.ParsePayloadChain(payloadBytes, innerFirstPayloadType);

        var header = new IkeWire.IkeMessage
        {
            InitiatorSpi = BinaryPrimitives.ReadUInt64BigEndian(packet[..8]),
            ResponderSpi = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(8, 8)),
            Version = packet[17],
            Exchange = (IkeExchangeType)packet[18],
            Flags = (IkeFlags)packet[19],
            MessageId = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(20, 4))
        };

        return (header, payloads);
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
