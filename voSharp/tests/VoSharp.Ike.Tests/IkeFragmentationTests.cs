using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using VoSharp.Ike;

namespace VoSharp.Ike.Tests;

public class IkeFragmentationTests
{
    private static IkeWire.IkeMessage Header(uint id = 1, IkeFlags flags = IkeFlags.Response) => new()
    {
        InitiatorSpi = 0x0102030405060708, ResponderSpi = 0x1112131415161718,
        Exchange = IkeExchangeType.IkeAuth, Flags = flags, MessageId = id
    };
    private static byte[] Key(int count, byte value) => Enumerable.Repeat(value, count).ToArray();
    private static byte[] Hex(JsonElement element, string name) => Convert.FromHexString(element.GetProperty(name).GetString()!);

    [Fact]
    public void IndependentOpenSslVectorsMatchAllAesSizesAndIntegrityAlgorithms()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "swu-protected-v1.json")));
        var vectors = contract.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        for (var index = 0; index < vectors.Length; index += 3)
        {
            var item = vectors[index]; var suite = new IkeSuite(12, item.GetProperty("bits").GetInt32(), 5, item.GetProperty("integrity").GetUInt16(), 14);
            var enc = Hex(item, "encryption"); var auth = Hex(item, "authentication");
            using var fragments = new IkeEncryptedMessageReceiver(Header(), suite, enc, auth, true, TimeSpan.FromSeconds(5));
            for (var part = 0; part < 3; part++)
            {
                var v = vectors[index + part]; var packet = Hex(v, "packet");
                Assert.Equal(packet, IkeProtectedMessage.EncryptChunk(Header(), suite, enc, auth,
                    (IkePayloadType)v.GetProperty("first").GetByte(), v.GetProperty("number").GetUInt16(), v.GetProperty("total").GetUInt16(), Hex(v, "plaintext"), Hex(v, "iv")));
                if (part == 0)
                {
                    var (_, payloads) = IkeCrypto.DecryptPayloads(packet, suite, enc, auth);
                    Assert.Equal(Hex(contract.RootElement, "chain"), IkeWire.SerializePayloadChain(payloads));
                }
                else if (part == 1) Assert.Null(fragments.Accept(packet));
                else Assert.Equal(Hex(contract.RootElement, "chain"), IkeWire.SerializePayloadChain(fragments.Accept(packet)!.Payloads));
            }
        }
    }

    [Theory]
    [InlineData(128, 2)] [InlineData(128, 12)] [InlineData(128, 13)] [InlineData(128, 14)]
    [InlineData(192, 2)] [InlineData(192, 12)] [InlineData(192, 13)] [InlineData(192, 14)]
    [InlineData(256, 2)] [InlineData(256, 12)] [InlineData(256, 13)] [InlineData(256, 14)]
    public void AuthenticatedFragmentsCannotBePoisonedByWrongHeaderMacOrReplay(int bits, ushort integrity)
    {
        var suite = new IkeSuite(12, bits, 5, integrity, 14); var enc = Key(bits / 8, 0x42); var auth = Key(suite.IntegrityKeyLength, 0x24);
        var payloads = new[] { new IkePayload(IkePayloadType.Eap, Key(3100, 7)) };
        var packets = IkeProtectedMessage.Encrypt(Header(), payloads, suite, enc, auth, true);
        Assert.True(packets.Count > 2); Assert.All(packets, p => Assert.True(p.Length <= 540));
        using var receiver = new IkeEncryptedMessageReceiver(Header(), suite, enc, auth, true, TimeSpan.FromSeconds(5));
        Assert.Null(receiver.Accept(packets[^1])); Assert.Null(receiver.Accept(packets[^1]));
        foreach (var offset in new[] { 0, 8, 17, 18, 19, 20, 28, 32, 34, 36, 52, packets[0].Length - 1 })
        {
            var bad = packets[0].ToArray(); bad[offset] ^= 1;
            var error = Record.Exception(() => receiver.Accept(bad)); Assert.True(error is IkeFormatException or CryptographicException);
        }
        foreach (var packet in packets.Take(packets.Count - 2).Reverse()) Assert.Null(receiver.Accept(packet));
        Assert.Equal(payloads[0].Body, receiver.Accept(packets[^2])!.Payloads[0].Body);
        Assert.Throws<IkeFormatException>(() => receiver.Accept(packets[0]));
        using var unnegotiated = new IkeEncryptedMessageReceiver(Header(), suite, enc, auth, false, TimeSpan.FromSeconds(5));
        Assert.Throws<IkeFormatException>(() => unnegotiated.Accept(packets[0]));
    }

    [Fact]
    public void LargerAuthenticatedTotalRestartsAssemblyAndBoundsAreEnforced()
    {
        var suite = IkeSuite.Preferred; var enc = Key(32, 3); var auth = Key(32, 4);
        var payloads = new[] { new IkePayload(IkePayloadType.Eap, Key(3100, 7)) };
        var old = IkeProtectedMessage.Encrypt(Header(), payloads, suite, enc, auth, true, 800);
        var newer = IkeProtectedMessage.Encrypt(Header(), payloads, suite, enc, auth, true);
        using var receiver = new IkeEncryptedMessageReceiver(Header(), suite, enc, auth, true, TimeSpan.FromSeconds(5));
        Assert.Null(receiver.Accept(old[0])); var bad = newer[0].ToArray(); bad[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => receiver.Accept(bad));
        Assert.Null(receiver.Accept(newer[0])); Assert.Null(receiver.Accept(old[1]));
        foreach (var p in newer.Skip(1).Take(newer.Count - 2)) Assert.Null(receiver.Accept(p));
        Assert.Equal(payloads[0].Body, receiver.Accept(newer[^1])!.Payloads[0].Body);
        foreach (var (number, total, first) in new[] { (0, 1, 48), (2, 1, 0), (1, 65, 48), (2, 2, 48), (1, 0, 48) })
        {
            var packet = IkeProtectedMessage.EncryptChunk(Header(), suite, enc, auth, (IkePayloadType)first, 1, 2, [1, 2, 3]);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(32), (ushort)number);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(34), (ushort)total);
            IkeCrypto.IntegrityMac(suite, auth, packet.AsSpan(0, packet.Length - 16)).CopyTo(packet, packet.Length - 16);
            using var invalid = new IkeEncryptedMessageReceiver(Header(), suite, enc, auth, true, TimeSpan.FromSeconds(5));
            Assert.Throws<IkeFormatException>(() => invalid.Accept(packet));
        }
        using var expired = new IkeEncryptedMessageReceiver(Header(), suite, enc, auth, true, TimeSpan.Zero);
        Assert.Throws<TimeoutException>(() => expired.Accept(old[0]));
        Assert.Throws<IkeFormatException>(() => IkeProtectedMessage.Encrypt(Header(), [new(IkePayloadType.Eap, new byte[65000])], suite, enc, auth, true, 256));
        Assert.Throws<CryptographicException>(() => IkeProtectedMessage.Encrypt(Header(), payloads, suite, new byte[16], auth, true));
        Assert.False(IkeSession.ReadFragmentationSupport([]));
        var support = new IkePayload(IkePayloadType.Notify, [0, 0, 0x40, 0x2e]);
        Assert.True(IkeSession.ReadFragmentationSupport([support]));
        Assert.Throws<IkeFormatException>(() => IkeSession.ReadFragmentationSupport([support, support]));
        Assert.Throws<IkeFormatException>(() => IkeSession.ReadFragmentationSupport([new(IkePayloadType.Notify, [1, 0, 0x40, 0x2e])]));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task RealUdpEncryptedRoundTripReassemblesAndRetransmitsThroughBothReceivePaths(bool ipv6, bool pump)
    {
        var ip = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var server = new UdpClient(new IPEndPoint(ip, 0)); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var peer = (IPEndPoint)server.Client.LocalEndPoint!;
        using var transport = new IkeTransport(ip, peer.Port, TimeSpan.FromSeconds(5));
        if (pump) transport.StartPacketPump(0xdeadbeef);
        var suite = IkeSuite.Preferred; var encI = Key(32, 1); var authI = Key(32, 2); var encR = Key(32, 3); var authR = Key(32, 4);
        var worker = Task.Run(async () =>
        {
            var batch = new List<byte[]>(); IPEndPoint? source = null;
            using var receive = new IkeEncryptedMessageReceiver(Header(flags: IkeFlags.Initiator), suite, encI, authI, true, TimeSpan.FromSeconds(5));
            while (true)
            {
                var datagram = await server.ReceiveAsync(deadline.Token); source = datagram.RemoteEndPoint; batch.Add(datagram.Buffer);
                var result = receive.Accept(datagram.Buffer); if (result != null) { Assert.Equal(Key(2300, 7), result.Payloads[0].Body); break; }
            }
            Assert.True(batch.Count > 1);
            foreach (var expected in batch) Assert.Equal(expected, (await server.ReceiveAsync(deadline.Token)).Buffer);
            var replies = IkeProtectedMessage.Encrypt(Header(), [new(IkePayloadType.Eap, Key(3100, 8))], suite, encR, authR, true);
            async Task Send(byte[] bytes)
            {
                var wire = pump ? new byte[4 + bytes.Length] : bytes;
                if (pump) bytes.CopyTo(wire, 4);
                await server.SendAsync(wire, source!, deadline.Token);
            }
            using var rogue = new UdpClient(new IPEndPoint(ip, 0)); await rogue.SendAsync(replies[0], source!, deadline.Token);
            foreach (var offset in new[] { 0, 8, 19, 20, replies[0].Length - 1 })
            { var bad = replies[0].ToArray(); bad[offset] ^= 1; await Send(bad); }
            await Send(replies[^1]); await Send(replies[^1]);
            foreach (var packet in replies.Take(replies.Count - 1).Reverse()) await Send(packet);
        });
        var response = await transport.RoundTripEncryptedAsync(Header(flags: IkeFlags.Initiator), [new(IkePayloadType.Eap, Key(2300, 7))],
            suite, encI, authI, encR, authR, true, deadline.Token);
        Assert.Equal(Key(3100, 8), response.Payloads[0].Body); await worker;
    }

    [Fact]
    public async Task CancelledExchangeLeavesNoDetachedUdpRetries()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new IkeTransport(IPAddress.Loopback, ((IPEndPoint)server.Client.LocalEndPoint!).Port);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var operation = transport.RoundTripEncryptedAsync(Header(flags: IkeFlags.Initiator), [new(IkePayloadType.Eap, [1])],
            IkeSuite.Preferred, Key(32, 1), Key(32, 2), Key(32, 3), Key(32, 4), true, cancel.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await server.ReceiveAsync(deadline.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        using var silence = new CancellationTokenSource(TimeSpan.FromMilliseconds(650));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.ReceiveAsync(silence.Token));
    }

    [Fact]
    public async Task PeerFragmentedRequestsUseAuthenticatedAssemblyAndOnlyFirstFragmentReplaysCachedResponse()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new IkeTransport(IPAddress.Loopback, ((IPEndPoint)server.Client.LocalEndPoint!).Port);
        var suite = IkeSuite.Preferred; var encI = Key(32, 1); var authI = Key(32, 2); var encR = Key(32, 3); var authR = Key(32, 4);
        using var controller = new IkeSessionController(transport, suite, 0x0102030405060708, 0x1112131415161718,
            4, 0xdeadbeef, 0xcafebabe, encI, authI, encR, authR, fragmentationNegotiated: true);
        transport.StartPacketPump(0xdeadbeef); var local = transport.LocalEndpoint!;
        var destination = new IPEndPoint(IPAddress.Loopback, local.Port);
        async Task Send(byte[] packet) => await server.SendAsync(packet, destination);
        var header = Header(0, 0); header.Exchange = IkeExchangeType.Informational;
        var packets = IkeProtectedMessage.Encrypt(header, [new(IkePayloadType.Notify, [0, 0, 0xfd, 0xe8, .. Key(2300, 8)])], suite, encR, authR, true);
        var wrongExchange = packets[0].ToArray(); wrongExchange[18] = (byte)IkeExchangeType.CreateChildSa;
        await Send(wrongExchange); // a forged first datagram must not claim the assembler's exchange
        var bad = packets[0].ToArray(); bad[^1] ^= 1; await Send(bad);
        foreach (var packet in packets.Reverse()) await Send(packet);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var response = (await server.ReceiveAsync(deadline.Token)).Buffer;
        var (actual, payloads) = IkeCrypto.DecryptPayloads(response, suite, encI, authI);
        Assert.Equal((uint)0, actual.MessageId); Assert.Empty(payloads); Assert.Equal(IkeFlags.Initiator | IkeFlags.Response, actual.Flags);
        await Send(packets[^1]);
        using (var silence = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.ReceiveAsync(silence.Token));
        await Send(packets[0]); Assert.Equal(response, (await server.ReceiveAsync(deadline.Token)).Buffer);
        // Verify the independent peer ID advances exactly once, including a
        // fragmented CREATE_CHILD_SA request which is not yet rekey-supported.
        var next = Header(1, 0); next.Exchange = IkeExchangeType.CreateChildSa;
        foreach (var packet in IkeProtectedMessage.Encrypt(next, [new(IkePayloadType.Nonce, Key(2300, 9))], suite, encR, authR, true)) await Send(packet);
        var (nextHeader, nextPayloads) = IkeCrypto.DecryptPayloads((await server.ReceiveAsync(deadline.Token)).Buffer, suite, encI, authI);
        Assert.Equal((uint)1, nextHeader.MessageId); Assert.Equal(IkeExchangeType.CreateChildSa, nextHeader.Exchange);
        Assert.Equal(43, BinaryPrimitives.ReadUInt16BigEndian(nextPayloads.Single().Body.AsSpan(2)));
    }

    [Fact]
    public void AuthenticatedBadPaddingCannotEvictValidBufferedFragments()
    {
        var suite = IkeSuite.Preferred; var enc = Key(32, 3); var auth = Key(32, 4);
        var payloads = new[] { new IkePayload(IkePayloadType.Eap, Key(2300, 8)) };
        var fragments = IkeProtectedMessage.Encrypt(Header(), payloads, suite, enc, auth, true);
        Assert.Equal((byte)IkePayloadType.Encrypted, IkeProtectedMessage.Encrypt(Header(), payloads, suite, enc, auth, false)[0][16]);
        using var receiver = new IkeEncryptedMessageReceiver(Header(), suite, enc, auth, true, TimeSpan.FromSeconds(5));
        Assert.Null(receiver.Accept(fragments[0]));
        var invalid = IkeCrypto.EncryptPayloads(Header(), [new(IkePayloadType.Eap, [1])], suite, enc, auth);
        using var aes = Aes.Create(); aes.Key = enc;
        var padded = new byte[16]; padded[^1] = 255;
        aes.EncryptCbc(padded, invalid.AsSpan(32, 16), PaddingMode.None).CopyTo(invalid, 48);
        IkeCrypto.IntegrityMac(suite, auth, invalid.AsSpan(0, invalid.Length - 16)).CopyTo(invalid, invalid.Length - 16);
        Assert.Throws<IkeFormatException>(() => receiver.Accept(invalid));
        foreach (var packet in fragments.Skip(1).Take(fragments.Count - 2)) Assert.Null(receiver.Accept(packet));
        Assert.Equal(payloads[0].Body, receiver.Accept(fragments[^1])!.Payloads[0].Body);
    }
}
