using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class HostImsUserspaceEspSessionTests
{
    private sealed class FakeRawIo : IHostImsRawEspIo
    {
        internal readonly Queue<byte[]> Inbound = new();
        internal readonly List<byte[]> Outbound = [];
        internal bool Disposed;

        public void Send(byte[] packet) => Outbound.Add(packet);
        public byte[] Receive(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Inbound.Dequeue();
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void ProtectedHostLegUsesExistingCSharpEspAndNeverFallsBackToPlainUdp()
    {
        var local = IPAddress.Parse("2001:db8:1::10");
        var pcscf = IPAddress.Parse("2001:db8:1::20");
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            1100, 1200, 40000, 40001);
        var agreement = new SecurityAgreement(proposal, "verify", 1001, 1002,
            41000, 41001);
        var key = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        var io = new FakeRawIo();
        using (var host = new HostImsUserspaceEspSession(local, pcscf, io, proposal))
        {
            var request = new SipDatagram("REGISTER"u8.ToArray(),
                new(local, 40000), new(pcscf, 41001));
            Assert.Throws<InvalidOperationException>(() => host.Send(request));
            Assert.Empty(io.Outbound);
            host.Activate(agreement, key, key);
            host.Send(request);
            Assert.Single(io.Outbound);
            Assert.Equal(50, io.Outbound[0][6]);
            Assert.Equal(-1, io.Outbound[0].AsSpan().IndexOf("REGISTER"u8));

            var peerProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
                1001, 1002, 41000, 41001);
            using var peer = new ImsIpsecTransport(pcscf, local, peerProposal);
            peer.Activate(new SecurityAgreement(peerProposal, "verify", 1100, 1200,
                40000, 40001), key, key);
            Assert.Equal("REGISTER"u8.ToArray(),
                IpPacketUtils.ParseUdpPacket(peer.Unprotect(io.Outbound[0])).Payload);

            var response = peer.Protect(new SipDatagram("200 OK"u8.ToArray(),
                new(pcscf, 41000), new(local, 40001)));
            var tampered = response.ToArray();
            tampered[^1] ^= 1;
            io.Inbound.Enqueue(tampered);
            Assert.Throws<CryptographicException>(() => host.Receive());
            io.Inbound.Enqueue(response);
            Assert.Equal("200 OK"u8.ToArray(), host.Receive().Payload);
            io.Inbound.Enqueue(response);
            Assert.Throws<FormatException>(() => host.Receive());
        }
        Assert.True(io.Disposed);
    }

    [Theory]
    [InlineData("hmac-sha-1-96", "aes-cbc")]
    [InlineData("hmac-sha-1-96", "des-ede3-cbc")]
    [InlineData("hmac-sha-1-96", "null")]
    [InlineData("hmac-md5-96", "aes-cbc")]
    [InlineData("hmac-md5-96", "des-ede3-cbc")]
    [InlineData("hmac-md5-96", "null")]
    public void AdvertisedCarrierAlgorithmCanActivateWithoutChangingLocalSpiOrPorts(
        string integrity, string encryption)
    {
        var local = IPAddress.Parse("2001:db8:1::10");
        var pcscf = IPAddress.Parse("2001:db8:1::20");
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            1100, 1200, 40000, 40001);
        var offer = SecurityAgreementBuilder.BuildSecurityClient(proposal);
        Assert.Contains($"alg={integrity};prot=esp;mod=trans;ealg={encryption}", offer);
        var header = $"ipsec-3gpp;alg={integrity};ealg={encryption};prot=esp;mod=trans;" +
            "spi-c=1001;spi-s=1002;port-c=41000;port-s=41001";
        var agreement = Assert.IsType<SecurityAgreement>(
            SecurityAgreementBuilder.ParseSecurityServer(header, proposal));
        var key = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        var io = new FakeRawIo();
        using var host = new HostImsUserspaceEspSession(local, pcscf, io, proposal);
        Assert.Throws<InvalidOperationException>(() => host.Activate(
            agreement with { Selected = agreement.Selected with { PortClient = 40002 } }, key, key));
        Assert.Throws<InvalidOperationException>(() => host.Activate(
            agreement with { Selected = agreement.Selected with { SpiServer = 201 } }, key, key));
        Assert.Throws<InvalidOperationException>(() => host.Activate(
            agreement with { Selected = agreement.Selected with { EncryptionAlgorithm = "aes-gcm" } }, key, key));
        host.Activate(agreement, key, key);

        var peerProposal = new SecurityProposal(integrity, encryption,
            1001, 1002, 41000, 41001);
        using var peer = new ImsIpsecTransport(pcscf, local, peerProposal);
        peer.Activate(new SecurityAgreement(peerProposal, "verify", 1100, 1200,
            40000, 40001), key, key);
        host.Send(new SipDatagram("REGISTER"u8.ToArray(),
            new(local, 40000), new(pcscf, 41001)));
        Assert.Equal("REGISTER"u8.ToArray(),
            IpPacketUtils.ParseUdpPacket(peer.Unprotect(Assert.Single(io.Outbound))).Payload);
        io.Inbound.Enqueue(peer.Protect(new SipDatagram("200 OK"u8.ToArray(),
            new(pcscf, 41000), new(local, 40001))));
        Assert.Equal("200 OK"u8.ToArray(), host.Receive().Payload);
    }

    [Fact]
    public void CarrierRechallengeCanReplaceEspKeysAndAlgorithmOnReservedLocalSelectors()
    {
        var local = IPAddress.Parse("2001:db8:1::10");
        var pcscf = IPAddress.Parse("2001:db8:1::20");
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            1100, 1200, 40000, 40001);
        var firstAgreement = new SecurityAgreement(proposal, "verify", 1001, 1002,
            41000, 41001);
        var firstKey = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        var nextKey = Enumerable.Range(16, 16).Select(x => (byte)x).ToArray();
        var io = new FakeRawIo();
        using var host = new HostImsUserspaceEspSession(local, pcscf, io, proposal);
        host.Activate(firstAgreement, firstKey, firstKey);

        var firstPeerProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            1001, 1002, 41000, 41001);
        using var firstPeer = new ImsIpsecTransport(pcscf, local, firstPeerProposal);
        firstPeer.Activate(new SecurityAgreement(firstPeerProposal, "verify", 1100, 1200,
            40000, 40001), firstKey, firstKey);
        var request = new SipDatagram("REGISTER"u8.ToArray(),
            new(local, 40000), new(pcscf, 41001));
        host.Send(request);
        Assert.Equal("REGISTER"u8.ToArray(),
            IpPacketUtils.ParseUdpPacket(firstPeer.Unprotect(Assert.Single(io.Outbound))).Payload);
        var oldResponse = firstPeer.Protect(new SipDatagram("old 200"u8.ToArray(),
            new(pcscf, 41000), new(local, 40001)));

        var selected = proposal with
        {
            IntegrityAlgorithm = "hmac-md5-96",
            EncryptionAlgorithm = "des-ede3-cbc"
        };
        var nextAgreement = new SecurityAgreement(selected, "verify", 3001, 3002,
            41000, 41001);
        Assert.Throws<InvalidOperationException>(() => host.Activate(
            nextAgreement with { Selected = selected with { PortClient = 40002 } }, nextKey, nextKey));
        host.Send(request);
        Assert.Equal("REGISTER"u8.ToArray(),
            IpPacketUtils.ParseUdpPacket(firstPeer.Unprotect(io.Outbound[^1])).Payload);

        host.Activate(nextAgreement, nextKey, nextKey);
        var nextPeerProposal = new SecurityProposal("hmac-md5-96", "des-ede3-cbc",
            3001, 3002, 41000, 41001);
        using var nextPeer = new ImsIpsecTransport(pcscf, local, nextPeerProposal);
        nextPeer.Activate(new SecurityAgreement(nextPeerProposal, "verify", 1100, 1200,
            40000, 40001), nextKey, nextKey);
        host.Send(request);
        Assert.Equal("REGISTER"u8.ToArray(),
            IpPacketUtils.ParseUdpPacket(nextPeer.Unprotect(io.Outbound[^1])).Payload);
        Assert.Throws<FormatException>(() => firstPeer.Unprotect(io.Outbound[^1]));
        io.Inbound.Enqueue(oldResponse);
        Assert.Throws<CryptographicException>(() => host.Receive());
        io.Inbound.Enqueue(nextPeer.Protect(new SipDatagram("new 200"u8.ToArray(),
            new(pcscf, 41000), new(local, 40001))));
        Assert.Equal("new 200"u8.ToArray(), host.Receive().Payload);
    }

    [Fact]
    public async Task SipAkaRegisterCompletesOverActualUserspaceEspCodec()
    {
        var local = IPAddress.Parse("2001:db8:1::10");
        var pcscf = IPAddress.Parse("2001:db8:1::20");
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            1100, 1200, 40000, 40001);
        var agreement = new SecurityAgreement(proposal, "ipsec-3gpp", 1001, 1002,
            41000, 41001);
        var key = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        var io = new FakeRawIo();
        using var host = new HostImsUserspaceEspSession(local, pcscf, io, proposal);
        var peerProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            1001, 1002, 41000, 41001);
        using var peer = new ImsIpsecTransport(pcscf, local, peerProposal);
        peer.Activate(new SecurityAgreement(peerProposal, "ipsec-3gpp", 1100, 1200,
            40000, 40001), key, key);
        var inbound = Channel.CreateUnbounded<object>();
        var sawProtectedRegister = false;
        byte[]? previousProtectedResponse = null;
        var receiveErrors = 0;
        using var transport = new SipTransport(timeoutMs: 3000);
        transport.ReceiveError += (_, _) => Interlocked.Increment(ref receiveErrors);
        transport.ConnectDatagrams(new(local, 40000), new(pcscf, 5060),
            (datagram, _) =>
            {
                if (!sawProtectedRegister && datagram.RemoteEndPoint.Port == 5060)
                {
                    var initial = SipMessage.Parse(datagram.Payload);
                    Assert.Equal("REGISTER", initial.Method);
                    var challenge = initial.CreateResponse(401, "Unauthorized");
                    challenge.SetHeader("WWW-Authenticate",
                        $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
                    challenge.SetHeader("Security-Server",
                        "ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;" +
                        "spi-c=1001;spi-s=1002;port-c=41000;port-s=41001");
                    Assert.True(inbound.Writer.TryWrite(new SipDatagram(challenge.ToBytes(),
                        datagram.LocalEndPoint, datagram.RemoteEndPoint)));
                }
                else
                {
                    host.Send(datagram);
                    var raw = io.Outbound[^1];
                    Assert.Equal(50, raw[6]);
                    var authenticated = SipMessage.Parse(
                        IpPacketUtils.ParseUdpPacket(peer.Unprotect(raw)).Payload);
                    Assert.NotNull(authenticated.GetHeader("Security-Verify"));
                    Assert.Contains(authenticated.Method, new[] { "REGISTER", "INVITE" });
                    if (authenticated.Method == "REGISTER") sawProtectedRegister = true;
                    var accepted = authenticated.CreateResponse(200, "OK");
                    if (authenticated.Method == "REGISTER")
                        accepted.SetHeader("Contact", authenticated.GetHeader("Contact")!);
                    var protectedResponse = peer.Protect(new SipDatagram(accepted.ToBytes(),
                        new(pcscf, 41000), new(local, 40001)));
                    if (authenticated.Method == "REGISTER")
                    {
                        var tampered = protectedResponse.ToArray();
                        tampered[^1] ^= 1;
                        Assert.True(inbound.Writer.TryWrite(tampered));
                        previousProtectedResponse = protectedResponse;
                    }
                    else
                    {
                        Assert.NotNull(previousProtectedResponse);
                        Assert.True(inbound.Writer.TryWrite(previousProtectedResponse));
                    }
                    Assert.True(inbound.Writer.TryWrite(protectedResponse));
                }
                return Task.CompletedTask;
            },
            async token =>
            {
                var item = await inbound.Reader.ReadAsync(token);
                if (item is SipDatagram plain) return plain;
                io.Inbound.Enqueue(Assert.IsType<byte[]>(item));
                return host.Receive(token);
            });
        var profile = new ImsProfile("001010123456789@ims.test",
            "sip:001010123456789@ims.test", "ims.test", "123456789012345",
            local.ToString(), 40000);
        using var registration = new SipRegisterSession(transport, profile, proposal,
            (selected, ck, ik) =>
            {
                Assert.Equal(agreement.PcscfServerSpi, selected.PcscfServerSpi);
                host.Activate(selected, ck, ik);
                transport.SetEndpoints(new(local, proposal.PortClient),
                    new(pcscf, selected.PcscfServerPort));
            });
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> Aka(byte[] _, byte[] __, CancellationToken ___) =>
            Task.FromResult((new byte[8], key.ToArray(), key.ToArray()));

        var result = await registration.RegisterAsync(Aka).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(sawProtectedRegister);
        Assert.True(result.ExpiresSeconds > 0);

        var invite = new SipMessage { IsRequest = true, Method = "INVITE", RequestUri = "sip:peer@ims.test" };
        invite.SetHeader("Via", "SIP/2.0/UDP [2001:db8:1::10]:40000;branch=z9hG4bK-esp-invite;rport");
        invite.SetHeader("From", "<sip:001010123456789@ims.test>;tag=local");
        invite.SetHeader("To", "<sip:peer@ims.test>");
        invite.SetHeader("Call-ID", "esp-invite@ims.test");
        invite.SetHeader("CSeq", "1 INVITE");
        Assert.Equal(200, (await transport.SendAndReceiveFinalAsync(invite,
            timeoutMs: 3000).WaitAsync(TimeSpan.FromSeconds(3))).StatusCode);
        Assert.Equal(2, io.Outbound.Count);
        Assert.Equal(2, Volatile.Read(ref receiveErrors));
    }
}
