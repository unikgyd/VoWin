using System.Net;
using System.Net.Sockets;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

public sealed class PortableMediaTests
{
    [Fact]
    public void LinuxSonamesAreSeparateAndNeverUseAWindowsLibrary()
    {
        Assert.Equal(new[] { "libopencore-amrnb.so.0", "libopencore-amrnb.so" }, AmrNativeCodec.GetUnixLibraryNames(false));
        Assert.Equal(new[] { "libopencore-amrwb.so.0", "libopencore-amrwb.so" }, AmrNativeCodec.GetUnixLibraryNames(true));
    }

    [Theory]
    [InlineData(0, "PCMU")]
    [InlineData(8, "PCMA")]
    public async Task G711LoopbackDoesNotLoadAmrAndDisposalCannotInitializeIt(int payload, string codec)
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var session = new RtpSession(null, localAddress: IPAddress.Loopback);
        Assert.False(session.IsAmrCodecInitialized);
        session.SetRemoteEndpoint(IPAddress.Loopback, ((IPEndPoint)peer.Client.LocalEndPoint!).Port, (byte)payload, codec);
        session.SendAudioPcm(Enumerable.Repeat((short)2000, 160).ToArray());
        var packet = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(payload, packet.Buffer[1] & 127);
        Assert.False(session.IsAmrCodecInitialized);
        session.Dispose(); session.Dispose();
        Assert.False(session.IsAmrCodecInitialized);
    }
}
