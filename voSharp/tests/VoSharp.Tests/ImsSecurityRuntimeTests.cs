using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class ImsSecurityRuntimeTests
{
    private static readonly IPAddress Local = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Remote = IPAddress.Parse("10.0.0.2");
    private static byte[] Key => Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static SecurityProposal Proposal(string encryption) => new("hmac-sha-1-96", encryption, 100, 200, 40000, 40001);
    private static SecurityAgreement Agreement(SecurityProposal proposal) => new(proposal, "verify", 1001, 1002, 41000, 41001);

    // Independent fixtures computed with Python hmac + cryptography, fixed IV 000102...0f.
    [Theory]
    [InlineData("null", "000000c800000001a0289c41000b0000616263010203031128617f4ee0c2d02e682d0324")]
    [InlineData("aes-cbc", "000000c800000001000102030405060708090a0b0c0d0e0f17c808e55fd58344d0b0d130fb78dfce14396f1ed9a46c9c5a635fc2")]
    public void ProtectedInboundUdpUsesServerSaAndRejectsTamperReplay(string encryption, string fixture)
    {
        using var security = new ImsIpsecTransport(Local, Remote, Proposal(encryption));
        security.Activate(Agreement(security.Proposal), Key, Key);
        var carrierIp = IpPacketUtils.BuildIpv4UdpPacket(Remote, Local, 41000, 40001, []);
        var packet = IpPacketUtils.ReplaceIpv4Payload(carrierIp, 50, Convert.FromHexString(fixture));
        var corrupt = packet.ToArray();
        corrupt[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => security.Unprotect(corrupt));
        var decoded = IpPacketUtils.ParseIpv4UdpPacket(security.Unprotect(packet));
        Assert.Equal("abc"u8.ToArray(), decoded.Payload);
        Assert.Equal(new IPEndPoint(Local, 40001), decoded.LocalEndPoint);
        Assert.Equal(new IPEndPoint(Remote, 41000), decoded.RemoteEndPoint);
        Assert.Throws<FormatException>(() => security.Unprotect(packet));
        var plain = IpPacketUtils.BuildIpv4UdpPacket(Remote, Local, 41000, 40001, [1]);
        Assert.Throws<FormatException>(() => security.Unprotect(plain));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("aes-cbc")]
    [InlineData("des-ede3-cbc")]
    public void OutboundSaUsesPeerSpiAndSeparateSequenceSpaces(string encryption)
    {
        using var security = new ImsIpsecTransport(Local, Remote, Proposal(encryption));
        security.Activate(Agreement(security.Proposal), Key, Key);
        var client = security.Protect(new SipDatagram("request"u8.ToArray(), new(Local, 40000), new(Remote, 41001)));
        var server = security.Protect(new SipDatagram("response"u8.ToArray(), new(Local, 40001), new(Remote, 41000)));
        Assert.Equal(50, client[9]);
        Assert.Equal(1002u, BinaryPrimitives.ReadUInt32BigEndian(client.AsSpan(20)));
        Assert.Equal(1001u, BinaryPrimitives.ReadUInt32BigEndian(server.AsSpan(20)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(client.AsSpan(24)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(server.AsSpan(24)));
        // Reverse the roles to verify both protected UDP legs can be received.
        var peerProposal = new SecurityProposal("hmac-sha-1-96", encryption, 1001, 1002, 41000, 41001);
        using var peer = new ImsIpsecTransport(Remote, Local, peerProposal);
        peer.Activate(new SecurityAgreement(peerProposal, "verify", 100, 200, 40000, 40001), Key, Key);
        Assert.Equal("request"u8.ToArray(), IpPacketUtils.ParseIpv4UdpPacket(peer.Unprotect(client)).Payload);
        Assert.Equal("response"u8.ToArray(), IpPacketUtils.ParseIpv4UdpPacket(peer.Unprotect(server)).Payload);
    }

    [Fact]
    public async Task RegistrationActivatesSecurityBeforeAuthenticatedRequestAndKeepsAllRoutes()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var proposal = Proposal("aes-cbc");
        const string securityHeader = "ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;spi-c=1001;spi-s=1002;port-c=41000;port-s=41001";
        bool activated = false;
        int count = 0;
        var profile = new ImsProfile("ue@example.test", "sip:ue@example.test", "example.test", "123456789012345", "10.0.0.1");
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            var request = SipMessage.Parse(bytes);
            var response = request.CreateResponse(++count == 1 ? 401 : 200, count == 1 ? "Unauthorized" : "OK");
            if (count == 1)
            {
                Assert.NotNull(request.GetHeader("Security-Client"));
                Assert.Equal("sec-agree", request.GetHeader("Require"));
                Assert.Equal("sec-agree", request.GetHeader("Proxy-Require"));
                var identityAuthorization = request.GetHeader("Authorization");
                Assert.NotNull(identityAuthorization);
                Assert.Contains("algorithm=AKAv1-MD5", identityAuthorization, StringComparison.Ordinal);
                Assert.Contains("integrity-protected=no", identityAuthorization, StringComparison.Ordinal);
                response.SetHeader("WWW-Authenticate", $"Digest realm=\"example.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
                response.SetHeader("Security-Server", securityHeader);
            }
            else
            {
                Assert.True(activated);
                Assert.Equal(securityHeader, request.GetHeader("Security-Verify"));
                Assert.Contains(":40000;", request.GetHeader("Via"));
                Assert.Contains(":40001;", request.GetHeader("Contact"));
                Assert.Contains("integrity-protected=yes", request.GetHeader("Authorization"));
                response.AddHeader("Contact", "<sip:another@10.0.0.3>;expires=0");
                response.AddHeader("Contact", request.GetHeader("Contact")!);
                response.AddHeader("Service-Route", "<sip:first.example.test;lr>");
                response.AddHeader("Service-Route", "<sip:second.example.test;lr>");
            }
            input.Writer.TryWrite(response.ToBytes());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());
        using var session = new SipRegisterSession(transport, profile, proposal,
            (agreement, ck, ik) => { activated = true; Assert.Equal(proposal.SpiServer, agreement.Selected.SpiServer); Assert.Equal(Key, ck); });
        var result = await session.RegisterAsync((_, _, _) => Task.FromResult((new byte[8], Key, Key)));
        Assert.True(result.SmsCapabilityConfirmed);
        Assert.Contains("first.example.test", result.ServiceRoute);
        Assert.Contains("second.example.test", result.ServiceRoute);
        var rpAck = new SipMessage { IsRequest = true, Method = "MESSAGE" };
        session.AddSecurityHeaders(rpAck);
        Assert.Equal("sec-agree", rpAck.GetHeader("Require"));
        Assert.Equal("sec-agree", rpAck.GetHeader("Proxy-Require"));
        Assert.Equal(securityHeader, rpAck.GetHeader("Security-Verify"));
    }

    [Fact]
    public async Task RegistrationRefreshesWithNewCseqBeforeGrantedExpiry()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var cseqs = new List<string>();
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            var request = SipMessage.Parse(bytes);
            lock (cseqs) cseqs.Add(request.GetHeader("CSeq")!);
            var response = request.CreateResponse(200, "OK");
            response.SetHeader("Contact", request.GetHeader("Contact")!.Replace("expires=3600", "expires=1"));
            input.Writer.TryWrite(response.ToBytes());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());
        using var session = new SipRegisterSession(transport, new("ue@example.test", "sip:ue@example.test", "example.test", "123456789012345", "10.0.0.1"));
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___) => throw new InvalidOperationException("No challenge expected");
        var refreshed = new TaskCompletionSource<SipRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RegistrationRefreshed += (_, result) => refreshed.TrySetResult(result);
        await session.RegisterAsync(Aka);
        session.StartRefreshing(Aka);
        var result = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopRefreshingAsync();
        Assert.True(result.SmsCapabilityConfirmed);
        Assert.Equal(1, result.ExpiresSeconds);
        Assert.Equal(new[] { "1 REGISTER", "2 REGISTER" }, cseqs);
    }

    [Fact]
    public async Task RegistrationRefreshRetriesTransientFailureBeforeGrantedExpiry()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var requestCount = 0;
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            var request = SipMessage.Parse(bytes);
            var current = Interlocked.Increment(ref requestCount);
            var response = current == 2
                ? request.CreateResponse(500, "Temporary Failure")
                : request.CreateResponse(200, "OK");
            if (response.StatusCode == 200)
                response.SetHeader("Contact", request.GetHeader("Contact")!.Replace("expires=3600", "expires=1"));
            input.Writer.TryWrite(response.ToBytes());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());

        using var session = new SipRegisterSession(transport,
            new("ue@example.test", "sip:ue@example.test", "example.test", "123456789012345", "10.0.0.1"));
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No challenge expected");
        var refreshed = new TaskCompletionSource<SipRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RegistrationRefreshed += (_, result) => refreshed.TrySetResult(result);

        await session.RegisterAsync(Aka);
        session.StartRefreshing(Aka);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopRefreshingAsync();

        Assert.Equal(3, Volatile.Read(ref requestCount));
    }
}
