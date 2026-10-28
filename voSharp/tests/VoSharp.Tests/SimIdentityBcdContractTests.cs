using System.Text.Json;
using VoSharp.Modem;

namespace VoSharp.Tests;

public class SimIdentityBcdContractTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "contracts", "sim-identity-bcd-v1.json")));
        Assert.Equal(1, contract.RootElement.GetProperty("version").GetInt32());
        foreach (var item in contract.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("id").GetString()!, item.GetProperty("raw").GetString()!, item.GetProperty("iccid").GetString() ?? ""];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DirectCardIdentityDoesNotCollapseMalformedPadding(string id, string raw, string expected)
    {
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(expected, Ec25AkaProvider.DecodeEfIccid(Convert.FromHexString(raw)));
    }
}
