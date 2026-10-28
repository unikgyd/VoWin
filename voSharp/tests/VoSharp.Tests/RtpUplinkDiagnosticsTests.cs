using System.Net;
using System.Reflection;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

public sealed class RtpUplinkDiagnosticsTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SpeechStartsWithMarkerAndUsesTheNegotiatedAmrWireFormat(bool octetAligned)
    {
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        var packets = new List<byte[]>();
        rtp.CustomSender = bytes => { packets.Add(bytes); return Task.CompletedTask; };
        rtp.SetRemoteEndpoint(IPAddress.Loopback, 25000, 102, "AMR", octetAligned,
            amrModeSet: "2,7", startSenders: false);
        rtp.SendAudioPcm(new short[320]);
        Assert.Equal(2, packets.Count);
        Assert.Equal(0x80, packets[0][1] & 0x80);
        Assert.Equal(0, packets[1][1] & 0x80);
        Assert.All(packets, packet =>
        {
            Assert.Equal(102, packet[1] & 0x7f);
            Assert.Equal(octetAligned ? 33 : 32, packet.Length - 12);
            Assert.Equal(7, AmrNbDecoder.RtpPayloadToAmrFrame(packet[12..], octetAligned)[0] >> 3 & 15);
        });
        var config = rtp.GetMediaConfiguration();
        Assert.Equal(octetAligned, config.AmrOctetAligned);
        Assert.Equal("2,7", config.AmrModeSet);
        Assert.Equal(7, config.AmrEncodeMode);
        Assert.Equal(IPAddress.Loopback, config.RemoteAddress);
        Assert.Equal(25000, config.RemotePort);
        typeof(RtpSession).GetField("_lastUplinkSend", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(rtp, DateTime.UtcNow.AddSeconds(-1));
        rtp.SendAudioPcm(new short[160]);
        Assert.Equal(0x80, packets[2][1] & 0x80);
    }

    [Theory]
    [InlineData((byte)8, "PCMA")]
    [InlineData((byte)98, "AMR")]
    public void PcmFramesAndSuccessfullySentAudioPacketsHaveIndependentCounters(byte payload, string codec)
    {
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        var emitted = new List<byte[]>();
        rtp.CustomSender = bytes => { emitted.Add(bytes); return Task.CompletedTask; };
        rtp.SetRemoteEndpoint(IPAddress.Loopback, 25000, payload, codec);
        rtp.SendAudioPcm(Enumerable.Repeat((short)1500, 70).ToArray());
        Assert.Equal(70UL, rtp.GetMediaStats().PcmSamplesReceived);
        Assert.Equal(0UL, rtp.GetMediaStats().AudioFramesEncoded);
        rtp.SendAudioPcm(Enumerable.Repeat((short)-1500, 90).ToArray());
        var stats = rtp.GetMediaStats();
        Assert.Equal(160UL, stats.PcmSamplesReceived); Assert.Equal(1UL, stats.AudioFramesEncoded);
        Assert.Equal(1UL, stats.AudioPacketsSent); Assert.Equal(0UL, stats.AudioEncodeErrors);
        Assert.Equal(payload, Assert.Single(emitted)[1] & 0x7f);
    }

    [Fact]
    public async Task ComfortSilenceDoesNotMasqueradeAsMicrophoneAudio()
    {
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rtp.CustomSender = _ => { sent.TrySetResult(); return Task.CompletedTask; };
        rtp.SetRemoteEndpoint(IPAddress.Loopback, 25000);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(20);
        var stats = rtp.GetMediaStats();
        Assert.True(stats.PacketsSent > 0); Assert.Equal(0UL, stats.AudioPacketsSent);
        Assert.Equal(0UL, stats.PcmSamplesReceived); Assert.Equal(0UL, stats.AudioFramesEncoded);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedSendsNeverIncreaseTheSuccessfulAudioPacketCounter(bool asynchronous)
    {
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        rtp.CustomSender = _ => asynchronous ? Task.FromException(new IOException("offline send failure"))
            : throw new IOException("offline send failure");
        rtp.SetRemoteEndpoint(IPAddress.Loopback, 25000);
        rtp.SendAudioPcm(new short[160]);
        var stats = rtp.GetMediaStats();
        Assert.Equal(1UL, stats.AudioFramesEncoded); Assert.Equal(1UL, stats.SendErrors);
        Assert.Equal(0UL, stats.AudioPacketsSent);
    }

    [Fact]
    public void EmptyAmrEncoderOutputIsVisibleInsteadOfSilentlyDisappearing()
    {
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        rtp.CustomSender = _ => Task.CompletedTask;
        rtp.SetRemoteEndpoint(IPAddress.Loopback, 25000, 98, "AMR");
        Assert.False(rtp.IsAmrCodecInitialized);
        // Explicit AMR use initializes lazily; invalidate that exact codec without
        // sending an extra frame or weakening the original counter assertions.
        ((AmrNativeCodec)typeof(RtpSession).GetProperty("AmrCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rtp)!).Dispose();
        rtp.SendAudioPcm(new short[160]);
        var stats = rtp.GetMediaStats();
        Assert.Equal(1UL, stats.AudioEncodeErrors); Assert.Equal(0UL, stats.AudioFramesEncoded);
        Assert.Equal(0UL, stats.AudioPacketsSent);
    }
}
