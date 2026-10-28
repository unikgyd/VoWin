using System.Net;
using System.Text.Json;
using VoSharp.Modem;

namespace VoSharp.Tests;

public class HostImsPdnContractTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "contracts", "host-ims-pdn-v1.json")));
        foreach (var group in new[] { "addresses", "runtime" })
            foreach (var vector in document.RootElement.GetProperty(group).EnumerateArray())
                yield return [group, vector.GetProperty("id").GetString()!, vector.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SharedRustAndWindowsVectors(string group, string id, string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(id));
        using var document = JsonDocument.Parse(json);
        var vector = document.RootElement;
        if (group == "addresses")
        {
            Assert.Equal(Addresses(vector, "expected"), HostImsPdnParser.ParseAddresses(
                vector.GetProperty("value").GetString(), vector.GetProperty("local").GetBoolean()));
            return;
        }
        var contexts = HostImsPdnParser.ParseRuntime([vector.GetProperty("line").GetString()!]);
        if (!vector.GetProperty("accepted").GetBoolean()) { Assert.Empty(contexts); return; }
        var context = Assert.Single(contexts);
        Assert.Equal(vector.GetProperty("cid").GetInt32(), context.ContextId);
        Assert.Equal(vector.GetProperty("apn").GetString(), context.Apn);
        Assert.Equal(Addresses(vector, "local"), context.LocalAddresses);
        Assert.Equal(Addresses(vector, "pcscf"), context.PcscfServers);
    }

    private static IPAddress[] Addresses(JsonElement value, string key) => value.GetProperty(key)
        .EnumerateArray().Select(item => IPAddress.Parse(item.GetString()!)).ToArray();
}
