using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using VoWin.Services;
using Xunit.Abstractions;

namespace VoWin.Tests;

public sealed class ReadOnlyAdbFactAttribute : FactAttribute
{
    public ReadOnlyAdbFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_READONLY_ADB") != "1")
            Skip = "Requires an explicitly selected live QDC507 read-only lab run.";
    }
}

public class AdbServerClientTests(ITestOutputHelper output)
{
    private static async Task ServeAsync(Func<NetworkStream, Task> server, Func<AdbServerClient, Task> client)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await server(peer.GetStream());
        });
        try { await client(new AdbServerClient(port)); await serving.WaitAsync(deadline.Token); }
        finally { listener.Stop(); }
    }

    private static async Task<string> RequestAsync(NetworkStream stream)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header);
        var length = int.Parse(Encoding.ASCII.GetString(header), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task ReplyAsync(NetworkStream stream, string value)
    {
        // Deliberately fragment responses across TCP reads.
        foreach (var b in Encoding.UTF8.GetBytes(value)) await stream.WriteAsync(new byte[] { b });
    }

    private static async Task<string> TransportAsync(NetworkStream stream, string serial)
    {
        Assert.Equal("host:transport:" + serial, await RequestAsync(stream));
        await ReplyAsync(stream, "OKAY");
        var service = await RequestAsync(stream);
        await ReplyAsync(stream, "OKAY");
        return service;
    }

    private static async Task<string> TransportIdAsync(NetworkStream stream, ulong id)
    {
        Assert.Equal("host:transport-id:" + id.ToString(CultureInfo.InvariantCulture), await RequestAsync(stream));
        await ReplyAsync(stream, "OKAY");
        var service = await RequestAsync(stream);
        await ReplyAsync(stream, "OKAY");
        return service;
    }

    [Fact]
    public async Task LongDeviceListPreservesDuplicateSerialsAndUint64TransportIds()
    {
        const string devices = "?\tdevice product:qdc transport_id:8\n?\tdevice transport_id:18446744073709551615\n";
        await ServeAsync(async stream =>
        {
            Assert.Equal("host:devices-l", await RequestAsync(stream));
            await ReplyAsync(stream, "OKAY" + Encoding.UTF8.GetByteCount(devices).ToString("X4") + devices);
        }, async client =>
        {
            var parsed = AdbServerClient.ParseDeviceTransports(await client.DevicesLongAsync(CancellationToken.None));
            Assert.Equal(new[] { new AdbTransportDevice("?", 8), new AdbTransportDevice("?", ulong.MaxValue) }, parsed);
        });
    }

    [Theory]
    [InlineData("? device")]
    [InlineData("? device transport_id:0")]
    [InlineData("? device transport_id:-1")]
    [InlineData("? device transport_id:18446744073709551616")]
    [InlineData("? device transport_id:1 transport_id:2")]
    [InlineData("? device transport_id:1\nother device transport_id:1")]
    public void MissingOrAmbiguousTransportIdsNeverFallBackToSerial(string inventory) =>
        Assert.Throws<IOException>(() => AdbServerClient.ParseDeviceTransports(inventory));

    [Fact]
    public void TransportInventoryExcludesNetworkEmulatorOfflineAndUnselectedSerials()
    {
        const string inventory = "127.0.0.1:5555 device\nemulator-5554 device\nphone offline\nother unauthorized\n? device transport_id:7\nselected device transport_id:9";
        Assert.Equal(2, AdbServerClient.ParseDeviceTransports(inventory).Length);
        Assert.Equal(new AdbTransportDevice("selected", 9), Assert.Single(AdbServerClient.ParseDeviceTransports(inventory, " selected ")));
    }

    [Fact]
    public async Task TransportIdShellTargetsUint64IdNotAmbiguousQuestionMarkSerial()
    {
        await ServeAsync(async stream =>
        {
            var service = await TransportIdAsync(stream, ulong.MaxValue);
            var marker = Regex.Match(service, @"__VOWIN_EXIT_[0-9a-f]{32}__").Value;
            await ReplyAsync(stream, "identity-guard-rejected\n" + marker + ":72\n");
        }, async client => Assert.Equal(72, (await client.ShellAsync(ulong.MaxValue, "exit 72", CancellationToken.None)).ExitCode));
    }

    [Fact]
    public async Task DisconnectedTransportIdIsNotRetriedBySerialOrDefaultTransport()
    {
        await ServeAsync(async stream =>
        {
            Assert.Equal("host:transport-id:12", await RequestAsync(stream));
            await ReplyAsync(stream, "FAIL0010device not found");
        }, async client => await Assert.ThrowsAsync<IOException>(() => client.ShellAsync(12UL, "true", CancellationToken.None)));
    }

    [Fact]
    public async Task ZeroTransportIdIsRejectedBeforeConnection() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new AdbServerClient(1).ShellAsync(0UL, "true", CancellationToken.None));

    [Fact]
    public async Task NonModemHardwareReceivesOnlyFingerprintQueryAndNeverHelperDeployment()
    {
        await ServeAsync(async stream =>
        {
            var service = await TransportIdAsync(stream, 44);
            Assert.Contains(Qdc507VoiceRuntime.IdentityProbeCommand, service);
            Assert.DoesNotContain("sync:", service); Assert.DoesNotContain("mkdir", service);
            var marker = Regex.Match(service, @"__VOWIN_EXIT_[0-9a-f]{32}__").Value;
            await ReplyAsync(stream, "5.15\nMSM8996\n2000\n\n" + marker + ":0\n");
        }, async client => Assert.Null(await Qdc507AdbIdentity.ReadImeiAsync(client, 44, CancellationToken.None)));
    }

    [Fact]
    public async Task DeviceListUsesUtf8ByteLengthAndFragmentedReplies()
    {
        const string devices = "module\tdevice\n手机\toffline\n";
        await ServeAsync(async stream =>
        {
            Assert.Equal("host:devices", await RequestAsync(stream));
            await ReplyAsync(stream, "OKAY" + Encoding.UTF8.GetByteCount(devices).ToString("X4") + devices);
        }, async client => Assert.Equal(devices, await client.DevicesAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task LegacyShellIsExplicitlyTargetedQuotedAndReturnsActualExitStatus()
    {
        await ServeAsync(async stream =>
        {
            var service = await TransportAsync(stream, "verified-module");
            Assert.StartsWith("shell:sh -c '", service);
            Assert.Contains("'\"'\"'", service); // quote inside caller command
            Assert.Contains("</dev/null", service);
            var marker = Regex.Match(service, @"__VOWIN_EXIT_[0-9a-f]{32}__").Value;
            Assert.NotEmpty(marker);
            await ReplyAsync(stream, "中文 result\n\n" + marker + ":17\n");
        }, async client =>
        {
            var result = await client.ShellAsync("verified-module", "printf '%s' 中文; exit 17", CancellationToken.None);
            Assert.Equal(17, result.ExitCode); Assert.Equal("中文 result\n", result.Output);
        });
    }

    [Theory]
    [InlineData("lost transport")]
    [InlineData("\nmarker:0\ntrailing data")]
    [InlineData("\nmarker:999\n")]
    public void MissingOrMalformedExitStatusNeverLooksSuccessful(string output) =>
        Assert.Throws<IOException>(() => AdbServerClient.ParseShellResult(output, "marker"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncV1StreamsBoundedChunksAndWaitsForDeviceAcknowledgement(bool useTransportId)
    {
        var expected = Enumerable.Range(0, 150000).Select(i => (byte)i).ToArray();
        await ServeAsync(async stream =>
        {
            Assert.Equal("sync:", useTransportId ? await TransportIdAsync(stream, 33) : await TransportAsync(stream, "module"));
            var header = new byte[8]; await stream.ReadExactlyAsync(header);
            Assert.Equal("SEND", Encoding.ASCII.GetString(header, 0, 4));
            var path = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4))]; await stream.ReadExactlyAsync(path);
            Assert.Equal("/tmp/vowin-call/test.bin,33188", Encoding.UTF8.GetString(path));
            using var received = new MemoryStream();
            while (true)
            {
                await stream.ReadExactlyAsync(header);
                var id = Encoding.ASCII.GetString(header, 0, 4);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                if (id == "DONE") { Assert.True(length > 1700000000); break; }
                Assert.Equal("DATA", id); Assert.InRange(length, 1u, 65536u);
                var data = new byte[length]; await stream.ReadExactlyAsync(data); received.Write(data);
            }
            Assert.Equal(expected, received.ToArray());
            await stream.WriteAsync("OKAY\0\0\0\0"u8.ToArray());
        }, async client =>
        {
            using var content = new MemoryStream(expected);
            if (useTransportId) await client.PushAsync(33UL, content, "/tmp/vowin-call/test.bin", CancellationToken.None);
            else await client.PushAsync("module", content, "/tmp/vowin-call/test.bin", CancellationToken.None);
        });
    }

    [Fact]
    public async Task TransportFailIsNotRetriedOnAnotherDevice()
    {
        await ServeAsync(async stream =>
        {
            Assert.Equal("host:transport:missing", await RequestAsync(stream));
            await ReplyAsync(stream, "FAIL0010device not found");
        }, async client =>
        {
            var error = await Assert.ThrowsAsync<IOException>(() => client.ShellAsync("missing", "true", CancellationToken.None));
            Assert.Contains("device not found", error.Message);
        });
    }

    [Fact]
    public async Task TimeoutClosesOnlyTheClientConnection()
    {
        await ServeAsync(async stream =>
        {
            Assert.Equal("host:devices", await RequestAsync(stream));
            Assert.Equal(0, await stream.ReadAsync(new byte[1])); // client canceled and disposed, no host:kill
        }, async client =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DevicesAsync(deadline.Token));
        });
    }

    [Theory]
    [InlineData("/dev/block/test")]
    [InlineData("/tmp/vowin-call/../other")]
    [InlineData("/tmp/vowin-call/test,33188")]
    public async Task UploadRejectsUnsafeDestinationBeforeConnecting(string path) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new AdbServerClient(1).PushAsync("module", Stream.Null, path, CancellationToken.None));

    [Fact]
    public async Task TargetSelectionRequiresExactKernelHardwareAndRootAndRejectsAmbiguity()
    {
        Task<AdbShellResult> Probe(string serial, CancellationToken _) => Task.FromResult(new AdbShellResult(0,
            serial.StartsWith("module") ? "3.18.44\nMDM9207\n0\n" : "3.18.44\nMSM8996\n2000\n"));
        Assert.Equal("module", await Qdc507VoiceRuntime.SelectQdc507Async(["phone", "module"], Probe, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Qdc507VoiceRuntime.SelectQdc507Async(["phone"], Probe, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Qdc507VoiceRuntime.SelectQdc507Async(["module1", "module2"], Probe, CancellationToken.None));
    }

    [ReadOnlyAdbFact]
    [Trait("Lab", "ReadOnlyAdb")]
    public async Task ExplicitLiveAdbQueriesVerifiedModuleWithoutStartingCliOrChangingRuntime()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = new AdbServerClient(); // No daemon bootstrap in this read-only test.
        var devices = (await client.DevicesAsync(deadline.Token)).Split('\n')
            .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 2 && fields[1] == "device" && !fields[0].StartsWith("emulator-"))
            .Select(fields => fields[0]);
        var selected = await Qdc507VoiceRuntime.SelectQdc507Async(devices, async (serial, token) =>
        {
            var result = await client.ShellAsync(serial, Qdc507VoiceRuntime.IdentityProbeCommand, token);
            output.WriteLine("Read-only hardware fingerprint: {0}; exit={1}", result.Output.Replace('\n', ' ').Trim(), result.ExitCode);
            return result;
        }, deadline.Token);
        var root = await client.ShellAsync(selected, "id -u", deadline.Token);
        Assert.Equal(0, root.ExitCode);
        Assert.Equal("0", root.Output.Trim());
    }
}
