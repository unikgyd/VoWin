using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VoSharp.HostIms;

namespace VoSharp.Tests;

[SupportedOSPlatform("windows")]
public sealed class ManagedRasTests
{
    [Fact]
    public void MultiModemBindingUsesContainerAndRejectsAmbiguity()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        qmiSharp.Transport.WindowsUsbFunction Port(Guid c, string p) => new(@"USB\VID_2C7C&PID_0125", c, Guid.Empty, "AT", p, null, null);
        qmiSharp.Transport.WindowsUsbFunction Ras(Guid c, string n) => new(@"USB\RAS", c,
            qmiSharp.Transport.WindowsUsbTopology.ModemClass, n, null, null, null);
        qmiSharp.Transport.WindowsUsbFunction Net(Guid c) => new(@"USB\NET", c,
            qmiSharp.Transport.WindowsUsbTopology.NetworkClass, "Quectel Ethernet", null, Guid.NewGuid().ToString("B"), null);
        var nameA = "Quectel USB Modem Port"; var nameB = nameA + " #2";
        qmiSharp.Transport.WindowsUsbFunction[] inventory = [Port(b, "COM16"), Ras(b, nameB), Net(b), Port(a, "COM6"), Ras(a, nameA), Net(a)];
        var selected = WindowsModemBinding.Select(inventory, "COM16");
        Assert.Equal(b, selected.ContainerId); Assert.Equal(nameB, selected.RasDeviceName);
        RasDevice[] devices = [new() { Type = "modem", Name = nameA }, new() { Type = "modem", Name = nameB }];
        Assert.Equal(nameB, selected.SelectRasDevice(devices).Name);
        Assert.Throws<InvalidOperationException>(() => selected.SelectRasDevice([devices[0]]));
        Assert.Throws<InvalidOperationException>(() => selected.SelectRasDevice([devices[1], devices[1]]));
        Assert.Throws<InvalidOperationException>(() => WindowsModemBinding.Select([.. inventory, Net(b)], "COM16"));
        Assert.Throws<InvalidOperationException>(() => WindowsModemBinding.Select(inventory.Where(f => f.ClassId != qmiSharp.Transport.WindowsUsbTopology.ModemClass), "COM16"));
        Assert.NotEqual(WindowsModemBinding.EntryForImeiHash(new('a', 64)), WindowsModemBinding.EntryForImeiHash(new('b', 64)));
        Assert.Equal(WindowsModemBinding.EntryForImeiHash(ModemControl.HashImei("123456789012345")),
            VoSharp.Common.Utils.HostImsBearerIdentity.EntryForImei("123456789012345"));
    }

    [Fact]
    public void RasInteropMatchesTheX64WindowsSdk()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return;
        Assert.Equal(296, Marshal.SizeOf<RasDevice>());
        Assert.Equal(1388, Marshal.SizeOf<RasConnection>());
        Assert.Equal(608, Marshal.SizeOf<RasStatus>());
        Assert.Equal(2120, Marshal.SizeOf<RasDialParameters>());
        var entry = WindowsRas.BuildEntry(new() { Type = "modem", Name = "Quectel USB Modem Port" });
        Assert.Equal(6724, entry.Length);
        Assert.Equal(6724u, BinaryPrimitives.ReadUInt32LittleEndian(entry));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(324)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(328)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4))); // No software compression.
        Assert.Equal(9999u, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(WindowsRas.Ipv6MetricOffset)));
        Assert.Equal("*99***5#", System.Text.Encoding.Unicode.GetString(entry, 38, 16).TrimEnd('\0'));
    }

    [Fact]
    public void ModemSelectionRejectsAbsentOrAmbiguousDevices()
    {
        var modem = new RasDevice { Type = "modem", Name = "Quectel USB Modem Port" };
        Assert.Throws<InvalidOperationException>(() => WindowsRas.SelectModem([]));
        Assert.Throws<InvalidOperationException>(() => WindowsRas.SelectModem([modem, modem]));
        Assert.Equal(modem.Name, WindowsRas.SelectModem([modem, new() { Type = "vpn", Name = "not a modem" }]).Name);
    }

    [Fact]
    public void WindowsRasAndTaskSchedulerEnumerationDoesNotDialOrMutateRoutes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var report = System.Text.Json.JsonSerializer.Serialize(ManagedRuntimeDiagnostics.VerifyReadOnly());
        Assert.Contains("\"ManagedRas\":true", report);
        Assert.Contains("\"ManagedTaskScheduler\":true", report);
        Assert.Contains("\"ModemAccessed\":false", report);
        Assert.Contains("\"ChildProcessesStarted\":false", report);
    }

    [Fact]
    public void TemporaryPhonebookRoundTripsThroughWindowsWithoutDialing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var devices = WindowsRas.Devices().Where(device => device.Type == "modem").ToArray();
        if (devices.Length == 0) return;
        var path = WindowsRas.CreatePhonebook(devices[0], Path.GetTempPath());
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(9999u, WindowsRas.ReadPhonebookMetric(path));
        }
        finally { WindowsRas.RemovePhonebook(path); }
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData("")][InlineData("STOP\n")][InlineData("RENEW\nSTOP\n")][InlineData("RENEW\n")]
    public async Task PrivateLeaseStopsOnExplicitStopOrParentEof(string commands)
    {
        await WindowsRas.MonitorLeaseAsync(new StringReader(commands), TimeSpan.FromSeconds(2), () => true);
    }

    [Fact]
    public async Task PrivateLeaseRejectsInvalidCommandAndConnectionLoss()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsRas.MonitorLeaseAsync(new StringReader("unexpected\n"), TimeSpan.FromSeconds(2), () => true));
        await Assert.ThrowsAsync<IOException>(() => WindowsRas.MonitorLeaseAsync(new StringReader("STOP\n"), TimeSpan.FromSeconds(2), () => false));
    }

    private sealed class StalledInput : TextReader
    {
        public TaskCompletionSource<string?> Line { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<string?> ReadLineAsync() => Line.Task;
    }

    [Fact]
    public async Task UnresponsiveParentCannotBlockTheRasLeaseExpiry()
    {
        using var input = new StalledInput();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => WindowsRas.MonitorLeaseAsync(input,
                TimeSpan.FromMilliseconds(20), () => true)).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { input.Line.TrySetResult(null); }
    }
}
