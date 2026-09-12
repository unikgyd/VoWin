using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class Ipv6PacketUtilsTests
{
    [Fact]
    public void BuildAndParseIpv6UdpPacket_RoundTrips()
    {
        var source = IPAddress.Parse("2001:db8::10");
        var destination = IPAddress.Parse("2001:db8::20");
        var payload = "REGISTER sip:ims.test"u8.ToArray();

        var bytes = IpPacketUtils.BuildUdpPacket(source, destination, 5062, 5060, payload);
        var packet = IpPacketUtils.ParseUdpPacket(bytes);

        Assert.Equal(6, bytes[0] >> 4);
        Assert.NotEqual(0, bytes[46] | bytes[47]); // UDP checksum is mandatory for IPv6.
        Assert.Equal(new IPEndPoint(source, 5062), packet.RemoteEndPoint);
        Assert.Equal(new IPEndPoint(destination, 5060), packet.LocalEndPoint);
        Assert.Equal(payload, packet.Payload);
    }

    [Fact]
    public void ImsTransport_PreservesIpv6BeforeSecurityAgreement()
    {
        var source = IPAddress.Parse("2001:db8::10");
        var destination = IPAddress.Parse("2001:db8::20");
        using var transport = new ImsIpsecTransport(source, destination);
        var datagram = new SipDatagram(
            "OPTIONS sip:ims.test"u8.ToArray(),
            new IPEndPoint(source, 5062),
            new IPEndPoint(destination, 5060));

        var protectedPacket = transport.Protect(datagram);
        var parsed = IpPacketUtils.ParseUdpPacket(protectedPacket);

        Assert.Equal(datagram.Payload, parsed.Payload);
        Assert.Equal(datagram.LocalEndPoint, parsed.RemoteEndPoint);
        Assert.Equal(datagram.RemoteEndPoint, parsed.LocalEndPoint);
    }

    [Fact]
    public void ImsTransport_ProtectsAndUnprotectsIpv6AfterSecurityAgreement()
    {
        var ue = IPAddress.Parse("2001:db8::10");
        var pcscf = IPAddress.Parse("2001:db8::20");
        var ueProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001);
        var pcscfProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 41000, 41001);
        var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        using var ueTransport = new ImsIpsecTransport(ue, pcscf, ueProposal);
        using var pcscfTransport = new ImsIpsecTransport(pcscf, ue, pcscfProposal);
        ueTransport.Activate(new SecurityAgreement(ueProposal, "verify", 1001, 1002, 41000, 41001), key, key);
        pcscfTransport.Activate(new SecurityAgreement(pcscfProposal, "verify", 100, 200, 40000, 40001), key, key);

        var payload = "REGISTER sip:ims.test SIP/2.0\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        var protectedPacket = ueTransport.Protect(new SipDatagram(payload,
            new IPEndPoint(ue, 40000), new IPEndPoint(pcscf, 41001)));
        var decoded = IpPacketUtils.ParseUdpPacket(pcscfTransport.Unprotect(protectedPacket));

        Assert.Equal(payload, decoded.Payload);
        Assert.Equal(new IPEndPoint(pcscf, 41001), decoded.LocalEndPoint);
        Assert.Equal(new IPEndPoint(ue, 40000), decoded.RemoteEndPoint);
    }

    [Fact]
    public void OversizedIpv6ImsPacket_IsFragmentedAndReassembledBeforeOuterEsp()
    {
        var ue = IPAddress.Parse("2001:db8::10");
        var pcscf = IPAddress.Parse("2001:db8::20");
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001);
        var key = RandomNumberGenerator.GetBytes(16);
        using var transport = new ImsIpsecTransport(ue, pcscf, proposal);
        transport.Activate(new SecurityAgreement(proposal, "verify", 1001, 1002, 41000, 41001), key, key);
        var protectedPacket = transport.Protect(new SipDatagram(RandomNumberGenerator.GetBytes(1800),
            new IPEndPoint(ue, 40000), new IPEndPoint(pcscf, 41001)));

        var fragments = IpPacketUtils.FragmentForTunnel(protectedPacket);
        Assert.True(fragments.Count > 1);
        Assert.All(fragments, fragment => Assert.InRange(fragment.Length, 48, IpPacketUtils.DefaultTunnelInnerMtu));
        Assert.All(fragments, fragment => Assert.Equal(44, fragment[6]));

        var reassembler = new Ipv6FragmentReassembler();
        byte[]? reassembled = null;
        foreach (var fragment in fragments.Reverse())
            reassembled = reassembler.Process(fragment) ?? reassembled;
        Assert.Equal(protectedPacket, reassembled);
    }

    [Fact]
    public void OversizedIpv4Packet_IsFragmentedDespiteGeneratedDfFlagAndReassembled()
    {
        var source = IPAddress.Parse("10.0.0.1");
        var destination = IPAddress.Parse("10.0.0.2");
        var payload = RandomNumberGenerator.GetBytes(1800);
        var packet = IpPacketUtils.BuildUdpPacket(source, destination, 40000, 41001, payload);
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2)) & 0x4000);

        var fragments = IpPacketUtils.FragmentForTunnel(packet);
        Assert.True(fragments.Count > 1);
        Assert.All(fragments, fragment => Assert.InRange(fragment.Length, 28, IpPacketUtils.DefaultTunnelInnerMtu));
        var reassembler = new Ipv4FragmentReassembler();
        byte[]? reassembled = null;
        foreach (var fragment in fragments.Reverse())
            reassembled = reassembler.Process(fragment) ?? reassembled;

        Assert.NotNull(reassembled);
        Assert.Equal(payload, IpPacketUtils.ParseUdpPacket(reassembled!).Payload);
    }

    [Fact]
    public void Icmpv6EchoRequest_BuildsEchoReplyWithSwappedEndpoints()
    {
        var source = IPAddress.Parse("2001:db8::20").GetAddressBytes();
        var destination = IPAddress.Parse("2001:db8::10").GetAddressBytes();
        var request = new byte[48];
        request[0] = 0x60;
        request[5] = 8;
        request[6] = 58;
        request[7] = 63;
        source.CopyTo(request, 8);
        destination.CopyTo(request, 24);
        request[40] = 128;
        request[44] = 0x12;
        request[45] = 0x34;
        request[46] = 0;
        request[47] = 7;

        Assert.True(IpPacketUtils.TryBuildIcmpv6EchoReply(request, out var reply));
        Assert.Equal(129, reply[40]);
        Assert.Equal(destination, reply[8..24]);
        Assert.Equal(source, reply[24..40]);
        // Cross-checked against an independent RFC 4443 pseudo-header implementation: the ePDG
        // silently drops an echo reply whose checksum is wrong, so the exact bytes are pinned.
        Assert.Equal(0x10, reply[42]);
        Assert.Equal(0xE0, reply[43]);
        Assert.Equal(64, reply[7]);
        Assert.Equal(request[44..48], reply[44..48]);
    }

    [Fact]
    public void Ipv6ExtensionHeader_DoesNotHideTheUpperLayerProtocol()
    {
        var source = IPAddress.Parse("2001:db8::10");
        var destination = IPAddress.Parse("2001:db8::20");
        var payload = "REGISTER sip:ims.test"u8.ToArray();

        var udp = IpPacketUtils.BuildUdpPacket(source, destination, 5062, 5060, payload);
        // Insert an 8-byte hop-by-hop options header between the fixed header and UDP.
        var withOptions = new byte[udp.Length + 8];
        udp.AsSpan(0, 40).CopyTo(withOptions);
        withOptions[6] = 0;   // next header = hop-by-hop options
        withOptions[40] = 17; // options next header = UDP
        withOptions[41] = 0;  // header extension length 0 -> 8 bytes
        udp.AsSpan(40).CopyTo(withOptions.AsSpan(48));
        BinaryPrimitives.WriteUInt16BigEndian(withOptions.AsSpan(4, 2), (ushort)(udp.Length - 40 + 8));

        Assert.True(IpPacketUtils.TryGetInnerTransport(withOptions, out var protocol));
        Assert.Equal(17, protocol);
        Assert.True(IpPacketUtils.IsUdpInnerPacket(withOptions));

        var parsed = IpPacketUtils.ParseUdpPacket(withOptions);
        Assert.Equal(payload, parsed.Payload);
        Assert.Equal(new IPEndPoint(source, 5062), parsed.RemoteEndPoint);
        Assert.Equal(new IPEndPoint(destination, 5060), parsed.LocalEndPoint);
    }

    [Fact]
    public void Icmpv6InnerPacket_IsControlTrafficRatherThanUdp()
    {
        var request = new byte[48];
        request[0] = 0x60;
        request[5] = 8;
        request[6] = 58;
        request[40] = 128; // Echo Request

        Assert.True(IpPacketUtils.TryGetInnerTransport(request, out var protocol));
        Assert.Equal(58, protocol);
        Assert.False(IpPacketUtils.IsUdpInnerPacket(request));
    }
}
