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
    private sealed class Context(long generation, SecurityAgreement agreement, byte[] auth, byte[] enc) : IDisposable
    {
        public long Generation { get; } = generation;
        public SecurityAgreement Agreement { get; } = agreement;
        public SecurityProposal Proposal => Agreement.Selected;
        public EspSa ClientInbound { get; } = new(agreement.Selected.SpiClient, auth, enc, agreement.Selected);
        public EspSa ServerInbound { get; } = new(agreement.Selected.SpiServer, auth, enc, agreement.Selected);
        public EspSa ClientOutbound { get; } = new(agreement.PcscfServerSpi, auth, enc, agreement.Selected);
        public EspSa ServerOutbound { get; } = new(agreement.PcscfClientSpi, auth, enc, agreement.Selected);
        public DateTimeOffset? RetiresAt { get; set; }
        public void Dispose() { ClientInbound.Dispose(); ServerInbound.Dispose(); ClientOutbound.Dispose(); ServerOutbound.Dispose(); }
    }
    private readonly Dictionary<uint, (Context Context, bool Client)> _inbound = new();
    private readonly Dictionary<long, Context> _contexts = new();
    private Context? _active;
    private Context? _pending;
    private long _generation;
    private bool _disposed;
    private readonly Timer _retirementTimer;
    private SecurityProposal? _lastRenewalProposal;
    public SecurityProposal Proposal { get; private set; }
    public SecurityAgreement? Agreement { get; private set; }

    public ImsIpsecTransport(IPAddress local, IPAddress remote, SecurityProposal? proposal = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        if (local.AddressFamily != remote.AddressFamily || local.Equals(remote) ||
            local.Equals(IPAddress.Any) || local.Equals(IPAddress.IPv6Any) || remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any) ||
            local.IsIPv6Multicast || remote.IsIPv6Multicast ||
            local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (local.GetAddressBytes()[0] is >= 224 or 0 || remote.GetAddressBytes()[0] is >= 224 or 0))
            throw new ArgumentException("IMS IPsec requires distinct, same-family unicast endpoints.");
        _local = local;
        _remote = remote;
        var spis = RandomNumberGenerator.GetBytes(8);
        uint spiC = BinaryPrimitives.ReadUInt32BigEndian(spis.AsSpan(0, 4)) | 0x100;
        uint spiS = BinaryPrimitives.ReadUInt32BigEndian(spis.AsSpan(4, 4)) | 0x100;
        if (spiS == spiC) spiS ^= 0x80000000;
        int port = RandomNumberGenerator.GetInt32(20000, 30000) * 2;
        Proposal = proposal ?? new SecurityProposal("hmac-sha-1-96", "aes-cbc", spiC, spiS, port, port + 1);
        _retirementTimer = new Timer(_ => RetireExpiredContexts(DateTimeOffset.UtcNow), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public bool AcceptsPort(int port)
    {
        lock (_gate) return (_active is null && port == 5060) ||
            _contexts.Values.Any(context => port == context.Proposal.PortClient || port == context.Proposal.PortServer);
    }

    /// <summary>Reserve virtual parameters without replacing the active data plane.</summary>
    public SecurityProposal CreateRenewalProposal()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RetireExpiredContextsCore(DateTimeOffset.UtcNow);
            var usedSpis = _contexts.Values.SelectMany(context => Spis(context.Agreement)).Concat(_lastRenewalProposal is { } last ? new[] { last.SpiClient, last.SpiServer } : [])
                .Append(Proposal.SpiClient).Append(Proposal.SpiServer).ToHashSet();
            uint FreshSpi()
            {
                uint candidate;
                do { candidate = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)) | 0x100; } while (!usedSpis.Add(candidate));
                return candidate;
            }
            var usedPorts = _contexts.Values.Select(context => context.Proposal.PortClient)
                .Append(Proposal.PortServer).Append(Proposal.PortClient).Append(_lastRenewalProposal?.PortClient ?? 0).ToHashSet();
            int client;
            do { client = RandomNumberGenerator.GetInt32(20000, 60000); } while (usedPorts.Contains(client));
            return _lastRenewalProposal = Proposal with { SpiClient = FreshSpi(), SpiServer = FreshSpi(), PortClient = client };
        }
    }

    public void Activate(SecurityAgreement agreement, byte[] ck, byte[] ik)
    {
        var (auth, enc) = SecurityAgreementBuilder.ExpandKeys(ck, ik, agreement.Selected.IntegrityAlgorithm, agreement.Selected.EncryptionAlgorithm);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ValidateAgreement(agreement, replacingAll: true);
                ClearSas();
                _active = AddContext(agreement, auth, enc);
                Proposal = agreement.Selected;
                Agreement = agreement;
            }
        }
        finally { CryptographicOperations.ZeroMemory(auth); CryptographicOperations.ZeroMemory(enc); }
    }

    /// <summary>Install temporary SAs for the authenticated REGISTER only. Other transactions
    /// keep the previous committed SAs until a successful registrar response.</summary>
    public SipDatagramRoute Stage(SecurityAgreement agreement, byte[] ck, byte[] ik)
    {
        var (auth, enc) = SecurityAgreementBuilder.ExpandKeys(ck, ik, agreement.Selected.IntegrityAlgorithm, agreement.Selected.EncryptionAlgorithm);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RetireExpiredContextsCore(DateTimeOffset.UtcNow);
                ValidateAgreement(agreement, replacingAll: false);
                if (_pending is { } previousPending) RemoveContext(previousPending);
                if (_contexts.Count >= 8) throw new InvalidOperationException("IMS security rotation exceeded the bounded overlap window.");
                _pending = AddContext(agreement, auth, enc);
                return Route(_pending);
            }
        }
        finally { CryptographicOperations.ZeroMemory(auth); CryptographicOperations.ZeroMemory(enc); }
    }

    public void Commit(long generation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not { } pending || pending.Generation != generation)
                throw new InvalidOperationException("IMS registration did not select the pending security generation.");
            // Covers the application's 180 s INVITE deadline plus 64 s cached ACK/server
            // transaction window. Old routes remain pinned, not silently re-encrypted.
            if (_active is { } previous) previous.RetiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            _active = pending;
            _pending = null;
            Proposal = pending.Proposal;
            Agreement = pending.Agreement;
        }
    }

    private Context AddContext(SecurityAgreement agreement, byte[] auth, byte[] enc)
    {
        ValidateAgreement(agreement, replacingAll: false);
        var context = new Context(checked(++_generation), agreement, auth, enc);
        _contexts.Add(context.Generation, context);
        _inbound.Add(context.Proposal.SpiClient, (context, true));
        _inbound.Add(context.Proposal.SpiServer, (context, false));
        return context;
    }
    private static uint[] Spis(SecurityAgreement agreement) => [agreement.Selected.SpiClient, agreement.Selected.SpiServer, agreement.PcscfClientSpi, agreement.PcscfServerSpi];
    private void ValidateAgreement(SecurityAgreement agreement, bool replacingAll)
    {
        static bool Port(int value) => value is > 1024 and <= 65535 and not 5060 and not 5061;
        var proposal = agreement.Selected;
        var spis = Spis(agreement);
        if (spis.Any(spi => spi < 256) || spis.Distinct().Count() != 4 ||
            !Port(proposal.PortClient) || !Port(proposal.PortServer) || proposal.PortClient == proposal.PortServer ||
            !Port(agreement.PcscfClientPort) || !Port(agreement.PcscfServerPort) || agreement.PcscfClientPort == agreement.PcscfServerPort ||
            proposal.IntegrityAlgorithm is not ("hmac-sha-1-96" or "hmac-md5-96") ||
            proposal.EncryptionAlgorithm is not ("aes-cbc" or "des-ede3-cbc" or "null"))
            throw new InvalidOperationException("Invalid IMS security agreement selectors or algorithms.");
        if (!replacingAll && _contexts.Values.Any(context => Spis(context.Agreement).Intersect(spis).Any() ||
            context.Proposal.PortClient == proposal.PortClient || context.Proposal.PortServer != proposal.PortServer))
            throw new InvalidOperationException("IMS security generation reuses an SPI/client port or changes the listening port.");
    }

    private SipDatagramRoute Route(Context context) => new(new(_local, context.Proposal.PortClient),
        new(_remote, context.Agreement.PcscfServerPort), context.Generation);

    public (SipDatagramRoute Route, string SecurityVerify)? GetActiveRoute()
    {
        lock (_gate) return _active is { } active ? (Route(active), active.Agreement.VerifyValue) : null;
    }

    internal void RetireExpiredContexts(DateTimeOffset now)
    {
        lock (_gate) { if (!_disposed) RetireExpiredContextsCore(now); }
    }
    private void RetireExpiredContextsCore(DateTimeOffset now)
    {
        foreach (var context in _contexts.Values.Where(context => context.RetiresAt <= now).ToArray()) RemoveContext(context);
    }
    private void RemoveContext(Context context)
    {
        _contexts.Remove(context.Generation);
        _inbound.Remove(context.Proposal.SpiClient);
        _inbound.Remove(context.Proposal.SpiServer);
        if (ReferenceEquals(_pending, context)) _pending = null;
        context.Dispose();
    }

    public byte[] Protect(SipDatagram datagram)
    {
        var plain = IpPacketUtils.BuildUdpPacket(datagram.LocalEndPoint.Address, datagram.RemoteEndPoint.Address,
            (ushort)datagram.LocalEndPoint.Port, (ushort)datagram.RemoteEndPoint.Port, datagram.Payload);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RetireExpiredContextsCore(DateTimeOffset.UtcNow);
            var context = datagram.SecurityGeneration is { } generation
                ? _contexts.GetValueOrDefault(generation) ?? throw new InvalidOperationException("IMS transaction security generation has expired or was not installed.")
                : _active;
            if (context is null)
            {
                if (_contexts.Values.Any(candidate => candidate.Proposal.PortClient == datagram.LocalEndPoint.Port || candidate.Proposal.PortServer == datagram.LocalEndPoint.Port))
                    throw new InvalidOperationException("An uncommitted IMS port requires an explicit temporary security generation.");
                return plain;
            }
            if (!datagram.LocalEndPoint.Address.Equals(_local) || !datagram.RemoteEndPoint.Address.Equals(_remote))
                throw new InvalidOperationException("IMS IPsec endpoint does not match the negotiated P-CSCF.");
            var sa = datagram.LocalEndPoint.Port == context.Proposal.PortClient ? context.ClientOutbound :
                datagram.LocalEndPoint.Port == context.Proposal.PortServer ? context.ServerOutbound : null;
            if (sa == null) throw new InvalidOperationException("No IMS SA for the local SIP port.");
            var expectedRemotePort = datagram.LocalEndPoint.Port == context.Proposal.PortClient
                ? context.Agreement.PcscfServerPort : context.Agreement.PcscfClientPort;
            if (datagram.RemoteEndPoint.Port != expectedRemotePort)
                throw new InvalidOperationException("IMS protected SIP destination port does not match the negotiated SA selector.");
            var headerLength = _local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 20 : 40;
            var protectedPayload = sa.Seal(plain.AsSpan(headerLength).ToArray());
            return _local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? IpPacketUtils.ReplaceIpv4Payload(plain, 50, protectedPayload)
                : IpPacketUtils.ReplaceIpv6Payload(plain, 50, protectedPayload);
        }
    }

    public byte[] Unprotect(byte[] inner) => Unprotect(inner, out _);

    public byte[] Unprotect(byte[] inner, out SipDatagramRoute? responseRoute)
    {
        responseRoute = null;
        var isIpv6 = inner.Length > 0 && inner[0] >> 4 == 6;
        var (ihl, total, protocol) = isIpv6
            ? IpPacketUtils.ReadIpv6Header(inner)
            : IpPacketUtils.ReadIpv4Header(inner);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RetireExpiredContextsCore(DateTimeOffset.UtcNow);
            if (protocol != 50)
            {
                if (protocol == 17 && _contexts.Values.Any(context =>
                    IpPacketUtils.ParseUdpPacket(inner).LocalEndPoint.Port == context.Proposal.PortClient ||
                    IpPacketUtils.ParseUdpPacket(inner).LocalEndPoint.Port == context.Proposal.PortServer))
                    throw new FormatException("Unprotected packet on an active IMS IPsec port.");
                return inner; // RTP remains outside the IMS signalling SA.
            }
            IpPacketUtils.ValidateImsOuterHeader(inner, ihl, total);
            var source = isIpv6 ? new IPAddress(inner.AsSpan(8, 16)) : new IPAddress(inner.AsSpan(12, 4));
            var destination = isIpv6 ? new IPAddress(inner.AsSpan(24, 16)) : new IPAddress(inner.AsSpan(16, 4));
            if (total - ihl < 8 || !source.Equals(_remote) || !destination.Equals(_local))
                throw new FormatException("Invalid IMS ESP endpoint or length.");
            var esp = inner.AsSpan(ihl, total - ihl).ToArray();
            uint spi = BinaryPrimitives.ReadUInt32BigEndian(esp);
            if (!_inbound.TryGetValue(spi, out var inbound)) throw new FormatException($"Unknown IMS inbound SPI {spi:x8}.");
            var context = inbound.Context;
            int expectedPort = inbound.Client ? context.Proposal.PortClient : context.Proposal.PortServer;
            int expectedRemotePort = inbound.Client ? context.Agreement.PcscfServerPort : context.Agreement.PcscfClientPort;
            var openedPayload = (inbound.Client ? context.ClientInbound : context.ServerInbound).Open(esp, (ushort)expectedRemotePort, (ushort)expectedPort, source, destination);
            var result = isIpv6
                ? IpPacketUtils.ReplaceIpv6Payload(inner, 17, openedPayload)
                : IpPacketUtils.ReplaceIpv4Payload(inner, 17, openedPayload);
            var packet = IpPacketUtils.ParseUdpPacket(result);
            if (packet.LocalEndPoint.Port != expectedPort ||
                packet.RemoteEndPoint.Port != expectedRemotePort)
                throw new FormatException("IMS ESP SPI/port selector mismatch.");
            responseRoute = Route(context);
            return result;
        }
    }

    private void ClearSas()
    {
        foreach (var context in _contexts.Values) context.Dispose();
        _contexts.Clear();
        _inbound.Clear();
        _active = _pending = null;
        Agreement = null;
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _retirementTimer.Dispose(); ClearSas(); }
    }

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

        public byte[] Open(byte[] packet, ushort expectedSourcePort, ushort expectedDestinationPort, IPAddress source, IPAddress destination)
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
                if (BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(0, 2)) != expectedSourcePort ||
                    BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(2, 2)) != expectedDestinationPort)
                    throw new FormatException("IMS ESP SPI/port selector mismatch.");
                IpPacketUtils.ValidateImsUdpPayload(source, destination, plain.AsSpan(0, length));
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
