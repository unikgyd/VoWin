using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VoSharp.Common.Utils;
using VoSharp.Modem;

namespace VoSharp.Tests;

public sealed class HostImsOwnerCompatibilityTests
{
    [Fact]
    public void CurrentOwnerReportsManagedRasAndPerModemProtocol()
    {
        var protocol = HostImsOwnerCompatibility.Parse(JsonSerializer.Serialize(HostImsOwnerProtocol.Current));
        Assert.True(protocol.IsCompatible);
        Assert.True(protocol.ManagedRas);
        Assert.True(protocol.PerModemBinding);
    }

    [Theory]
    [InlineData("{\"Success\":true,\"ManagedOwner\":true}")]
    [InlineData("{\"ProtocolVersion\":1,\"ManagedRas\":true,\"PerModemBinding\":true}")]
    [InlineData("{\"ProtocolVersion\":1,\"ManagedRas\":false,\"PerModemBinding\":true}")]
    [InlineData("{\"ProtocolVersion\":1,\"ManagedRas\":true,\"PerModemBinding\":false}")]
    [InlineData("{\"ProtocolVersion\":999,\"ManagedRas\":true,\"PerModemBinding\":true}")]
    [InlineData("Usage: VoSharp.HostIms --own COMn RasImsPppProbe.exe lease-seconds")]
    [InlineData("null")]
    public void OldOrMismatchedOwnerIsRejectedBeforeBearerStart(string reply)
    {
        var error = Assert.Throws<InvalidOperationException>(() => HostImsOwnerCompatibility.Parse(reply));
        Assert.Contains("版本不兼容", error.Message);
        Assert.Contains("未修改模组", error.Message);
    }

    private static ProcessStartInfo FakeProbe(string script)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
    }

    [Fact]
    public async Task ReadOnlyHandshakeAcceptsMatchingProcessAndRejectsLegacyExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        await HostImsOwnerCompatibility.VerifyProcessAsync(FakeProbe(
            "[Console]::WriteLine('" + JsonSerializer.Serialize(HostImsOwnerProtocol.Current) + "')"), TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsOwnerCompatibility.VerifyProcessAsync(
            FakeProbe("[Console]::Error.WriteLine('legacy command rejected'); exit 1"), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task HungOrFloodingProbeDoesNotStrandStartup()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsOwnerCompatibility.VerifyProcessAsync(
            FakeProbe("Start-Sleep -Seconds 30"), TimeSpan.FromMilliseconds(300)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsOwnerCompatibility.VerifyProcessAsync(
            FakeProbe("[Console]::WriteLine(('x' * 10000))"), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CanceledProbeDoesNotStartAProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostImsOwnerCompatibility.VerifyProcessAsync(
            new ProcessStartInfo("a-process-that-must-never-start"), TimeSpan.FromSeconds(10), cancellation.Token));
    }
}
