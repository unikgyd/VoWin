using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VoSharp.Common.Aka;

namespace VoSharp.Ike.Tests;

public class IkeRetryProofTests
{
    [Theory]
    [InlineData(0UL, 14, false, true)]
    [InlineData(17UL, 14, false, true)]
    [InlineData(17UL, 17, false, true)]
    [InlineData(17UL, 14, true, false)]
    [InlineData(0UL, 14, true, false)]
    public async Task ActualInitialProposalRejectionDoesNotRequireZeroResponderSpi(ulong spi, ushort kind, bool mixed, bool retry)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            var buffer = new byte[65535];
            var packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            var init = IkeWire.ParseMessage(buffer[..packet.ReceivedBytes]);
            var response = new IkeWire.IkeMessage { InitiatorSpi = init.InitiatorSpi, ResponderSpi = spi, Exchange = IkeExchangeType.IkeSaInit, Flags = IkeFlags.Response, MessageId = 0 };
            response.Payloads.Add(new(IkePayloadType.Notify, kind == 14 ? new byte[] { 0, 0, 0, 14 } : new byte[] { 0, 0, 0, 17, 0, 19 }));
            if (mixed) response.Payloads.Add(new(IkePayloadType.Notify, [0, 0, 0, 24]));
            await socket.SendToAsync(IkeWire.SerializeMessage(response), SocketFlags.None, packet.RemoteEndPoint, timeout.Token);
        });
        var sim = new Sim();
        var result = await IkeSession.EstablishAsync(new(IPAddress.Loopback, sim, "001010123456789", "001", "01",
            EpdgPort: ((IPEndPoint)socket.LocalEndPoint!).Port,
            Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 }), timeout.Token);
        await server;
        Assert.False(result.Success);
        Assert.Equal(0, sim.Calls);
        Assert.Equal(retry, result.RetryableInitialProposal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PendingCleanupAlwaysClosesAndRetainsEveryFailure(bool sendFails, bool closeFails)
    {
        var order = new List<string>();
        var failure = await IkeSession.CleanupFailedAttemptAsync(_ =>
        {
            order.Add("delete");
            return sendFails ? Task.FromException(new IOException("synthetic send")) : Task.CompletedTask;
        }, () =>
        {
            order.Add("close");
            if (closeFails) throw new IOException("synthetic close");
        });
        Assert.Equal(new[] { "delete", "close" }, order);
        Assert.Equal(sendFails || closeFails, failure != null);
        if (sendFails && closeFails) Assert.Equal(2, Assert.IsType<AggregateException>(failure).InnerExceptions.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(255)]
    public void EmptySpiProtocolIdRemainsIgnored(byte protocol)
    {
        Assert.True(IkeSession.IsAddressFamilyRejection([new(IkePayloadType.Notify, [protocol, 0, 0, 36])]));
    }

    [Theory]
    [InlineData("spi")]
    [InlineData("data")]
    [InlineData("eap")]
    [InlineData("configuration")]
    public void FamilyHintMustNotHideOtherOrMalformedPayloads(string mode)
    {
        var payloads = new List<IkePayload> { new(IkePayloadType.Notify, [0, 0, 0, 36]) };
        if (mode == "spi") payloads[0] = new(IkePayloadType.Notify, [3, 1, 0, 36, 1]);
        if (mode == "data") payloads[0] = new(IkePayloadType.Notify, [0, 0, 0, 36, 1]);
        if (mode == "eap") payloads.Add(new(IkePayloadType.Eap, [4, 1, 0, 4]));
        if (mode == "configuration") payloads.Add(new(IkePayloadType.Configuration, [2]));
        Assert.False(IkeSession.IsAddressFamilyRejection(payloads));
    }

    private sealed class Sim : IAkaProvider
    {
        public int Calls;
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("This identity-only transcript must never query a SIM."); }
    }

    [Theory]
    [InlineData("plain", true)]
    [InlineData("identity", true)]
    [InlineData("backoff", false)]
    [InlineData("earlier-backoff", false)]
    [InlineData("malformed-challenge", false)]
    [InlineData("mixed-auth", false)]
    [InlineData("duplicate-eap", false)]
    [InlineData("family", false)]
    public async Task ActualEncryptedFailureHasOnlyPreciselyProvenRetry(string mode, bool retry)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Serve(socket, mode, timeout.Token);
        var sim = new Sim();
        var result = await IkeSession.EstablishAsync(new(IPAddress.Loopback, sim, "001010123456789", "001", "01",
            EpdgPort: ((IPEndPoint)socket.LocalEndPoint!).Port,
            Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 }), timeout.Token);
        Assert.False(result.Success);
        Assert.Equal(0, sim.Calls);
        Assert.Equal(retry, (bool?)typeof(IkeSessionResult).GetProperty("RetryableIdentityRejection")?.GetValue(result) ?? false);
        Assert.True(await server, "The pending IKE SA was not deleted before the failed attempt returned.");
    }

    private static async Task<bool> Serve(Socket socket, string mode, CancellationToken ct)
    {
        byte[] buffer = new byte[65535];
        var any = new IPEndPoint(IPAddress.Any, 0);
        var first = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
        var peer = first.RemoteEndPoint;
        var init = IkeWire.ParseMessage(buffer[..first.ReceivedBytes]);
        var suite = IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 };
        using var dh = Ecp256Group.Create();
        var shared = dh.ComputeSharedSecret(init.Payloads.Single(p => p.Type == IkePayloadType.KeyExchange).Body.AsSpan(4));
        var ni = init.Payloads.Single(p => p.Type == IkePayloadType.Nonce).Body;
        var nr = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var keys = IkeCrypto.DeriveIkeKeys(suite, shared, ni, nr, init.InitiatorSpi, 17);
        CryptographicOperations.ZeroMemory(shared);
        IkeWire.IkeMessage Header(uint id, IkeExchangeType exchange, IkeFlags flags) => new()
        { InitiatorSpi = init.InitiatorSpi, ResponderSpi = 17, Exchange = exchange, Flags = flags, MessageId = id };
        var initial = Header(0, IkeExchangeType.IkeSaInit, IkeFlags.Response);
        initial.Payloads.Add(new(IkePayloadType.SecurityAssociation, IkeWire.EncodeProposals([new()
        { ProposalNumber = 1, Protocol = IkeProtocolId.Ike, Transforms = suite.ToTransforms() }])));
        initial.Payloads.Add(new(IkePayloadType.KeyExchange, [0, 19, 0, 0, .. dh.Public]));
        initial.Payloads.Add(new(IkePayloadType.Nonce, nr));
        await socket.SendToAsync(IkeWire.SerializeMessage(initial), SocketFlags.None, peer, ct);
        async Task Receive(uint id)
        {
            var packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
            using var receiver = new IkeEncryptedMessageReceiver(Header(id, IkeExchangeType.IkeAuth, IkeFlags.Initiator), suite, keys.SkEi, keys.SkAi, false, TimeSpan.FromSeconds(2));
            Assert.NotNull(receiver.Accept(buffer.AsSpan(0, packet.ReceivedBytes)));
        }
        async Task Send(uint id, IReadOnlyList<IkePayload> payloads) =>
            await socket.SendToAsync(IkeCrypto.EncryptPayloads(Header(id, IkeExchangeType.IkeAuth, IkeFlags.Response), payloads, suite, keys.SkEr, keys.SkAr), SocketFlags.None, peer, ct);
        static IkePayload Notify(ushort type) => new(IkePayloadType.Notify, [0, 0, (byte)(type >> 8), (byte)type]);
        await Receive(1);
        uint messageId = 1;
        if (mode is "identity" or "earlier-backoff" or "malformed-challenge")
        {
            var body = mode == "malformed-challenge"
                ? EapAkaClient.MarshalEapPacket(new(EapCode.Request, 21, EapType.Aka, [AkaSubtype.Challenge]))
                : EapAkaClient.MarshalEapPacket(new(EapCode.Request, 21, EapType.Identity, []));
            var payloads = new List<IkePayload> { new(IkePayloadType.Eap, body) };
            if (mode == "earlier-backoff") payloads.Add(Notify(41041));
            await Send(1, payloads); await Receive(2); messageId = 2;
        }
        var failure = new IkePayload(IkePayloadType.Eap, [EapCode.Failure, 21, 0, 4]);
        var reply = mode == "family" ? new List<IkePayload> { Notify(36) } : new List<IkePayload> { failure };
        if (mode == "backoff") reply.Add(Notify(41041));
        if (mode == "mixed-auth") reply.Add(Notify(24));
        if (mode == "duplicate-eap") reply.Add(failure);
        await Send(messageId, reply);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cleanup.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cleanup.Token);
            using var receiver = new IkeEncryptedMessageReceiver(Header(messageId + 1, IkeExchangeType.Informational, IkeFlags.Initiator), suite, keys.SkEi, keys.SkAi, false, TimeSpan.FromSeconds(1));
            var deleted = receiver.Accept(buffer.AsSpan(0, packet.ReceivedBytes));
            return deleted?.Payloads.Single().Type == IkePayloadType.Delete && deleted.Payloads.Single().Body.SequenceEqual(new byte[] { 1, 0, 0, 0 });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
