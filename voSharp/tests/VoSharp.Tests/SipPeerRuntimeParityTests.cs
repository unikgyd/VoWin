using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;

namespace VoSharp.Tests;

public sealed partial class SipServerFeatureTests
{
    private static SipGatewayPeer RuntimePeer(UdpClient peer, bool sms = true) => new(End(peer), "pbx-inbox",
        new SipPeerInboundCredentials("alice", "different-peer-in-secret"),
        new SipPeerOutboundCredentials("gateway-out", "different-peer-out-secret", "pbx.test"))
        { AllowOutboundCalls = true, AllowOutboundMessages = sms };

    private static async Task PeerAuthenticate(UdpClient peer, SipMessage request, SipGatewayOptions options,
        IPEndPoint gateway, string? username = null, string? password = null)
    {
        request.Headers.Remove("Authorization"); request.Headers.Remove("Proxy-Authorization");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{End(peer).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await Send(peer, request, gateway);
        var challenge = await Receive(peer);
        Assert.Equal(407, challenge.StatusCode);
        Assert.Contains("algorithm=SHA-256", challenge.GetHeader("Proxy-Authenticate"));
        var client = new SipGatewayPeer(gateway, "unused",
            new SipPeerInboundCredentials("unused", "unused-test-secret"),
            new SipPeerOutboundCredentials(username ?? options.Peer!.InboundAuth!.Username,
                password ?? options.Peer!.InboundAuth!.Password, options.Realm));
        var proof = SipPeerDigest.Create(challenge.GetHeader("Proxy-Authenticate")!, request, client);
        Assert.NotNull(proof);
        request.SetHeader("Proxy-Authorization", proof);
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{End(peer).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await Send(peer, request, gateway);
    }

    [Fact]
    public async Task PeerCoexistsWithRegistrarAndLocalMessagesWithoutInheritingPeerRestrictions()
    {
        using var peer = Socket(); using var alice = Socket(); using var bob = Socket();
        var options = Options() with { Peer = RuntimePeer(peer, sms: false) };
        var controller = new Controller();
        await using var gateway = new SipGateway(options, controller); await gateway.StartAsync();
        var endpoint = gateway.Status.LocalEndPoint!;
        await Register(bob, "bob", endpoint);
        await Authenticate(alice, Text("bob"), "alice", endpoint);
        var forwarded = await Receive(bob);
        Assert.Contains("sip:alice@", forwarded.GetHeader("From"));
        await Send(bob, forwarded.CreateResponse(200, "OK"), endpoint);
        Assert.Equal(200, (await Receive(alice)).StatusCode);
        await PeerAuthenticate(peer, Text("bob"), options, endpoint);
        Assert.Equal(403, (await Receive(peer)).StatusCode); // No peer-to-local reflection.
        await PeerAuthenticate(peer, Text("+15551234567"), options, endpoint);
        Assert.Equal(403, (await Receive(peer)).StatusCode);
        Assert.Equal(0, controller.SmsCount);
    }

    [Theory]
    [InlineData("local", 407)]
    [InlineData("outbound", 407)]
    [InlineData("inbound", 202)]
    public async Task ExactPeerSourceAcceptsOnlyItsIndependentInboundCredential(string role, int expected)
    {
        using var peer = Socket(); var controller = new Controller();
        var options = Options() with { Peer = RuntimePeer(peer) };
        // A same-named local account's ACL must not become the peer ACL.
        options.ExtensionPolicies["alice"] = new() { AllowSms = false, AllowCalls = false };
        await using var gateway = new SipGateway(options, controller); await gateway.StartAsync();
        var username = role == "outbound" ? "gateway-out" : "alice";
        var password = role == "local" ? "test-alice" : role == "outbound" ? "different-peer-out-secret" : "different-peer-in-secret";
        await PeerAuthenticate(peer, Text("+15551234567"), options, gateway.Status.LocalEndPoint!, username, password);
        Assert.Equal(expected, (await Receive(peer)).StatusCode);
        Assert.Equal(role == "inbound" ? 1 : 0, controller.SmsCount);
    }

    [Fact]
    public async Task SamePaidOperationCannotCrossLocalAndPeerAuthorityBeforeOrAfterRestart()
    {
        using var peer = Socket(); using var alice = Socket(); var journal = new OperationStore();
        var options = Options() with { Peer = RuntimePeer(peer), SmsOperationStore = journal };
        var request = Text("+15551234567"); var firstController = new Controller();
        await using (var first = new SipGateway(options, firstController))
        {
            await first.StartAsync(); await Authenticate(alice, request, "alice", first.Status.LocalEndPoint!);
            Assert.Equal(202, (await Receive(alice)).StatusCode);
            await PeerAuthenticate(peer, request, options, first.Status.LocalEndPoint!);
            Assert.Equal(400, (await Receive(peer)).StatusCode);
        }
        var nextController = new Controller();
        await using var next = new SipGateway(options, nextController); await next.StartAsync();
        await PeerAuthenticate(peer, request, options, next.Status.LocalEndPoint!);
        Assert.Equal(400, (await Receive(peer)).StatusCode);
        Assert.Equal(1, firstController.SmsCount); Assert.Equal(0, nextController.SmsCount);
    }

    [Theory]
    [InlineData(SipSmsOperationOutcome.Unknown)]
    [InlineData(SipSmsOperationOutcome.Submitted)]
    public async Task LegacyPaidFingerprintIsFencedRatherThanReceivingANewOperationId(SipSmsOperationOutcome outcome)
    {
        var journal = new OperationStore(); var options = Options() with { SmsOperationStore = journal };
        var request = Text("+15551234567");
        var id = SipGateway.SmsOperationId(options.Realm, "alice", request.GetHeader("Call-ID")!, 1);
        var oldFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "sip-sms-attempt-v1\0modem-a\0+15551234567\0" + Encoding.UTF8.GetString(request.RawBody!)))).ToLowerInvariant();
        var reserved = await journal.ReserveAsync(id, oldFingerprint, default);
        await journal.CompleteAsync(id, oldFingerprint, reserved.ReservationToken!, outcome, default);
        var controller = new Controller(); await using var gateway = new SipGateway(options, controller); await gateway.StartAsync();
        using var alice = Socket(); await Authenticate(alice, request, "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(400, (await Receive(alice)).StatusCode); Assert.Equal(0, controller.SmsCount);
    }

    [Fact]
    public async Task IncomingSmsUsesSeparateLocalAndPeerTargetsWhenTheyCoexist()
    {
        using var peer = Socket(); using var bob = Socket();
        var options = Options() with { Peer = RuntimePeer(peer) };
        options.LineRoutes["modem-a"] = new() { ForwardIncomingSms = true, SmsTarget = "pbx-specific", LocalSmsTarget = "bob" };
        var controller = new Controller(); await using var gateway = new SipGateway(options, controller); await gateway.StartAsync();
        await Register(bob, "bob", gateway.Status.LocalEndPoint!);
        controller.Receive(new SmsMessage(1, SmsStatus.Unread, "+15550001111", "synthetic coexist SMS",
            DateTime.UtcNow, Id: "coexist-only-test"), "modem-a");
        var local = await Receive(bob); var remote = await Receive(peer);
        Assert.Contains("sip:bob@", local.RequestUri); Assert.Contains("sip:pbx-specific@", remote.RequestUri);
        Assert.Equal("synthetic coexist SMS", local.Body); Assert.Equal(local.Body, remote.Body);
        await Send(bob, local.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await Send(peer, remote.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        Assert.Equal(0, controller.SmsCount);
    }

    private static Task<bool> ForwardForBudget(SipGateway gateway, SipGatewayRegistration binding, string text) =>
        (Task<bool>)typeof(SipGateway).GetMethod("SendTextToBindingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, [binding, "synthetic", text, null, CancellationToken.None])!;

    private static async Task<SipMessage> ReceiveUntil(UdpClient socket, Func<SipMessage, bool> match)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (true)
        {
            var message = SipMessage.Parse((await socket.ReceiveAsync(deadline.Token)).Buffer);
            if (match(message)) return message;
        }
    }

    [Fact]
    public async Task MessageAuthenticationRetryDoesNotRenewTheLogicalDeadline()
    {
        using var peer = Socket(); var options = Options() with { Peer = RuntimePeer(peer) };
        await using var gateway = new SipGateway(options, new Controller()) { MessageTransactionTimeout = TimeSpan.FromMilliseconds(1600) };
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        var binding = new SipGatewayRegistration("pbx-inbox", $"<sip:pbx-inbox@127.0.0.1:{End(peer).Port}>", End(peer), DateTimeOffset.MaxValue, "synthetic");
        var forwarding = ForwardForBudget(gateway, binding, "synthetic timeout");
        var first = await Receive(peer); await Task.Delay(900);
        var challenge = first.CreateResponse(407, "Proxy Authentication Required");
        challenge.SetHeader("Proxy-Authenticate", "Digest realm=\"pbx.test\", nonce=\"test\", algorithm=SHA-256, qop=auth");
        await Send(peer, challenge, endpoint);
        var retry = await ReceiveUntil(peer, message => message.GetHeader("CSeq") == "2 MESSAGE");
        Assert.NotNull(retry.GetHeader("Proxy-Authorization"));
        await Task.Delay(1000); // After original deadline, before a renewed one.
        await Send(peer, retry.CreateResponse(200, "OK"), endpoint);
        Assert.False(await forwarding.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task AllMessageChunksShareOneDeadlineInsteadOfOnePerChunk()
    {
        using var phone = Socket(); await using var gateway = new SipGateway(Options(), new Controller())
            { MessageTransactionTimeout = TimeSpan.FromMilliseconds(1600) };
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        var binding = new SipGatewayRegistration("bob", $"<sip:bob@127.0.0.1:{End(phone).Port}>", End(phone), DateTimeOffset.MaxValue, "synthetic");
        var forwarding = ForwardForBudget(gateway, binding, new string('x', 400));
        var first = await Receive(phone); await Task.Delay(900);
        await Send(phone, first.CreateResponse(200, "OK"), endpoint);
        var second = await ReceiveUntil(phone, message => message.GetHeader("Call-ID") != first.GetHeader("Call-ID"));
        Assert.StartsWith("[2/2]", second.Body);
        await Task.Delay(1000);
        await Send(phone, second.CreateResponse(200, "OK"), endpoint);
        Assert.False(await forwarding.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task InviteAuthenticationRetryRetainsOriginalDeadlineAndLateSuccessIsCleaned()
    {
        using var peer = Socket(); using var rtp = Socket(); var options = Options() with { Peer = RuntimePeer(peer) };
        await using var gateway = new SipGateway(options, new Controller()) { InviteTransactionTimeout = TimeSpan.FromMilliseconds(1600) };
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        var binding = new SipGatewayRegistration("pbx-inbox", $"<sip:pbx-inbox@127.0.0.1:{End(peer).Port}>", End(peer), DateTimeOffset.MaxValue, "synthetic");
        var branch = (Task)typeof(SipGateway).GetMethod("InviteBindingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, [binding, new IncomingCallEventArgs("fake-carrier", "15550001111", slotId: "modem-a"), CancellationToken.None])!;
        var first = await Receive(peer); await Task.Delay(900);
        var challenge = first.CreateResponse(401, "Unauthorized");
        challenge.SetHeader("To", first.GetHeader("To") + ";tag=challenge");
        challenge.SetHeader("WWW-Authenticate", "Digest realm=\"pbx.test\", nonce=\"test\", algorithm=SHA-256, qop=auth");
        await Send(peer, challenge, endpoint);
        var retry = await ReceiveUntil(peer, message => message.Method == "INVITE" && message.GetHeader("CSeq") == "2 INVITE");
        await Task.Delay(1000);
        var late = retry.CreateResponse(200, "OK"); late.SetHeader("To", retry.GetHeader("To") + ";tag=late-success");
        late.SetHeader("Contact", $"<sip:pbx-inbox@127.0.0.1:{End(peer).Port}>");
        late.SetHeader("Content-Type", "application/sdp");
        late.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {End(rtp).Port} RTP/AVP 8\r\n";
        await Send(peer, late, endpoint); await branch.WaitAsync(TimeSpan.FromSeconds(3));
        var result = branch.GetType().GetProperty("Result")!.GetValue(branch);
        (result as IDisposable)?.Dispose();
        Assert.Null(result);
        var bye = await ReceiveUntil(peer, message => message.Method == "BYE");
        Assert.Equal("3 BYE", bye.GetHeader("CSeq"));
        await Send(peer, bye.CreateResponse(200, "OK"), endpoint);
    }
}
