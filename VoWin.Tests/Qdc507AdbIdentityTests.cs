using System.IO;
using System.Security.Cryptography;
using qmiSharp.Client;
using qmiSharp.Services;
using qmiSharp.Transport;
using VoWin.Services;
using Xunit.Abstractions;

namespace VoWin.Tests;

public sealed class Qdc507AdbIdentityTests(ITestOutputHelper output)
{
    private const string FirstImei = "111111111111111";
    private const string SecondImei = "222222222222222";

    [Fact]
    public async Task SameSerialTransportsAreSelectedByImeiRegardlessOfOrderOrReusedId()
    {
        AdbTransportDevice[] devices = [new("?", 7), new("?", 9)];
        Task<string?> Probe(ulong id, CancellationToken _) => Task.FromResult<string?>(id == 7 ? SecondImei : FirstImei);
        Assert.Equal(9UL, await Qdc507AdbIdentity.SelectTransportAsync(devices, FirstImei, Probe, CancellationToken.None));
        Assert.Equal(7UL, await Qdc507AdbIdentity.SelectTransportAsync(devices.Reverse(), SecondImei, Probe, CancellationToken.None));
        // Reconnected devices may reuse the previous ID; a fresh identity determines selection.
        Task<string?> Swapped(ulong id, CancellationToken _) => Task.FromResult<string?>(id == 9 ? SecondImei : FirstImei);
        Assert.Equal(7UL, await Qdc507AdbIdentity.SelectTransportAsync(devices, FirstImei, Swapped, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDuplicatedImeiMatchFailsClosedWithoutDisclosingIdentity(bool duplicated)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Qdc507AdbIdentity.SelectTransportAsync(
            [new("?", 7), new("?", 9)], FirstImei, (_, _) => Task.FromResult<string?>(duplicated ? FirstImei : SecondImei), CancellationToken.None));
        Assert.DoesNotContain(FirstImei, error.Message); Assert.DoesNotContain(SecondImei, error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000000000000")]
    [InlineData("11111111111111")]
    [InlineData("11111111111111x")]
    [InlineData("111111111111111;reboot")]
    public async Task MissingOrMalformedExpectedImeiNeverProbesDevices(string? expected)
    {
        var probed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Qdc507AdbIdentity.SelectTransportAsync([new("?", 7)], expected!,
            (_, _) => { probed = true; return Task.FromResult<string?>(FirstImei); }, CancellationToken.None));
        Assert.False(probed);
        Assert.Throws<InvalidOperationException>(() => Qdc507AdbIdentity.GuardCommand(expected!, "echo unsafe"));
    }

    [Theory]
    [InlineData("3.18.44\nMDM9207\n0\n", 0, true)]
    [InlineData("3.18.44\r\nMDM9607\r\n0\r\n", 0, true)]
    [InlineData("3.18.44\nMDM9207\n2000\n", 0, false)]
    [InlineData("3.18.44\nMSM8996\n0\n", 0, false)]
    [InlineData("4.19\nMDM9207\n0\n", 0, false)]
    [InlineData("3.18.44\nMDM9207\n0\nunexpected\n", 0, false)]
    [InlineData("3.18.44\nMDM9207\n0\n", 1, false)]
    public void OnlyExactRootQdcHardwareMayReceiveHelper(string text, int exitCode, bool expected) =>
        Assert.Equal(expected, Qdc507AdbIdentity.IsVerifiedHardware(new(exitCode, text)));

    [Theory]
    [InlineData("VOWIN_IMEI=111111111111111\n", 0, true)]
    [InlineData("VOWIN_IMEI=111111111111111\n", 1, false)]
    [InlineData("VOWIN_IMEI=11111111111111\n", 0, false)]
    [InlineData("VOWIN_IMEI=000000000000000\n", 0, false)]
    [InlineData("VOWIN_IMEI=111111111111111\nVOWIN_IMEI=111111111111111\n", 0, false)]
    [InlineData("identity:error:query:-3\n", 0, false)]
    public void IdentityResponsesRequireSuccessfulUniqueExactDecimalImei(string text, int exitCode, bool expected) =>
        Assert.Equal(expected ? FirstImei : null, Qdc507AdbIdentity.ParseImei(new(exitCode, text)));

    [Fact]
    public void RouteCommandChecksHelperHashAndIdentityInSameRemoteShellBeforeChangingRoute()
    {
        const string route = "echo 1 >/sys/class/android_usb/f_audio/audio_enable";
        var guarded = Qdc507AdbIdentity.GuardCommand(FirstImei, route);
        Assert.Contains("uname -r", guarded); Assert.Contains("MDM9207|MDM9607", guarded);
        Assert.Contains(Qdc507AdbIdentity.HelperSha256, guarded);
        Assert.Contains("--expect " + FirstImei, guarded);
        Assert.Contains("exit 72; fi;\n" + route, guarded);
        Assert.True(guarded.IndexOf("--expect", StringComparison.Ordinal) < guarded.IndexOf(route, StringComparison.Ordinal));
    }

    [Fact]
    public void PackagedReadOnlyHelperIsTinyHashVerifiedArmElfNotALinuxRuntime()
    {
        using var helper = Qdc507AdbIdentity.OpenHelper();
        Assert.Equal(3660, helper.Length);
        var header = new byte[20]; helper.ReadExactly(header);
        Assert.Equal(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 1 }, header[..6]);
        Assert.Equal(40, header[18]); // EM_ARM
        helper.Position = 0;
        Assert.Equal(Qdc507AdbIdentity.HelperSha256, Convert.ToHexString(SHA256.HashData(helper)).ToLowerInvariant());
    }

    [QdcIdentityFact]
    [Trait("Lab", "QdcIdentity")]
    public async Task LiveTemporaryHelperMatchesSelectedWindowsQmiAndRejectsWrongIdentityWithoutRouteChanges()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var port = Environment.GetEnvironmentVariable("VOWIN_TEST_QDC_IDENTITY_PORT")!;
        var physical = WindowsUsbTopology.SelectPort(WindowsUsbTopology.EnumeratePresent(), port);
        var path = QualcommWwanTransport.FindControlPathForAtPort(port) ?? throw new IOException("Selected modem has no QMI control path.");
        await using var transport = new QualcommWwanTransport(path);
        await using var qmi = await QmiClient.CreateAsync(transport, cancellationToken: deadline.Token);
        await using var dms = new DmsService(qmi);
        var imei = (await dms.GetSerialNumbersAsync(deadline.Token)).IMEI;
        Qdc507AdbIdentity.RequireImei(imei);
        UsbAudioModemBinding.RequireQdc507Runtime(WindowsUsbTopology.EnumeratePresent(), port, physical.ContainerId, imei);
        var client = new AdbServerClient(); // Never bootstrap/restart an existing ADB server in this lab.
        var devices = AdbServerClient.ParseDeviceTransports(await client.DevicesLongAsync(deadline.Token));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var selected = await Qdc507AdbIdentity.SelectTransportAsync(devices, imei!,
            (id, ct) => Qdc507AdbIdentity.ReadImeiAsync(client, id, ct), deadline.Token);
        var success = await client.ShellAsync(selected, Qdc507AdbIdentity.GuardCommand(imei!, "printf identity-matched"), deadline.Token);
        Assert.Equal(0, success.ExitCode); Assert.Equal("identity-matched", success.Output.TrimEnd('\r'));
        var wrong = imei == FirstImei ? SecondImei : FirstImei;
        var rejected = await client.ShellAsync(selected, Qdc507AdbIdentity.GuardCommand(wrong, "printf MUST_NOT_RUN"), deadline.Token);
        Assert.Equal(72, rejected.ExitCode); Assert.DoesNotContain("MUST_NOT_RUN", rejected.Output);
        output.WriteLine("Windows QMI / ADB IMEI match: true; mismatched identity refused: true; {0} ms; helper 3660 bytes; no routes/calls/SMS/RF changes.", started.ElapsedMilliseconds);
    }
}

internal sealed class QdcIdentityFactAttribute : FactAttribute
{
    public QdcIdentityFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOWIN_TEST_QDC_IDENTITY_PORT")))
            Skip = "Explicit selected-modem lab: deploys only a temporary read-only DMS identity helper; no radio or route changes.";
    }
}
