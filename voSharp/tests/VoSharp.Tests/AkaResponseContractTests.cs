using System.Text.Json;
using VoSharp.Sim;

namespace VoSharp.Tests;

public class AkaResponseContractTests
{
    private static JsonDocument Contract() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "usim-aka-response-v1.json")));

    public static IEnumerable<object[]> Cases()
    {
        using var contract = Contract();
        Assert.Equal(1, contract.RootElement.GetProperty("version").GetInt32());
        foreach (var item in contract.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("id").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedRustWindowsAkaResponsesHaveTheSameMeaning(string id, string json)
    {
        using var item = JsonDocument.Parse(json);
        var row = item.RootElement;
        var result = HardwareAka.ParseAuthenticateResponse(Convert.FromHexString(row.GetProperty("raw").GetString()!));
        var expected = row.GetProperty("outcome").GetString();
        Assert.Equal(expected == "success", result.Success);
        Assert.Equal(expected == "sync", result.SyncFailure);
        if (expected == "success")
        {
            Assert.Equal(row.GetProperty("res").GetString()!, Convert.ToHexString(result.Res!).ToLowerInvariant());
            Assert.Equal(row.GetProperty("ck").GetString()!, Convert.ToHexString(result.Ck!).ToLowerInvariant());
            Assert.Equal(row.GetProperty("ik").GetString()!, Convert.ToHexString(result.Ik!).ToLowerInvariant());
        }
        else
        {
            Assert.Null(result.Res); Assert.Null(result.Ck); Assert.Null(result.Ik);
            if (expected == "sync") Assert.Equal(row.GetProperty("auts").GetString()!, Convert.ToHexString(result.Auts!).ToLowerInvariant());
            else Assert.Null(result.Auts);
        }
        Assert.DoesNotContain(row.GetProperty("raw").GetString()!, result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public void EveryTruncatedBareAkaBodyAndEmptyWrapperIsRejectedWithoutThrowing()
    {
        using var contract = Contract();
        var raw = Convert.FromHexString(contract.RootElement.GetProperty("cases").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "direct-res8").GetProperty("raw").GetString()!);
        for (var length = 0; length < raw.Length - 2; length++)
            Assert.False(HardwareAka.ParseAuthenticateResponse([.. raw.AsSpan(0, length), 0x90, 0]).Success);
        for (var declared = 0; declared <= byte.MaxValue; declared++)
        {
            Assert.False(HardwareAka.ParseAuthenticateResponse([0x80, (byte)declared, 0x90, 0]).Success);
            Assert.False(HardwareAka.ParseAuthenticateResponse([0x80, 0x81, (byte)declared, 0x90, 0]).Success);
        }
    }
}
