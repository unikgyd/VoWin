using System.Net;
using System.Text.Json;
using VoSharp.Ike;

namespace VoSharp.Ike.Tests;

public class SwuConfigurationContractTests
{
    [Fact]
    public void SharedConfigurationReplyVectorsAreAtomicAndFamilyAware()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "contracts", "swu-cp-reply-v1.json")));
        foreach (var item in contract.RootElement.GetProperty("cases").EnumerateArray())
        {
            var data = Convert.FromHexString(item.GetProperty("hex").GetString()!);
            IkeConfigurationReply? parsed = null;
            var error = Record.Exception(() => parsed = IkeConfigurationReply.Parse(data));
            Assert.True((error is null) == item.GetProperty("accepted").GetBoolean(), item.GetProperty("id").GetString());
            if (error is not null) { Assert.IsType<FormatException>(error); continue; }
            IPAddress[] Expected(string name) => item.GetProperty(name).EnumerateArray().Select(value => IPAddress.Parse(value.GetString()!)).ToArray();
            Assert.Equal(Expected("assigned"), parsed!.AssignedAddresses);
            Assert.Equal(Expected("dns"), parsed.DnsAddresses);
            Assert.Equal(Expected("pcscf"), parsed.PcscfAddresses);
            var subnets = item.GetProperty("subnets").EnumerateArray().Select(value => (IPAddress.Parse(value[0].GetString()!), value[1].GetByte())).ToArray();
            Assert.Equal(subnets, parsed.Ipv6Subnets);
        }
    }
    [Fact]
    public void ConfigurationNeverAcceptsPartialPairOrUnboundedAttributes()
    {
        byte[] valid = [2,0,0,0,0,1,0,4,192,0,2,1,0,20,0,4,192,0,2,2];
        Assert.Equal("192.0.2.1", IkeSession.ParseConfigurationPayload(valid).AssignedIp);
        foreach (byte[] tail in new byte[][] { [0], [0,3], [0,3,0], [0,3,0,4,8,8] })
            Assert.Throws<FormatException>(() => IkeSession.ParseConfigurationPayload([..valid, ..tail]));
        List<byte> excessive = [2,0,0,0];
        for (int i = 0; i < 129; i++) excessive.AddRange([0x7f,0xff,0,0]);
        Assert.Throws<FormatException>(() => IkeConfigurationReply.Parse(excessive.ToArray()));
        Assert.Throws<FormatException>(() => IkeConfigurationReply.Parse(new byte[4097]));
    }
}
