using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoSharp.Common.Aka;
using Xunit;

namespace VoSharp.Ike.Tests;

public class FinalRejectionReviewTests
{
    private sealed class Sim : IAkaProvider
    {
        public int Calls;
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(AkaResult.Succeeded([0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88],
                Enumerable.Repeat((byte)0xCC, 16).ToArray(), Enumerable.Repeat((byte)0xEE, 16).ToArray()));
        }
    }

    private sealed class Transcript
    {
        public bool FinalSent, Deleted;
        public int Observations, ObservationsAtDelete;
    }

    [Theory]
    [InlineData("network", true)]
    [InlineData("apn", true)]
    [InlineData("deactivated", true)]
    [InlineData("missing-timer", true)]
    [InlineData("zero", true)]
    [InlineData("observer-fails", true)]
    [InlineData("invalid-auth", false)]
    [InlineData("missing-auth", false)]
    [InlineData("duplicate-auth", false)]
    [InlineData("wrong-transcript", false)]
    [InlineData("mixed-error", false)]
    [InlineData("timer-only", false)]
    public async Task DeferredFinalAUTHAuthenticatesPolicyBeforeCleanup(string mode, bool wantProof)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var state = new Transcript();
        var server = Serve(socket, mode, state, deadline.Token);
        var sim = new Sim();
        var result = await IkeSession.EstablishAsync(new(IPAddress.Loopback, sim, "460001234567890", "460", "00",
            EpdgPort: ((IPEndPoint)socket.LocalEndPoint!).Port, AddressFamilyMode: IkeAddressFamilyMode.Ipv4,
            Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 }, EapMethod: EapType.Aka,
            ObserveNetworkRejection: (proof, peer, ct) =>
            {
                Assert.False(state.Deleted);
                Assert.Equal(IPAddress.Loopback, peer);
                Interlocked.Increment(ref state.Observations);
                if (mode == "observer-fails") throw new IOException("synthetic policy store failure");
                return Task.CompletedTask;
            }), deadline.Token);
        await server;
        Assert.False(result.Success);
        Assert.True(state.FinalSent); Assert.True(result.EapSucceeded); Assert.Equal(1, sim.Calls);
        Assert.True(state.Deleted); Assert.True(result.FailureCleanupComplete);
        Assert.False(result.InitialResponderAuthVerified); // Do not relabel late proof as initial certificate proof.
        Assert.False(result.RetryableAddressFamilyFailure); Assert.False(result.RetryableIdentityRejection);
        Assert.False(result.RetryableTransportFailure); Assert.Null(result.Transport);
        Assert.True((result.NetworkRejection != null) == wantProof,
            $"final MSK AUTH policy capture mismatch for {mode}: {result.ErrorMessage}");
        Assert.Equal(wantProof ? 1 : 0, state.Observations);
        Assert.Equal(state.Observations, state.ObservationsAtDelete);
        if (wantProof)
        {
            Assert.True(result.FinalAuthVerified);
            var proof = result.NetworkRejection!;
            Assert.Equal(mode == "apn" ? (ushort)9002 : (ushort)10500, proof.NotifyType);
            var status = mode == "deactivated" ? IkeBackoffTimerStatus.Deactivated : mode == "missing-timer" ? IkeBackoffTimerStatus.Missing : IkeBackoffTimerStatus.Timed;
            Assert.Equal(status, proof.BackoffTimer.Status);
            if (status == IkeBackoffTimerStatus.Timed) Assert.Equal(mode == "zero" ? TimeSpan.Zero : TimeSpan.FromMinutes(10), proof.BackoffTimer.Duration);
        }
        else Assert.False(result.FinalAuthVerified && mode is "invalid-auth" or "missing-auth" or "duplicate-auth" or "wrong-transcript");
    }

    private static async Task Serve(Socket socket, string mode, Transcript state, CancellationToken ct)
    {
        static byte[] Combine(params byte[][] values) => values.SelectMany(v => v).ToArray();
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "eap-aka-v1.json")));
        var vector = contract.RootElement.GetProperty("flows")[1];
        byte[] Fixture(string name) => Convert.FromHexString(vector.GetProperty(name).GetString()!);
        var buffer = new byte[65535];
        var packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
        var peer = packet.RemoteEndPoint; var initBytes = buffer[..packet.ReceivedBytes];
        var init = IkeWire.ParseMessage(initBytes);
        var suite = IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 };
        using var dh = Ecp256Group.Create();
        var shared = dh.ComputeSharedSecret(init.Payloads.Single(p => p.Type == IkePayloadType.KeyExchange).Body.AsSpan(4));
        var ni = init.Payloads.Single(p => p.Type == IkePayloadType.Nonce).Body;
        var nr = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var keys = IkeCrypto.DeriveIkeKeys(suite, shared, ni, nr, init.InitiatorSpi, 17);
        CryptographicOperations.ZeroMemory(shared);
        try
        {
            IkeWire.IkeMessage Header(uint id, IkeExchangeType exchange) => new()
            { InitiatorSpi = init.InitiatorSpi, ResponderSpi = 17, Exchange = exchange, Flags = IkeFlags.Response, MessageId = id };
            var initial = Header(0, IkeExchangeType.IkeSaInit);
            initial.Payloads.Add(new(IkePayloadType.SecurityAssociation, IkeWire.EncodeProposals([new()
            { ProposalNumber = 1, Protocol = IkeProtocolId.Ike, Transforms = suite.ToTransforms() }])));
            initial.Payloads.Add(new(IkePayloadType.KeyExchange, [0, 19, 0, 0, .. dh.Public]));
            initial.Payloads.Add(new(IkePayloadType.Nonce, nr));
            var initialBytes = IkeWire.SerializeMessage(initial);
            await socket.SendToAsync(initialBytes, SocketFlags.None, peer, ct);
            async Task<List<IkePayload>> Receive(uint id)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, peer, ct);
                Assert.Equal(peer, received.RemoteEndPoint);
                var (header, items) = IkeCrypto.DecryptPayloads(buffer.AsSpan(0, received.ReceivedBytes), suite, keys.SkEi, keys.SkAi);
                Assert.Equal(id, header.MessageId); Assert.Equal(IkeExchangeType.IkeAuth, header.Exchange);
                return items;
            }
            async Task Send(uint id, IReadOnlyList<IkePayload> items)
            {
                await socket.SendToAsync(IkeCrypto.EncryptPayloads(Header(id, IkeExchangeType.IkeAuth), items, suite, keys.SkEr, keys.SkAr), SocketFlags.None, peer, ct);
            }
            var first = await Receive(1);
            var idi = first.Single(p => p.Type == IkePayloadType.IdentificationInitiator);
            var idr = first.Single(p => p.Type == IkePayloadType.IdentificationResponder);
            await Send(1, [idr, new(IkePayloadType.Eap, Fixture("request"))]);
            var response = await Receive(2);
            Assert.Equal(Fixture("response"), response.Single(p => p.Type == IkePayloadType.Eap).Body);
            await Send(2, [new(IkePayloadType.Eap, Fixture("notificationRequest"))]);
            response = await Receive(3);
            Assert.Equal(Fixture("notificationResponse"), response.Single(p => p.Type == IkePayloadType.Eap).Body);
            await Send(3, [new(IkePayloadType.Eap, Fixture("success"))]);
            var final = await Receive(4);
            var pad = IkeCrypto.Prf(suite, Fixture("msk"), Encoding.ASCII.GetBytes("Key Pad for IKEv2"));
            var expected = IkeCrypto.Prf(suite, pad, Combine(initBytes, nr, IkeCrypto.Prf(suite, keys.SkPi, idi.Body)));
            Assert.Equal(Combine([2, 0, 0, 0], expected), final.Single(p => p.Type == IkePayloadType.Authentication).Body);
            var mic = IkeCrypto.Prf(suite, pad, Combine(initialBytes, ni, IkeCrypto.Prf(suite, keys.SkPr, idr.Body)));
            if (mode == "invalid-auth") mic[0] ^= 1;
            if (mode == "wrong-transcript")
            {
                CryptographicOperations.ZeroMemory(mic);
                mic = IkeCrypto.Prf(suite, pad, Combine(initialBytes, ni, IkeCrypto.Prf(suite, keys.SkPr, [2, 0, 0, 0, (byte)'x'])));
            }
            var auth = new IkePayload(IkePayloadType.Authentication, Combine([2, 0, 0, 0], mic));
            var reply = new List<IkePayload> { auth };
            if (mode == "missing-auth") reply.Clear();
            if (mode == "duplicate-auth") reply.Add(auth);

            if (mode != "timer-only") reply.Add(IkeBackoffEvidenceTests.Notify(mode == "apn" ? (ushort)9002 : (ushort)10500));
            if (mode == "mixed-error") reply.Add(IkeBackoffEvidenceTests.Notify(24));
            if (mode != "missing-timer") reply.Add(IkeBackoffEvidenceTests.Notify(41041, [1, mode == "deactivated" ? (byte)0xE0 : mode == "zero" ? (byte)0 : (byte)1]));
            state.FinalSent = true;
            await Send(4, reply);
            var deletedPacket = await socket.ReceiveFromAsync(buffer, SocketFlags.None, peer, ct);
            var (deleted, payloads) = IkeCrypto.DecryptPayloads(buffer.AsSpan(0, deletedPacket.ReceivedBytes), suite, keys.SkEi, keys.SkAi);
            Assert.Equal(IkeExchangeType.Informational, deleted.Exchange); Assert.Equal(5u, deleted.MessageId);
            Assert.Equal(IkePayloadType.Delete, payloads.Single().Type);
            state.ObservationsAtDelete = Volatile.Read(ref state.Observations); state.Deleted = true;
            foreach (var secret in new[] { pad, mic, expected }) CryptographicOperations.ZeroMemory(secret);
        }
        finally
        {
            foreach (var secret in new[] { keys.SkD, keys.SkAi, keys.SkAr, keys.SkEi, keys.SkEr, keys.SkPi, keys.SkPr }) CryptographicOperations.ZeroMemory(secret);
        }
    }
}
