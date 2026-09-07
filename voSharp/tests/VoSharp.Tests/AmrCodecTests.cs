using VoSharp.Telephony.Audio;
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

        // Pack bandwidth-efficient
        var rtpBe = AmrNativeCodec.AmrFrameToRtpPayload(amrFrame, octetAligned: false);
        Assert.NotEmpty(rtpBe);

        // Unpack
        var unpackedBe = AmrNbDecoder.RtpPayloadToAmrFrame(rtpBe, forceOctetAligned: false);
        Assert.Equal(amrFrame.Length, unpackedBe.Length);
        Assert.Equal(amrFrame[0], unpackedBe[0]);

        // Pack octet-aligned
        var rtpOa = AmrNativeCodec.AmrFrameToRtpPayload(amrFrame, octetAligned: true);
        Assert.Equal(33, rtpOa.Length); // CMR (1 byte) + 32 bytes

        // Unpack
        var unpackedOa = AmrNbDecoder.RtpPayloadToAmrFrame(rtpOa, forceOctetAligned: true);
        Assert.Equal(amrFrame.Length, unpackedOa.Length);
        Assert.Equal(amrFrame[0], unpackedOa[0]);
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
        audio.PlayPcmSamples(samples);
        audio.ClearRecording();
    }
}
