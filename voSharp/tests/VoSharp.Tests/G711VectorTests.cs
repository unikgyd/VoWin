using VoSharp.Telephony.Audio;
using System.Text.Json;

namespace VoSharp.Tests;

public class G711VectorTests
{
    [Fact]
    public void SharedRustWindowsG711VectorsAgree()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "g711-codec-v1.json")));
        Assert.Equal(1, contract.RootElement.GetProperty("version").GetInt32());
        foreach (var row in contract.RootElement.GetProperty("cases").EnumerateArray())
            KnownFullScalePcmVectors(row[0].GetInt16(), row[1].GetByte(), row[2].GetInt16(), row[3].GetByte(), row[4].GetInt16());
    }
    [Theory]
    [InlineData(0, 0xd5, 8, 0xff, 0)]
    [InlineData(1000, 0xfa, 1008, 0xce, 988)]
    [InlineData(-1000, 0x7a, -1008, 0x4e, -988)]
    [InlineData(2000, 0xea, 2016, 0xbf, 1980)]
    [InlineData(-2000, 0x6a, -2016, 0x3f, -1980)]
    [InlineData(16000, 0xba, 16128, 0x90, 15996)]
    [InlineData(32767, 0xaa, 32256, 0x80, 32124)]
    [InlineData(-32768, 0x2a, -32256, 0x00, -32124)]
    public void KnownFullScalePcmVectors(short pcm, byte alaw, short decodedA, byte ulaw, short decodedU)
    {
        Assert.Equal(alaw, G711Codec.PcmToAlaw(pcm));
        Assert.Equal(decodedA, G711Codec.AlawToPcm(alaw));
        Assert.Equal(ulaw, G711Codec.PcmToUlaw(pcm));
        Assert.Equal(decodedU, G711Codec.UlawToPcm(ulaw));
    }

    [Fact]
    public void AllPcmValuesPreserveSignAndQuantizationScale()
    {
        for (int value = short.MinValue; value <= short.MaxValue; value++)
        {
            var a = G711Codec.AlawToPcm(G711Codec.PcmToAlaw((short)value));
            var u = G711Codec.UlawToPcm(G711Codec.PcmToUlaw((short)value));
            Assert.InRange(Math.Abs(value - a), 0, 512);
            Assert.InRange(Math.Abs(value - u), 0, 644);
            Assert.Equal(value >= 0, a > 0);
            if (Math.Abs(value) >= 4) Assert.Equal(value > 0, u > 0);
        }
    }
}
