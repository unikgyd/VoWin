using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VoSharp.Common.Aka;

namespace VoSharp.Ike.Tests;

public class IkeBackoffProviderProofTests
{
    private sealed class Sim : IAkaProvider
    {
        public int Calls;
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("No AKA challenge in this transcript."); }
    }

    [Theory]
    [InlineData("network", true, true, IkeBackoffTimerStatus.Timed)]
    [InlineData("apn", true, true, IkeBackoffTimerStatus.Timed)]
    [InlineData("plmn", true, true, IkeBackoffTimerStatus.Timed)]
    [InlineData("deactivated", true, true, IkeBackoffTimerStatus.Deactivated)]
    [InlineData("zero", true, true, IkeBackoffTimerStatus.Timed)]
    [InlineData("bare", true, true, IkeBackoffTimerStatus.Invalid)]
    [InlineData("missing", true, true, IkeBackoffTimerStatus.Missing)]
    [InlineData("duplicate-timer", true, true, IkeBackoffTimerStatus.Invalid)]
    [InlineData("duplicate-error", true, false, IkeBackoffTimerStatus.Missing)]
    [InlineData("no-auth", false, false, IkeBackoffTimerStatus.Missing)]
    [InlineData("wrong-name", false, false, IkeBackoffTimerStatus.Missing)]
    [InlineData("bad-signature", false, false, IkeBackoffTimerStatus.Missing)]
    [InlineData("later-error", true, true, IkeBackoffTimerStatus.Timed)]
    [InlineData("earlier-timer", true, true, IkeBackoffTimerStatus.Missing)]
    [InlineData("later-untrusted", false, false, IkeBackoffTimerStatus.Missing)]
    public async Task ActualEncryptedRejectionBindsOnlyCurrentAuthenticatedSA(string mode, bool authenticated, bool hasProof, IkeBackoffTimerStatus status)
    {
        using var certificates = new ResponderCertificates();
        using var key = RSA.Create(2048);
        using var certificate = certificates.Leaf(key);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Serve(socket, mode, key, certificate.RawData, deadline.Token);
        var sim = new Sim();
        var result = await IkeSession.EstablishAsync(new(IPAddress.Loopback, sim, "999990123456789", "999", "99",
            EpdgPort: ((IPEndPoint)socket.LocalEndPoint!).Port,
            Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 },
            ResponderAuthentication: new(mode == "wrong-name" ? "other.example" : "epdg.example", [certificates.Root.RawData])), deadline.Token);
        Assert.True(await server);
        Assert.False(result.Success); Assert.True(result.FailureCleanupComplete);
        Assert.Equal(0, sim.Calls); Assert.Equal(authenticated, result.InitialResponderAuthVerified);
        Assert.False(result.FinalAuthVerified); Assert.False(result.RetryableIdentityRejection);
        Assert.False(result.RetryableAddressFamilyFailure); Assert.False(result.RetryableTransportFailure);
        Assert.Equal(hasProof, result.NetworkRejection != null);
        if (hasProof)
        {
            var proof = result.NetworkRejection!;
            Assert.Equal(status, proof.BackoffTimer.Status);
            Assert.Equal(mode == "apn" ? (ushort)9002 : mode == "plmn" ? (ushort)9003 : (ushort)10500, proof.NotifyType);
            Assert.Equal(mode == "apn" ? IkeRejectionScope.PlmnApn : mode == "plmn" ? IkeRejectionScope.Plmn : IkeRejectionScope.Epdg, proof.Scope);
            if (status == IkeBackoffTimerStatus.Timed)
                Assert.Equal(mode == "zero" ? TimeSpan.Zero : TimeSpan.FromMinutes(10), proof.BackoffTimer.Duration);
        }
    }

    private static async Task<bool> Serve(Socket socket, string mode, RSA key, byte[] certificate, CancellationToken ct)
    {
        byte[] buffer = new byte[65535];
        var first = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
        var peer = first.RemoteEndPoint;
        var init = IkeWire.ParseMessage(buffer[..first.ReceivedBytes]);
        var suite = IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 };
        using var dh = Ecp256Group.Create();
        var shared = dh.ComputeSharedSecret(init.Payloads.Single(p => p.Type == IkePayloadType.KeyExchange).Body.AsSpan(4));
        var ni = init.Payloads.Single(p => p.Type == IkePayloadType.Nonce).Body;
        var nr = Enumerable.Repeat((byte)0x37, 32).ToArray();
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
            var initResponse = IkeWire.SerializeMessage(initial);
            await socket.SendToAsync(initResponse, SocketFlags.None, peer, ct);
            var packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, peer, ct);
            var (request, _) = IkeCrypto.DecryptPayloads(buffer.AsSpan(0, packet.ReceivedBytes), suite, keys.SkEi, keys.SkAi);
            Assert.Equal(1u, request.MessageId);
            var id = new IkePayload(IkePayloadType.IdentificationResponder, [2, 0, 0, 0, (byte)'i', (byte)'m', (byte)'s']);
            var reply = new List<IkePayload> { id };
            if (mode is not ("no-auth" or "later-untrusted"))
            {
                var idHash = suite.PrfId == IkePrfId.HmacSha1 ? HMACSHA1.HashData(keys.SkPr, id.Body) : HMACSHA256.HashData(keys.SkPr, id.Body);
                var signature = key.SignData([.. initResponse, .. ni, .. idHash], HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
                if (mode == "bad-signature") signature[^1] ^= 1;
                reply.Add(new(IkePayloadType.Authentication, [1, 0, 0, 0, .. signature]));
                reply.Add(new(IkePayloadType.Certificate, [4, .. certificate]));
            }
            var later = mode is "later-error" or "earlier-timer" or "later-untrusted";
            uint messageID = 1;
            if (later)
            {
                reply.Add(new(IkePayloadType.Eap, [EapCode.Request, 21, 0, 5, EapType.Identity]));
                if (mode == "earlier-timer") reply.Add(IkeBackoffEvidenceTests.Notify(41041, [1, 0xAA]));
                await socket.SendToAsync(IkeCrypto.EncryptPayloads(Header(1, IkeExchangeType.IkeAuth), reply, suite, keys.SkEr, keys.SkAr), SocketFlags.None, peer, ct);
                packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, peer, ct);
                var (eap, items) = IkeCrypto.DecryptPayloads(buffer.AsSpan(0, packet.ReceivedBytes), suite, keys.SkEi, keys.SkAi);
                Assert.Equal(2u, eap.MessageId); Assert.Contains(items, p => p.Type == IkePayloadType.Eap);
                messageID = 2; reply.Clear();
            }
            reply.Add(IkeBackoffEvidenceTests.Notify(mode == "apn" ? (ushort)9002 : mode == "plmn" ? (ushort)9003 : (ushort)10500));
            if (mode is not ("missing" or "earlier-timer"))
                reply.Add(IkeBackoffEvidenceTests.Notify(41041, mode == "bare" ? [0xAA] : [1, mode == "deactivated" ? (byte)0xE0 : mode == "zero" ? (byte)0 : (byte)0xAA]));
            if (mode == "duplicate-timer") reply.Add(IkeBackoffEvidenceTests.Notify(41041, [1, 0]));
            if (mode == "duplicate-error") reply.Add(IkeBackoffEvidenceTests.Notify(9002));
            await socket.SendToAsync(IkeCrypto.EncryptPayloads(Header(messageID, IkeExchangeType.IkeAuth), reply, suite, keys.SkEr, keys.SkAr), SocketFlags.None, peer, ct);
            packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, peer, ct);
            var (deleted, payloads) = IkeCrypto.DecryptPayloads(buffer.AsSpan(0, packet.ReceivedBytes), suite, keys.SkEi, keys.SkAi);
            return deleted.Exchange == IkeExchangeType.Informational && deleted.MessageId == messageID + 1 &&
                payloads.Single().Type == IkePayloadType.Delete && payloads.Single().Body.SequenceEqual(new byte[] { 1, 0, 0, 0 });
        }
        finally
        {
            foreach (var secret in new[] { keys.SkD, keys.SkAi, keys.SkAr, keys.SkEi, keys.SkEr, keys.SkPi, keys.SkPr }) CryptographicOperations.ZeroMemory(secret);
        }
    }
}
