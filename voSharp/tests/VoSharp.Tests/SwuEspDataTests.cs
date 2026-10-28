using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VoSharp.Ike;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class SwuEspDataTests
{
    private static byte[] Hex(JsonElement value, string name) => Convert.FromHexString(value.GetProperty(name).GetString()!);
    private static EspTunnel Tunnel(JsonElement value)
    {
        var local = IPAddress.Parse(value.GetProperty("local").GetString()!);
        var peer = IPAddress.Parse(value.GetProperty("peer").GetString()!);
        var policy = new IkeChildPolicy([new(local, local, 17, 45000, 45000)], [new(peer, peer, 17, 5060, 5060)], local);
        return new EspTunnel(value.GetProperty("spi").GetUInt32(), value.GetProperty("spi").GetUInt32(),
            Hex(value, "key"), Hex(value, "auth"), Hex(value, "key"), Hex(value, "auth"),
            encryption: $"AES-CBC-{value.GetProperty("bits").GetInt32()}",
            integrity: value.GetProperty("integrity").GetInt32() switch { 1 => "HMAC-MD5-96", 2 => "HMAC-SHA1-96", _ => "HMAC-SHA256-128" }, policy: policy);
    }
    [Fact]
    public void IndependentTunnelEspAndInnerPolicyFramesMatchRust()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "swu-esp-v1.json")));
        var vectors = contract.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        Assert.Equal(18, vectors.Length);
        foreach (var value in vectors)
        {
            using var tunnel = Tunnel(value);
            Assert.Equal(Hex(value, "inner"), tunnel.Open(Hex(value, "esp")));
            Assert.Null(tunnel.Open(Hex(value, "esp")));
        }
        foreach (var value in contract.RootElement.GetProperty("policies").EnumerateArray())
        {
            using var tunnel = Tunnel(value);
            var actual = tunnel.Open(Hex(value, "esp"));
            var accepted = value.GetProperty("accepted").GetBoolean();
            Assert.True((actual != null) == accepted, value.GetProperty("id").GetString());
            if (!accepted)
            {
                var valid = vectors.Single(v => v.GetProperty("bits").GetInt32() == value.GetProperty("bits").GetInt32() &&
                    v.GetProperty("integrity").GetInt32() == value.GetProperty("integrity").GetInt32() && v.GetProperty("local").GetString() == value.GetProperty("local").GetString());
                Assert.Equal(Hex(valid, "inner"), tunnel.Open(Hex(valid, "esp")));
            }
        }
    }
    [Fact]
    public async Task ActualUdpPumpKeepsBoundedEspBacklogAndNewestPackets()
    {
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new IkeTransport(IPAddress.Loopback, ((IPEndPoint)peer.LocalEndPoint!).Port);
        transport.StartPacketPump(0x11223344);
        Assert.True(transport.EspPackets.CanCount);
        var frame = new byte[64]; BinaryPrimitives.WriteUInt32BigEndian(frame, 0x11223344);
        for (uint i = 1; i <= 160; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), i);
            await peer.SendToAsync(frame, SocketFlags.None, transport.LocalEndpoint!);
            if (i % 8 == 0) await Task.Delay(2);
        }
        await Task.Delay(40);
        Assert.InRange(transport.EspPackets.Count, 1, 64);
        uint latest = 0;
        while (transport.EspPackets.TryRead(out var value)) latest = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(4));
        Assert.Equal((uint)160, latest);
    }
    [Fact]
    public void OversizeEspFailsBeforeConsumingSequence()
    {
        var key = new byte[16]; var auth = new byte[20];
        using var tunnel = new EspTunnel(256, 256, key, auth, key, auth);
        Assert.Throws<ArgumentException>(() => tunnel.Seal(new byte[65507]));
        var packet = tunnel.Seal([1, 2, 3]);
        Assert.Equal((uint)1, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
    }
}
