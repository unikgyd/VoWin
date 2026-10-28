using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace VoSharp.Ike;

public sealed record IkeDecryptedMessage(IkeWire.IkeMessage Header, List<IkePayload> Payloads);

/// <summary>SK/SKF framing. Each fragment has its own IV and full-message ICV.</summary>
public static class IkeProtectedMessage
{
    public const int MaximumFragments = 64;
    internal const int MaximumPlaintext = 1024 * 1024 - 28;

    internal static void ValidateKeys(IkeSuite suite, byte[] encryption, byte[] integrity)
    {
        if (suite.EncryptionId != IkeEncryptionId.AesCbc || encryption.Length != suite.EncryptionKeyLength ||
            integrity.Length != suite.IntegrityKeyLength)
            throw new CryptographicException("Invalid IKE protection algorithm or key length.");
        _ = suite.ChecksumLength;
    }

    public static IReadOnlyList<byte[]> Encrypt(
        IkeWire.IkeMessage header, IReadOnlyList<IkePayload> payloads, IkeSuite suite,
        byte[] encryption, byte[] integrity, bool fragmentationNegotiated, int maximumIkeBytes = 540)
    {
        ValidateKeys(suite, encryption, integrity);
        if ((header.Version >> 4) != 2 || header.InitiatorSpi == 0 || header.ResponderSpi == 0 ||
            header.Exchange is < IkeExchangeType.IkeAuth or > IkeExchangeType.Informational ||
            maximumIkeBytes is < 256 or > 65507 ||
            payloads.Count == 0 && header.Exchange != IkeExchangeType.Informational ||
            payloads.Any(p => p.Type is IkePayloadType.Encrypted or IkePayloadType.EncryptedFragment))
            throw new IkeFormatException("Invalid IKE protected message.");
        var plaintext = IkeWire.SerializePayloadChain(payloads);
        try
        {
            var first = payloads.Count == 0 ? IkePayloadType.None : payloads[0].Type;
            var size = 28 + 4 + 16 + (plaintext.Length + 16) / 16 * 16 + suite.ChecksumLength;
            if (size <= maximumIkeBytes || !fragmentationNegotiated)
            {
                if (size > 65507) throw new IkeFormatException("IKE encrypted datagram exceeds UDP limit.");
                return [EncryptChunk(header, suite, encryption, integrity, first, 0, 0, plaintext)];
            }
            var chunk = (maximumIkeBytes - 28 - 8 - 16 - suite.ChecksumLength) / 16 * 16 - 1;
            var count = (plaintext.Length + chunk - 1) / chunk;
            if (count is < 1 or > MaximumFragments) throw new IkeFormatException("IKE fragment count exceeds limit.");
            var packets = new List<byte[]>(count);
            for (var index = 0; index < count; index++)
                packets.Add(EncryptChunk(header, suite, encryption, integrity, index == 0 ? first : IkePayloadType.None,
                    (ushort)(index + 1), (ushort)count, plaintext.AsSpan(index * chunk, Math.Min(chunk, plaintext.Length - index * chunk))));
            return packets;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    // Deterministic IV is internal, solely for independently computed fixtures.
    internal static byte[] EncryptChunk(IkeWire.IkeMessage header, IkeSuite suite, byte[] encryption, byte[] integrity,
        IkePayloadType first, ushort number, ushort total, ReadOnlySpan<byte> plaintext, byte[]? testIv = null)
    {
        ValidateKeys(suite, encryption, integrity);
        var iv = testIv ?? RandomNumberGenerator.GetBytes(16);
        var padding = 15 - plaintext.Length % 16;
        var padded = new byte[plaintext.Length + padding + 1];
        plaintext.CopyTo(padded); padded[^1] = (byte)padding;
        try
        {
            using var aes = Aes.Create(); aes.Key = encryption;
            var encrypted = aes.EncryptCbc(padded, iv, PaddingMode.None);
            var prefix = number == 0 ? 0 : 4;
            var body = new byte[prefix + 16 + encrypted.Length + suite.ChecksumLength];
            if (number != 0)
            {
                BinaryPrimitives.WriteUInt16BigEndian(body, number);
                BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), total);
            }
            iv.CopyTo(body, prefix); encrypted.CopyTo(body, prefix + 16);
            var wire = new IkeWire.IkeMessage
            {
                InitiatorSpi = header.InitiatorSpi, ResponderSpi = header.ResponderSpi, Version = header.Version,
                Exchange = header.Exchange, Flags = header.Flags, MessageId = header.MessageId,
                Payloads = { new(number == 0 ? IkePayloadType.Encrypted : IkePayloadType.EncryptedFragment, body) { NextPayloadOverride = first } }
            };
            var packet = IkeWire.SerializeMessage(wire);
            var at = packet.Length - suite.ChecksumLength;
            IkeCrypto.IntegrityMac(suite, integrity, packet.AsSpan(0, at)).CopyTo(packet, at);
            return packet;
        }
        finally { CryptographicOperations.ZeroMemory(padded); }
    }
}

/// <summary>
/// One expected message and bounded lifetime. Wrong SPI/role/MID or bad ICV
/// cannot poison fragments. Dispose clears every buffered plaintext chunk.
/// Integrity protection alone is not final peer AUTH or a usable CHILD_SA.
/// </summary>
public sealed class IkeEncryptedMessageReceiver : IDisposable
{
    private readonly IkeWire.IkeMessage _expected;
    private readonly IkeSuite _suite;
    private readonly byte[] _encryption, _integrity;
    private readonly bool _fragmented;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _timeout;
    private byte[]?[] _pieces = [];
    private int _bytes;
    private IkePayloadType _first;
    private bool _completed, _disposed;
    internal int AssemblyGeneration { get; private set; }

    public IkeEncryptedMessageReceiver(IkeWire.IkeMessage expected, IkeSuite suite, byte[] encryption, byte[] integrity,
        bool fragmentationNegotiated, TimeSpan timeout)
    {
        IkeProtectedMessage.ValidateKeys(suite, encryption, integrity);
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (expected.InitiatorSpi == 0 || expected.ResponderSpi == 0 || expected.Version >> 4 != 2 ||
            expected.Exchange is < IkeExchangeType.IkeAuth or > IkeExchangeType.Informational)
            throw new IkeFormatException("Invalid expected protected IKE message.");
        _expected = new() { InitiatorSpi = expected.InitiatorSpi, ResponderSpi = expected.ResponderSpi,
            Exchange = expected.Exchange, Flags = expected.Flags, MessageId = expected.MessageId };
        _suite = suite; _encryption = encryption; _integrity = integrity; _fragmented = fragmentationNegotiated; _timeout = timeout;
    }

    public IkeDecryptedMessage? Accept(ReadOnlySpan<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Stopwatch.GetElapsedTime(_started) >= _timeout) { Clear(); throw new TimeoutException("IKE fragment assembly expired."); }
        if (_completed) throw new IkeFormatException("IKE message was already completed.");
        var header = IkeWire.ParseMessage(packet);
        if (header.InitiatorSpi != _expected.InitiatorSpi || header.ResponderSpi != _expected.ResponderSpi ||
            header.Exchange != _expected.Exchange || header.MessageId != _expected.MessageId ||
            ((byte)header.Flags & 0x28) != ((byte)_expected.Flags & 0x28) || header.Payloads.Count != 1)
            throw new IkeFormatException("IKE encrypted message does not match the expected transaction.");
        var payload = header.Payloads[0];
        var fragment = payload.Type == IkePayloadType.EncryptedFragment;
        if (payload.Type != IkePayloadType.Encrypted && !fragment || fragment && !_fragmented)
            throw new IkeFormatException("IKE protected payload was not negotiated.");
        var prefix = fragment ? 4 : 0;
        var body = payload.Body.AsSpan();
        if (body.Length < prefix + 32 + _suite.ChecksumLength) throw new IkeFormatException("IKE encrypted body is truncated.");
        var number = fragment ? BinaryPrimitives.ReadUInt16BigEndian(body) : 0;
        var total = fragment ? BinaryPrimitives.ReadUInt16BigEndian(body[2..]) : 0;
        var first = payload.NextPayloadOverride ?? IkePayloadType.None;
        if (fragment && (number == 0 || total == 0 || number > total || total > IkeProtectedMessage.MaximumFragments || number > 1 && first != IkePayloadType.None))
            throw new IkeFormatException("Invalid IKE fragment metadata.");
        var size = body.Length - prefix - 16 - _suite.ChecksumLength;
        if (size % 16 != 0) throw new IkeFormatException("IKE ciphertext is not block aligned.");
        var icv = IkeCrypto.IntegrityMac(_suite, _integrity, packet[..^_suite.ChecksumLength]);
        if (!CryptographicOperations.FixedTimeEquals(icv, packet[^_suite.ChecksumLength..]))
            throw new CryptographicException("IKE encrypted payload ICV integrity check failed.");
        using var aes = Aes.Create(); aes.Key = _encryption;
        var padded = aes.DecryptCbc(body.Slice(prefix + 16, size), body.Slice(prefix, 16), PaddingMode.None);
        byte[]? plain = null;
        try
        {
            var pad = padded[^1] + 1;
            if (pad > padded.Length) throw new IkeFormatException("Invalid IKE padding length.");
            var content = padded.AsSpan(0, padded.Length - pad);
            if (!fragment) return Finish(header, IkeWire.ParsePayloadChain(content, first));
            if (total < _pieces.Length) return null;
            if (total > _pieces.Length) { Clear(); _pieces = new byte[total][]; AssemblyGeneration++; }
            if (_pieces[number - 1] != null) return null;
            if (_bytes + content.Length > IkeProtectedMessage.MaximumPlaintext) throw new IkeFormatException("IKE reassembly exceeds limit.");
            plain = content.ToArray(); _pieces[number - 1] = plain; plain = null; _bytes += content.Length;
            if (number == 1) _first = first;
            if (_pieces.Any(piece => piece == null)) return null;
            var joined = new byte[_bytes];
            try
            {
                var at = 0;
                foreach (var piece in _pieces) { piece!.CopyTo(joined, at); at += piece.Length; }
                return Finish(header, IkeWire.ParsePayloadChain(joined, _first));
            }
            finally { CryptographicOperations.ZeroMemory(joined); Clear(); }
        }
        finally { CryptographicOperations.ZeroMemory(padded); if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }

    private IkeDecryptedMessage Finish(IkeWire.IkeMessage header, List<IkePayload> payloads)
    {
        if (payloads.Count == 0 && header.Exchange != IkeExchangeType.Informational ||
            payloads.Any(p => p.Type is IkePayloadType.Encrypted or IkePayloadType.EncryptedFragment))
            throw new IkeFormatException("Invalid decrypted IKE payload chain.");
        Clear(); _completed = true; header.Payloads.Clear();
        return new(header, payloads);
    }
    private void Clear()
    {
        foreach (var piece in _pieces) if (piece != null) CryptographicOperations.ZeroMemory(piece);
        _pieces = []; _bytes = 0; _first = IkePayloadType.None;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); }
}
