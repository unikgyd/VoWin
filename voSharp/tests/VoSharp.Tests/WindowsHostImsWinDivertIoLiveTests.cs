using System.Net;
using System.Net.NetworkInformation;
using VoSharp.Telephony;

namespace VoSharp.Tests;

public sealed class WindowsHostImsWinDivertIoLiveTests
{
    [Fact]
    [Trait("Lab", "ExplicitElevatedWinDivert")]
    public void ExplicitIsolatedWinDivertAndWintunExchange()
    {
        var dll = Environment.GetEnvironmentVariable("VOWIN_WINDIVERT_LAB_DLL");
        if (string.IsNullOrEmpty(dll)) return; // Elevated, explicit lab only.

        var adapter = Assert.Single(NetworkInterface.GetAllNetworkInterfaces(),
            nic => nic.Name == "VoWin IMS Lab" &&
                   nic.OperationalStatus == OperationalStatus.Up);
        var index = adapter.GetIPProperties().GetIPv6Properties()?.Index;
        Assert.True(index is > 0);
        var local = IPAddress.Parse("fd7a:6f57:494d:5301::1");
        var peer = IPAddress.Parse("fd7a:6f57:494d:5301::2");
        var routeAllowed = true;
        using var io = new WindowsHostImsWinDivertIo(dll, local, peer,
            index!.Value, () => routeAllowed); // Lab-only route override.

        var outbound = new byte[56];
        outbound[0] = 0x60;
        outbound[5] = 16;
        outbound[6] = 50;
        outbound[7] = 64;
        local.GetAddressBytes().CopyTo(outbound, 8);
        peer.GetAddressBytes().CopyTo(outbound, 24);
        new byte[] { 0x55, 0x66, 0x77, 0x88, 0, 0, 0, 1,
            (byte)'V', (byte)'o', (byte)'W', (byte)'i', (byte)'n',
            (byte)'S', (byte)'e', (byte)'n' }.CopyTo(outbound, 40);
        var wrongSource = outbound.ToArray();
        wrongSource[8] ^= 1;
        Assert.Throws<FormatException>(() => io.Send(wrongSource));
        io.Send(outbound);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var inbound = io.Receive(timeout.Token);
        Assert.Equal(56, inbound.Length);
        Assert.Equal(50, inbound[6]);
        Assert.Equal(peer.GetAddressBytes(), inbound.AsSpan(8, 16).ToArray());
        Assert.Equal(local.GetAddressBytes(), inbound.AsSpan(24, 16).ToArray());
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0, 0, 0, 1,
            (byte)'V', (byte)'o', (byte)'W', (byte)'i', (byte)'n',
            (byte)'E', (byte)'S', (byte)'P' }, inbound.AsSpan(40, 16).ToArray());
        routeAllowed = false;
        Assert.Throws<InvalidOperationException>(() => io.Send(outbound));
    }
}
