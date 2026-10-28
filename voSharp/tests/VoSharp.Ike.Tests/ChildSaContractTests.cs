using System.Text.Json;
using VoSharp.Ike;

namespace VoSharp.Ike.Tests;

public class ChildSaContractTests
{
    private static JsonDocument Contract() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "swu-child-v1.json")));
    private static byte[] Hex(JsonElement v, string field) => Convert.FromHexString(v.GetProperty(field).GetString()!);
    [Fact]
    public void IndependentFinalAuthAndChildKeySchedulesMatchAllPrfs()
    {
        using var c = Contract(); foreach (var v in c.RootElement.GetProperty("auth").EnumerateArray())
        {
            var suite = IkeSuite.Preferred with { PrfId = v.GetProperty("prf").GetUInt16() };
            foreach (var side in new[] { "i", "r" })
            {
                var signed = Hex(v, side + "Transcript").Concat(Hex(v, side == "i" ? "nr" : "ni")).Concat(IkeCrypto.Prf(suite, Hex(v, side == "i" ? "skpi" : "skpr"), Hex(v, side == "i" ? "idi" : "idr"))).ToArray();
                var pad = IkeCrypto.Prf(suite, Hex(v, "msk"), System.Text.Encoding.ASCII.GetBytes("Key Pad for IKEv2"));
                var mic = IkeCrypto.Prf(suite, pad, signed); Assert.Equal(Hex(v, side + "Mic"), mic);
                IkeSession.VerifyFinalResponderAuth(new(IkePayloadType.Authentication, new byte[] { 2, 0xff, 0x55, 0xaa }.Concat(mic).ToArray()), mic);
            }
        }
        foreach (var v in c.RootElement.GetProperty("keys").EnumerateArray())
        {
            var suite = IkeSuite.Preferred with { PrfId = v.GetProperty("prf").GetUInt16() }; var esp = new EspSuite(12, v.GetProperty("bits").GetInt32(), v.GetProperty("integrity").GetUInt16());
            var skd = Hex(v, "skd"); var copy = skd.ToArray(); var (outEnc, outAuth, inEnc, inAuth) = IkeCrypto.DeriveChildSaKeys(suite, esp, skd, Hex(v, "ni"), Hex(v, "nr"));
            var actual = new[] { outEnc, outAuth, inEnc, inAuth }; var expected = v.GetProperty("keys").EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!)).ToArray();
            for (var i = 0; i < 4; i++) Assert.Equal(expected[i], actual[i]); Assert.Equal(copy, skd);
        }
    }
    [Fact]
    public void SharedSelectorFramesAndUnionContainment()
    {
        using var c = Contract(); foreach (var v in c.RootElement.GetProperty("frames").EnumerateArray())
        {
            IReadOnlyList<IkeTrafficSelector>? parsed = null; var error = Record.Exception(() => parsed = IkeTrafficSelectors.Parse(Hex(v, "hex")));
            Assert.True((error == null) == v.GetProperty("accepted").GetBoolean(), v.GetProperty("id").GetString());
            if (error != null) { Assert.IsType<IkeFormatException>(error); continue; }
            var canonical = IkeTrafficSelectors.Encode(parsed!); Assert.Equal(parsed, IkeTrafficSelectors.Parse(canonical));
        }
        foreach (var v in c.RootElement.GetProperty("scopes").EnumerateArray())
        {
            var error = Record.Exception(() => IkeTrafficSelectors.ValidateNarrowing(IkeTrafficSelectors.Parse(Hex(v, "offer")), IkeTrafficSelectors.Parse(Hex(v, "chosen"))));
            Assert.True((error == null) == v.GetProperty("accepted").GetBoolean(), v.GetProperty("id").GetString());
        }
    }
    [Fact]
    public void SharedEspProposalSelectionRejectsUnrequestedTransformsAndReservedSpis()
    {
        using var c = Contract(); foreach (var v in c.RootElement.GetProperty("proposals").EnumerateArray())
        {
            EspSuite? selected = null; var error = Record.Exception(() => selected = EspSuite.FromProposal(IkeWire.DecodeProposals(Hex(v, "hex")).Single()));
            Assert.True((error == null) == v.GetProperty("accepted").GetBoolean(), v.GetProperty("id").GetString());
            if (error == null) { Assert.Equal(v.GetProperty("bits").GetInt32(), selected!.EncryptionBits); Assert.Equal(v.GetProperty("integrity").GetUInt16(), selected.IntegrityId); }
        }
    }
}
