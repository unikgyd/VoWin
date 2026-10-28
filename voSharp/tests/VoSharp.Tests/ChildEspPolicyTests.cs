using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using VoSharp.Ike;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class ChildEspPolicyTests
{
    private static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0; for (var i = 0; i < bytes.Length; i += 2) sum += (uint)(bytes[i] << 8) + (i + 1 < bytes.Length ? bytes[i + 1] : (uint)0);
        while (sum > 65535) sum = (sum & 65535) + (sum >> 16); return (ushort)~sum;
    }
    private static byte[] Packet(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort)
    {
        var ipv6 = source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6; var header = ipv6 ? 40 : 20; var packet = new byte[header + 11];
        packet[0] = ipv6 ? (byte)0x60 : (byte)0x45;
        if (ipv6) { BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 11); packet[6] = 17; packet[7] = 64; source.GetAddressBytes().CopyTo(packet, 8); destination.GetAddressBytes().CopyTo(packet, 24); }
        else { BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length); packet[8] = 64; packet[9] = 17; source.GetAddressBytes().CopyTo(packet, 12); destination.GetAddressBytes().CopyTo(packet, 16); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20))); }
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(header), sourcePort); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(header + 2), destinationPort); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(header + 4), 11);
        packet[^3] = 1; packet[^2] = 2; packet[^1] = 3;
        var pseudo = source.GetAddressBytes().Concat(destination.GetAddressBytes()).Concat(ipv6 ? new byte[] { 0, 0, 0, 11, 0, 0, 0, 17 } : new byte[] { 0, 17, 0, 11 }).Concat(packet.AsSpan(header).ToArray()).ToArray();
        var checksum = Checksum(pseudo); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(header + 6), checksum == 0 ? (ushort)65535 : checksum); return packet;
    }
    [Theory]
    [InlineData(false, "HMAC-SHA1-96", 20)]
    [InlineData(true, "HMAC-SHA1-96", 20)]
    [InlineData(false, "HMAC-SHA256-128", 32)]
    [InlineData(true, "HMAC-SHA256-128", 32)]
    [InlineData(false, "HMAC-MD5-96", 16)]
    [InlineData(true, "HMAC-MD5-96", 16)]
    public void ActualEspDataPlaneEnforcesAssignedAddressPortsDirectionAndReplayCommit(bool ipv6, string integrity, int authLength)
    {
        var local = IPAddress.Parse(ipv6 ? "2001:db8::2" : "10.0.0.2"); var peer = IPAddress.Parse(ipv6 ? "2001:db8::1" : "10.0.0.1"); var other = IPAddress.Parse(ipv6 ? "2001:db8::3" : "10.0.0.3");
        var policy = new IkeChildPolicy([new(local, other, 17, 0, 65535)], [new(peer, peer, 17, 5060, 5060), new(other, other, 17, 40000, 50000)], local);
        var key = Enumerable.Repeat((byte)0xa5, 16).ToArray(); var auth = Enumerable.Repeat((byte)0x5a, authLength).ToArray(); var header = ipv6 ? (byte)41 : (byte)4;
        using var client = new EspTunnel(0x11223344, 0x55667788, key, auth, key, auth, integrity: integrity, policy: policy);
        using var server = new EspTunnel(0x55667788, 0x11223344, key, auth, key, auth, integrity: integrity);
        var outbound = Packet(local, peer, 45000, 5060); var incoming = Packet(peer, local, 5060, 45000);
        Assert.Throws<InvalidOperationException>(() => client.Seal(Packet(local, peer, 45000, 5061), header));
        Assert.Throws<InvalidOperationException>(() => client.Seal(Packet(other, peer, 45000, 5060), header));
        var protectedPacket = client.Seal(outbound, header); Assert.Equal((uint)1, BinaryPrimitives.ReadUInt32BigEndian(protectedPacket.AsSpan(4)));
        Assert.Equal(outbound, server.Open(protectedPacket));
        var icv = integrity == "HMAC-SHA256-128" ? 16 : 12; var signed = protectedPacket.AsSpan(0, protectedPacket.Length - icv);
        var independent = integrity switch { "HMAC-MD5-96" => HMACMD5.HashData(auth, signed), "HMAC-SHA1-96" => HMACSHA1.HashData(auth, signed), _ => HMACSHA256.HashData(auth, signed) };
        Assert.Equal(independent[..icv], protectedPacket[^icv..]);
        Assert.Equal(Packet(local, other, 45000, 45000), server.Open(client.Seal(Packet(local, other, 45000, 45000), header)));
        typeof(EspTunnel).GetField("_outboundSeq", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, (uint)900);
        Assert.Null(client.Open(server.Seal(Packet(peer, other, 5060, 45000), header)));
        var malformed = incoming.ToArray(); if (ipv6) malformed[5] ^= 1; else malformed[10] ^= 1;
        Assert.Null(client.Open(server.Seal(malformed, header)));
        using var low = new EspTunnel(0x55667788, 0x11223344, key, auth, key, auth, integrity: integrity);
        Assert.Equal(incoming, client.Open(low.Seal(incoming, header))); // rejected high sequences never evict valid low packet
    }
    [Fact]
    public void Ipv6ExtensionsAndOpaqueFragmentsDoNotBypassPortPolicy()
    {
        var local = IPAddress.Parse("2001:db8::2"); var peer = IPAddress.Parse("2001:db8::1"); var policy = new IkeChildPolicy([new(local, local, 17, 0, 65535)], [new(peer, peer, 17, 5060, 5060)], local);
        var plain = Packet(peer, local, 5060, 45000); var extended = new byte[plain.Length + 8]; plain.AsSpan(0, 40).CopyTo(extended); extended[6] = 60; BinaryPrimitives.WriteUInt16BigEndian(extended.AsSpan(4), 19); extended[40] = 17; plain.AsSpan(40).CopyTo(extended.AsSpan(48));
        Assert.True(policy.AllowsPacket(extended, false, 41)); extended[41] = 255; Assert.False(policy.AllowsPacket(extended, false, 41));
        var fragment = new byte[56]; plain.AsSpan(0, 40).CopyTo(fragment); fragment[6] = 44; BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(4), 16); fragment[40] = 17; BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(42), 8);
        Assert.False(policy.AllowsPacket(fragment, false, 41));
        var opaque = new IkeChildPolicy([new(local, local, 17, 0, 65535)], [new(peer, peer, 17, 65535, 0)], local); Assert.True(opaque.AllowsPacket(fragment, false, 41));
    }
    [Fact]
    public void UnsupportedAlgorithmOrWrongKeyWidthFailsBeforeTunnelCreation()
    {
        var key = new byte[16]; var auth = new byte[20]; Assert.Throws<ArgumentException>(() => new EspTunnel(1, 2, key, auth, key, auth, integrity: "unknown"));
        Assert.Throws<ArgumentException>(() => new EspTunnel(1, 2, key, auth, key, auth, encryption: "AES-GCM-128"));
        Assert.Throws<ArgumentException>(() => new EspTunnel(1, 2, key, auth, key, auth, encryption: "AES-CBC-256"));
        Assert.Throws<ArgumentException>(() => new EspTunnel(1, 2, key, auth, key, auth, integrity: "HMAC-MD5-96")); Assert.All(key, b => Assert.Equal(0, b));
    }
    [Theory]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(256)]
    public void AdvertisedAesWidthMatchesActualCipher(int bits)
    {
        var key = Enumerable.Repeat((byte)0x75, bits / 8).ToArray(); var auth = new byte[20]; var plain = new byte[] { 1, 2, 3 };
        using var tunnel = new EspTunnel(256, 256, key, auth, key, auth, encryption: $"AES-CBC-{bits}");
        Assert.Equal(plain, tunnel.Open(tunnel.Seal(plain)));
    }
}
