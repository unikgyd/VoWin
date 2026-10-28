using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class ImsEspContractTests
{
    private static JsonDocument Contract() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "ims-esp-v1.json")));
    public static IEnumerable<object[]> Cases()
    {
        using var root = Contract();
        foreach (var item in root.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("id").GetString()!, item.GetProperty("ipv6").GetBoolean(),
                item.GetProperty("integrity").GetString()!, item.GetProperty("encryption").GetString()!,
                item.GetProperty("accepted").GetBoolean(), item.GetProperty("packetHex").GetString()!];
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void IndependentEspVectorsValidateBeforeCommittingReplay(string id, bool ipv6,
        string integrity, string encryption, bool accepted, string packetHex)
    {
        using var contract = Contract();
        var root = contract.RootElement;
        var local = IPAddress.Parse(ipv6 ? "2001:db8::1" : "192.0.2.1");
        var remote = IPAddress.Parse(ipv6 ? "2001:db8::2" : "192.0.2.2");
        var proposal = new SecurityProposal(integrity, encryption, 1000, 2000, 40000, 40001);
        using var ue = new ImsIpsecTransport(local, remote, proposal);
        ue.Activate(new SecurityAgreement(proposal, "synthetic", 3000, 4000, 41000, 41001),
            Convert.FromHexString(root.GetProperty("ckHex").GetString()!), Convert.FromHexString(root.GetProperty("ikHex").GetString()!));
        var packet = Convert.FromHexString(packetHex);
        if (accepted)
        {
            Assert.Equal(Convert.FromHexString(root.GetProperty("payloadHex").GetString()!),
                IpPacketUtils.ParseUdpPacket(ue.Unprotect(packet)).Payload);
            Assert.Throws<FormatException>(() => ue.Unprotect(packet));
        }
        else
        {
            var error = Record.Exception(() => ue.Unprotect(packet));
            Assert.True(error is FormatException or CryptographicException, id);
            // Bad fixtures use authenticated sequence 1000. None may advance
            // the window and evict the independently generated valid sequence 1.
            var valid = root.GetProperty("cases").EnumerateArray().First(item =>
                item.GetProperty("ipv6").GetBoolean() == ipv6 &&
                item.GetProperty("integrity").GetString() == integrity &&
                item.GetProperty("encryption").GetString() == encryption &&
                item.GetProperty("id").GetString()!.EndsWith("-valid", StringComparison.Ordinal));
            Assert.Equal(Convert.FromHexString(root.GetProperty("payloadHex").GetString()!),
                IpPacketUtils.ParseUdpPacket(ue.Unprotect(Convert.FromHexString(valid.GetProperty("packetHex").GetString()!))).Payload);
        }
    }
}
