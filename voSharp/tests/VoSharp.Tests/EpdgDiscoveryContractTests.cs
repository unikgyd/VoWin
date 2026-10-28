using System.Text.Json;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class EpdgDiscoveryContractTests
{
    [Fact]
    public void SharedHostValidationAndPlmnDomainContract()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "epdg-discovery-v1.json")));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(15, root.GetProperty("hosts").GetArrayLength());
        foreach (var item in root.GetProperty("hosts").EnumerateArray())
        {
            var configuration = new EpdgUserConfiguration(item.GetProperty("input").GetString());
            if (item.TryGetProperty("invalid", out var invalid) && invalid.GetBoolean())
                Assert.Throws<ArgumentException>(() => configuration.Normalize());
            else
                Assert.Equal(item.GetProperty("normalized").GetString(), configuration.Normalize().PreferredAddresses);
        }
        foreach (var item in root.GetProperty("plmns").EnumerateArray())
            Assert.Equal(item.GetProperty("host").GetString(), EpdgResolver.BuildEpdgDomain(item.GetProperty("mcc").GetString()!, item.GetProperty("mnc").GetString()!));
    }
}
