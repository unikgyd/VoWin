using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

[Collection("SIP loopback sockets")]
public class SipGatewayPeerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static UdpClient Socket() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static IPEndPoint Endpoint(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;
    private static int FreePort() { using var socket = Socket(); return Endpoint(socket).Port; }
    private static SipGatewayOptions Options(UdpClient peer, bool outbound = false) => new()
    {
        BindAddress = IPAddress.Loopback, SipPort = FreePort(), Realm = "vowin.local",
        Accounts = new(),
        Peer = new(Endpoint(peer), "600",
            new SipPeerInboundCredentials("hostims", "independent-inbound-test-secret"),
            new SipPeerOutboundCredentials("hostims", "private-test-secret", "asterisk")
                { AllowedAlgorithms = ["SHA-256", "MD5"] }) { AllowOutboundCalls = outbound }
    };
    private static SipGatewayPeer InboundProofPeer(SipGatewayOptions options) => options.Peer! with
    {
        OutboundAuth = new(options.Peer!.InboundAuth!.Username, options.Peer.InboundAuth.Password, options.Realm)
            { AllowedAlgorithms = [options.Peer.InboundAuth.Algorithm] }
    };
    private static async Task<SipMessage> Receive(UdpClient socket) => SipMessage.Parse((await socket.ReceiveAsync().WaitAsync(Timeout)).Buffer);
    private static Task Send(UdpClient socket, SipMessage message, IPEndPoint destination) => socket.SendAsync(message.ToBytes(), destination).AsTask();
    private static async Task Eventually(Func<bool> check)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!check()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public void PeerRequiresPrivatePinnedEndpointAndIndependentExplicitCredentials()
    {
        using var peer = Socket();
        var options = Options(peer);
        options.Validate();
        Assert.Empty(options.Accounts);
        (options with { Accounts = new() { ["local"] = "unrelated-local-secret", ["hostims"] = "different-local-secret" } }).Validate();
        Assert.False(options.Peer!.AllowOutboundCalls);
        Assert.Throws<ArgumentException>(() => (options with { Peer = options.Peer with { EndPoint = new(IPAddress.Parse("8.8.8.8"), 5060) } }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { Peer = options.Peer with { IncomingTarget = "600\r\nX: bad" } }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { Peer = options.Peer with { Password = "mismatch" } }).Validate());
        Assert.DoesNotContain("private-test-secret", options.Peer.ToString());
    }

    [Theory]
    [InlineData(401, 8, false)]
    [InlineData(407, 0, false)]
    [InlineData(401, 8, true)]
    [InlineData(407, 0, true)]
    public async Task FixedPeerDigestThenAnswerBridgesBothG711DirectionsAndHangsUp(int challengeStatus, byte payload, bool hostOnlyContact)
    {
        using var peer = Socket(); using var rtp = Socket();
        var controller = new Controller(); var options = Options(peer);
        await using var gateway = new SipGateway(options, controller);
        var diagnostics = new List<string>(); gateway.Diagnostic += (_, text) => diagnostics.Add(text);
        await gateway.StartAsync();
        Assert.Empty(gateway.Status.Registrations);
        Assert.True(gateway.RoutesIncomingCalls);
        var destination = gateway.Status.LocalEndPoint!;
        controller.Incoming();
        var invite = await Receive(peer);
        Assert.Equal("INVITE", invite.Method);
        Assert.Equal("slot-1", invite.GetHeader("X-VoWin-Line"));
        Assert.Equal($"sip:600@127.0.0.1:{Endpoint(peer).Port}", invite.RequestUri);
        Assert.Null(invite.GetHeader("Authorization"));
        var ringing = invite.CreateResponse(180, "Ringing");
        ringing.SetHeader("To", invite.GetHeader("To") + ";tag=early");
        await Send(peer, ringing, destination);
        Assert.False(controller.Answered.Task.IsCompleted); // No early carrier pickup / billing.

        var outbound = options.Peer!.OutboundAuth!;
        var auth = new SipDigestAuthenticator(outbound.ExpectedRealm,
            new Dictionary<string, string> { [outbound.Username] = outbound.Password });
        var challenge = invite.CreateResponse(challengeStatus, "Authentication Required");
        challenge.SetHeader("To", invite.GetHeader("To") + ";tag=challenge");
        challenge.SetHeader(challengeStatus == 401 ? "WWW-Authenticate" : "Proxy-Authenticate", auth.CreateChallenge());
        await Send(peer, challenge, destination);
        var challengeAck = await Receive(peer);
        Assert.Equal("1 ACK", challengeAck.GetHeader("CSeq"));
        Assert.Equal(invite.GetHeader("Via"), challengeAck.GetHeader("Via"));
        var authenticated = await Receive(peer);
        Assert.Equal("slot-1", authenticated.GetHeader("X-VoWin-Line"));
        Assert.Equal("2 INVITE", authenticated.GetHeader("CSeq"));
        Assert.Equal(invite.GetHeader("Call-ID"), authenticated.GetHeader("Call-ID"));
        Assert.Equal(invite.GetHeader("To"), authenticated.GetHeader("To"));
        Assert.NotEqual(invite.GetHeader("Via"), authenticated.GetHeader("Via"));
        Assert.True(auth.Validate(authenticated, out var user)); Assert.Equal("hostims", user);
        Assert.False(controller.Answered.Task.IsCompleted);
        var accepted = Answer(authenticated, peer, rtp, payload);
        var remoteTarget = $"sip:{(hostOnlyContact ? string.Empty : "600@")}127.0.0.1:{Endpoint(peer).Port}";
        accepted.SetHeader("Contact", $"<{remoteTarget}>");
        await Send(peer, accepted, destination);
        var ack = await Receive(peer);
        Assert.Equal("2 ACK", ack.GetHeader("CSeq"));
        Assert.Equal(remoteTarget, ack.RequestUri);
        await controller.Answered.Task.WaitAsync(Timeout);
        Assert.False(controller.Media.HasBridge); // Carrier ACK/media readiness is still pending.
        controller.AnswerRelease.SetResult();
        await Eventually(() => controller.Media.HasBridge);
        Assert.Equal(1, gateway.Status.ActiveDialogs);

        // Repeated 2xx still gets ACK without answering the carrier a second time.
        await Send(peer, accepted, destination);
        Assert.Equal("2 ACK", (await Receive(peer)).GetHeader("CSeq"));
        Assert.Equal(1, controller.AnswerCount);
        var pcm = Enumerable.Repeat((short)2000, 160).ToArray();
        var expected = payload == 8 ? G711Codec.EncodeAlaw(pcm) : G711Codec.EncodeUlaw(pcm);
        controller.Media.Emit(pcm);
        UdpReceiveResult audio;
        using (var timeout = new CancellationTokenSource(Timeout))
        {
            do { audio = await rtp.ReceiveAsync(timeout.Token); }
            while (!audio.Buffer.AsSpan(12).SequenceEqual(expected));
        }
        Assert.Equal(payload, audio.Buffer[1] & 0x7f);
        var packet = new byte[172]; packet[0] = 0x80; packet[1] = payload;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 160);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 123);
        expected.CopyTo(packet, 12);
        await rtp.SendAsync(packet, audio.RemoteEndPoint);
        var decoded = await controller.Media.Received.Task.WaitAsync(Timeout);
        Assert.Equal(160, decoded.Length); Assert.All(decoded, sample => Assert.InRange(sample, 1800, 2200));

        controller.End();
        var bye = await Receive(peer); Assert.Equal("3 BYE", bye.GetHeader("CSeq"));
        Assert.Equal(0, gateway.Status.ActiveDialogs); // Never wait for peer digest/BYE cleanup to become idle.
        Assert.Equal(remoteTarget, bye.RequestUri);
        var byeChallenge = bye.CreateResponse(challengeStatus, "Authentication Required");
        byeChallenge.SetHeader(challengeStatus == 401 ? "WWW-Authenticate" : "Proxy-Authenticate", auth.CreateChallenge());
        await Send(peer, byeChallenge, destination);
        var authenticatedBye = await Receive(peer);
        Assert.Equal("4 BYE", authenticatedBye.GetHeader("CSeq"));
        Assert.NotEqual(bye.GetHeader("Via"), authenticatedBye.GetHeader("Via"));
        Assert.Equal(remoteTarget, authenticatedBye.RequestUri);
        Assert.Equal(bye.GetHeader("From"), authenticatedBye.GetHeader("From"));
        Assert.Equal(bye.GetHeader("To"), authenticatedBye.GetHeader("To"));
        Assert.True(auth.Validate(authenticatedBye, out user)); Assert.Equal("hostims", user);
        var finalBye = authenticatedBye.CreateResponse(hostOnlyContact ? challengeStatus : 200, hostOnlyContact ? "Authentication Required" : "OK");
        if (hostOnlyContact) finalBye.SetHeader(challengeStatus == 401 ? "WWW-Authenticate" : "Proxy-Authenticate", auth.CreateChallenge());
        await Send(peer, finalBye, destination);
        await Eventually(() => gateway.Status.ActiveDialogs == 0);
        if (hostOnlyContact)
        {
            using var noFurtherRetry = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await peer.ReceiveAsync(noFurtherRetry.Token));
        }
        Assert.DoesNotContain(diagnostics, item => item.Contains("private-test-secret"));
    }

    [Fact]
    public async Task CarrierCancelCancelsRingingPeerAndCleansLateAnswerWithoutPickup()
    {
        using var peer = Socket(); using var rtp = Socket();
        var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer), controller);
        await gateway.StartAsync(); var destination = gateway.Status.LocalEndPoint!;
        controller.Incoming(); var invite = await Receive(peer);
        // An event from another SIM with the same Call-ID must not cancel this fork.
        controller.End("other-slot");
        var ringing = invite.CreateResponse(180, "Ringing");
        await Send(peer, ringing, destination);
        controller.End();
        var cancel = await Receive(peer); Assert.Equal("CANCEL", cancel.Method);
        await Send(peer, Answer(invite, peer, rtp, 8), destination);
        Assert.Equal("ACK", (await Receive(peer)).Method);
        var bye = await Receive(peer); Assert.Equal("BYE", bye.Method);
        await Send(peer, bye.CreateResponse(200, "OK"), destination);
        Assert.False(controller.Answered.Task.IsCompleted);
        Assert.Equal(0, gateway.Status.ActiveDialogs);
    }

    [Fact]
    public async Task DesktopPickupCancelsSipAndCleansLateAnswerWithoutReleasingLocalCarrierCall()
    {
        using var peer = Socket(); using var rtp = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer) with { LocalIncomingEnabled = true }, controller);
        var pickups = new List<SipIncomingCallClaimed>(); gateway.IncomingCallClaimed += (_, e) => pickups.Add(e);
        await gateway.StartAsync(); controller.Incoming();
        var invite = await Receive(peer);
        await Send(peer, invite.CreateResponse(180, "Ringing"), gateway.Status.LocalEndPoint!);
        Assert.False(gateway.TryClaimLocalIncomingCall("carrier", "other-slot"));
        Assert.False(gateway.TryClaimLocalIncomingCall("another-call", "slot-1"));
        Assert.True(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        Assert.False(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        Assert.Equal("CANCEL", (await Receive(peer)).Method);
        await Send(peer, Answer(invite, peer, rtp, 8), gateway.Status.LocalEndPoint!);
        Assert.Equal("ACK", (await Receive(peer)).Method);
        var bye = await Receive(peer); Assert.Equal("BYE", bye.Method);
        await Send(peer, bye.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        controller.Incoming(); // Duplicate delivery must not create a second fork after local pickup.
        Assert.False(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        Assert.True(Assert.Single(pickups).IsLocal);
        Assert.Equal(0, controller.AnswerCount); Assert.Equal(0, controller.HangupCount);
        Assert.False(controller.Media.HasBridge);
        controller.End(); await Eventually(() => !gateway.HasBridgedCalls);
        await gateway.DisposeAsync(); Assert.Equal(0, controller.HangupCount);
    }

    [Fact]
    public async Task SipPickupAtomicallyPreventsDesktopPickupBeforeCarrierAck()
    {
        using var peer = Socket(); using var rtp = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer) with { LocalIncomingEnabled = true }, controller);
        var pickup = new TaskCompletionSource<SipIncomingCallClaimed>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.IncomingCallClaimed += (_, e) => pickup.TrySetResult(e);
        await gateway.StartAsync(); controller.Incoming();
        var invite = await Receive(peer);
        await Send(peer, Answer(invite, peer, rtp, 8), gateway.Status.LocalEndPoint!);
        Assert.False((await pickup.Task.WaitAsync(Timeout)).IsLocal);
        Assert.False(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        Assert.Equal("ACK", (await Receive(peer)).Method);
        await controller.Answered.Task.WaitAsync(Timeout);
        Assert.Equal(1, controller.AnswerCount);
        Assert.False(controller.Media.HasBridge);
        controller.AnswerRelease.TrySetResult();
        await Eventually(() => controller.Media.HasBridge);
        controller.End();
        var bye = await Receive(peer); await Send(peer, bye.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await Eventually(() => !gateway.HasBridgedCalls);
    }

    [Fact]
    public async Task UnavailableSipPhonesDoNotRejectDesktopParticipantAndFailedLocalPickupCanRetry()
    {
        using var peer = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer) with { LocalIncomingEnabled = true }, controller);
        await gateway.StartAsync(); controller.Incoming();
        var invite = await Receive(peer);
        await Send(peer, invite.CreateResponse(480, "Temporarily Unavailable"), gateway.Status.LocalEndPoint!);
        Assert.Equal("ACK", (await Receive(peer)).Method);
        Assert.Equal(0, controller.HangupCount);
        Assert.True(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        gateway.ReleaseLocalIncomingCallClaim("carrier", "other-slot");
        Assert.False(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        gateway.ReleaseLocalIncomingCallClaim("carrier", "slot-1");
        Assert.True(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        controller.End(); await Eventually(() => !gateway.HasBridgedCalls);
        Assert.False(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        Assert.Equal(0, controller.HangupCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClosingSipDoesNotReleaseTheRingingOrLocallyAcceptedDesktopCall(bool localPickedUp)
    {
        using var peer = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer) with { LocalIncomingEnabled = true }, controller);
        await gateway.StartAsync(); controller.Incoming();
        Assert.Equal("INVITE", (await Receive(peer)).Method);
        if (localPickedUp) Assert.True(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        var stopping = gateway.DisposeAsync().AsTask();
        Assert.Equal("CANCEL", (await Receive(peer)).Method);
        await stopping.WaitAsync(Timeout);
        Assert.Equal(0, controller.HangupCount);
        Assert.Equal(0, controller.AnswerCount);
        Assert.False(gateway.HasBridgedCalls);
    }

    [Fact]
    public async Task DesktopRejectLocksOutBothPickupsAndCancelsSipWithoutASecondCarrierAnswer()
    {
        using var peer = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer) with { LocalIncomingEnabled = true }, controller);
        await gateway.StartAsync(); controller.Incoming();
        Assert.Equal("INVITE", (await Receive(peer)).Method);
        gateway.CancelIncomingCallRinging("carrier", "slot-1");
        Assert.False(gateway.TryClaimLocalIncomingCall("carrier", "slot-1"));
        Assert.Equal("CANCEL", (await Receive(peer)).Method);
        controller.End(); // The desktop's carrier Hangup/Reject completes, not a SIP pickup.
        await Eventually(() => !gateway.HasBridgedCalls);
        Assert.Equal(0, controller.AnswerCount);
        Assert.Equal(0, controller.HangupCount);
    }

    [Fact]
    public async Task PeerByeWhileCarrierAnswerIsPendingReleasesCallWithoutAttachingMedia()
    {
        using var peer = Socket(); using var rtp = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer), controller);
        await gateway.StartAsync(); var destination = gateway.Status.LocalEndPoint!;
        controller.Incoming(); var invite = await Receive(peer);
        var accepted = Answer(invite, peer, rtp, 8);
        await Send(peer, accepted, destination); Assert.Equal("ACK", (await Receive(peer)).Method);
        await controller.Answered.Task.WaitAsync(Timeout);
        var bye = Request("BYE");
        bye.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        bye.SetHeader("From", accepted.GetHeader("To")!); bye.SetHeader("To", invite.GetHeader("From")!);
        await Send(peer, bye, destination); Assert.Equal(200, (await Receive(peer)).StatusCode);
        await Eventually(() => !gateway.HasBridgedCalls);
        Assert.Equal(1, controller.HangupCount);
        Assert.False(controller.Media.HasBridge);
    }

    [Fact]
    public async Task DisablingAnActiveBridgeSendsByeReleasesCarrierAndClosesSocket()
    {
        using var peer = Socket(); using var rtp = Socket(); var controller = new Controller();
        controller.AnswerRelease.SetResult();
        await using var gateway = new SipGateway(Options(peer), controller);
        await gateway.StartAsync(); var destination = gateway.Status.LocalEndPoint!;
        Task? stopping = null;
        gateway.StatusChanged += (_, _) =>
        {
            // Stop synchronously at media promotion, before the fork's finally
            // removes it. This deterministically exercises the overlapping owners.
            if (controller.Media.HasBridge && stopping == null)
                stopping = gateway.DisposeAsync().AsTask();
        };
        controller.Incoming(); var invite = await Receive(peer);
        var accepted = Answer(invite, peer, rtp, 8);
        await Send(peer, accepted, destination); Assert.Equal("ACK", (await Receive(peer)).Method);
        await Eventually(() => stopping != null);
        var bye = await Receive(peer); Assert.Equal("BYE", bye.Method);
        Assert.Equal(invite.GetHeader("From"), bye.GetHeader("From"));
        Assert.Equal(accepted.GetHeader("To"), bye.GetHeader("To"));
        await Send(peer, bye.CreateResponse(200, "OK"), destination); await stopping!.WaitAsync(Timeout);
        Assert.Equal(1, controller.HangupCount);
        Assert.False(controller.Media.HasBridge);
        Assert.False(gateway.Status.IsRunning);
        using var rebound = new UdpClient(destination);
    }

    [Fact]
    public async Task StatusPollingDoesNotReadTheSocketDuringItsDisposalWindow()
    {
        using var peer = Socket();
        await using var gateway = new SipGateway(Options(peer), new Controller());
        await gateway.StartAsync();
        var endpoint = gateway.Status.LocalEndPoint;
        // Reproduce the interval after socket disposal but before reference cleanup.
        var socket = (UdpClient)typeof(SipGateway).GetField("_udp",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(gateway)!;
        socket.Dispose();
        Assert.Equal(endpoint, gateway.Status.LocalEndPoint);
        await gateway.DisposeAsync();
        Assert.False(gateway.Status.IsRunning);
        Assert.Null(gateway.Status.LocalEndPoint);
    }

    [Fact]
    public async Task DisablingRingingBridgeCancelsForkAndReleasesOriginalCarrierCall()
    {
        using var peer = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer), controller);
        await gateway.StartAsync(); controller.Incoming();
        Assert.Equal("INVITE", (await Receive(peer)).Method);
        var stopping = gateway.DisposeAsync().AsTask();
        Assert.Equal("CANCEL", (await Receive(peer)).Method);
        await stopping.WaitAsync(Timeout);
        Assert.Equal(1, controller.HangupCount);
        Assert.False(controller.Answered.Task.IsCompleted);
        Assert.False(gateway.HasBridgedCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task PeerReverseCallingDeniedUnlessExplicitlyEnabledAndSourceMatches(bool enabled, bool wrongSource)
    {
        using var peer = Socket(); using var stranger = Socket();
        var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer, enabled), controller);
        await gateway.StartAsync();
        var request = Request("INVITE");
        var sender = wrongSource ? stranger : peer;
        await Send(sender, request, gateway.Status.LocalEndPoint!);
        Assert.Equal(403, (await Receive(sender)).StatusCode);
        Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public async Task AllowedPeerStillRequiresDigestAndDoesNotAcceptRegistrations()
    {
        using var peer = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer, true), controller);
        await gateway.StartAsync();
        await Send(peer, Request("INVITE"), gateway.Status.LocalEndPoint!);
        Assert.Equal(407, (await Receive(peer)).StatusCode);
        await Send(peer, Request("REGISTER"), gateway.Status.LocalEndPoint!);
        Assert.Equal(403, (await Receive(peer)).StatusCode);
        Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public async Task AllowedPeerWithDigestCanReachCarrierController()
    {
        using var peer = Socket(); using var rtp = Socket(); var controller = new Controller();
        var options = Options(peer, true);
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync();
        var request = Request("INVITE");
        request.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {Endpoint(rtp).Port} RTP/AVP 8\r\n";
        request.SetHeader("Content-Type", "application/sdp");
        await Send(peer, request, gateway.Status.LocalEndPoint!);
        var challenge = await Receive(peer); Assert.Equal(407, challenge.StatusCode);
        request.SetHeader("Proxy-Authorization", SipPeerDigest.Create(challenge.GetHeader("Proxy-Authenticate")!, request, InboundProofPeer(options))!);
        request.SetHeader("CSeq", "2 INVITE");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bK{Guid.NewGuid():N}");
        await Send(peer, request, gateway.Status.LocalEndPoint!);
        SipMessage response;
        do { response = await Receive(peer); } while (response.StatusCode < 200);
        Assert.Equal(503, response.StatusCode); // Offline fake carrier, not an authentication rejection.
        Assert.Equal(1, controller.DialCount);
    }

    [Theory]
    [InlineData("SHA-512-256", "auth")]
    [InlineData("MD5", "auth-int")]
    public void UnsupportedChallengeIsNotAnswered(string algorithm, string qop)
    {
        using var socket = Socket();
        Assert.Null(SipPeerDigest.Create($"Digest realm=\"asterisk\", nonce=\"n\", algorithm={algorithm}, qop=\"{qop}\"", Request("INVITE"), Options(socket).Peer!));
    }

    [Theory]
    [InlineData("SHA-256", true)]
    [InlineData("MD5", false)]
    public void PeerDigestMatchesIndependentHashAndPreservesOpaque(string algorithm, bool useQop)
    {
        using var socket = Socket();
        var request = Request("INVITE");
        var challenge = $"Digest realm=\"asterisk\", nonce=\"nonce\", algorithm={algorithm}, opaque=\"opaque\"" + (useQop ? ", qop=\"auth-int,auth\"" : "");
        var header = SipPeerDigest.Create(challenge, request, Options(socket).Peer!);
        Assert.NotNull(header);
        var values = SipDigestAuthenticator.ParseParameters(header![7..]);
        string Hash(string value) => Convert.ToHexString(algorithm == "MD5"
            ? System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value))
            : System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var a1 = Hash("hostims:asterisk:private-test-secret");
        var a2 = Hash($"INVITE:{request.RequestUri}");
        var expected = useQop ? Hash($"{a1}:nonce:00000001:{values["cnonce"]}:auth:{a2}") : Hash($"{a1}:nonce:{a2}");
        Assert.Equal(expected, values["response"]);
        Assert.Equal("opaque", values["opaque"]);
        Assert.DoesNotContain("private-test-secret", header);
    }

    private static SipMessage Request(string method)
    {
        var request = new SipMessage { IsRequest = true, Method = method, RequestUri = "sip:17387799413@vowin.local" };
        request.SetHeader("Call-ID", Guid.NewGuid().ToString()); request.SetHeader("CSeq", $"1 {method}");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bK{Guid.NewGuid():N}");
        request.SetHeader("From", "<sip:hostims@asterisk>;tag=phone"); request.SetHeader("To", "<sip:17387799413@vowin.local>");
        return request;
    }
    [Fact]
    public void SharedRustWindowsPeerDigestContractRejectsAmbiguousOrMalformedChallenges()
    {
        using var contract = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "sip-peer-digest-v1.json")));
        Assert.Equal(1, contract.RootElement.GetProperty("version").GetInt32());
        foreach (var test in contract.RootElement.GetProperty("cases").EnumerateArray())
        {
            var supported = SipPeerDigest.TryParseChallenge(test.GetProperty("challenge").GetString()!, out var parsed);
            Assert.Equal(test.GetProperty("supported").GetBoolean(), supported);
            if (!supported) continue;
            Assert.Equal(test.GetProperty("realm").GetString(), parsed.Realm);
            Assert.Equal(test.GetProperty("nonce").GetString(), parsed.Nonce);
            Assert.Equal(test.GetProperty("algorithm").GetString(), parsed.Algorithm);
            Assert.Equal(test.GetProperty("qop").GetBoolean(), parsed.UseQop);
            if (test.TryGetProperty("opaque", out var opaque)) Assert.Equal(opaque.GetString(), parsed.Opaque);
        }
        Assert.False(SipPeerDigest.TryParseChallenge("Digest realm=\"" + new string('界', 1400) + "\", nonce=\"n\"", out _));
    }
    [Fact]
    public void PeerRejectsAmbiguousMultipleChallengesInsteadOfChoosingAnAlgorithm()
    {
        using var socket = Socket(); var request = Request("INVITE");
        var response = request.CreateResponse(401, "Unauthorized");
        response.AddHeader("WWW-Authenticate", "Digest realm=\"asterisk\", nonce=\"n\", algorithm=MD5, qop=auth");
        response.AddHeader("WWW-Authenticate", "Digest realm=\"asterisk\", nonce=\"n\", algorithm=SHA-256, qop=auth");
        var header = SipPeerDigest.CreatePreferred(response, request, Options(socket).Peer!);
        Assert.Null(header);
    }
    private static SipMessage Answer(SipMessage invite, UdpClient peer, UdpClient rtp, byte payload)
    {
        var response = invite.CreateResponse(200, "OK");
        response.SetHeader("To", invite.GetHeader("To") + ";tag=answered");
        response.SetHeader("Contact", $"<sip:600@127.0.0.1:{Endpoint(peer).Port}>");
        response.SetHeader("Content-Type", "application/sdp");
        response.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {Endpoint(rtp).Port} RTP/AVP {payload}\r\n";
        return response;
    }
    private sealed class Media : ICallPcmMedia
    {
        public event Action<short[]>? RemotePcmReceived;
        public bool HasBridge => RemotePcmReceived != null;
        public void Emit(short[] pcm) => RemotePcmReceived?.Invoke(pcm);
        public TaskCompletionSource<short[]> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SendExternalPcm(short[] samples) => Received.TrySetResult(samples);
    }
    private sealed class Controller : ISipGatewayCallController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall;
        public event EventHandler<CallEndedEventArgs>? CallEnded;
        public Media Media { get; } = new();
        public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AnswerRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AnswerCount; public int DialCount; public int HangupCount;
        public void Incoming() => IncomingCall?.Invoke(this, new("carrier", "12345678", slotId: "slot-1"));
        public void End(string slot = "slot-1") => CallEnded?.Invoke(this, new("carrier", "12345678", null, "BYE", null, DateTime.UtcNow, slot));
        public async Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct)
        {
            Interlocked.Increment(ref AnswerCount); Answered.TrySetResult(); await AnswerRelease.Task.WaitAsync(ct);
            return new(new("carrier", "12345678", CallState.Active, DateTime.UtcNow, DateTime.UtcNow, null, "AMR", null), slotId, Media);
        }
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct)
        { Interlocked.Increment(ref DialCount); throw new NotSupportedException(); }
        public Task HangupAsync(string? slotId, CancellationToken ct) { Interlocked.Increment(ref HangupCount); return Task.CompletedTask; }
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
