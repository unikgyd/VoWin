using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VoSharp.Common.Aka;

namespace VoSharp.Ike.Tests;

public class IkeCookieParityTests
{
    public static IEnumerable<object[]> CookieCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "ike-init-cookie-v1.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            yield return [c.GetProperty("name").GetString()!, c.GetProperty("length").GetInt32(), c.GetProperty("protocol").GetByte(),
                c.GetProperty("spi_size").GetByte(), c.GetProperty("extra").GetString()!, c.GetProperty("valid").GetBoolean()];
    }

    private static IkeWire.IkeMessage CookieResponse(int length, byte protocol, byte spiSize, string extra)
    {
            var body = new byte[4 + spiSize + length];
            body[0] = protocol; body[1] = spiSize;
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), 16390); body.AsSpan(4).Fill(7);
            var response = new IkeWire.IkeMessage();
            response.Payloads.Add(new(IkePayloadType.Notify, body));
            if (extra == "duplicate") response.Payloads.Add(new(IkePayloadType.Notify, body.ToArray()));
            if (extra == "sa") response.Payloads.Add(new(IkePayloadType.SecurityAssociation, []));
            if (extra == "ke") response.Payloads.Add(new(IkePayloadType.KeyExchange, []));
            if (extra == "nonce") response.Payloads.Add(new(IkePayloadType.Nonce, []));
            if (extra == "fatal") response.Payloads.Add(new(IkePayloadType.Notify, [0, 0, 0, 24]));
            if (extra == "malformed") response.Payloads.Add(new(IkePayloadType.Notify, [0]));
            return response;
    }

    [Theory]
    [MemberData(nameof(CookieCases))]
    public void SharedCookieReceiptVectors(string name, int length, byte protocol, byte spiSize, string extra, bool valid)
    {
        var response = CookieResponse(length, protocol, spiSize, extra);
        Exception? error = Record.Exception(() => Assert.NotNull(IkeSession.ReadInitCookie(response)));
        Assert.True((error == null) == valid, name);
    }

    private sealed class NoSim : IAkaProvider
    {
        public int Calls;
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("COOKIE must precede AKA."); }
    }

    [Theory]
    [InlineData(false, 0UL, 0, false)]
    [InlineData(false, 17UL, 0, false)]
    [InlineData(false, 17UL, 255, false)]
    [InlineData(true, 17UL, 0, false)]
    [InlineData(false, 0UL, 0, true)]
    [InlineData(false, 17UL, 255, true)]
    public async Task ActualCookieRefreshKeepsOriginalRequestAndBound(bool ipv6, ulong responderSpi, byte protocol, bool exhaust)
    {
        var ip = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(ip, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = 0;
        var server = Task.Run(async () =>
        {
            var bytes = new byte[65535];
            EndPoint any = new IPEndPoint(ipv6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            IkeWire.IkeMessage? original = null;
            try { for (var i = 0; i < (exhaust ? 4 : 3); i++)
            {
                var packet = await socket.ReceiveFromAsync(bytes, SocketFlags.None, any, timeout.Token);
                var request = IkeWire.ParseMessage(bytes.AsSpan(0, packet.ReceivedBytes));
                received++;
                original ??= request;
                Assert.Equal(0UL, request.ResponderSpi); Assert.Equal(0u, request.MessageId);
                Assert.Equal(original.InitiatorSpi, request.InitiatorSpi);
                if (i > 0)
                {
                    Assert.Equal(new byte[] { 0, 0, 0x40, 6, (byte)i }, request.Payloads[0].Body);
                    Assert.Equal(original.Payloads.Count + 1, request.Payloads.Count);
                    for (var p = 0; p < original.Payloads.Count; p++)
                    {
                        Assert.Equal(original.Payloads[p].Type, request.Payloads[p + 1].Type);
                        Assert.Equal(original.Payloads[p].Body, request.Payloads[p + 1].Body);
                    }
                }
                var reply = new IkeWire.IkeMessage { InitiatorSpi = request.InitiatorSpi, ResponderSpi = responderSpi, MessageId = 0, Exchange = IkeExchangeType.IkeSaInit, Flags = IkeFlags.Response };
                reply.Payloads.Add(new(IkePayloadType.Notify, exhaust || i < 2 ? new byte[] { protocol, 0, 0x40, 6, (byte)(i + 1) } : new byte[] { 0, 0, 0, 14 }));
                await socket.SendToAsync(IkeWire.SerializeMessage(reply), SocketFlags.None, packet.RemoteEndPoint, timeout.Token);
            } } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        var sim = new NoSim();
        var result = await IkeSession.EstablishAsync(new(ip, sim, "001010123456789", "001", "01",
            EpdgPort: ((IPEndPoint)socket.LocalEndPoint!).Port, Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 }), timeout.Token);
        timeout.Cancel(); await server;
        Assert.Equal(exhaust ? 4 : 3, received);
        Assert.False(result.Success); Assert.Equal(0, sim.Calls);
        Assert.Equal(!exhaust, result.RetryableInitialProposal);
        if (exhaust) Assert.Contains("COOKIE", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidCookieCases() => CookieCases().Where(c => !(bool)c[5]);

    [Theory]
    [MemberData(nameof(InvalidCookieCases))]
    public async Task ActualInvalidCookieCannotTriggerAnotherInit(string name, int length, byte protocol, byte spiSize, string extra, bool valid)
    {
        Assert.False(valid);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = 0;
        var server = Task.Run(async () =>
        {
            var bytes = new byte[65535];
            try { for (var i = 0; i < 2; i++)
            {
                var packet = await socket.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                received++;
                var request = IkeWire.ParseMessage(bytes.AsSpan(0, packet.ReceivedBytes));
                var response = i == 0 ? CookieResponse(length, protocol, spiSize, extra) : new IkeWire.IkeMessage();
                if (i > 0) response.Payloads.Add(new(IkePayloadType.Notify, [0, 0, 0, 14]));
                response.InitiatorSpi = request.InitiatorSpi; response.ResponderSpi = 0;
                response.Exchange = IkeExchangeType.IkeSaInit; response.Flags = IkeFlags.Response;
                await socket.SendToAsync(IkeWire.SerializeMessage(response), SocketFlags.None, packet.RemoteEndPoint, timeout.Token);
            } } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        var sim = new NoSim();
        var result = await IkeSession.EstablishAsync(new(IPAddress.Loopback, sim, "001010123456789", "001", "01",
            EpdgPort: ((IPEndPoint)socket.LocalEndPoint!).Port, Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 }), timeout.Token);
        timeout.Cancel(); await server;
        Assert.False(result.Success); Assert.Equal(0, sim.Calls);
        Assert.True(received == 1, name); Assert.False(result.RetryableInitialProposal);
    }
}
