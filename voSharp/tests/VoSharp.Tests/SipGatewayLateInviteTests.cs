using System.Net;
using System.Net.Sockets;
using System.Reflection;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

[Collection("SIP loopback sockets")]
public sealed class SipGatewayLateInviteTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static UdpClient Socket() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static IPEndPoint Endpoint(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;
    private static SipGatewayOptions Options(UdpClient peer)
    {
        using var reservation = Socket();
        return new() { BindAddress = IPAddress.Loopback, SipPort = Endpoint(reservation).Port, Realm = "late.test",
            Accounts = new() { ["gateway"] = "synthetic-late-invite-secret" },
            Peer = new(Endpoint(peer), "600",
                new SipPeerInboundCredentials("gateway", "synthetic-late-invite-secret") { Algorithm = "SHA-256" },
                new SipPeerOutboundCredentials("late-out", "synthetic-late-outbound-secret", "late.peer.test")) };
    }
    private static async Task<SipMessage> Receive(UdpClient socket) =>
        SipMessage.Parse((await socket.ReceiveAsync().WaitAsync(Deadline)).Buffer);
    private static Task Send(UdpClient socket, SipMessage message, IPEndPoint remote) => socket.SendAsync(message.ToBytes(), remote).AsTask();
    private static SipMessage Answer(SipMessage invite, UdpClient peer, UdpClient media, string tag)
    {
        var response = invite.CreateResponse(200, "OK");
        response.SetHeader("To", invite.GetHeader("To") + ";tag=" + tag);
        response.SetHeader("Contact", $"<sip:600@127.0.0.1:{Endpoint(peer).Port}>");
        response.SetHeader("Content-Type", "application/sdp");
        response.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {Endpoint(media).Port} RTP/AVP 8\r\n";
        return response;
    }
    private static async Task NoPacket(UdpClient socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await socket.ReceiveAsync(timeout.Token));
    }
    private static async Task ReceiveCleanup(UdpClient peer, IPEndPoint remote, string tag)
    {
        var ack = await Receive(peer);
        Assert.Equal("ACK", ack.Method); Assert.EndsWith(";tag=" + tag, ack.GetHeader("To"));
        var bye = await Receive(peer);
        Assert.Equal("BYE", bye.Method); Assert.EndsWith(";tag=" + tag, bye.GetHeader("To"));
        await Send(peer, bye.CreateResponse(200, "OK"), remote);
    }

    [Fact]
    public async Task AbandonedInviteCleansEveryDistinctToTagButNeverResubmitsByeForRetransmission()
    {
        using var peer = Socket(); using var media = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer), controller);
        await gateway.StartAsync(); var remote = gateway.Status.LocalEndPoint!;
        controller.Incoming(); var invite = await Receive(peer);
        await Send(peer, invite.CreateResponse(180, "Ringing"), remote);
        controller.End(); Assert.Equal("CANCEL", (await Receive(peer)).Method);
        var first = Answer(invite, peer, media, "first-dialog");
        await Send(peer, first, remote); await ReceiveCleanup(peer, remote, "first-dialog");
        var second = Answer(invite, peer, media, "second-dialog");
        await Send(peer, second, remote); await ReceiveCleanup(peer, remote, "second-dialog");
        foreach (var response in new[] { first, second })
        {
            await Send(peer, response, remote);
            Assert.Equal("ACK", (await Receive(peer)).Method);
        }
        await NoPacket(peer);
        Assert.Equal(0, controller.Answers);
    }

    [Fact]
    public async Task AcceptedDialogSurvivesOtherTagsAndRetiredAttemptWithTheSameWinnerTag()
    {
        using var peer = Socket(); using var media = Socket(); var controller = new Controller();
        var options = Options(peer);
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); var remote = gateway.Status.LocalEndPoint!;
        controller.Incoming(); var initial = await Receive(peer);
        var challenge = initial.CreateResponse(401, "Unauthorized");
        challenge.SetHeader("To", initial.GetHeader("To") + ";tag=challenge");
        challenge.SetHeader("WWW-Authenticate", new SipDigestAuthenticator("late.peer.test",
            new Dictionary<string, string> { ["late-out"] = "synthetic-late-outbound-secret" }, "SHA-256").CreateChallenge());
        await Send(peer, challenge, remote);
        Assert.Equal("ACK", (await Receive(peer)).Method);
        var current = await Receive(peer); Assert.Equal("2 INVITE", current.GetHeader("CSeq"));
        var accepted = Answer(current, peer, media, "winner");
        await Send(peer, accepted, remote);
        Assert.Equal("2 ACK", (await Receive(peer)).GetHeader("CSeq"));
        await controller.Answered.Task.WaitAsync(Deadline);
        var other = Answer(current, peer, media, "other-dialog");
        await Send(peer, other, remote); await ReceiveCleanup(peer, remote, "other-dialog");
        await Send(peer, Answer(initial, peer, media, "winner"), remote);
        var retiredAck = await Receive(peer);
        Assert.Equal("1 ACK", retiredAck.GetHeader("CSeq"));
        Assert.EndsWith(";tag=winner", retiredAck.GetHeader("To"));
        await Send(peer, accepted, remote);
        Assert.Equal("2 ACK", (await Receive(peer)).GetHeader("CSeq"));
        await NoPacket(peer);
        Assert.Equal(1, controller.Answers);
        Assert.Equal(1, gateway.Status.ActiveDialogs);
        controller.End();
        var bye = await Receive(peer); Assert.Equal("BYE", bye.Method);
        Assert.EndsWith(";tag=winner", bye.GetHeader("To"));
        await Send(peer, bye.CreateResponse(200, "OK"), remote);
    }

    [Fact]
    public async Task UnknownCleanupByeIsNotReissuedOnAnotherInviteRetransmission()
    {
        using var peer = Socket(); using var media = Socket(); var controller = new Controller();
        await using var gateway = new SipGateway(Options(peer), controller);
        var unknown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Diagnostic += (_, message) => { if (message.StartsWith("Late SIP INVITE cleanup failed", StringComparison.Ordinal)) unknown.TrySetResult(); };
        await gateway.StartAsync(); var remote = gateway.Status.LocalEndPoint!;
        controller.Incoming(); var invite = await Receive(peer);
        await Send(peer, invite.CreateResponse(180, "Ringing"), remote);
        controller.End(); Assert.Equal("CANCEL", (await Receive(peer)).Method);
        var response = Answer(invite, peer, media, "unknown-dialog");
        await Send(peer, response, remote); Assert.Equal("ACK", (await Receive(peer)).Method);
        var originalBye = await Receive(peer); Assert.Equal("BYE", originalBye.Method);
        // Deliberately withhold a response. Same-transaction UDP retransmits are
        // legal; after its bounded budget no new logical BYE may be created.
        await unknown.Task.WaitAsync(TimeSpan.FromSeconds(7));
        await Send(peer, response, remote);
        SipMessage packet;
        do
        {
            packet = await Receive(peer);
            if (packet.Method == "BYE") Assert.Equal(originalBye.GetHeader("Via"), packet.GetHeader("Via"));
        } while (packet.Method != "ACK");
        using var noNewAttempt = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            while (true)
            {
                packet = SipMessage.Parse((await peer.ReceiveAsync(noNewAttempt.Token)).Buffer);
                Assert.Equal("BYE", packet.Method);
                Assert.Equal(originalBye.GetHeader("Via"), packet.GetHeader("Via"));
            }
        }
        catch (OperationCanceledException) when (noNewAttempt.IsCancellationRequested) { }
    }

    private static object Invoke(SipGateway gateway, string name, params object?[] arguments) =>
        typeof(SipGateway).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gateway, arguments)!;
    private static SipMessage TrackedInvite(int sequence, string callId)
    {
        var invite = new SipMessage { IsRequest = true, Method = "INVITE", RequestUri = "sip:600@127.0.0.1:15060" };
        invite.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:15061;branch=z9hG4bK{Guid.NewGuid():N};rport");
        invite.SetHeader("From", "<sip:synthetic@late.test>;tag=local");
        invite.SetHeader("To", "<sip:600@late.test>");
        invite.SetHeader("Call-ID", callId); invite.SetHeader("CSeq", $"{sequence} INVITE");
        return invite;
    }
    private static (bool Ack, bool Bye) Observe(SipGateway gateway, object attempt, SipMessage response)
    {
        object?[] args = [attempt, response, false];
        var ack = (bool)Invoke(gateway, "ObserveLateInviteResponse", args);
        return (ack, (bool)args[2]!);
    }

    [Fact]
    public async Task RetiredCleanupAndAcceptanceShareOneAtomicDialogGate()
    {
        using var peer = Socket(); using var media = Socket();
        await using var gateway = new SipGateway(Options(peer), new Controller());
        // These pure state transitions perform no I/O. Retire happens before
        // any response, and acceptance never has another tag to dispatch.
        for (var i = 0; i < 128; i++)
        {
            var initial = TrackedInvite(1, "atomic-" + i);
            var retired = Invoke(gateway, "TrackLateInvite", initial, Endpoint(peer), null);
            var current = TrackedInvite(2, "atomic-" + i);
            var live = Invoke(gateway, "TrackLateInvite", current, Endpoint(peer), retired);
            Invoke(gateway, "RetireLateInvite", retired);
            var lateAnswer = Answer(initial, peer, media, "same-dialog");
            var liveAnswer = Answer(current, peer, media, "same-dialog");
            Observe(gateway, live, liveAnswer);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accept = Task.Run(async () => { await start.Task; return (bool)Invoke(gateway, "TryAcceptLateInvite", live, liveAnswer); });
            var cleanup = Task.Run(async () => { await start.Task; return Observe(gateway, retired, lateAnswer); });
            start.SetResult();
            await Task.WhenAll(accept, cleanup);
            var acceptedResult = await accept;
            var cleanupResult = await cleanup;
            Assert.True(cleanupResult.Ack);
            Assert.NotEqual(acceptedResult, cleanupResult.Bye);
            if (acceptedResult)
                Assert.False(Observe(gateway, retired, lateAnswer).Bye);
            else
                Assert.False((bool)Invoke(gateway, "TryAcceptLateInvite", live, liveAnswer));
        }
    }

    [Fact]
    public async Task TagOverflowAndTrackingCapacityFenceWithoutEvictingOldCleanupClaims()
    {
        using var peer = Socket(); using var media = Socket();
        await using var gateway = new SipGateway(Options(peer), new Controller());
        var invite = TrackedInvite(1, "tag-capacity");
        var attempt = Invoke(gateway, "TrackLateInvite", invite, Endpoint(peer), null);
        Invoke(gateway, "RetireLateInvite", attempt);
        for (var i = 0; i < 32; i++) Assert.True(Observe(gateway, attempt, Answer(invite, peer, media, "tag-" + i)).Bye);
        var overflow = Observe(gateway, attempt, Answer(invite, peer, media, "overflow"));
        Assert.True(overflow.Ack); Assert.False(overflow.Bye);
        Assert.False(Observe(gateway, attempt, Answer(invite, peer, media, "tag-0")).Bye);
        var rejected = Assert.Throws<TargetInvocationException>(() => Invoke(gateway, "TrackLateInvite", TrackedInvite(2, "tag-capacity"), Endpoint(peer), attempt));
        Assert.IsType<InvalidOperationException>(rejected.InnerException);
        for (var i = 1; i < 512; i++) Invoke(gateway, "TrackLateInvite", TrackedInvite(1, "capacity-" + i), Endpoint(peer), null);
        rejected = Assert.Throws<TargetInvocationException>(() => Invoke(gateway, "TrackLateInvite", TrackedInvite(1, "capacity-overflow"), Endpoint(peer), null));
        Assert.IsType<InvalidOperationException>(rejected.InnerException);
    }

    private sealed class Media : ICallPcmMedia
    {
        public event Action<short[]>? RemotePcmReceived;
        public void SendExternalPcm(short[] samples) { _ = RemotePcmReceived; }
    }
    private sealed class Controller : ISipGatewayCallController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall;
        public event EventHandler<CallEndedEventArgs>? CallEnded;
        public int Answers;
        public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Incoming() => IncomingCall?.Invoke(this, new("synthetic-call", "+15550000001", slotId: "synthetic-line"));
        public void End() => CallEnded?.Invoke(this, new("synthetic-call", "+15550000001", null, "BYE", null, DateTime.UtcNow, "synthetic-line"));
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct)
        {
            Interlocked.Increment(ref Answers); Answered.TrySetResult();
            return Task.FromResult(new SipGatewayCarrierCall(new("synthetic-call", "+15550000001", CallState.Active, DateTime.UtcNow, DateTime.UtcNow, null, "synthetic", null), slotId, new Media()));
        }
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct) => throw new NotSupportedException("Synthetic test does not dial");
        public Task HangupAsync(string? slotId, CancellationToken ct) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
