using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class ImsSecurityRotationTests
{
    private static byte[] Key(byte value) => Enumerable.Repeat(value, 16).ToArray();
    private static SecurityAgreement Agreement(SecurityProposal ue, SecurityProposal peer) =>
        new(ue, "verify-" + peer.SpiServer, peer.SpiClient, peer.SpiServer, peer.PortClient, peer.PortServer);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryRegistrationDoesNotMoveLiveTrafficAndOldTransactionsSurviveCommit(bool ipv6)
    {
        var local = IPAddress.Parse(ipv6 ? "2001:db8::1" : "192.0.2.1");
        var remote = IPAddress.Parse(ipv6 ? "2001:db8::2" : "192.0.2.2");
        var oldUe = new SecurityProposal("hmac-sha-1-96", "null", 1000, 2000, 40000, 40001);
        var oldPeer = new SecurityProposal("hmac-sha-1-96", "null", 1001, 1002, 41000, 41001);
        var nextUe = oldUe with { SpiClient = 300, SpiServer = 400, PortClient = 42000 };
        var nextPeer = oldPeer with { SpiClient = 2001, SpiServer = 2002, PortClient = 43000, PortServer = 43001 };
        using var ue = new ImsIpsecTransport(local, remote, oldUe);
        using var peer = new ImsIpsecTransport(remote, local, oldPeer);
        using var newPeer = new ImsIpsecTransport(remote, local, nextPeer);
        ue.Activate(Agreement(oldUe, oldPeer), Key(1), Key(1));
        peer.Activate(Agreement(oldPeer, oldUe), Key(1), Key(1));
        newPeer.Activate(Agreement(nextPeer, nextUe), Key(2), Key(2));
        var oldRoute = ue.GetActiveRoute()!.Value.Route;
        var staged = ue.Stage(Agreement(nextUe, nextPeer), Key(2), Key(2));
        Assert.Equal(oldRoute, ue.GetActiveRoute()!.Value.Route);
        Assert.Equal(oldUe, ue.Proposal);
        Assert.True(ue.AcceptsPort(nextUe.PortClient)); // Authenticated response can reach temporary SAs.

        var normal = ue.Protect(new("normal"u8.ToArray(), oldRoute.LocalEndPoint, oldRoute.RemoteEndPoint));
        Assert.Equal("normal"u8.ToArray(), IpPacketUtils.ParseUdpPacket(peer.Unprotect(normal)).Payload);
        var auth = ue.Protect(new("auth"u8.ToArray(), staged.LocalEndPoint, staged.RemoteEndPoint) { SecurityGeneration = staged.SecurityGeneration });
        Assert.Equal("auth"u8.ToArray(), IpPacketUtils.ParseUdpPacket(newPeer.Unprotect(auth)).Payload);
        Assert.Throws<InvalidOperationException>(() => ue.Protect(new("wrong generation"u8.ToArray(), staged.LocalEndPoint, staged.RemoteEndPoint)
        { SecurityGeneration = oldRoute.SecurityGeneration }));

        ue.Commit(staged.SecurityGeneration!.Value);
        Assert.Equal(nextUe, ue.Proposal);
        var incoming = peer.Protect(new("old INVITE"u8.ToArray(), new(remote, oldPeer.PortClient), new(local, oldUe.PortServer)));
        var clear = ue.Unprotect(incoming, out var responseRoute);
        Assert.Equal("old INVITE"u8.ToArray(), IpPacketUtils.ParseUdpPacket(clear).Payload);
        Assert.Equal(oldRoute, responseRoute);
        var response = ue.Protect(new("old 200 OK"u8.ToArray(), responseRoute!.LocalEndPoint, responseRoute.RemoteEndPoint)
        { SecurityGeneration = responseRoute.SecurityGeneration });
        Assert.Equal(oldPeer.SpiServer, BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(ipv6 ? 40 : 20)));
        Assert.Equal("old 200 OK"u8.ToArray(), IpPacketUtils.ParseUdpPacket(peer.Unprotect(response)).Payload);
        Assert.Throws<FormatException>(() => ue.Unprotect(incoming)); // Replay state was not reset by rotation.

        ue.RetireExpiredContexts(DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.Throws<InvalidOperationException>(() => ue.Protect(new("expired"u8.ToArray(), oldRoute.LocalEndPoint, oldRoute.RemoteEndPoint)
        { SecurityGeneration = oldRoute.SecurityGeneration }));
        var active = ue.Protect(new("current"u8.ToArray(), staged.LocalEndPoint, staged.RemoteEndPoint) { SecurityGeneration = staged.SecurityGeneration });
        Assert.Equal("current"u8.ToArray(), IpPacketUtils.ParseUdpPacket(newPeer.Unprotect(active)).Payload);
    }

    [Fact]
    public void RenewalOffersAreFreshAndNeverChangeTheContactBindingBeforeChallenge()
    {
        using var security = new ImsIpsecTransport(IPAddress.Loopback, IPAddress.Parse("127.0.0.2"));
        var original = security.Proposal;
        var previous = original;
        for (var i = 0; i < 20; i++)
        {
            var next = security.CreateRenewalProposal();
            Assert.NotEqual(previous.SpiClient, next.SpiClient);
            Assert.NotEqual(previous.SpiServer, next.SpiServer);
            Assert.NotEqual(next.SpiClient, next.SpiServer);
            Assert.NotEqual(previous.PortClient, next.PortClient);
            Assert.Equal(original.PortServer, next.PortServer);
            Assert.Equal(original, security.Proposal);
            previous = next;
        }
    }

    [Fact]
    public void TemporaryPortWithoutItsExplicitGenerationCannotFallBackToPlaintext()
    {
        var local = IPAddress.Loopback;
        var remote = IPAddress.Parse("127.0.0.2");
        var proposal = new SecurityProposal("hmac-sha-1-96", "null", 300, 400, 42000, 42001);
        var peer = proposal with { SpiClient = 500, SpiServer = 600, PortClient = 43000, PortServer = 43001 };
        using var security = new ImsIpsecTransport(local, remote, proposal);
        var route = security.Stage(Agreement(proposal, peer), Key(1), Key(1));
        Assert.Throws<InvalidOperationException>(() => security.Protect(new("must encrypt"u8.ToArray(), route.LocalEndPoint, route.RemoteEndPoint)));
        Assert.NotNull(security.Protect(new("initial"u8.ToArray(), new(local, 5060), new(remote, 5060))));
    }

    [Fact]
    public async Task ClientRetransmissionKeepsTheCapturedSecurityGenerationAfterDefaultChanges()
    {
        using var transport = new SipTransport();
        var input = Channel.CreateUnbounded<SipDatagram>();
        var sent = new List<SipDatagram>();
        var local = IPAddress.Loopback;
        var remote = IPAddress.Parse("127.0.0.2");
        transport.ConnectDatagrams(new(local, 40000), new(remote, 41001), (packet, _) =>
        {
            sent.Add(packet);
            if (sent.Count == 1) transport.SetEndpoints(new(local, 42000), new(remote, 43001), 2);
            if (sent.Count == 2)
            {
                var response = SipMessage.Parse(packet.Payload).CreateResponse(200, "OK");
                input.Writer.TryWrite(new(response.ToBytes(), packet.LocalEndPoint, packet.RemoteEndPoint));
            }
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());
        transport.SetEndpoints(new(local, 40000), new(remote, 41001), 1);
        await transport.SendAndReceiveFinalAsync(Request("OPTIONS"), timeoutMs: 2000);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, packet => { Assert.Equal(40000, packet.LocalEndPoint.Port); Assert.Equal(41001, packet.RemoteEndPoint.Port); Assert.Equal(1L, packet.SecurityGeneration); });
        Assert.Equal(sent[0].Payload, sent[1].Payload);
    }

    [Fact]
    public async Task ProtectedIncomingResponseAndCachedRetransmissionUseTheReceivedGenerationClientRoute()
    {
        using var transport = new SipTransport();
        var input = Channel.CreateUnbounded<SipDatagram>();
        var output = Channel.CreateUnbounded<SipDatagram>();
        var local = IPAddress.Loopback;
        var remote = IPAddress.Parse("127.0.0.2");
        transport.ConnectDatagrams(new(local, 42000), new(remote, 43001), (packet, _) =>
        { output.Writer.TryWrite(packet); return Task.CompletedTask; }, ct => input.Reader.ReadAsync(ct).AsTask());
        transport.SetEndpoints(new(local, 42000), new(remote, 43001), 2);
        var replyWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.IncomingRequestReceived += (_, request) => _ = ReplyAsync(request);
        async Task ReplyAsync(SipMessage request)
        {
            try { await transport.SendAsync(request.CreateResponse(200, "OK")); replyWork.TrySetResult(); }
            catch (Exception ex) { replyWork.TrySetException(ex); }
        }
        var request = Request("INVITE");
        request.SetHeader("Via", $"SIP/2.0/UDP {remote}:41000;branch=z9hG4bKold;rport");
        var packet = new SipDatagram(request.ToBytes(), new(local, 40001), new(remote, 41000))
        { ResponseRoute = new(new(local, 40000), new(remote, 41001), 1) };
        input.Writer.TryWrite(packet);
        await replyWork.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        transport.SetEndpoints(new(local, 44000), new(remote, 45001), 3);
        input.Writer.TryWrite(packet);
        var repeated = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(40000, first.LocalEndPoint.Port); Assert.Equal(41001, first.RemoteEndPoint.Port);
        Assert.Equal(1L, first.SecurityGeneration);
        Assert.Equal(first, repeated);
        Assert.DoesNotContain("SecurityGeneration", System.Text.Encoding.UTF8.GetString(first.Payload));
    }

    [Fact]
    public void InvalidActivationAndStageKeepCommittedAndPendingGenerations()
    {
        var local = IPAddress.Parse("192.0.2.1"); var remote = IPAddress.Parse("192.0.2.2");
        var ue = new SecurityProposal("hmac-sha-1-96", "null", 1000, 2000, 40000, 40001);
        var peer = new SecurityProposal("hmac-sha-1-96", "null", 3000, 4000, 41000, 41001);
        using var security = new ImsIpsecTransport(local, remote, ue);
        security.Activate(Agreement(ue, peer), Key(1), Key(2));
        var original = security.GetActiveRoute()!.Value.Route;
        Assert.Throws<InvalidOperationException>(() => security.Activate(Agreement(ue with { SpiClient = 0 }, peer), Key(1), Key(2)));
        Assert.Equal(original, security.GetActiveRoute()!.Value.Route);
        var next = ue with { SpiClient = 5000, SpiServer = 6000, PortClient = 42000 };
        var nextPeer = peer with { SpiClient = 7000, SpiServer = 8000, PortClient = 43000 };
        var pending = security.Stage(Agreement(next, nextPeer), Key(3), Key(4));
        var bad = next with { SpiClient = 9000, SpiServer = 10000, PortClient = 44000 };
        foreach (var invalid in new[] { Agreement(bad, nextPeer with { SpiClient = 3000, SpiServer = 11000 }),
            Agreement(bad with { PortClient = 40000 }, nextPeer with { SpiClient = 11000, SpiServer = 12000 }),
            Agreement(bad with { PortServer = 44001 }, nextPeer with { SpiClient = 11000, SpiServer = 12000 }) })
            Assert.Throws<InvalidOperationException>(() => security.Stage(invalid, Key(3), Key(4)));
        security.Commit(pending.SecurityGeneration!.Value);
        Assert.Equal(pending, security.GetActiveRoute()!.Value.Route);
        using var peerSecurity = new ImsIpsecTransport(remote, local, nextPeer);
        peerSecurity.Activate(Agreement(nextPeer, next), Key(3), Key(4));
        Assert.Equal("still pending then committed"u8.ToArray(), IpPacketUtils.ParseUdpPacket(peerSecurity.Unprotect(security.Protect(new("still pending then committed"u8.ToArray(), pending.LocalEndPoint, pending.RemoteEndPoint) { SecurityGeneration = pending.SecurityGeneration }))).Payload);
    }

    [Fact]
    public void InvalidEndpointsCannotConstructAnImsSecurityTransport()
    {
        Assert.Throws<ArgumentException>(() => new ImsIpsecTransport(IPAddress.Any, IPAddress.Loopback));
        Assert.Throws<ArgumentException>(() => new ImsIpsecTransport(IPAddress.Loopback, IPAddress.IPv6Loopback));
        Assert.Throws<ArgumentException>(() => new ImsIpsecTransport(IPAddress.Loopback, IPAddress.Loopback));
        Assert.Throws<ArgumentException>(() => new ImsIpsecTransport(IPAddress.Parse("239.1.1.1"), IPAddress.Loopback));
    }

    private static SipMessage Request(string method)
    {
        var request = new SipMessage { IsRequest = true, Method = method, RequestUri = "sip:peer.test" };
        request.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:40001;branch=z9hG4bKrotate;rport");
        request.SetHeader("Call-ID", "rotation-test"); request.SetHeader("CSeq", "1 " + method);
        request.SetHeader("From", "<sip:ue@peer.test>;tag=ue"); request.SetHeader("To", "<sip:peer@peer.test>");
        return request;
    }
}
