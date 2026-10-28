using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VoSharp.Common.Aka;

namespace VoSharp.Ike.Tests;

public class IkeResponderAuthProofTests
{
    private sealed class Sim : IAkaProvider
    {
        public int Calls;
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("This identity-only test must not consume a SIM challenge."); }
    }

    [Theory]
    [InlineData("eap-only", true)]
    [InlineData("malformed-certificate", false)]
    [InlineData("unverifiable-auth", false)]
    public async Task ActualInitialResponderAuthMustNotBecomeAnIdentityRetry(string mode, bool retry)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Serve(socket, mode, deadline.Token);
        var sim = new Sim();
        var result = await IkeSession.EstablishAsync(new(IPAddress.Loopback, sim, "999990123456789", "999", "99",
            EpdgPort: ((IPEndPoint)socket.LocalEndPoint!).Port,
            Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 }), deadline.Token);
        var deleted = await server;
        Assert.False(result.Success);
        Assert.Equal(0, sim.Calls);
        Assert.True(deleted, "Failure must still send an encrypted IKE DELETE.");
        Assert.True(result.FailureCleanupComplete);
        Assert.Equal(retry, result.RetryableIdentityRejection);
        Assert.False(result.FinalAuthVerified);
    }

    [Theory]
    [InlineData("signed-rsa", true)]
    [InlineData("signed-generic", true)]
    [InlineData("signed-pss", true)]
    [InlineData("signed-bad", false)]
    [InlineData("signed-wrong-name", false)]
    public async Task ActualSignedHandshakePreservesBothTrustAndCleanup(string mode, bool valid)
    {
        using var certificates = new ResponderCertificates();
        using var key = RSA.Create(2048);
        using var certificate = certificates.Leaf(key);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Serve(socket, mode, deadline.Token, key, certificate.RawData);
        var sim = new Sim();
        var result = await IkeSession.EstablishAsync(new(IPAddress.Loopback, sim, "999990123456789", "999", "99",
            EpdgPort: ((IPEndPoint)socket.LocalEndPoint!).Port,
            Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 },
            ResponderAuthentication: new(mode == "signed-wrong-name" ? "other.example" : "epdg.example", [certificates.Root.RawData])), deadline.Token);
        Assert.True(await server);
        Assert.False(result.Success); Assert.Equal(0, sim.Calls);
        Assert.Equal(valid, result.InitialResponderAuthVerified);
        Assert.Equal(valid, result.RetryableIdentityRejection);
        Assert.True(result.FailureCleanupComplete); Assert.False(result.FinalAuthVerified);
    }

    private static async Task<bool> Serve(Socket socket, string mode, CancellationToken ct, RSA? signingKey = null, byte[]? certificate = null)
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
        var generic = mode is "signed-generic" or "signed-pss";
        if (generic) initial.Payloads.Add(new(IkePayloadType.Notify, [0, 0, 0x40, 0x2f, 0, 2]));
        var initResponseBytes = IkeWire.SerializeMessage(initial);
        await socket.SendToAsync(initResponseBytes, SocketFlags.None, peer, ct);
        var packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
        var (authHeader, _) = IkeCrypto.DecryptPayloads(buffer.AsSpan(0, packet.ReceivedBytes), suite, keys.SkEi, keys.SkAi);
        Assert.Equal(IkeExchangeType.IkeAuth, authHeader.Exchange);
        Assert.Equal(1u, authHeader.MessageId);
        var reply = new List<IkePayload>
        {
            new(IkePayloadType.IdentificationResponder, [2, 0, 0, 0, (byte)'i', (byte)'m', (byte)'s']),
            new(IkePayloadType.Eap, [EapCode.Failure, 21, 0, 4])
        };
        if (signingKey != null)
        {
            var idHash = suite.PrfId == IkePrfId.HmacSha1 ? HMACSHA1.HashData(keys.SkPr, reply[0].Body) : HMACSHA256.HashData(keys.SkPr, reply[0].Body);
            byte[] signed = [.. initResponseBytes, .. ni, .. idHash];
            var signature = signingKey.SignData(signed, generic ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1,
                mode == "signed-pss" ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
            if (generic)
            {
                var algorithm = IkeResponderAuthenticationTests.GenericAlgorithm(mode == "signed-pss" ? "1.2.840.113549.1.1.10" : "1.2.840.113549.1.1.11", mode == "signed-pss");
                signature = [(byte)algorithm.Length, .. algorithm, .. signature];
            }
            if (mode == "signed-bad") signature[^1] ^= 1;
            reply.Add(new(IkePayloadType.Authentication, [generic ? (byte)14 : (byte)1, 0, 0, 0, .. signature]));
            reply.Add(new(IkePayloadType.Certificate, [4, .. certificate!]));
        }
        else if (mode != "eap-only") reply.Add(new(IkePayloadType.Authentication, [1, 0, 0, 0, 0x99]));
        if (mode == "malformed-certificate") reply.Add(new(IkePayloadType.Certificate, [4, 0]));
        await socket.SendToAsync(IkeCrypto.EncryptPayloads(Header(1, IkeExchangeType.IkeAuth, IkeFlags.Response), reply, suite, keys.SkEr, keys.SkAr), SocketFlags.None, peer, ct);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cleanup.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cleanup.Token);
            var (header, payloads) = IkeCrypto.DecryptPayloads(buffer.AsSpan(0, packet.ReceivedBytes), suite, keys.SkEi, keys.SkAi);
            return header.Exchange == IkeExchangeType.Informational && header.MessageId == 2 &&
                payloads.Single().Type == IkePayloadType.Delete && payloads.Single().Body.SequenceEqual(new byte[] { 1, 0, 0, 0 });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        finally
        {
            foreach (var key in new[] { keys.SkD, keys.SkAi, keys.SkAr, keys.SkEi, keys.SkEr, keys.SkPi, keys.SkPr })
                CryptographicOperations.ZeroMemory(key);
        }
    }
}
