using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace VoSharp.Ike.Tests;

public class IkeControllerTests
{
    private static byte[] Key(byte value) => Enumerable.Repeat(value, 32).ToArray();
    private static IkeWire.IkeMessage Header(uint id, IkeFlags flags) => new()
    {
        InitiatorSpi = 11, ResponderSpi = 22, Exchange = IkeExchangeType.Informational,
        MessageId = id, Flags = flags
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedDeleteContractAppliesOnlyToOwnedSpiAndMalformedRequestsAreFatal(bool ipv6)
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "ike-control-v1.json")));
        foreach (var item in contract.RootElement.GetProperty("cases").EnumerateArray())
        {
            var ip = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            using var peer = new UdpClient(new IPEndPoint(ip, 0));
            using var transport = new IkeTransport(ip, ((IPEndPoint)peer.Client.LocalEndPoint!).Port);
            using var controller = new IkeSessionController(transport, IkeSuite.Preferred, 11, 22, 4, 0x55667788,
                contract.RootElement.GetProperty("outboundSpi").GetUInt32(), Key(1), Key(2), Key(3), Key(4));
            transport.StartPacketPump(0x55667788);
            var target = new IPEndPoint(ip, transport.LocalEndpoint!.Port);
            var payloads = item.GetProperty("payloads").EnumerateArray().Select(p =>
                new IkePayload((IkePayloadType)p.GetProperty("kind").GetByte(), Convert.FromHexString(p.GetProperty("hex").GetString()!))).ToArray();
            var request = IkeCrypto.EncryptPayloads(Header(0, 0), payloads, IkeSuite.Preferred, Key(3), Key(4));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await peer.SendAsync(request, target, deadline.Token);
            var raw = (await peer.ReceiveAsync(deadline.Token)).Buffer;
            var (header, response) = IkeCrypto.DecryptPayloads(raw, IkeSuite.Preferred, Key(1), Key(2));
            Assert.Equal((uint)0, header.MessageId);
            Assert.Equal(IkeFlags.Initiator | IkeFlags.Response, header.Flags);
            var accepted = item.GetProperty("accepted").GetBoolean();
            var ike = item.GetProperty("ikeDeleted").GetBoolean();
            var child = item.GetProperty("childDeleted").GetBoolean();
            if (!accepted)
                Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(Assert.Single(response).Body.AsSpan(2, 2)));
            else if (child)
                Assert.Equal(new byte[] { 3, 4, 0, 1, 0x55, 0x66, 0x77, 0x88 }, Assert.Single(response).Body);
            else Assert.Empty(response);
            if (ike || child)
                while (!controller.IsPeerDeleted && !controller.IsChildSaDeleted) await Task.Delay(2, deadline.Token);
            else
            {
                // A second request proves processing has passed the prior
                // state update; no timing-only assertion of "still alive".
                await peer.SendAsync(IkeCrypto.EncryptPayloads(Header(1, 0), [], IkeSuite.Preferred, Key(3), Key(4)), target, deadline.Token);
                await peer.ReceiveAsync(deadline.Token);
            }
            Assert.Equal(ike, controller.IsPeerDeleted);
            Assert.Equal(child, controller.IsChildSaDeleted);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalProbeUsesPostAuthIdImmutableRetryAndIndependentPeerWindow(bool ipv6)
    {
        var ip = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var peer = new UdpClient(new IPEndPoint(ip, 0));
        using var transport = new IkeTransport(ip, ((IPEndPoint)peer.Client.LocalEndPoint!).Port);
        using var controller = new IkeSessionController(transport, IkeSuite.Preferred, 11, 22, 4, 0x55667788, 0x11223344,
            Key(1), Key(2), Key(3), Key(4), fragmentationNegotiated: true);
        transport.StartPacketPump(0x55667788);
        var target = new IPEndPoint(ip, transport.LocalEndpoint!.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var probe = controller.ProbeAsync(deadline.Token);
        var first = (await peer.ReceiveAsync(deadline.Token)).Buffer;
        var (header, payloads) = IkeCrypto.DecryptPayloads(first, IkeSuite.Preferred, Key(1), Key(2));
        Assert.Equal((uint)5, header.MessageId); Assert.Empty(payloads);
        await peer.SendAsync(IkeCrypto.EncryptPayloads(Header(4, IkeFlags.Response), [], IkeSuite.Preferred, Key(3), Key(4)), target, deadline.Token);
        var bad = IkeCrypto.EncryptPayloads(Header(5, IkeFlags.Response), [], IkeSuite.Preferred, Key(3), Key(4));
        bad[^1] ^= 1; await peer.SendAsync(bad, target, deadline.Token);
        Assert.Equal(first, (await peer.ReceiveAsync(deadline.Token)).Buffer);
        Assert.False(probe.IsCompleted);
        await peer.SendAsync(IkeCrypto.EncryptPayloads(Header(0, 0), [], IkeSuite.Preferred, Key(3), Key(4)), target, deadline.Token);
        var (remote, _) = IkeCrypto.DecryptPayloads((await peer.ReceiveAsync(deadline.Token)).Buffer, IkeSuite.Preferred, Key(1), Key(2));
        Assert.Equal((uint)0, remote.MessageId); Assert.Equal(IkeFlags.Initiator | IkeFlags.Response, remote.Flags);
        Assert.False(probe.IsCompleted);
        foreach (var packet in IkeProtectedMessage.Encrypt(Header(5, IkeFlags.Response),
            [new(IkePayloadType.Notify, [0, 0, 0xfd, 0xe8, .. new byte[1700]])], IkeSuite.Preferred, Key(3), Key(4), true, 256).Reverse())
            await peer.SendAsync(packet, target, deadline.Token);
        await probe;
        var next = controller.ProbeAsync(deadline.Token);
        var (second, _) = IkeCrypto.DecryptPayloads((await peer.ReceiveAsync(deadline.Token)).Buffer, IkeSuite.Preferred, Key(1), Key(2));
        Assert.Equal((uint)6, second.MessageId);
        await peer.SendAsync(IkeCrypto.EncryptPayloads(Header(6, IkeFlags.Response), [], IkeSuite.Preferred, Key(3), Key(4)), target, deadline.Token);
        await next;
    }

    [Theory]
    [InlineData("00000007", 41)]
    [InlineData("0000", 41)]
    [InlineData("01000000", 42)]
    public async Task ProbeRejectsAuthenticatedUnexpectedResponsePayloads(string hex, byte kind)
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new IkeTransport(IPAddress.Loopback, ((IPEndPoint)peer.Client.LocalEndPoint!).Port);
        using var controller = new IkeSessionController(transport, IkeSuite.Preferred, 11, 22, 4, 7, 8,
            Key(1), Key(2), Key(3), Key(4));
        transport.StartPacketPump(7);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var operation = controller.ProbeAsync(deadline.Token);
        await peer.ReceiveAsync(deadline.Token);
        await peer.SendAsync(IkeCrypto.EncryptPayloads(Header(5, IkeFlags.Response),
            [new((IkePayloadType)kind, Convert.FromHexString(hex))], IkeSuite.Preferred, Key(3), Key(4)),
            new IPEndPoint(IPAddress.Loopback, transport.LocalEndpoint!.Port), deadline.Token);
        await Assert.ThrowsAsync<IkeFormatException>(() => operation);
    }
}
