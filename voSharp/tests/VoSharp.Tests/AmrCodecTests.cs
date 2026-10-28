using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using Xunit;

namespace VoSharp.Tests;

public class AmrCodecTests
{
    [Fact]
    public void AmrNativeCodec_CanInitialize()
    {
        using var codec = new AmrNativeCodec(initEncoder: true);
        Assert.True(codec.IsAvailable, "opencore-amr native library should be loaded and initialized");
    }

    [Fact]
    public void AmrNativeCodec_CanEncodeAndDecodeFrame()
    {
        using var codec = new AmrNativeCodec(initEncoder: true);
        if (!codec.IsAvailable) return;

        // Generate 160 samples of 440Hz sine wave @ 8000Hz (20ms)
        var pcmIn = new short[160];
        for (int i = 0; i < 160; i++)
        {
            pcmIn[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 8000.0) * 16000);
        }

        // Encode to AMR Mode 7 (12.2 kbps)
        var amrFrame = codec.EncodeAmrNbFrame(pcmIn, mode: 7);
        Assert.NotEmpty(amrFrame);
        Assert.Equal(32, amrFrame.Length); // 1 TOC byte + 31 bytes speech data

        // Decode back to PCM
        var pcmOut = codec.DecodeAmrNbFrame(amrFrame);
        Assert.Equal(160, pcmOut.Length);

        // Verify non-silent output
        long energy = 0;
        foreach (var s in pcmOut) energy += Math.Abs(s);
        Assert.True(energy > 10000, "Decoded PCM audio should not be silent");
    }

    [Fact]
    public void AmrNativeCodec_RtpPayloadPackingRoundTrip()
    {
        // Fake 12.2k frame (32 bytes)
        var amrFrame = new byte[32];
        amrFrame[0] = 0x3C; // Mode 7 TOC with Q=1
        for (int i = 1; i < 32; i++) amrFrame[i] = (byte)i;
        amrFrame[^1] &= 0xf0; // 244 speech bits, four storage-padding bits.

        // Pack bandwidth-efficient
        var rtpBe = AmrNativeCodec.AmrFrameToRtpPayload(amrFrame, octetAligned: false);
        Assert.Equal(32, rtpBe.Length);

        // Unpack
        var unpackedBe = AmrNbDecoder.RtpPayloadToAmrFrame(rtpBe, forceOctetAligned: false);
        Assert.Equal(amrFrame.Length, unpackedBe.Length);
        Assert.Equal(amrFrame, unpackedBe);

        // Pack octet-aligned
        var rtpOa = AmrNativeCodec.AmrFrameToRtpPayload(amrFrame, octetAligned: true);
        Assert.Equal(33, rtpOa.Length); // CMR (1 byte) + 32 bytes

        // Unpack
        var unpackedOa = AmrNbDecoder.RtpPayloadToAmrFrame(rtpOa, forceOctetAligned: true);
        Assert.Equal(amrFrame.Length, unpackedOa.Length);
        Assert.Equal(amrFrame[0], unpackedOa[0]);
    }

    [Theory]
    [InlineData(0, 95)] [InlineData(1, 103)] [InlineData(2, 118)]
    [InlineData(3, 134)] [InlineData(4, 148)] [InlineData(5, 159)]
    [InlineData(6, 204)] [InlineData(7, 244)] [InlineData(8, 39)] [InlineData(15, 0)]
    public void BandwidthEfficientPackingMatchesIndependentBitVector(int mode, int speechBits)
    {
        var frame = new byte[1 + (speechBits + 7) / 8];
        frame[0] = (byte)((mode << 3) | 4);
        for (var i = 1; i < frame.Length; i++) frame[i] = (byte)(i * 7);
        // Deliberately leave storage padding nonzero: it must never leak onto the wire.
        var bits = "11110" + Convert.ToString(mode, 2).PadLeft(4, '0') + "1" +
            string.Concat(frame.Skip(1).Select(value => Convert.ToString(value, 2).PadLeft(8, '0')))[..speechBits];
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        var expected = Enumerable.Range(0, bits.Length / 8)
            .Select(index => Convert.ToByte(bits.Substring(index * 8, 8), 2)).ToArray();
        var payload = AmrNativeCodec.AmrFrameToRtpPayload(frame, octetAligned: false);
        Assert.Equal(expected, payload);
        var unpacked = AmrNbDecoder.RtpPayloadToAmrFrame(payload, forceOctetAligned: false);
        if (speechBits % 8 != 0) frame[^1] &= (byte)(0xff << (8 - speechBits % 8));
        Assert.Equal(frame, unpacked);
    }

    [Theory]
    [InlineData(9)] [InlineData(14)] [InlineData(7)]
    public void BandwidthEfficientPackingRejectsReservedOrTruncatedFrames(int mode)
    {
        Assert.Empty(AmrNativeCodec.AmrFrameToRtpPayload([(byte)((mode << 3) | 4)], false));
    }

    [Fact]
    public void AmrNativeCodec_CanDecodeRealRecordedCallFile()
    {
        string amrPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "VoWin", "VoWin", "call_185_20260902_204942.amr");
        if (!File.Exists(amrPath)) return;

        byte[] amrData = File.ReadAllBytes(amrPath);
        Assert.True(amrData.Length > 6);

        // Verify AMR header "#!AMR\n"
        Assert.Equal(0x23, amrData[0]);
        Assert.Equal(0x21, amrData[1]);
        Assert.Equal(0x41, amrData[2]);
        Assert.Equal(0x4D, amrData[3]);
        Assert.Equal(0x52, amrData[4]);
        Assert.Equal(0x0A, amrData[5]);

        using var codec = new AmrNativeCodec(initEncoder: false);
        int offset = 6;
        int frameCount = 0;
        long totalEnergy = 0;

        int[] frameSizes = { 12, 13, 15, 17, 19, 20, 26, 31, 5 };

        while (offset < amrData.Length)
        {
            byte toc = amrData[offset];
            int ft = (toc >> 3) & 0x0F;
            if (ft >= frameSizes.Length) break;

            int frameLen = 1 + frameSizes[ft];
            if (offset + frameLen > amrData.Length) break;

            var frame = new byte[frameLen];
            Buffer.BlockCopy(amrData, offset, frame, 0, frameLen);
            offset += frameLen;

            var pcm = codec.DecodeAmrNbFrame(frame);
            Assert.Equal(160, pcm.Length);
            frameCount++;
            foreach (var s in pcm) totalEnergy += Math.Abs(s);
        }

        Assert.True(frameCount > 100, $"Expected >100 frames, decoded {frameCount}");
        Assert.True(totalEnergy > 0, "Decoded PCM should contain audio energy");
    }

    [Fact]
    public void WindowsAudioDevice_CanInitializeAndPlaySamples()
    {
        using var audio = new WindowsAudioDevice(sampleRate: 8000);
        var samples = new short[160];
        var before = audio.GetPlaybackStats();
        audio.PlayPcmSamples(samples);
        var after = audio.GetPlaybackStats();
        Assert.Equal(before.FramesQueued + before.FramesDropped + 1,
            after.FramesQueued + after.FramesDropped);
        Assert.True(after.FramesWritten <= after.FramesQueued);
        var detached = audio.TakeRecording();
        Assert.Equal(320, detached.Length);
        Assert.Empty(audio.TakeRecording());
        audio.SetRecordingEnabled(false);
        audio.PlayPcmSamples(samples);
        Assert.Empty(audio.TakeRecording());
        audio.SetRecordingEnabled(true);
        audio.PlayPcmSamples(samples);
        Assert.Equal(320, audio.TakeRecording().Length);
        using var session = new RtpSession(audio);
        var callStart = session.GetMediaStats().Playback!;
        Assert.Equal(0, callStart.FramesQueued + callStart.FramesDropped);
        audio.PlayPcmSamples(samples);
        var duringCall = session.GetMediaStats().Playback!;
        Assert.Equal(1, duringCall.FramesQueued + duringCall.FramesDropped);
        audio.ClearRecording();
    }

    [Fact]
    public void RtpSession_OnlyRetainsAmrFramesWhenRecordingEnabled()
    {
        using var codec = new AmrNativeCodec(initEncoder: true);
        var frame = codec.EncodeAmrNbFrame(new short[160]);
        var payload = AmrNativeCodec.AmrFrameToRtpPayload(frame, octetAligned: true);
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = 98;
        packet[3] = 1;
        payload.CopyTo(packet, 12);

        var directory = Path.Combine(Path.GetTempPath(), "VoWin-AmrRecord-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var disabled = new RtpSession(null))
            {
                disabled.SetRemoteEndpoint(System.Net.IPAddress.Loopback, 25000,
                    payloadType: 98, codec: "AMR", amrOctetAligned: true);
                disabled.ProcessRtpPacket(packet);
                disabled.SaveAudioRecording(Path.Combine(directory, "disabled.wav"), []);
            }
            Assert.False(File.Exists(Path.Combine(directory, "disabled.amr")));

            using (var enabled = new RtpSession(null, recordAudio: true))
            {
                enabled.SetRemoteEndpoint(System.Net.IPAddress.Loopback, 25000,
                    payloadType: 98, codec: "AMR", amrOctetAligned: true);
                packet[2] = 0xff;
                packet[3] = 0xff;
                enabled.ProcessRtpPacket(packet);
                packet[2] = 0;
                packet[3] = 0;
                enabled.ProcessRtpPacket(packet);
                enabled.SaveAudioRecording(Path.Combine(directory, "enabled.wav"), []);
            }
            var savedAmr = File.ReadAllBytes(Path.Combine(directory, "enabled.amr"));
            Assert.Equal(6 + frame.Length * 2, savedAmr.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
