using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Transport;

namespace qmiSharp.Tests;

public sealed class QualcommWwanTransportTests
{
    [Fact]
    public void AutomaticBindingUsesPhysicalContainerNotDiscoveryOrder()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var firstNet = Guid.NewGuid().ToString("B"); var secondNet = Guid.NewGuid().ToString("B");
        WindowsUsbFunction Port(Guid container, string port) => new("USB\\AT", container, Guid.Empty, "AT", port, null, null);
        WindowsUsbFunction Net(Guid container, string id) => new("USB\\VID_2C7C&PID_0125", container,
            WindowsUsbTopology.NetworkClass, "Quectel Wireless Ethernet Adapter", null, id, @"\DosDevices\Global\" + id + "_1");
        WindowsUsbFunction[] inventory = [Net(second, secondNet), Port(first, "COM6"), Net(first, firstNet), Port(second, "COM16")];
        Assert.Equal(@"\\.\Global\" + firstNet + "_1", QualcommWwanTransport.SelectControlPathForAtPort(inventory, "com6"));
        Assert.Equal(@"\\.\Global\" + secondNet + "_1", QualcommWwanTransport.SelectControlPathForAtPort(inventory, "COM16"));
        Assert.Null(QualcommWwanTransport.SelectControlPathForAtPort([inventory[1], inventory[0]], "COM6"));
        Assert.Null(QualcommWwanTransport.SelectControlPathForAtPort([.. inventory, Net(first, secondNet)], "COM6"));
        Assert.Throws<InvalidOperationException>(() => WindowsUsbTopology.SelectPort([.. inventory, inventory[1]], "COM6"));
        Assert.Throws<InvalidOperationException>(() => WindowsUsbTopology.SelectPort(inventory, "COM999"));
    }

    [Fact]
    public void AutomaticBindingRejectsControlFileFromAnotherNetworkInterface()
    {
        var container = Guid.NewGuid();
        WindowsUsbFunction port = new("USB\\AT", container, Guid.Empty, "AT", "COM6", null, null);
        WindowsUsbFunction network = new("USB\\VID_2C7C&PID_0125", container, WindowsUsbTopology.NetworkClass,
            "Quectel Adapter", null, Guid.NewGuid().ToString("B"), @"\DosDevices\Global\" + Guid.NewGuid().ToString("B") + "_1");
        Assert.Null(QualcommWwanTransport.SelectControlPathForAtPort([port, network], "COM6"));
        Assert.Throws<InvalidOperationException>(() => WindowsUsbTopology.SelectPort([port with { ContainerId = Guid.Empty }], "COM6"));
    }

    [Theory]
    [InlineData(@"\\.\Global\{075BE8A9-B03C-4A87-8F17-3888DD064DB8}_1", true)]
    [InlineData(@"\\.\Global\{075BE8A9-B03C-4A87-8F17-3888DD064DB8}_0", false)]
    [InlineData(@"\\.\Global\{075BE8A9-B03C-4A87-8F17-3888DD064DB8}_1\2", false)]
    [InlineData(@"\\.\Global\not-a-guid_1", false)]
    [InlineData(@"\\.\COM9", false)]
    public void ControlPathMustBeValidated(string path, bool expected)
    {
        Assert.Equal(expected, QualcommWwanTransport.TryParseControlPath(path, out _));
    }

    [Fact]
    public void ServiceBodyRestoresQmuxEnvelope()
    {
        var response = new QmiPacket(QmiServiceType.DMS, 7, 0x1234, 0x0021,
            [new QmiTlv(0x02, [0, 0, 0, 0])]) { IsResponse = true };
        var marshalled = response.Marshal();
        var restored = QualcommWwanTransport.WrapServiceBody((ushort)QmiServiceType.DMS,
            7, marshalled.AsSpan(QmuxHeader.Size));
        var decoded = QmiPacket.Unmarshal(restored);
        Assert.Equal(response.ServiceType, decoded.ServiceType);
        Assert.Equal(response.ClientId, decoded.ClientId);
        Assert.Equal(response.TransactionId, decoded.TransactionId);
        Assert.Equal(response.MessageId, decoded.MessageId);
        Assert.True(decoded.IsResponse);
        decoded.CheckResult();
    }

    [Fact]
    public void ServiceBodyRejectsCtlAndShortFrames()
    {
        Assert.Throws<FormatException>(() => QualcommWwanTransport.WrapServiceBody(0, 1, new byte[7]));
        Assert.Throws<FormatException>(() => QualcommWwanTransport.WrapServiceBody(2, 0, new byte[7]));
        Assert.Throws<FormatException>(() => QualcommWwanTransport.WrapServiceBody(2, 1, new byte[6]));
    }

    [Fact]
    public async Task DriverManagedTransportSkipsCtlAndOwnsCidLifetime()
    {
        await using var transport = new ManagedTransportStub();
        await using var client = await QmiClient.CreateAsync(transport);
        Assert.Equal(0, transport.SendCount); // no CTL Sync

        var first = await client.AllocateClientIdAsync(QmiServiceType.DMS);
        var second = await client.AllocateClientIdAsync(QmiServiceType.DMS);
        Assert.Equal((byte)7, first);
        Assert.Equal(first, second);
        Assert.Equal(1, transport.AllocateCount);
        Assert.Equal(0, transport.SendCount); // no CTL Allocate CID

        await client.ReleaseClientIdAsync(QmiServiceType.DMS, first);
        Assert.Equal(0, transport.ReleaseCount);
        await client.ReleaseClientIdAsync(QmiServiceType.DMS, second);
        Assert.Equal(1, transport.ReleaseCount);
        Assert.Equal(0, transport.SendCount); // no CTL Release CID
    }

    private sealed class ManagedTransportStub : IDriverManagedQmiTransport
    {
        public string Name => "managed-stub";
        public bool IsConnected => true;
        public int SendCount { get; private set; }
        public int AllocateCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public ValueTask<byte> AllocateClientIdAsync(ushort service,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal((ushort)QmiServiceType.DMS, service);
            AllocateCount++;
            return ValueTask.FromResult((byte)7);
        }

        public ValueTask ReleaseClientIdAsync(ushort service, byte clientId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal((ushort)QmiServiceType.DMS, service);
            Assert.Equal((byte)7, clientId);
            ReleaseCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            throw new InvalidOperationException("Unexpected CTL frame.");
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
