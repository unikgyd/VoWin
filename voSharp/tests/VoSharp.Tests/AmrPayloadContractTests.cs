using System.Text.Json;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using System.Net;

namespace VoSharp.Tests;

public class AmrPayloadContractTests
{
    [Fact]
    public void RealRtpConsumerDecodesCompoundFramesAndDropsTruncatedPayload()
    {
        using var codec = new AmrNativeCodec();
        var a = codec.EncodeAmrNbFrame(new short[160], 7);
        var b = codec.EncodeAmrNbFrame(new short[160], 7);
        Assert.NotEmpty(a); Assert.NotEmpty(b);
        byte[] payload = [0xf0, (byte)(a[0] | 0x80), b[0], .. a[1..], .. b[1..]];
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80; packet[1] = 110; packet[3] = 1;
        payload.CopyTo(packet, 12);
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        rtp.SetRemoteEndpoint(IPAddress.Loopback, 45000, payloadType: 110, codec: "AMR", amrOctetAligned: true, startSenders: false);
        var decoded = 0;
        rtp.OnAudioDecoded = samples => { Assert.Equal(160, samples.Length); decoded++; };
        rtp.ProcessRtpPacket(packet);
        Assert.Equal(2, decoded);
        rtp.ProcessRtpPacket(packet[..^1]);
        Assert.Equal(2, decoded);
    }
    [Fact]
    public void SharedGoWindowsAmrPayloadFramesAndMalformedPacketsAgree()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "amr-nb-rtp-v1.json")));
        foreach (var row in contract.RootElement.GetProperty("cases").EnumerateArray())
        {
            var actual = AmrNbDecoder.RtpPayloadToAmrFrames(Convert.FromHexString(row.GetProperty("payloadHex").GetString()!), row.GetProperty("octet").GetBoolean());
            Assert.Equal(row.GetProperty("accepted").GetBoolean(), actual.Length > 0);
            var expected = row.GetProperty("framesHex").EnumerateArray().Select(v => Convert.FromHexString(v.GetString()!)).ToArray();
            Assert.Equal(expected.Length, actual.Length);
            for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void NativeEncoderNeverMutatesCallerPcmAndRejectsInvalidModes()
    {
        using var codec = new AmrNativeCodec();
        Assert.True(codec.IsAvailable);
        var samples = Enumerable.Range(0, 160).Select(i => (short)(12000 * Math.Sin(2 * Math.PI * 440 * i / 8000))).ToArray();
        var original = samples.ToArray();
        for (var mode = 0; mode < 8; mode++)
        {
            long energy = 0;
            for (var i = 0; i < 8; i++)
            {
                var frame = codec.EncodeAmrNbFrame(samples, mode);
                Assert.NotEmpty(frame);
                Assert.Equal(original, samples);
                energy += codec.DecodeAmrNbFrame(frame).Sum(value => Math.Abs((int)value));
            }
            Assert.True(energy > 10000);
        }
        Assert.Empty(codec.EncodeAmrNbFrame(samples, -1));
        Assert.Empty(codec.EncodeAmrNbFrame(samples, 8));
    }
}
