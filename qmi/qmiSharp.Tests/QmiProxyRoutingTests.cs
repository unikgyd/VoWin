using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using qmiSharp.Core;
using qmiSharp.Proxy;
using qmiSharp.Transport;

namespace qmiSharp.Tests;

public sealed class QmiProxyRoutingTests
{
    [Fact]
    public async Task SameClientTransactionFromTwoSocketsIsRemappedAndRoutedPrivately()
    {
        await using var modem = new ProxyHarnessTransport();
        await using var proxy = new QmiProxyServer(modem, 0);
        await proxy.StartAsync();
        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync("127.0.0.1", proxy.BoundPort);
        await second.ConnectAsync("127.0.0.1", proxy.BoundPort);

        await first.GetStream().WriteAsync(Request(0xA1).Marshal());
        QmiPacket firstUpstream = QmiPacket.Unmarshal(await modem.Sent.Reader.ReadAsync());
        await second.GetStream().WriteAsync(Request(0xB2).Marshal());
        QmiPacket secondUpstream = QmiPacket.Unmarshal(await modem.Sent.Reader.ReadAsync());
        Assert.NotEqual(firstUpstream.TransactionId, secondUpstream.TransactionId);

        modem.Queue(Response(secondUpstream, 0xB2));
        modem.Queue(Response(firstUpstream, 0xA1));
        QmiPacket firstResponse = QmiPacket.Unmarshal(await ReadFrameAsync(first.GetStream()));
        QmiPacket secondResponse = QmiPacket.Unmarshal(await ReadFrameAsync(second.GetStream()));
        Assert.Equal((ushort)9, firstResponse.TransactionId);
        Assert.Equal((byte)0xA1, firstResponse.GetTlv(0x10)!.AsByte());
        Assert.Equal((ushort)9, secondResponse.TransactionId);
        Assert.Equal((byte)0xB2, secondResponse.GetTlv(0x10)!.AsByte());
    }

    private static QmiPacket Request(byte marker) => new(QmiServiceType.DMS, 0, 9, 0x22,
        new[] { QmiTlv.FromByte(0x10, marker) });

    private static QmiPacket Response(QmiPacket request, byte marker)
    {
        var response = new QmiPacket(request.ServiceType, request.ClientId, request.TransactionId, request.MessageId,
            new[] { new QmiTlv(0x02, new byte[] { 0, 0, 0, 0 }), QmiTlv.FromByte(0x10, marker) })
        { IsResponse = true };
        return response;
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
    {
        byte[] prefix = new byte[3];
        await stream.ReadExactlyAsync(prefix);
        int total = 1 + BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(1));
        byte[] frame = new byte[total];
        prefix.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(3));
        return frame;
    }

    private sealed class ProxyHarnessTransport : IQmiTransport
    {
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
        public Channel<byte[]> Sent { get; } = Channel.CreateUnbounded<byte[]>();
        public string Name => "Proxy protocol harness";
        public bool IsConnected { get; private set; } = true;
        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Sent.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] packet = await _received.Reader.ReadAsync(cancellationToken);
            packet.CopyTo(buffer);
            return packet.Length;
        }
        public void Queue(QmiPacket packet) => _received.Writer.TryWrite(packet.Marshal());
        public void Dispose()
        {
            IsConnected = false;
            _received.Writer.TryComplete();
            Sent.Writer.TryComplete();
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
