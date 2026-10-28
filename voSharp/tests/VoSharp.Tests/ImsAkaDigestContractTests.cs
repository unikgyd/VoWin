using System.Text.Json;
using VoSharp.Crypto;
using VoSharp.Sip;

namespace VoSharp.Tests;

public class ImsAkaDigestContractTests
{
    private static JsonDocument Contract() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "ims-aka-digest-v1.json")));

    [Fact]
    public void SharedBinaryResAndRegistrarProofVectors()
    {
        using var contract = Contract();
        var root = contract.RootElement;
        string Field(string name) => root.GetProperty(name).GetString()!;
        foreach (var item in root.GetProperty("digests").EnumerateArray())
        {
            var res = Convert.FromHexString(item.GetProperty("resHex").GetString()!);
            var unchanged = res.ToArray();
            var actual = DigestAka.ComputeResponse(Field("username"), Field("realm"), res,
                item.GetProperty("method").GetString()!, Field("uri"), Field("nonce"),
                "00000001", Field("cnonce"), item.GetProperty("qop").GetString());
            Assert.True(actual == item.GetProperty("expected").GetString(), item.GetProperty("id").GetString());
            // The helper clears its own copies, not a caller's owned SIM buffer.
            Assert.Equal(unchanged, res);
        }
    }

    [Fact]
    public void SharedBoundedChallengeParser()
    {
        using var contract = Contract();
        foreach (var item in contract.RootElement.GetProperty("challenges").EnumerateArray())
        {
            var parsed = ImsRegisterBuilder.ParseWwwAuthenticate(item.GetProperty("header").GetString()!);
            Assert.True((parsed is not null) == item.GetProperty("accepted").GetBoolean(), item.GetProperty("id").GetString());
        }
        const string valid = "Digest realm=\"ims.test\", nonce=\"challenge\", algorithm=AKAv1-MD5";
        Assert.Null(ImsRegisterBuilder.ParseWwwAuthenticate(valid + ", opaque=\"" + new string('x', 4096) + "\""));
        Assert.Null(ImsRegisterBuilder.ParseWwwAuthenticate(valid + ", opaque=\"" + new string('界', 1400) + "\""));
        Assert.Null(ImsRegisterBuilder.ParseWwwAuthenticate(valid + string.Concat(Enumerable.Range(0, 30).Select(n => $", x{n}=value"))));
    }
}
