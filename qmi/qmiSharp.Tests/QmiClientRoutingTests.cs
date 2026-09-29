using System.Threading.Channels;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;
using qmiSharp.Services;
using qmiSharp.Transport;

namespace qmiSharp.Tests;

public sealed class QmiClientRoutingTests
{
    [Fact]
    public void WdsProfileListRequiresCompleteEntries()
    {
        var profiles = WdsService.ParseProfileList(new QmiTlv(0x01,
            [2, 0, 7, 3, (byte)'i', (byte)'m', (byte)'s', 0, 8, 0]));
        Assert.Equal(2, profiles.Count);
        Assert.Equal(new WdsProfileSummary(0, 7, "ims"), profiles[0]);
        Assert.Equal(new WdsProfileSummary(0, 8, string.Empty), profiles[1]);

        Assert.Throws<FormatException>(() => WdsService.ParseProfileList(
            new QmiTlv(0x01, [1, 0, 7, 3, (byte)'i'])));
        Assert.Throws<FormatException>(() => WdsService.ParseProfileList(
            new QmiTlv(0x01, [2, 0, 7, 0])));
        Assert.Throws<FormatException>(() => WdsService.ParseProfileList(null));
    }

    [Fact]
    public async Task DedicatedWdsCidIsIsolatedFromSharedWdsCid()
    {
        await using var transport = new ProtocolHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromSeconds(2)
        });
        await using var shared = new WdsService(client);
        await using var dedicated = new WdsService(client, dedicatedClientId: true);

        var sharedStart = shared.InitializeAsync();
        var sharedAlloc = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(0x0022, sharedAlloc.MessageId);
        transport.QueueResponse(SuccessResponse(sharedAlloc,
            extra: new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 3])));
        await sharedStart;

        var dedicatedStart = dedicated.InitializeAsync();
        var dedicatedAlloc = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(0x0022, dedicatedAlloc.MessageId);
        transport.QueueResponse(SuccessResponse(dedicatedAlloc,
            extra: new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 4])));
        await dedicatedStart;
        Assert.Equal((byte)3, shared.ClientId);
        Assert.Equal((byte)4, dedicated.ClientId);

        var packetStatus = dedicated.GetPacketServiceStatusAsync();
        var dedicatedRequest = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal((byte)4, dedicatedRequest.ClientId);
        transport.QueueResponse(SuccessResponse(dedicatedRequest,
            extra: new QmiTlv(0x01, [2])));
        Assert.Equal(WdsPacketServiceStatus.Connected, await packetStatus);

        var setFamily = dedicated.SetIpFamilyAsync(4);
        var familyRequest = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal((byte)4, familyRequest.ClientId);
        Assert.Equal((byte)4, familyRequest.GetTlv(0x01)!.AsByte());
        transport.QueueResponse(SuccessResponse(familyRequest));
        await setFamily;

        var releaseDedicated = dedicated.DisposeAsync().AsTask();
        var dedicatedRelease = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(new byte[] { (byte)QmiServiceType.WDS, 4 },
            dedicatedRelease.GetTlv(0x01)!.Value.ToArray());
        transport.QueueResponse(SuccessResponse(dedicatedRelease));
        await releaseDedicated;

        var releaseShared = shared.DisposeAsync().AsTask();
        var sharedRelease = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(new byte[] { (byte)QmiServiceType.WDS, 3 },
            sharedRelease.GetTlv(0x01)!.Value.ToArray());
        transport.QueueResponse(SuccessResponse(sharedRelease));
        await releaseShared;
    }

    [Fact]
    public async Task CancelledWdsStartSendsAbortOnItsDedicatedCid()
    {
        await using var transport = new ProtocolHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromSeconds(2)
        });
        var wds = new WdsService(client, dedicatedClientId: true);
        using var cancellation = new CancellationTokenSource();

        var start = wds.StartNetworkInterfaceAsync("ims", cancellationToken: cancellation.Token);
        var allocate = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(allocate,
            extra: new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 7])));
        var startRequest = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal((ushort)WdsMessageId.StartNetwork, startRequest.MessageId);
        Assert.Equal((byte)7, startRequest.ClientId);
        Assert.Equal("ims", startRequest.GetTlv(0x14)!.AsString());

        cancellation.Cancel();
        var abort = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal((ushort)WdsMessageId.Abort, abort.MessageId);
        Assert.Equal(startRequest.ClientId, abort.ClientId);
        Assert.Equal(startRequest.TransactionId, abort.GetTlv(0x01)!.AsUInt16());
        transport.QueueResponse(SuccessResponse(abort));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);

        var releasing = wds.DisposeAsync().AsTask();
        var release = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(new byte[] { (byte)QmiServiceType.WDS, 7 },
            release.GetTlv(0x01)!.Value.ToArray());
        transport.QueueResponse(SuccessResponse(release));
        await releasing;
    }

    [Fact]
    public async Task WdsStartDoesNotUseShortClientDefaultTimeout()
    {
        await using var transport = new ProtocolHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromMilliseconds(100)
        });
        var wds = new WdsService(client, dedicatedClientId: true);

        var start = wds.StartNetworkInterfaceAsync("ims");
        var allocate = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(allocate,
            extra: new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 8])));
        var startRequest = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        await Task.Delay(180);
        Assert.False(start.IsCompleted);
        transport.QueueResponse(SuccessResponse(startRequest,
            extra: QmiTlv.FromUInt32(0x01, 42)));
        Assert.Equal((uint)42, await start);

        var stopping = wds.StopNetworkInterfaceAsync(42);
        var stop = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal((ushort)WdsMessageId.StopNetwork, stop.MessageId);
        Assert.Equal((uint)42, stop.GetTlv(0x01)!.AsUInt32());
        transport.QueueResponse(SuccessResponse(stop));
        await stopping;

        var releasing = wds.DisposeAsync().AsTask();
        var release = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(release));
        await releasing;
    }

    [Fact]
    public async Task FailedWdsAbortReportsIndeterminateBearerState()
    {
        await using var transport = new ProtocolHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromSeconds(2)
        });
        var wds = new WdsService(client, dedicatedClientId: true);
        using var cancellation = new CancellationTokenSource();

        var start = wds.StartNetworkInterfaceAsync("ims", cancellationToken: cancellation.Token);
        var allocate = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(allocate,
            extra: new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 9])));
        await transport.Sent.Reader.ReadAsync(); // StartNetwork was transmitted.
        cancellation.Cancel();
        var abort = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        var rejection = new QmiPacket(abort.ServiceType, abort.ClientId,
            abort.TransactionId, abort.MessageId) { IsResponse = true };
        rejection.TLVs.Add(new QmiTlv(0x02, [1, 0, 1, 0]));
        transport.QueueResponse(rejection);

        var error = await Assert.ThrowsAsync<QmiException>(() => start);
        Assert.Contains("indeterminate", error.Message);
        Assert.True(wds.IsBearerStateIndeterminate);
        Assert.Equal((byte)9, wds.ClientId);
        await Assert.ThrowsAsync<QmiException>(() => wds.DisposeAsync().AsTask());
        Assert.False(transport.Sent.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DisposeStopsOwnedNetworkBeforeReleasingDedicatedCid()
    {
        await using var transport = new ProtocolHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromSeconds(2)
        });
        var wds = new WdsService(client, dedicatedClientId: true);

        var start = wds.StartNetworkInterfaceAsync("ims");
        var allocate = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(allocate,
            extra: new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 10])));
        var startRequest = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(startRequest,
            extra: QmiTlv.FromUInt32(0x01, 123)));
        Assert.Equal((uint)123, await start);
        Assert.Equal((uint)123, wds.OwnedPacketDataHandle);

        var disposing = wds.DisposeAsync().AsTask();
        var stop = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal((ushort)WdsMessageId.StopNetwork, stop.MessageId);
        Assert.Equal((uint)123, stop.GetTlv(0x01)!.AsUInt32());
        Assert.False(transport.Sent.Reader.TryRead(out _)); // CID is not released before Stop succeeds.
        transport.QueueResponse(SuccessResponse(stop));
        var release = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal((ushort)0x0023, release.MessageId);
        Assert.Equal(new byte[] { (byte)QmiServiceType.WDS, 10 },
            release.GetTlv(0x01)!.Value.ToArray());
        transport.QueueResponse(SuccessResponse(release));
        await disposing;
        Assert.Null(wds.OwnedPacketDataHandle);
        Assert.Equal((byte)0, wds.ClientId);
    }

    [Fact]
    public async Task FailedStopRetainsHandleAndCidForRetry()
    {
        await using var transport = new ProtocolHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromSeconds(2)
        });
        var wds = new WdsService(client, dedicatedClientId: true);

        var start = wds.StartNetworkInterfaceAsync("ims");
        var allocate = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(allocate,
            extra: new QmiTlv(0x01, [(byte)QmiServiceType.WDS, 11])));
        var startRequest = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(startRequest,
            extra: QmiTlv.FromUInt32(0x01, 456)));
        await start;

        var stopping = wds.StopNetworkInterfaceAsync(456);
        var firstStop = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        var rejected = new QmiPacket(firstStop.ServiceType, firstStop.ClientId,
            firstStop.TransactionId, firstStop.MessageId) { IsResponse = true };
        rejected.TLVs.Add(new QmiTlv(0x02, [1, 0, 1, 0]));
        transport.QueueResponse(rejected);
        await Assert.ThrowsAsync<QmiException>(() => stopping);
        Assert.Equal((uint)456, wds.OwnedPacketDataHandle);
        Assert.Equal((byte)11, wds.ClientId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wds.StopNetworkInterfaceAsync(999));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wds.StartNetworkInterfaceAsync("ims"));

        var disposing = wds.DisposeAsync().AsTask();
        var retryStop = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal((uint)456, retryStop.GetTlv(0x01)!.AsUInt32());
        transport.QueueResponse(SuccessResponse(retryStop));
        var release = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        transport.QueueResponse(SuccessResponse(release));
        await disposing;
        Assert.Null(wds.OwnedPacketDataHandle);
        Assert.Equal((byte)0, wds.ClientId);
    }

    [Fact]
    public async Task ResponseMustMatchServiceCidTransactionAndMessage()
    {
        await using var transport = new ProtocolHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromSeconds(2)
        });

        Task<QmiPacket> pending = client.SendRequestAsync(new QmiPacket(QmiServiceType.DMS, 3, 0, 0x22));
        QmiPacket request = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());

        transport.QueueResponse(SuccessResponse(request, clientId: 4));
        await Task.Delay(25);
        Assert.False(pending.IsCompleted);

        transport.QueueResponse(SuccessResponse(request, clientId: 3));
        QmiPacket response = await pending;
        Assert.Equal(3, response.ClientId);
        Assert.Equal(request.TransactionId, response.TransactionId);
    }

    [Fact]
    public async Task SixteenBitServiceUsesQrtrAllocateAndReleaseCtlMessages()
    {
        await using var transport = new ProtocolHarnessTransport();
        await using var client = new QmiClient(transport, new QmiClientOptions
        {
            SyncOnOpen = false,
            DefaultTimeout = TimeSpan.FromSeconds(2)
        });

        Task<byte> allocating = client.AllocateClientIdAsync(QmiServiceType.SSC);
        QmiPacket allocate = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(0xFF22, allocate.MessageId);
        Assert.Equal((ushort)QmiServiceType.SSC, allocate.GetTlv(0x01)!.AsUInt16());
        transport.QueueResponse(SuccessResponse(allocate, extra: new QmiTlv(0x01, new byte[] { 0x90, 0x01, 0x07 })));
        Assert.Equal(7, await allocating);

        Task releasing = client.ReleaseClientIdAsync(QmiServiceType.SSC, 7);
        QmiPacket release = QmiPacket.Unmarshal(await transport.Sent.Reader.ReadAsync());
        Assert.Equal(0xFF23, release.MessageId);
        Assert.Equal(new byte[] { 0x90, 0x01, 0x07 }, release.GetTlv(0x01)!.Value.ToArray());
        transport.QueueResponse(SuccessResponse(release, extra: release.GetTlv(0x01)));
        await releasing;
    }

    private static QmiPacket SuccessResponse(QmiPacket request, byte? clientId = null, QmiTlv? extra = null)
    {
        var response = new QmiPacket(request.ServiceType, clientId ?? request.ClientId, request.TransactionId, request.MessageId)
        {
            IsResponse = true
        };
        response.TLVs.Add(new QmiTlv(0x02, new byte[] { 0, 0, 0, 0 }));
        if (extra is not null) response.TLVs.Add(extra);
        return response;
    }

    private sealed class ProtocolHarnessTransport : IQmiTransport
    {
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
        public Channel<byte[]> Sent { get; } = Channel.CreateUnbounded<byte[]>();
        public string Name => "Protocol harness";
        public bool IsConnected { get; private set; } = true;

        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Sent.Writer.WriteAsync(buffer.ToArray(), cancellationToken);

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] packet = await _received.Reader.ReadAsync(cancellationToken);
            packet.CopyTo(buffer);
            return packet.Length;
        }

        public void QueueResponse(QmiPacket packet) => _received.Writer.TryWrite(packet.Marshal());

        public void Dispose()
        {
            IsConnected = false;
            _received.Writer.TryComplete();
            Sent.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
