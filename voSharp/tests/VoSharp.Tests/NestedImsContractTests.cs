using System.Net;
using System.Text.Json;
using VoSharp.Ike;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class NestedImsContractTests
{
    [Fact]
    public void IndependentTwoLayerEspFramesValidateAtTheirOwnBoundary()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "nested-ims-esp-v1.json")));
        var root = contract.RootElement;
        byte[] Hex(string name) => Convert.FromHexString(root.GetProperty(name).GetString()!);
        foreach (var value in root.GetProperty("cases").EnumerateArray())
        {
            var ipv6 = value.GetProperty("ipv6").GetBoolean(); var local = IPAddress.Parse(ipv6 ? "2001:db8::1" : "192.0.2.1"); var remote = IPAddress.Parse(ipv6 ? "2001:db8::2" : "192.0.2.2");
            var policy = new IkeChildPolicy([IkeTrafficSelector.Any(ipv6)], [IkeTrafficSelector.Any(ipv6)], local);
            using var outer = new EspTunnel(0x11223344, 0x11223344, Hex("keyHex"), Hex("authHex"), Hex("keyHex"), Hex("authHex"), encryption: "AES-CBC-256", integrity: "HMAC-SHA256-128", policy: policy);
            var proposal = new SecurityProposal(value.GetProperty("integrity").GetString()!, value.GetProperty("encryption").GetString()!, 1000, 2000, 40000, 40001);
            using var inner = new ImsIpsecTransport(local, remote, proposal);
            inner.Activate(new(proposal, "synthetic", 3000, 4000, 41000, 41001), Hex("ckHex"), Hex("ikHex"));
            var packet = outer.Open(Convert.FromHexString(value.GetProperty("espHex").GetString()!));
            Assert.True((packet != null) == value.GetProperty("outerAccepted").GetBoolean(), value.GetProperty("id").GetString());
            var accepted = value.GetProperty("accepted").GetBoolean();
            if (accepted) Assert.Equal(Hex("payloadHex"), IpPacketUtils.ParseUdpPacket(inner.Unprotect(packet!)).Payload);
            else
            {
                if (packet != null) Assert.NotNull(Record.Exception(() => inner.Unprotect(packet)));
                var followup = outer.Open(Convert.FromHexString(value.GetProperty("followupHex").GetString()!));
                Assert.NotNull(followup); Assert.Equal(Hex("payloadHex"), IpPacketUtils.ParseUdpPacket(inner.Unprotect(followup!)).Payload);
            }
        }
    }
}
