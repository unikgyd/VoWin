using System.Threading.Channels;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Services;
using qmiSharp.Transport;
using Xunit;

namespace qmiSharp.Tests;

public sealed class UimLogicalChannelTests
{
    [Fact]
    public async Task OpenTransmitAndCloseCarryTheSelectedSlotAndChannel()
    {
        await using var wire = new ScriptedUimTransport();
        await using var client = await QmiClient.CreateAsync(wire,
            new QmiClientOptions { SyncOnOpen = false });
        await using var uim = new UimService(client);

        var aid = Convert.FromHexString("A0000000871004");
        var channel = await uim.OpenLogicalChannelAsync(1, aid);
        var response = await uim.SendApduAsync(1, channel, new byte[] { 0x40, 0xA4, 0, 4, 2, 0x6F, 2 });
        await uim.CloseLogicalChannelAsync(1, channel);

        Assert.Equal((byte)4, channel);
        Assert.Equal([0x90, 0x00], response);
        var open = Assert.Single(wire.Requests, request => request.MessageId == 0x0042);
        Assert.Equal((byte)1, open.GetTlv(0x01)!.AsByte());
        Assert.Equal([7, .. aid], open.GetTlv(0x10)!.Value.ToArray());
        var apdu = Assert.Single(wire.Requests, request => request.MessageId == 0x003B);
        Assert.Equal((byte)1, apdu.GetTlv(0x01)!.AsByte());
        Assert.Equal((byte)4, apdu.GetTlv(0x10)!.AsByte());
        Assert.Equal([7, 0, 0x40, 0xA4, 0, 4, 2, 0x6F, 2], apdu.GetTlv(0x02)!.Value.ToArray());
        var close = Assert.Single(wire.Requests, request => request.MessageId == 0x003F);
        Assert.Equal((byte)1, close.GetTlv(0x01)!.AsByte());
        Assert.Equal((byte)4, close.GetTlv(0x11)!.AsByte());
    }

    private sealed class ScriptedUimTransport : IQmiTransport
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        public List<QmiPacket> Requests { get; } = [];
        public string Name => "scripted-uim";
        public bool IsConnected => true;

        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var request = QmiPacket.Unmarshal(buffer.Span);
            Requests.Add(request);
            var tlvs = new List<QmiTlv> { new(0x02, [0, 0, 0, 0]) };
            if (request.ServiceType == (ushort)QmiServiceType.Control && request.MessageId == 0x0022)
                tlvs.Add(new QmiTlv(0x01, [(byte)QmiServiceType.UIM, 1]));
            if (request.MessageId == 0x0042)
            {
                tlvs.Add(new QmiTlv(0x10, [4]));
                tlvs.Add(new QmiTlv(0x11, [0x90, 0]));
            }
            if (request.MessageId == 0x003B)
                tlvs.Add(new QmiTlv(0x10, [2, 0, 0x90, 0]));
            var response = new QmiPacket(request.ServiceType, request.ClientId,
                request.TransactionId, request.MessageId, tlvs) { IsResponse = true };
            _responses.Writer.TryWrite(response.Marshal());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytes = await _responses.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public ValueTask DisposeAsync()
        {
            _responses.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _responses.Writer.TryComplete();
    }
}
