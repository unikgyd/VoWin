using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using qmiSharp.Core;
using qmiSharp.Transport;
using VoSharp.Modem;

namespace VoSharp.Tests;

public sealed class QmiImsProfileReaderTests
{
    [Fact]
    public async Task ReadsOnlyConfiguredImsProfileWithoutStartingNetwork()
    {
        await using var transport = new ProfileTransport();
        await using var reader = new QmiModemReader(() => transport);

        var profiles = await reader.GetConfiguredImsProfilesAsync();

        var ims = Assert.Single(profiles);
        Assert.Equal(new QmiImsProfile(7, "IPV4V6", "ims"), ims);
        Assert.Contains(transport.Requests, request => request == ((ushort)QmiServiceType.WDS, (ushort)0x002A));
        Assert.Equal(2, transport.Requests.Count(request => request ==
            ((ushort)QmiServiceType.WDS, (ushort)0x002B)));
        Assert.DoesNotContain(transport.Requests, request => request ==
            ((ushort)QmiServiceType.WDS, (ushort)0x0020));
    }

    private sealed class ProfileTransport : IQmiTransport
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        public ConcurrentQueue<(ushort Service, ushort Message)> Requests { get; } = new();
        public string Name => "WDS profile harness";
        public bool IsConnected { get; private set; } = true;

        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var request = QmiPacket.Unmarshal(buffer.Span);
            Requests.Enqueue((request.ServiceType, request.MessageId));
            var response = new QmiPacket(request.ServiceType, request.ClientId,
                request.TransactionId, request.MessageId) { IsResponse = true };
            response.TLVs.Add(new QmiTlv(0x02, [0, 0, 0, 0]));
            if (request.ServiceType == (ushort)QmiServiceType.Control && request.MessageId == 0x0022)
                response.TLVs.Add(new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 3]));
            else if (request.ServiceType == (ushort)QmiServiceType.WDS && request.MessageId == 0x002A)
                response.TLVs.Add(new QmiTlv(0x01,
                    [2, 0, 7, 3, (byte)'I', (byte)'M', (byte)'S',
                        0, 8, 8, (byte)'I', (byte)'n', (byte)'t', (byte)'e',
                        (byte)'r', (byte)'n', (byte)'e', (byte)'t']));
            else if (request.ServiceType == (ushort)QmiServiceType.WDS && request.MessageId == 0x002B)
            {
                var index = request.GetTlv(0x01)!.AsByte(1);
                response.TLVs.Add(QmiTlv.FromByte(0x11, index == 7 ? (byte)3 : (byte)0));
                response.TLVs.Add(new QmiTlv(0x14,
                    Encoding.ASCII.GetBytes(index == 7 ? "ims" : "internet")));
            }
            _responses.Writer.TryWrite(response.Marshal());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var frame = await _responses.Reader.ReadAsync(cancellationToken);
            frame.CopyTo(buffer);
            return frame.Length;
        }

        public void Dispose()
        {
            IsConnected = false;
            _responses.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
