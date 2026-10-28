using System.Text.Json;
using VoSharp.Modem;

namespace VoSharp.Tests;

public class SimHomePlmnContractTests
{
    [Fact]
    public void EfAdLengthComesFromFourthOctetNotExtensions()
    {
        using var doc = ReadContract();
        foreach (var item in doc.RootElement.GetProperty("ef_ad").EnumerateArray())
        {
            var expected = item.GetProperty("mnc_length");
            Assert.Equal(expected.ValueKind == JsonValueKind.Null ? (int?)null : expected.GetInt32(),
                ModemDriver.ParseHomeMncLength([$"+CRSM: 144,0,\"{item.GetProperty("data").GetString()}\"", "OK"]));
        }
    }

    [Fact]
    public void EquivalentHomeRecordsMatchSharedBcdContract()
    {
        using var doc = ReadContract();
        foreach (var item in doc.RootElement.GetProperty("ehplmn").EnumerateArray())
            Assert.Equal(item.GetProperty("plmns").EnumerateArray().Select(p => p.GetString()!),
                ModemDriver.ParsePlmnList([$"+CRSM: 144,0,\"{item.GetProperty("data").GetString()}\""], 3));
    }

    private static JsonDocument ReadContract()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "sim-home-plmn-v1.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        return doc;
    }

    [Fact]
    public void CrsmStatusRequiresCompletedDataWithoutOverflow()
    {
        using var doc = ReadContract();
        foreach (var item in doc.RootElement.GetProperty("crsm").EnumerateArray())
        {
            string[] lines = [item.GetProperty("response").GetString()!];
            var accepted = item.GetProperty("accepted").GetBoolean();
            Assert.Equal(accepted, ModemDriver.ParsePlmnList(lines, 3).Count != 0);
            Assert.Equal(accepted ? 2 : (int?)null, ModemDriver.ParseHomeMncLength(lines));
        }
    }
}
