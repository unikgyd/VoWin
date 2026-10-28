using System.Net;
using System.Net.Sockets;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

// Keep these behavioral RED cases unchanged after implementing the proof type.
public sealed class WindowsImsGatewayRedTests
{
    internal static ImsProfile Profile(SipTransport transport) =>
        new("001010123456789@ims.test", "sip:+15551234567@ims.test", "ims.test",
            "123456789012345", "127.0.0.1", transport.LocalEndPoint!.Port);

    [Fact]
    public async Task SilentActualRegisterCanAuthorizeGatewayRetry()
    {
        using var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport(timeoutMs: 100);
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)remote.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var aka = 0;
        var registration = session.RegisterAsync((_, _, _) =>
        {
            aka++;
            throw new InvalidOperationException("Silent peer cannot issue AKA.");
        });
        var packet = await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("REGISTER", SipMessage.Parse(packet.Buffer).Method);
        var failure = await Record.ExceptionAsync(async () => await registration);
        Assert.NotNull(failure);
        Assert.True(VoWifiManager.CanFailOverImsTransport(VoWifiState.ImsRegistering, failure));
        Assert.Equal(0, aka);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task ReaderIoAfterActual401IsNotGatewayTransportSilence()
    {
        using var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport(timeoutMs: 1000);
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)remote.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var aka = 0;
        var registration = session.RegisterAsync((_, _, _) =>
        {
            aka++;
            throw new IOException("Synthetic reader disconnected after challenge.");
        });
        var packet = await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var request = SipMessage.Parse(packet.Buffer);
        var challenge = request.CreateResponse(401, "Unauthorized");
        challenge.SetHeader("WWW-Authenticate", "Digest realm=\"ims.test\", nonce=\"" +
            Convert.ToBase64String(new byte[32]) + "\", algorithm=AKAv1-MD5, qop=\"auth\"");
        await remote.SendAsync(challenge.ToBytes(), packet.RemoteEndPoint);
        var failure = await Record.ExceptionAsync(async () => await registration);
        Assert.IsType<IOException>(failure);
        Assert.False(VoWifiManager.CanFailOverImsTransport(VoWifiState.ImsRegistering, failure));
        Assert.Equal(1, aka);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task LocalMetadataIoBeforeAnyRegisterIsNotGatewayProof()
    {
        using var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport(timeoutMs: 100);
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)remote.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport),
            readAccessNetworkInfo: _ => throw new IOException("Synthetic local metadata failure."));
        var failure = await Record.ExceptionAsync(() => session.RegisterAsync((_, _, _) =>
            throw new InvalidOperationException("No AKA expected.")));
        Assert.IsType<IOException>(failure);
        Assert.False(VoWifiManager.CanFailOverImsTransport(VoWifiState.ImsRegistering, failure));
        Assert.Equal(0, remote.Available);
    }
}
