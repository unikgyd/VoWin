using System.Collections.Concurrent;
using System.Threading.Channels;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;
using qmiSharp.Transport;
using VoSharp.Modem;

namespace VoSharp.Tests;

public sealed class QmiImsBearerLeaseTests
{
    [Fact]
    public async Task ValidImsSettingsAreHeldUntilExplicitLeaseDisposal()
    {
        await using var transport = new BearerTransport(validImsSettings: true);
        await using var client = new QmiClient(transport,
            new QmiClientOptions { SyncOnOpen = false });
        var lease = new QmiImsBearerLease(client);

        var pdn = await lease.StartAsync("ims");
        Assert.Equal("ims", pdn.Apn);
        Assert.Equal("10.0.0.2", pdn.LocalAddress.ToString());
        Assert.Equal("10.0.0.1", Assert.Single(pdn.PcscfServers).ToString());
        Assert.Equal((uint)77, lease.PacketDataHandle);
        var hostProbe = lease.ProbeWindowsRoute(simInserted: true);
        Assert.Equal("QMI WDS", hostProbe.ProbeSource);
        Assert.Equal("ims", Assert.Single(hostProbe.Contexts).Apn);
        Assert.Equal(lease.CurrentPdn!.LocalAddress, Assert.Single(hostProbe.Contexts[0].LocalAddresses));
        Assert.DoesNotContain(transport.Requests, request => request.Message == 0x0021);

        await lease.DisposeAsync();
        Assert.Equal(new ushort[] { 0x0022, 0x004D, 0x0020, 0x002D, 0x0021, 0x0023 },
            transport.Requests.Select(request => request.Message).ToArray());
        Assert.Null(lease.PacketDataHandle);
        Assert.Null(lease.CurrentPdn);
    }

    [Fact]
    public async Task NonImsRuntimeSettingsRollbackBeforeCidRelease()
    {
        await using var transport = new BearerTransport(validImsSettings: false);
        await using var client = new QmiClient(transport,
            new QmiClientOptions { SyncOnOpen = false });
        var lease = new QmiImsBearerLease(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.StartAsync("ims"));
        Assert.Null(lease.PacketDataHandle);
        Assert.Null(lease.CurrentPdn);
        await lease.DisposeAsync();

        Assert.Equal(new ushort[] { 0x0022, 0x004D, 0x0020, 0x002D, 0x0021, 0x0023 },
            transport.Requests.Select(request => request.Message).ToArray());
    }

    [Fact]
    public async Task Ipv6BearerWithoutSameFamilyPcscfRollsBack()
    {
        await using var transport = new BearerTransport(validImsSettings: true, ipFamily: 6);
        await using var client = new QmiClient(transport,
            new QmiClientOptions { SyncOnOpen = false });
        var lease = new QmiImsBearerLease(client);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => lease.StartAsync("ims", 6));
        Assert.Contains("P-CSCF", failure.Message);
        Assert.Null(lease.PacketDataHandle);
        await lease.DisposeAsync();
        Assert.Equal(new ushort[] { 0x0022, 0x004D, 0x0020, 0x002D, 0x0021, 0x0023 },
            transport.Requests.Select(request => request.Message).ToArray());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("10.0.0.2")]
    public async Task UnusableOrSelfPcscfRollsBackOnlyOwnedPacketHandle(string pcscf)
    {
        await using var transport = new BearerTransport(true, pcscf: pcscf);
        await using var client = new QmiClient(transport, new QmiClientOptions { SyncOnOpen = false });
        var lease = new QmiImsBearerLease(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.StartAsync("ims"));
        Assert.Null(lease.PacketDataHandle); Assert.Null(lease.CurrentPdn);
        Assert.Equal(1, transport.Requests.Count(request => request.Message == (ushort)WdsMessageId.StopNetwork));
        await lease.DisposeAsync();
    }

    private sealed class BearerTransport(bool validImsSettings, byte ipFamily = 4, string pcscf = "10.0.0.1") : IQmiTransport
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        public ConcurrentQueue<(ushort Service, ushort Message)> Requests { get; } = new();
        public string Name => "IMS bearer harness";
        public bool IsConnected { get; private set; } = true;

        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var request = QmiPacket.Unmarshal(buffer.Span);
            Requests.Enqueue((request.ServiceType, request.MessageId));
            var response = new QmiPacket(request.ServiceType, request.ClientId,
                request.TransactionId, request.MessageId) { IsResponse = true };
            response.TLVs.Add(new QmiTlv(0x02, [0, 0, 0, 0]));
            if (request.ServiceType == (ushort)QmiServiceType.Control && request.MessageId == 0x0022)
                response.TLVs.Add(new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 12]));
            else if (request.ServiceType == (ushort)QmiServiceType.WDS &&
                     request.MessageId == (ushort)WdsMessageId.SetIpFamily)
            {
                if (request.GetTlv(0x01)?.AsByte() != ipFamily)
                    throw new InvalidOperationException("Unexpected WDS IP family.");
            }
            else if (request.ServiceType == (ushort)QmiServiceType.WDS &&
                     request.MessageId == (ushort)WdsMessageId.StartNetwork)
                response.TLVs.Add(QmiTlv.FromUInt32(0x01, 77));
            else if (request.ServiceType == (ushort)QmiServiceType.WDS &&
                     request.MessageId == (ushort)WdsMessageId.GetCurrentSettings)
            {
                response.TLVs.Add(QmiTlv.FromString(0x14, validImsSettings ? "ims" : "internet"));
                if (ipFamily == 6)
                    response.TLVs.Add(new QmiTlv(0x25, [0x20, 0x01, 0x0d, 0xb8,
                        0x12, 0x34, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 64]));
                else
                {
                    response.TLVs.Add(new QmiTlv(0x1E, [2, 0, 0, 10]));
                    response.TLVs.Add(new QmiTlv(0x23, [1, .. System.Net.IPAddress.Parse(pcscf).GetAddressBytes().Reverse()]));
                }
            }
            else if (request.ServiceType != (ushort)QmiServiceType.Control &&
                     request.MessageId != (ushort)WdsMessageId.StopNetwork)
                throw new InvalidOperationException($"Unexpected QMI request 0x{request.MessageId:X4}.");
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
