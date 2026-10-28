using System.Text.Json;
using VoSharp.Modem;

namespace VoSharp.Tests;

public sealed class IsimTextContractTests
{
    public static IEnumerable<object?[]> Cases()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "contracts", "isim-identity-v1.json")));
        foreach (var row in contract.RootElement.GetProperty("cases").EnumerateArray())
            yield return [row.GetProperty("id").GetString(), row.GetProperty("raw").GetString(),
                row.GetProperty("text").ValueKind == JsonValueKind.Null ? null : row.GetProperty("text").GetString()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedGoWindowsIdentityTlvCodec(string id, string raw, string? text)
    {
        Assert.Equal(text, IsimIdentityReader.DecodeIdentityTlv(Convert.FromHexString(raw)));
        Assert.False(string.IsNullOrEmpty(id));
    }
}
