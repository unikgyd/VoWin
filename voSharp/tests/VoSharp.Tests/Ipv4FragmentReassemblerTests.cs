using System.Buffers.Binary;
using System.Net;
using System.Text;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class Ipv4FragmentReassemblerTests
{
    private static readonly IPAddress Pcscf = IPAddress.Parse("10.24.0.1");
    private static readonly IPAddress Ue = IPAddress.Parse("10.24.0.2");

    [Fact]
    public void ReassemblesOutOfOrderFragmentedSipInvite()
    {
        var sipBytes = BuildInvite();
        var original = CreatePacket(sipBytes);
        var fragments = Fragment(original, 1200, 1200);
        var reassembler = new Ipv4FragmentReassembler();

        Assert.Null(reassembler.Process(fragments[2]));
        Assert.Null(reassembler.Process(fragments[0]));
        var reassembled = reassembler.Process(fragments[1]);

        Assert.NotNull(reassembled);
        Assert.Equal(original, reassembled);
        var datagram = IpPacketUtils.ParseIpv4UdpPacket(reassembled!);
        Assert.Equal(sipBytes, datagram.Payload);
        var invite = SipMessage.Parse(datagram.Payload);
        Assert.True(invite.IsRequest);
        Assert.Equal("INVITE", invite.Method);
    }

    [Fact]
    public void AcceptsAnIdenticalRetransmittedFragment()
    {
        var original = CreatePacket(Enumerable.Range(0, 2500).Select(i => (byte)i).ToArray());
        var fragments = Fragment(original, 1200, 1200);
        var reassembler = new Ipv4FragmentReassembler();

        Assert.Null(reassembler.Process(fragments[0]));
        Assert.Null(reassembler.Process(fragments[0]));
        Assert.Null(reassembler.Process(fragments[1]));
        Assert.Equal(original, reassembler.Process(fragments[2]));
    }

    [Fact]
    public void ConflictingOverlapDropsTheWholeDatagram()
    {
        var original = CreatePacket(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var fragments = Fragment(original, 16);
        var conflicting = fragments[1].ToArray();
        // Move the second fragment back by 8 bytes and corrupt a byte in the overlap.
        BinaryPrimitives.WriteUInt16BigEndian(conflicting.AsSpan(6, 2), 0x2001);
        conflicting[20] ^= 0xff;
        WriteChecksum(conflicting);
        var reassembler = new Ipv4FragmentReassembler();

        Assert.Null(reassembler.Process(fragments[0]));
        Assert.Throws<FormatException>(() => reassembler.Process(conflicting));

        // The conflict removed all stored data for this tuple, so a clean retry works.
        byte[]? result = null;
        foreach (var fragment in fragments)
            result = reassembler.Process(fragment) ?? result;
        Assert.Equal(original, result);
    }

    [Fact]
    public void DoesNotMixSameIdentificationFromDifferentEndpoints()
    {
        var firstPacket = CreatePacket(Enumerable.Repeat((byte)0x11, 1600).ToArray());
        var secondPacket = IpPacketUtils.BuildIpv4UdpPacket(
            IPAddress.Parse("10.24.0.9"), Ue, 5060, 5060, Enumerable.Repeat((byte)0x22, 1600).ToArray());
        SetIdentificationAndClearDf(secondPacket, 0x4242);
        var firstFragments = Fragment(firstPacket, 1200);
        var secondFragments = Fragment(secondPacket, 1200);
        var reassembler = new Ipv4FragmentReassembler();

        Assert.Null(reassembler.Process(firstFragments[0]));
        Assert.Null(reassembler.Process(secondFragments[0]));
        Assert.Equal(firstPacket, reassembler.Process(firstFragments[1]));
        Assert.Equal(secondPacket, reassembler.Process(secondFragments[1]));
    }

    [Fact]
    public void ReassemblesImsEspBeforeUnprotectingSipInvite()
    {
        var ueProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001);
        var pcscfProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 41000, 41001);
        var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        using var ue = new ImsIpsecTransport(Ue, Pcscf, ueProposal);
        using var pcscf = new ImsIpsecTransport(Pcscf, Ue, pcscfProposal);
        ue.Activate(new SecurityAgreement(ueProposal, "verify", 1001, 1002, 41000, 41001), key, key);
        pcscf.Activate(new SecurityAgreement(pcscfProposal, "verify", 100, 200, 40000, 40001), key, key);

        var protectedInvite = pcscf.Protect(new SipDatagram(
            BuildInvite(), new IPEndPoint(Pcscf, 41000), new IPEndPoint(Ue, 40001)));
        SetIdentificationAndClearDf(protectedInvite, 0x5151);
        var fragments = Fragment(protectedInvite, 1200, 1200);
        var reassembler = new Ipv4FragmentReassembler();
        byte[]? reassembled = null;
        foreach (var fragment in fragments.Reverse())
            reassembled = reassembler.Process(fragment) ?? reassembled;

        Assert.NotNull(reassembled);
        var datagram = IpPacketUtils.ParseIpv4UdpPacket(ue.Unprotect(reassembled!));
        Assert.Equal("INVITE", SipMessage.Parse(datagram.Payload).Method);
        Assert.Equal(new IPEndPoint(Ue, 40001), datagram.LocalEndPoint);
    }

    private static byte[] BuildInvite()
    {
        var routeHeaders = string.Concat(Enumerable.Range(0, 45)
            .Select(i => $"Record-Route: <sip:edge-{i:D2}.ims.example.test;lr>\r\n"));
        return Encoding.ASCII.GetBytes(
            "INVITE sip:ue@ims.example.test SIP/2.0\r\n" +
            "Via: SIP/2.0/UDP 10.24.0.1:5060;branch=z9hG4bK-fragmented\r\n" +
            "From: <sip:caller@ims.example.test>;tag=remote\r\n" +
            "To: <sip:ue@ims.example.test>\r\n" +
            "Call-ID: fragmented-invite@example.test\r\n" +
            "CSeq: 1 INVITE\r\n" + routeHeaders +
            "Content-Length: 0\r\n\r\n");
    }

    private static byte[] CreatePacket(byte[] payload)
    {
        var packet = IpPacketUtils.BuildIpv4UdpPacket(Pcscf, Ue, 5060, 5060, payload);
        SetIdentificationAndClearDf(packet, 0x4242);
        return packet;
    }

    private static void SetIdentificationAndClearDf(byte[] packet, ushort identification)
    {
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), identification);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0);
        WriteChecksum(packet);
    }

    private static byte[][] Fragment(byte[] packet, params int[] fragmentPayloadLengths)
    {
        const int headerLength = 20;
        var payload = packet.AsSpan(headerLength);
        var lengths = fragmentPayloadLengths.ToList();
        int specified = lengths.Sum();
        if (specified < payload.Length)
            lengths.Add(payload.Length - specified);
        Assert.Equal(payload.Length, lengths.Sum());
        Assert.All(lengths.Take(lengths.Count - 1), length => Assert.Equal(0, length % 8));

        var result = new List<byte[]>();
        int offset = 0;
        for (int i = 0; i < lengths.Count; i++)
        {
            int length = lengths[i];
            var fragment = new byte[headerLength + length];
            packet.AsSpan(0, headerLength).CopyTo(fragment);
            payload.Slice(offset, length).CopyTo(fragment.AsSpan(headerLength));
            BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(2, 2), (ushort)fragment.Length);
            ushort flagsAndOffset = (ushort)(offset / 8);
            if (i < lengths.Count - 1) flagsAndOffset |= 0x2000;
            BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(6, 2), flagsAndOffset);
            WriteChecksum(fragment);
            result.Add(fragment);
            offset += length;
        }
        return result.ToArray();
    }

    private static void WriteChecksum(byte[] packet)
    {
        int headerLength = (packet[0] & 0x0f) * 4;
        packet[10] = packet[11] = 0;
        uint sum = 0;
        for (int i = 0; i < headerLength; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i, 2));
        while (sum > ushort.MaxValue)
            sum = (sum & ushort.MaxValue) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), (ushort)~sum);
    }
}
