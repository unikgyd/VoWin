using System.Text.Json;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class HomeMncCandidateContractTests
{
    [Fact]
    public void BothClientsPreserveAuthorityAndLegalImsiPrefixes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "ims-home-mnc-v1.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        foreach (var item in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var imsi = item.GetProperty("imsi").GetString()!;
            var mnc = item.GetProperty("mnc").GetString()!;
            var known = item.GetProperty("known").GetBoolean();
            var sim = SimIdentity.FromImsiAndIccid(imsi, "synthetic", mncLength: known ? mnc.Length : null) with
            {
                Mnc = mnc, IsHomePlmnAuthoritative = known,
                HomePlmns = item.GetProperty("equivalents").EnumerateArray().Select(p => p.GetString()!).ToArray()
            };
            Assert.Equal(item.GetProperty("homes").EnumerateArray().Select(p => p.GetString()!),
                EpdgResolver.BuildHomePlmnCandidates(sim).Select(p => p.Mcc + p.Mnc));
        }
    }
}
