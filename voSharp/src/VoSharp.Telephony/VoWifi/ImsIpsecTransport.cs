using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using VoSharp.Sip;

namespace VoSharp.Telephony.VoWifi;

/// <summary>IMS transport-mode ESP inside the existing ePDG tunnel. No Windows kernel IPsec dependency.</summary>
public sealed class ImsIpsecTransport : IDisposable
{
    private readonly IPAddress _local;
    private readonly IPAddress _remote;
    private readonly object _gate = new();
    private readonly Dictionary<uint, EspSa> _inbound = new();
    private EspSa? _clientOutbound;
    private EspSa? _serverOutbound;
    public SecurityProposal Proposal { get; }
    public SecurityAgreement? Agreement { get; private set; }

    public ImsIpsecTransport(IPAddress local, IPAddress remote, SecurityProposal? proposal = null)
    {
        _local = local;
        _remote = remote;
        var spis = RandomNumberGenerator.GetBytes(8);
        uint spiC = BinaryPrimitives.ReadUInt32BigEndian(spis.AsSpan(0, 4)) | 0x100;
        uint spiS = BinaryPrimitives.ReadUInt32BigEndian(spis.AsSpan(4, 4)) | 0x100;
        if (spiS == spiC) spiS ^= 0x80000000;
        int port = RandomNumberGenerator.GetInt32(20000, 30000) * 2;
        Proposal = proposal ?? new SecurityProposal("hmac-sha-1-96", "aes-cbc", spiC, spiS, port, port + 1);
    }

    public bool AcceptsPort(int port) => Agreement == null
        ? port == 5060
        : port == Proposal.PortClient || port == Proposal.PortServer;

    public void Activate(SecurityAgreement agreement, byte[] ck, byte[] ik)
    {
        var (auth, enc) = SecurityAgreementBuilder.ExpandKeys(ck, ik, agreement.Selected.IntegrityAlgorithm, agreement.Selected.EncryptionAlgorithm);
        lock (_gate)
        {
            ClearSas();
            try
            {
                _inbound[Proposal.SpiClient] = new EspSa(Proposal.SpiClient, auth, enc, agreement.Selected);
                _inbound[Proposal.SpiServer] = new EspSa(Proposal.SpiServer, auth, enc, agreement.Selected);
                _clientOutbound = new EspSa(agreement.PcscfServerSpi, auth, enc, agreement.Selected);
                _serverOutbound = new EspSa(agreement.PcscfClientSpi, auth, enc, agreement.Selected);
                Agreement = agreement;
            }
            finally { CryptographicOperations.ZeroMemory(auth); CryptographicOperations.ZeroMemory(enc); }
        }
    }

    public byte[] Protect(SipDatagram datagram)
    {
        var plain = IpPacketUtils.BuildIpv4UdpPacket(datagram.LocalEndPoint.Address, datagram.RemoteEndPoint.Address,
            (ushort)datagram.LocalEndPoint.Port, (ushort)datagram.RemoteEndPoint.Port, datagram.Payload);
        lock (_gate)
        {
            if (Agreement == null) return plain;
            if (!datagram.LocalEndPoint.Address.Equals(_local) || !datagram.RemoteEndPoint.Address.Equals(_remote))
                throw new InvalidOperationException("IMS IPsec endpoint does not match the negotiated P-CSCF.");
            var sa = datagram.LocalEndPoint.Port == Proposal.PortClient ? _clientOutbound :
                datagram.LocalEndPoint.Port == Proposal.PortServer ? _serverOutbound : null;
            if (sa == null) throw new InvalidOperationException("No IMS SA for the local SIP port.");
            return IpPacketUtils.ReplaceIpv4Payload(plain, 50, sa.Seal(plain.AsSpan(20).ToArray()));
        }
    }

    public byte[] Unprotect(byte[] inner)
    {
        var (ihl, total, protocol) = IpPacketUtils.ReadIpv4Header(inner);
        lock (_gate)
        {
            if (protocol != 50)
            {
                if (Agreement != null && protocol == 17 && AcceptsPort(IpPacketUtils.ParseIpv4UdpPacket(inner).LocalEndPoint.Port))
                    throw new FormatException("Unprotected packet on an active IMS IPsec port.");
                return inner; // RTP remains outside the IMS signalling SA.
            }
            if (total - ihl < 8 || !new IPAddress(inner.AsSpan(12, 4)).Equals(_remote) ||
                !new IPAddress(inner.AsSpan(16, 4)).Equals(_local)) throw new FormatException("Invalid IMS ESP endpoint or length.");
            var esp = inner.AsSpan(ihl, total - ihl).ToArray();
            uint spi = BinaryPrimitives.ReadUInt32BigEndian(esp);
            if (!_inbound.TryGetValue(spi, out var sa)) throw new FormatException($"Unknown IMS inbound SPI {spi:x8}.");
            var result = IpPacketUtils.ReplaceIpv4Payload(inner, 17, sa.Open(esp));
            var packet = IpPacketUtils.ParseIpv4UdpPacket(result);
            int expectedPort = spi == Proposal.SpiClient ? Proposal.PortClient : Proposal.PortServer;
            if (packet.LocalEndPoint.Port != expectedPort) throw new FormatException("IMS ESP SPI/port mismatch.");
            return result;
        }
    }

    private void ClearSas()
    {
        foreach (var sa in _inbound.Values) sa.Dispose();
        _inbound.Clear();
        _clientOutbound?.Dispose();
        _serverOutbound?.Dispose();
        _clientOutbound = _serverOutbound = null;
        Agreement = null;
    }
    public void Dispose() { lock (_gate) ClearSas(); }

    private sealed class EspSa(uint spi, byte[] auth, byte[] enc, SecurityProposal selected) : IDisposable
    {
        private readonly byte[] _auth = auth.ToArray();
        private readonly byte[] _enc = enc.ToArray();
        private uint _sequence;
        private uint _highest;
        private ulong _window;
        private int BlockSize => selected.EncryptionAlgorithm switch { "null" => 4, "des-ede3-cbc" => 8, _ => 16 };
        private int IvSize => selected.EncryptionAlgorithm == "null" ? 0 : BlockSize;

        private byte[] Authenticate(byte[] data, int length) => selected.IntegrityAlgorithm == "hmac-md5-96"
            ? HMACMD5.HashData(_auth, data.AsSpan(0, length))
            : HMACSHA1.HashData(_auth, data.AsSpan(0, length));

        private byte[] Crypt(byte[] data, byte[] iv, bool encrypt)
        {
            if (IvSize == 0) return data.ToArray();
            using SymmetricAlgorithm cipher = selected.EncryptionAlgorithm == "des-ede3-cbc" ? TripleDES.Create() : Aes.Create();
            cipher.Key = _enc;
            cipher.IV = iv;
            cipher.Mode = CipherMode.CBC;
            cipher.Padding = PaddingMode.None;
            using var transform = encrypt ? cipher.CreateEncryptor() : cipher.CreateDecryptor();
            return transform.TransformFinalBlock(data, 0, data.Length);
        }

        public byte[] Seal(byte[] udp)
        {
            if (_sequence == uint.MaxValue) throw new InvalidOperationException("IMS ESP SA requires rekey.");
            int padding = (BlockSize - (udp.Length + 2) % BlockSize) % BlockSize;
            var plain = new byte[udp.Length + padding + 2];
            udp.CopyTo(plain, 0);
            for (int i = 0; i < padding; i++) plain[udp.Length + i] = (byte)(i + 1);
            plain[^2] = (byte)padding;
            plain[^1] = 17;
            var iv = RandomNumberGenerator.GetBytes(IvSize);
            var cipher = Crypt(plain, iv, true);
            CryptographicOperations.ZeroMemory(plain);
            var result = new byte[8 + iv.Length + cipher.Length + 12];
            BinaryPrimitives.WriteUInt32BigEndian(result, spi);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), ++_sequence);
            iv.CopyTo(result, 8);
            cipher.CopyTo(result, 8 + iv.Length);
            Authenticate(result, result.Length - 12).AsSpan(0, 12).CopyTo(result.AsSpan(result.Length - 12));
            return result;
        }

        public byte[] Open(byte[] packet)
        {
            int cipherLength = packet.Length - 8 - IvSize - 12;
            if (cipherLength < BlockSize || cipherLength % BlockSize != 0) throw new FormatException("Invalid IMS ESP size.");
            var expected = Authenticate(packet, packet.Length - 12);
            if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 12), packet.AsSpan(packet.Length - 12)))
                throw new CryptographicException("IMS ESP integrity check failed.");
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
            if (sequence == 0 || sequence <= _highest && (_highest - sequence >= 64 || (_window & (1UL << (int)(_highest - sequence))) != 0))
                throw new FormatException("IMS ESP replay rejected.");
            var plain = Crypt(packet.AsSpan(8 + IvSize, cipherLength).ToArray(), packet.AsSpan(8, IvSize).ToArray(), false);
            try
            {
                int length = plain.Length - 2 - plain[^2];
                if (length < 8 || plain[^1] != 17) throw new FormatException("Invalid IMS ESP UDP trailer.");
                for (int i = 0; i < plain[^2]; i++)
                    if (plain[length + i] != i + 1) throw new FormatException("Invalid IMS ESP padding.");
                if (sequence > _highest)
                {
                    uint shift = sequence - _highest;
                    _window = shift >= 64 ? 1 : (_window << (int)shift) | 1;
                    _highest = sequence;
                }
                else _window |= 1UL << (int)(_highest - sequence);
                return plain.AsSpan(0, length).ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        public void Dispose() { CryptographicOperations.ZeroMemory(_auth); CryptographicOperations.ZeroMemory(_enc); }
    }
}
