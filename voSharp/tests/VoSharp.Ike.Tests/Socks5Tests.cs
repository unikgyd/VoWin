using System.Net;
using System.Net.Sockets;
using VoSharp.Ike.Transport;
using Xunit;

namespace VoSharp.Ike.Tests;

public class Socks5Tests
{
    [Fact]
    public void ParseProxyUrl_ValidUrls_ParsesCorrectly()
    {
        var client1 = Socks5Client.TryParse("socks5://127.0.0.1:10808");
        Assert.NotNull(client1);
        Assert.Equal("127.0.0.1", client1.ProxyHost);
        Assert.Equal(10808, client1.ProxyPort);
        Assert.Null(client1.Username);
        Assert.Null(client1.Password);

        var client2 = Socks5Client.TryParse("socks5://user:pass123@proxy.example.com:1080");
        Assert.NotNull(client2);
        Assert.Equal("proxy.example.com", client2.ProxyHost);
        Assert.Equal(1080, client2.ProxyPort);
        Assert.Equal("user", client2.Username);
        Assert.Equal("pass123", client2.Password);

        var direct = Socks5Client.TryParse("direct");
        Assert.Null(direct);

        // IKEv2 needs SOCKS5 UDP ASSOCIATE; an HTTP CONNECT endpoint is not
        // interchangeable and must never be interpreted as a direct route.
        Assert.Null(Socks5Client.TryParse("http://127.0.0.1:7890"));
    }

    [Fact]
    public void UdpEncapsulateAndDecapsulate_ValidDatagram_RoundtripsSuccessfully()
    {
        var targetEp = new IPEndPoint(IPAddress.Parse("183.221.243.208"), 4500);
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

        byte[] enc = Socks5Client.EncapsulateUdpDatagram(payload, targetEp);
        // Header is 4 bytes prefix + 4 bytes IPv4 + 2 bytes port = 10 bytes + 6 bytes payload = 16 bytes
        Assert.Equal(16, enc.Length);
        Assert.Equal(0x00, enc[0]); // RSV
        Assert.Equal(0x00, enc[1]); // RSV
        Assert.Equal(0x00, enc[2]); // FRAG
        Assert.Equal(0x01, enc[3]); // ATYP IPv4

        var innerPayload = Socks5Client.DecapsulateUdpDatagram(enc);
        Assert.NotNull(innerPayload);
        Assert.Equal(payload, innerPayload.Value.ToArray());
    }

    [Fact]
    public async Task Socks5Client_UdpAssociate_WithMockServer_Succeeds()
    {
        // Setup local TCP mock server
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int mockPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            // 1. Read Greeting (VER=5, NMETHODS=1, METHOD=0)
            byte[] buf = new byte[32];
            int read = await stream.ReadAsync(buf, 0, 3);
            Assert.Equal(0x05, buf[0]);

            // Respond No Auth Required (VER=5, METHOD=0)
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            // 2. Read Request (VER=5, CMD=3 (UDP), RSV=0, ATYP=1, IP=0.0.0.0, PORT=0)
            read = await stream.ReadAsync(buf, 0, 10);
            Assert.Equal(0x05, buf[0]);
            Assert.Equal(0x03, buf[1]); // UDP ASSOCIATE

            // Respond Success with BND.ADDR=127.0.0.1, BND.PORT=54321
            byte[] relayResp = new byte[] {
                0x05, 0x00, 0x00, 0x01,
                127, 0, 0, 1,
                (byte)(54321 >> 8), (byte)(54321 & 0xFF)
            };
            await stream.WriteAsync(relayResp);

            // Keep alive until client closes
            try
            {
                while (await stream.ReadAsync(buf, 0, buf.Length) > 0) { }
            }
            catch { }
        });

        using var socks = new Socks5Client("127.0.0.1", mockPort);
        var relayEp = await socks.UdpAssociateAsync();

        Assert.NotNull(relayEp);
        Assert.Equal(IPAddress.Loopback, relayEp.Address);
        Assert.Equal(54321, relayEp.Port);

        socks.Dispose();
        listener.Stop();
        await serverTask;
    }

    [Fact]
    public async Task IkeTransport_FloatTo4500_ReusesExistingSocksUdpAssociation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int mockPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var secondAssociateReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            var associate = new byte[10];
            await stream.ReadExactlyAsync(associate);
            Assert.Equal(0x03, associate[1]);
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0xD4, 0x31 });

            var extra = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try
            {
                secondAssociateReceived.TrySetResult(await stream.ReadAsync(extra, timeout.Token) > 0);
            }
            catch (OperationCanceledException)
            {
                secondAssociateReceived.TrySetResult(false);
            }
        });

        using var socks = new Socks5Client("127.0.0.1", mockPort);
        using var transport = new IkeTransport(IPAddress.Loopback, socks5Client: socks);
        var originalLocalPort = transport.LocalEndpoint!.Port;

        transport.FloatTo4500();

        Assert.Equal(originalLocalPort, transport.LocalEndpoint!.Port);
        Assert.Equal(4500, transport.RemoteEndpoint.Port);
        Assert.False(await secondAssociateReceived.Task);

        listener.Stop();
        await serverTask;
    }
}
