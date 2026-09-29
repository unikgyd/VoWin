using System.Net;
using VoSharp.Ipsec;
using VoSharp.Sip;
using VoSharp.Telephony;

namespace VoSharp.Tests;

public sealed class HostImsSaPlanTests
{
    private static readonly SecurityAgreement Agreement = new(
        new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 40666, 40667),
        "ipsec-3gpp", 2001, 2002, 50601, 50602);

    [Theory]
    [InlineData("192.0.2.10", "192.0.2.20")]
    [InlineData("2001:db8::10", "2001:db8::20")]
    public void MapsBothProtectedPortPairsAndReceiverOwnedSpis(string localText, string remoteText)
    {
        var local = IPAddress.Parse(localText);
        var remote = IPAddress.Parse(remoteText);
        using var plan = HostImsSaPlan.Create(local, remote, Agreement, new byte[16], new byte[16]);

        AssertPair(plan.ClientPair, local, remote, 40666, 50602, 1001, 2002);
        AssertPair(plan.ServerPair, local, remote, 40667, 50601, 1002, 2001);
        Assert.Equal(IpsecAuthAlgorithm.HmacSha1_96, plan.ClientPair.Inbound.Auth);
        Assert.Equal(IpsecCipherAlgorithm.AesCbc128, plan.ClientPair.Outbound.Cipher);
        Assert.Equal(uint.MaxValue, plan.ClientPair.Inbound.LifetimeSeconds);
    }

    [Fact]
    public void HoldsSeparateKeyCopiesAndErasesThemOnDispose()
    {
        var ck = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var ik = Enumerable.Range(17, 16).Select(value => (byte)value).ToArray();
        var originalCk = ck.ToArray();
        var originalIk = ik.ToArray();
        var plan = HostImsSaPlan.Create(IPAddress.Parse("192.0.2.10"),
            IPAddress.Parse("192.0.2.20"), Agreement, ck, ik);

        Assert.Equal(ik, plan.ClientPair.Inbound.AuthKey[..16]);
        Assert.Equal(new byte[4], plan.ClientPair.Inbound.AuthKey[16..]);
        Assert.Equal(ck, plan.ServerPair.Outbound.CipherKey);
        Assert.NotSame(plan.ClientPair.Inbound.AuthKey, plan.ClientPair.Outbound.AuthKey);
        Assert.NotSame(plan.ClientPair.Inbound.AuthKey, plan.ServerPair.Inbound.AuthKey);

        plan.Dispose();
        plan.Dispose();
        Assert.Equal(new byte[20], plan.ClientPair.Inbound.AuthKey);
        Assert.Equal(new byte[16], plan.ServerPair.Outbound.CipherKey);
        Assert.Equal(originalCk, ck);
        Assert.Equal(originalIk, ik);
    }

    [Fact]
    public void RejectsDuplicateSpisOrMixedAddressFamilies()
    {
        var local = IPAddress.Parse("192.0.2.10");
        var remote = IPAddress.Parse("192.0.2.20");
        var duplicate = Agreement with { PcscfServerSpi = Agreement.Selected.SpiClient };
        Assert.Throws<ArgumentException>(() => HostImsSaPlan.Create(
            local, remote, duplicate, new byte[16], new byte[16]));
        var reserved = Agreement with { PcscfServerSpi = 255 };
        Assert.Throws<ArgumentException>(() => HostImsSaPlan.Create(
            local, remote, reserved, new byte[16], new byte[16]));
        Assert.Throws<ArgumentException>(() => HostImsSaPlan.Create(
            local, IPAddress.IPv6Loopback, Agreement, new byte[16], new byte[16]));
    }

    private static void AssertPair(ChildSaRequest pair, IPAddress local, IPAddress remote,
        ushort localPort, ushort remotePort, uint inboundSpi, uint outboundSpi)
    {
        Assert.Equal(SaMode.Transport, pair.Mode);
        Assert.Equal(local, pair.LocalAddress);
        Assert.Equal(remote, pair.RemoteAddress);
        Assert.Equal(localPort, pair.LocalPort);
        Assert.Equal(remotePort, pair.RemotePort);
        Assert.Equal(localPort, pair.LocalSelector.StartPort);
        Assert.Equal(remotePort, pair.RemoteSelector.StartPort);
        Assert.Equal(local, pair.LocalSelector.StartAddress);
        Assert.Equal(remote, pair.RemoteSelector.StartAddress);
        Assert.Equal(SaDirection.Inbound, pair.Inbound.Direction);
        Assert.Equal(inboundSpi, pair.Inbound.Spi);
        Assert.Equal(SaDirection.Outbound, pair.Outbound.Direction);
        Assert.Equal(outboundSpi, pair.Outbound.Spi);
    }
}
