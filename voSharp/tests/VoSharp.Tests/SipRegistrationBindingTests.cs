using System.Net;
using System.Net.Sockets;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class SipRegistrationBindingTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("no-expiry")]
    [InlineData("wrong-transport")]
    public async Task RegisterDoesNotClaimSuccessWithoutOwnGrantedContact(string responseKind)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var request = SipMessage.Parse(packet.Buffer);
            var response = request.CreateResponse(200, "OK");
            var requested = request.GetHeader("Contact")!;
            var returned = responseKind switch
            {
                "foreign" => $"\"{requested}\" <sip:foreign@127.0.0.1:5060>;expires=3600",
                "no-expiry" => requested.Replace(";expires=3600", "", StringComparison.Ordinal),
                "wrong-transport" => requested.Replace(";transport=udp>", ";transport=tcp>", StringComparison.OrdinalIgnoreCase),
                _ => null
            };
            if (returned is not null) response.SetHeader("Contact", returned);
            if (responseKind == "missing") response.SetHeader("Expires", "3600");
            await pcscf.SendAsync(response.ToBytes(), packet.RemoteEndPoint);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task RegisterAcceptsOwnContactWhenRegistrarOmitsDefaultUdpTransportParameter()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var request = SipMessage.Parse(packet.Buffer);
            var response = request.CreateResponse(200, "OK");
            response.SetHeader("Contact", request.GetHeader("Contact")!
                .Replace(";transport=udp>", ">", StringComparison.OrdinalIgnoreCase));
            await pcscf.SendAsync(response.ToBytes(), packet.RemoteEndPoint);
        });

        var result = await session.RegisterAsync(UnexpectedAka);
        await server;
        Assert.NotEmpty(result.ContactUri);
        Assert.Equal(3600, result.ExpiresSeconds);
        Assert.NotNull(session.LastResult);
    }

    [Theory]
    [InlineData("Security-Server")]
    [InlineData("Require")]
    [InlineData("Proxy-Require")]
    public async Task RegisterDoesNotAcceptUnprotected200ThatRequiresSecurity(string header)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            var response = packet.Request.CreateResponse(200, "OK");
            response.SetHeader("Contact", packet.Request.GetHeader("Contact")!);
            response.SetHeader(header, header == "Security-Server" ? "ipsec-3gpp" : "timer, sec-agree");
            await pcscf.SendAsync(response.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Contains("no IMS security association", error.Message);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task RegisterRejectsSecAgreeChallengeWithoutSecurityServer()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            var response = packet.Request.CreateResponse(401, "Unauthorized");
            response.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            response.SetHeader("Require", "sec-agree");
            await pcscf.SendAsync(response.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Contains("omitted Security-Server", error.Message);
        Assert.Null(session.LastResult);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    public async Task RegisterRejectsSecurityDowngradeWhenOfferedIpsecButPeerOmitsAgreement(int statusCode)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 40000, 40001);
        var activated = false;
        using var session = new SipRegisterSession(transport, Profile(transport), proposal,
            (_, _, _) => activated = true);
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            Assert.NotNull(packet.Request.GetHeader("Security-Client"));
            var response = packet.Request.CreateResponse(statusCode,
                statusCode == 200 ? "OK" : "Unauthorized");
            if (statusCode == 200)
                response.SetHeader("Contact", packet.Request.GetHeader("Contact")!);
            else
                response.SetHeader("WWW-Authenticate",
                    $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(response.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Contains(statusCode == 200 ? "no IMS security association" : "omitted Security-Server", error.Message);
        Assert.False(activated);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public void RegisterRequiresSecurityProposalAndActivationCallbackTogether()
    {
        using var transport = new SipTransport();
        var profile = new ImsProfile("ue@ims.test", "sip:ue@ims.test", "ims.test",
            "123456789012345", "127.0.0.1");
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 40000, 40001);
        Assert.Throws<ArgumentException>(() => new SipRegisterSession(transport, profile, proposal));
        Assert.Throws<ArgumentException>(() => new SipRegisterSession(transport, profile,
            activateSecurity: (_, _, _) => { }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterRetriesOne423WithHigherLifetimeAndStableIdentity(bool afterAkaChallenge)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var first = await ReceiveAsync(pcscf);
            if (afterAkaChallenge)
            {
                var unauthorized = first.Request.CreateResponse(401, "Unauthorized");
                unauthorized.SetHeader("WWW-Authenticate",
                    $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
                await pcscf.SendAsync(unauthorized.ToBytes(), first.Remote);
            }
            var rejected = afterAkaChallenge ? await ReceiveAsync(pcscf) : first;
            var tooBrief = rejected.Request.CreateResponse(423, "Interval Too Brief");
            tooBrief.SetHeader("Min-Expires", "7200");
            await pcscf.SendAsync(tooBrief.ToBytes(), rejected.Remote);

            var retried = await ReceiveAsync(pcscf);
            var accepted = retried.Request.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", retried.Request.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), retried.Remote);
            return (first.Request, rejected.Request, retried.Request);
        });

        var akaCalls = 0;
        var result = await session.RegisterAsync((_, _, _) =>
        {
            Interlocked.Increment(ref akaCalls);
            return Task.FromResult((new byte[8], new byte[16], new byte[16]));
        });
        var (first, rejected, retried) = await server;
        Assert.Equal(afterAkaChallenge ? 1 : 0, akaCalls);
        Assert.EndsWith(";expires=3600", first.GetHeader("Contact"));
        Assert.EndsWith(";expires=7200", retried.GetHeader("Contact"));
        Assert.Equal(first.GetHeader("Call-ID"), retried.GetHeader("Call-ID"));
        Assert.Equal(first.GetHeader("From"), retried.GetHeader("From"));
        Assert.True(Cseq(retried) > Cseq(rejected));
        Assert.Equal(7200, result.ExpiresSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("3600")]
    public async Task RegisterRejects423WithoutUsableMinExpires(string? minExpires)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            var tooBrief = packet.Request.CreateResponse(423, "Interval Too Brief");
            if (minExpires is not null) tooBrief.SetHeader("Min-Expires", minExpires);
            await pcscf.SendAsync(tooBrief.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Contains("Min-Expires", error.Message);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task RegisterStopsAfterOne423NegotiationRetry()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var first = await ReceiveAsync(pcscf);
            var firstRejection = first.Request.CreateResponse(423, "Interval Too Brief");
            firstRejection.SetHeader("Min-Expires", "7200");
            await pcscf.SendAsync(firstRejection.ToBytes(), first.Remote);
            var second = await ReceiveAsync(pcscf);
            var secondRejection = second.Request.CreateResponse(423, "Interval Too Brief");
            secondRejection.SetHeader("Min-Expires", "10800");
            await pcscf.SendAsync(secondRejection.ToBytes(), second.Remote);
            return (first.Request, second.Request);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        var (firstRequest, secondRequest) = await server;
        Assert.Contains("Min-Expires=10800", error.Message);
        Assert.EndsWith(";expires=7200", secondRequest.GetHeader("Contact"));
        Assert.True(Cseq(secondRequest) > Cseq(firstRequest));
        Assert.Null(session.LastResult);
    }

    private static async Task<(SipMessage Request, IPEndPoint Remote)> ReceiveAsync(UdpClient pcscf)
    {
        var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return (SipMessage.Parse(packet.Buffer), packet.RemoteEndPoint);
    }

    private static uint Cseq(SipMessage request) =>
        uint.Parse(request.GetHeader("CSeq")!.Split(' ')[0]);

    private static ImsProfile Profile(SipTransport transport) =>
        new("001010123456789@ims.test", "sip:+15551234567@ims.test",
            "ims.test", "123456789012345", IPAddress.Loopback.ToString(),
            transport.LocalEndPoint!.Port);

    private static Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(
        byte[] _, byte[] __, CancellationToken ___) =>
        throw new InvalidOperationException("No AKA challenge expected.");
}
