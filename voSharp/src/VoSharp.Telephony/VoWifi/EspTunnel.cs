using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VoSharp.Telephony.VoWifi;

public class EspTunnel : IDisposable
{
    private readonly uint _outboundSpi;
    private readonly uint _inboundSpi;
    private readonly byte[] _outboundEncKey;
    private readonly byte[] _outboundAuthKey;
    private readonly byte[] _inboundEncKey;
    private readonly byte[] _inboundAuthKey;
    private readonly string _encryption;
    private readonly string _integrity;
    private readonly int _icvLength;

    private uint _outboundSeq;
    private uint _inboundHighestSeq;
    private ulong _inboundReplayWindow;
    private readonly object _lock = new();
    private bool _disposed;

    public uint OutboundSpi => _outboundSpi;
    public uint InboundSpi => _inboundSpi;

    public EspTunnel(
        uint outboundSpi,
        uint inboundSpi,
        byte[] outboundEncKey,
        byte[] outboundAuthKey,
        byte[] inboundEncKey,
        byte[] inboundAuthKey,
        string encryption = "AES-CBC-128",
        string integrity = "HMAC-SHA1-96")
    {
        _outboundSpi = outboundSpi;
        _inboundSpi = inboundSpi;
        ArgumentNullException.ThrowIfNull(outboundEncKey);
        ArgumentNullException.ThrowIfNull(outboundAuthKey);
        ArgumentNullException.ThrowIfNull(inboundEncKey);
        ArgumentNullException.ThrowIfNull(inboundAuthKey);
        _outboundEncKey = outboundEncKey.ToArray();
        _outboundAuthKey = outboundAuthKey.ToArray();
        _inboundEncKey = inboundEncKey.ToArray();
        _inboundAuthKey = inboundAuthKey.ToArray();
        _encryption = encryption;
        _integrity = integrity;
        _icvLength = integrity.Contains("SHA256", StringComparison.OrdinalIgnoreCase) ? 16 : 12;
    }

    /// <summary>
    /// Seals an inner IPv4 datagram into an RFC 4303 ESP packet.
    /// </summary>
    public byte[] Seal(byte[] innerPacket, byte nextHeader = 4) // 4 = IPv4 encapsulation
    {
        ArgumentNullException.ThrowIfNull(innerPacket);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_outboundSeq == uint.MaxValue)
                throw new InvalidOperationException("ESP outbound sequence exhausted; rekey the SA before sending more packets.");

            uint seq = ++_outboundSeq;

            if (nextHeader is not 4 and not 41)
                throw new ArgumentOutOfRangeException(nameof(nextHeader), "ESP tunnel mode supports only IPv4 or IPv6 inner packets.");

            const int blockSize = 16;
            int padLen = (blockSize - ((innerPacket.Length + 2) % blockSize)) % blockSize;
            byte[]? plaintext = new byte[innerPacket.Length + padLen + 2];
            byte[]? iv = new byte[blockSize];
            byte[]? ciphertext = null;
            byte[]? fullHash = null;
            byte[]? icv = null;

            try
            {
                Buffer.BlockCopy(innerPacket, 0, plaintext, 0, innerPacket.Length);
                for (int i = 0; i < padLen; i++)
                    plaintext[innerPacket.Length + i] = (byte)(i + 1);
                plaintext[^2] = (byte)padLen;
                plaintext[^1] = nextHeader;
                RandomNumberGenerator.Fill(iv);

                using (var aes = Aes.Create())
                {
                    aes.Key = _outboundEncKey;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;
                    aes.IV = iv;
                    using var encryptor = aes.CreateEncryptor();
                    ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }

                int authLen = 8 + blockSize + ciphertext.Length;
                var espPacket = new byte[authLen + _icvLength];
                BinaryPrimitives.WriteUInt32BigEndian(espPacket.AsSpan(0, 4), _outboundSpi);
                BinaryPrimitives.WriteUInt32BigEndian(espPacket.AsSpan(4, 4), seq);
                Buffer.BlockCopy(iv, 0, espPacket, 8, blockSize);
                Buffer.BlockCopy(ciphertext, 0, espPacket, 8 + blockSize, ciphertext.Length);

                if (_integrity.Contains("SHA256", StringComparison.OrdinalIgnoreCase))
                {
                    using var hmac = new HMACSHA256(_outboundAuthKey);
                    fullHash = hmac.ComputeHash(espPacket, 0, authLen);
                }
                else
                {
                    using var hmac = new HMACSHA1(_outboundAuthKey);
                    fullHash = hmac.ComputeHash(espPacket, 0, authLen);
                }

                icv = fullHash.AsSpan(0, _icvLength).ToArray();
                Buffer.BlockCopy(icv, 0, espPacket, authLen, _icvLength);
                return espPacket;
            }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (iv != null) CryptographicOperations.ZeroMemory(iv);
                if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
                if (fullHash != null) CryptographicOperations.ZeroMemory(fullHash);
                if (icv != null) CryptographicOperations.ZeroMemory(icv);
            }
        }
    }

    /// <summary>
    /// Opens and decrypts an incoming RFC 4303 ESP packet back into an inner IPv4 datagram.
    /// </summary>
    public byte[]? Open(byte[] espPacket) => Open(espPacket, out _);

    /// <summary>
    /// Opens an ESP packet and returns the RFC 4303 trailer next-header value.
    /// The ePDG may carry either IPv4 (4) or IPv6 (41) inside the same CHILD_SA.
    /// </summary>
    public byte[]? Open(byte[] espPacket, out byte nextHeader)
    {
        nextHeader = 0;
        ArgumentNullException.ThrowIfNull(espPacket);
        Monitor.Enter(_lock);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

        const int blockSize = 16;
        int minLen = 8 + blockSize + blockSize + _icvLength;
        if (espPacket.Length < minLen)
            return null;

        uint spi = BinaryPrimitives.ReadUInt32BigEndian(espPacket.AsSpan(0, 4));
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(espPacket.AsSpan(4, 4));
        if (spi != _inboundSpi || sequence == 0)
            return null;

        lock (_lock)
        {
            if (!IsSequenceAcceptable(sequence))
                return null;
        }

        int authLen = espPacket.Length - _icvLength;
        int cipherLen = authLen - (8 + blockSize);
        if (cipherLen <= 0 || cipherLen % blockSize != 0)
            return null;

        var receivedIcv = espPacket.AsSpan(authLen, _icvLength);
        byte[]? calculatedIcv = null;
        byte[]? fullHash = null;
        byte[]? iv = null;
        byte[]? ciphertext = null;
        byte[]? decrypted = null;

        try
        {
            if (_integrity.Contains("SHA256", StringComparison.OrdinalIgnoreCase))
            {
                using var hmac = new HMACSHA256(_inboundAuthKey);
                fullHash = hmac.ComputeHash(espPacket, 0, authLen);
            }
            else
            {
                using var hmac = new HMACSHA1(_inboundAuthKey);
                fullHash = hmac.ComputeHash(espPacket, 0, authLen);
            }

            calculatedIcv = fullHash.AsSpan(0, _icvLength).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(receivedIcv, calculatedIcv))
                return null;

            iv = espPacket.AsSpan(8, blockSize).ToArray();
            ciphertext = espPacket.AsSpan(8 + blockSize, cipherLen).ToArray();

            using (var aes = Aes.Create())
            {
                aes.Key = _inboundEncKey;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.IV = iv;
                using var decryptor = aes.CreateDecryptor();
                decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }

            if (decrypted.Length < 2)
                return null;

            int padLen = decrypted[^2];
            int innerLen = decrypted.Length - 2 - padLen;
            if (padLen > decrypted.Length - 2 || innerLen <= 0)
                return null;

            for (int i = 0; i < padLen; i++)
            {
                if (decrypted[innerLen + i] != (byte)(i + 1))
                    return null;
            }

            nextHeader = decrypted[^1];
            if (nextHeader is not 4 and not 41)
            {
                nextHeader = 0;
                return null;
            }

            lock (_lock)
            {
                if (!IsSequenceAcceptable(sequence))
                    return null;
                CommitSequence(sequence);
            }

            return decrypted.AsSpan(0, innerLen).ToArray();
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (calculatedIcv != null) CryptographicOperations.ZeroMemory(calculatedIcv);
            if (fullHash != null) CryptographicOperations.ZeroMemory(fullHash);
            if (iv != null) CryptographicOperations.ZeroMemory(iv);
            if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
            if (decrypted != null) CryptographicOperations.ZeroMemory(decrypted);
        }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private bool IsSequenceAcceptable(uint sequence)
    {
        if (_inboundHighestSeq == 0 || sequence > _inboundHighestSeq)
            return true;

        uint delta = _inboundHighestSeq - sequence;
        return delta < 64 && (_inboundReplayWindow & (1UL << (int)delta)) == 0;
    }

    private void CommitSequence(uint sequence)
    {
        if (_inboundHighestSeq == 0)
        {
            _inboundHighestSeq = sequence;
            _inboundReplayWindow = 1;
            return;
        }

        if (sequence > _inboundHighestSeq)
        {
            uint shift = sequence - _inboundHighestSeq;
            _inboundReplayWindow = shift >= 64 ? 1 : (_inboundReplayWindow << (int)shift) | 1;
            _inboundHighestSeq = sequence;
            return;
        }

        uint delta = _inboundHighestSeq - sequence;
        _inboundReplayWindow |= 1UL << (int)delta;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_outboundEncKey);
            CryptographicOperations.ZeroMemory(_outboundAuthKey);
            CryptographicOperations.ZeroMemory(_inboundEncKey);
            CryptographicOperations.ZeroMemory(_inboundAuthKey);
            _inboundReplayWindow = 0;
            _inboundHighestSeq = 0;
        }
    }
}
