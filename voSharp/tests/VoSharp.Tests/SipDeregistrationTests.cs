using System.Net;
using System.Net.Sockets;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class SipDeregistrationTests
{
    [Fact]
    public async Task DeregisterRemovesTheSameContactWithHigherCseq()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));

        var server = Task.Run(async () =>
        {
            var registered = await ReceiveAndReplyAsync(pcscf, 200);
            var removed = await ReceiveAndReplyAsync(pcscf, 200);
            return (registered, removed);
        });

        await session.RegisterAsync(UnexpectedAka);
        await session.DeregisterAsync();
        var (registered, removed) = await server;
        Assert.Equal(WithoutExpiry(registered.GetHeader("Contact")), WithoutExpiry(removed.GetHeader("Contact")));
        Assert.EndsWith(";expires=0", removed.GetHeader("Contact"));
        Assert.Equal("0", removed.GetHeader("Expires"));
        Assert.Equal(registered.GetHeader("Call-ID"), removed.GetHeader("Call-ID"));
        Assert.True(Cseq(removed) > Cseq(registered));
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task DeregisterAnswersAkaChallengeWithoutRestoringRegistration()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var akaCalls = 0;

        var server = Task.Run(async () =>
        {
            var registered = await ReceiveAndReplyAsync(pcscf, 200);
            var first = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var challenged = SipMessage.Parse(first.Buffer);
            var unauthorized = challenged.CreateResponse(401, "Unauthorized");
            unauthorized.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(unauthorized.ToBytes(), first.RemoteEndPoint);
            var authenticated = await ReceiveAndReplyAsync(pcscf, 200);
            return (registered, challenged, authenticated);
        });

        await session.RegisterAsync(UnexpectedAka);
        await session.DeregisterAsync((rand, autn, _) =>
        {
            Interlocked.Increment(ref akaCalls);
            Assert.Equal(16, rand.Length);
            Assert.Equal(16, autn.Length);
            return Task.FromResult((new byte[8], new byte[16], new byte[16]));
        });
        var (registered, challenged, authenticated) = await server;
        Assert.Equal(1, akaCalls);
        Assert.Equal(registered.GetHeader("Call-ID"), authenticated.GetHeader("Call-ID"));
        Assert.True(Cseq(authenticated) > Cseq(challenged));
        Assert.Equal("0", authenticated.GetHeader("Expires"));
        Assert.EndsWith(";expires=0", authenticated.GetHeader("Contact"));
        Assert.NotNull(authenticated.GetHeader("Authorization"));
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task DeregisterDoesNotClaimSuccessIfRegistrarStillListsTheBinding()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            await ReceiveAndReplyAsync(pcscf, 200);
            var datagram = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var removal = SipMessage.Parse(datagram.Buffer);
            var accepted = removal.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", removal.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), datagram.RemoteEndPoint);
        });

        await session.RegisterAsync(UnexpectedAka);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeregisterAsync());
        await server;
        Assert.Contains("still listed", error.Message);
        Assert.NotNull(session.LastResult);
    }

    [Fact]
    public async Task DeregisterRejectsEquivalentBindingWithNormalizedUdpUri()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            await ReceiveAndReplyAsync(pcscf, 200);
            var datagram = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var removal = SipMessage.Parse(datagram.Buffer);
            var accepted = removal.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", removal.GetHeader("Contact")!
                .Replace(";transport=udp>", ">", StringComparison.OrdinalIgnoreCase));
            await pcscf.SendAsync(accepted.ToBytes(), datagram.RemoteEndPoint);
        });

        await session.RegisterAsync(UnexpectedAka);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeregisterAsync());
        await server;
        Assert.Contains("still listed", error.Message);
        Assert.NotNull(session.LastResult);
    }

    private static ImsProfile Profile(SipTransport transport) =>
        new("001010123456789@ims.test", "sip:+15551234567@ims.test",
            "ims.test", "123456789012345", IPAddress.Loopback.ToString(),
            transport.LocalEndPoint!.Port);

    private static async Task<SipMessage> ReceiveAndReplyAsync(UdpClient server, int status)
    {
        var datagram = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var request = SipMessage.Parse(datagram.Buffer);
        var response = request.CreateResponse(status, status == 200 ? "OK" : "Unauthorized");
        if (status == 200 && request.GetHeader("Expires") != "0")
            response.SetHeader("Contact", request.GetHeader("Contact")!);
        await server.SendAsync(response.ToBytes(), datagram.RemoteEndPoint);
        return request;
    }

    private static Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(
        byte[] _, byte[] __, CancellationToken ___) =>
        throw new InvalidOperationException("No registration AKA challenge was expected.");

    private static string WithoutExpiry(string? contact) =>
        contact?.Split(";expires=", StringSplitOptions.None)[0] ?? string.Empty;

    private static uint Cseq(SipMessage request) =>
        uint.Parse(request.GetHeader("CSeq")!.Split(' ')[0]);
}
