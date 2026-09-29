using System.Threading.Channels;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Services;
using qmiSharp.Transport;

namespace qmiSharp.Tests;

public sealed class PdcProtocolTests
{
    [Fact]
    public async Task ActivateUsesMessage27AndCompletesOnlyAfterTokenMatchedIndication()
    {
        await using var transport = new PdcHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromSeconds(2)
        });
        var pdc = new PdcService(client);

        Task activation = pdc.ActivateConfigAsync();

        QmiPacket allocate = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(0x0022, allocate.MessageId);
        transport.Queue(Success(allocate, new QmiTlv(0x01, new byte[] { (byte)QmiServiceType.PDC, 5 })));

        QmiPacket request = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(0x0027, request.MessageId);
        Assert.Equal(1u, request.GetTlv(0x01)!.AsUInt32());
        uint token = request.GetTlv(0x10)!.AsUInt32();
        transport.Queue(Success(request));
        await Task.Delay(20);
        Assert.False(activation.IsCompleted);

        var indication = new QmiPacket(QmiServiceType.PDC, 5, 0, 0x0027, new[]
        {
            QmiTlv.FromUInt16(0x01, 0),
            QmiTlv.FromUInt32(0x10, token)
        }) { IsIndication = true };
        transport.Queue(indication);
        await activation;

        Task disposing = pdc.DisposeAsync().AsTask();
        QmiPacket release = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(0x0023, release.MessageId);
        transport.Queue(Success(release, release.GetTlv(0x01)));
        await disposing;
    }

    private static QmiPacket Success(QmiPacket request, QmiTlv? extra = null)
    {
        var response = new QmiPacket(request.ServiceType, request.ClientId, request.TransactionId, request.MessageId)
        {
            IsResponse = true
        };
        response.TLVs.Add(new QmiTlv(0x02, new byte[] { 0, 0, 0, 0 }));
        if (extra is not null) response.TLVs.Add(extra);
        return response;
    }

    private sealed class PdcHarnessTransport : IQmiTransport
    {
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
        public Channel<byte[]> Sent { get; } = Channel.CreateUnbounded<byte[]>();
        public string Name => "PDC protocol harness";
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
