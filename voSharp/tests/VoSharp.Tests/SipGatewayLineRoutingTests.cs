using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

public sealed class SipGatewayLineRoutingTests
{
    private static UdpClient Socket() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static IPEndPoint Endpoint(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;
    private static SipGatewayOptions Options(UdpClient peer)
    {
        using var probe = Socket();
        return new()
        {
            BindAddress = IPAddress.Loopback, SipPort = Endpoint(probe).Port,
            Accounts = new(),
            Peer = new(Endpoint(peer), "600",
                new SipPeerInboundCredentials("gateway", "local-test-secret"),
                new SipPeerOutboundCredentials("pbx-auth", "independent-pbx-test-secret", "asterisk")) { AllowOutboundCalls = true },
            RequireConfiguredLines = true,
            LineRoutes = new()
            {
                ["module-a"] = new() { IncomingTarget = "1001" },
                ["module-b"] = new() { IncomingTarget = "1002" },
                ["private-module"] = new() { Enabled = false }
            }
        };
    }
    private static async Task<SipMessage> Receive(UdpClient peer) => SipMessage.Parse((await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
    private static Task Send(UdpClient peer, SipMessage message, IPEndPoint target) => peer.SendAsync(message.ToBytes(), target).AsTask();

    [Fact]
    public void ConfiguredModuleRoutingNeverUsesAnUnspecifiedOrDisabledFallback()
    {
        using var peer = Socket(); var options = Options(peer);
        options.Validate();
        Assert.Equal("MODULE-A", options.ResolveOutboundLine("MODULE-A"));
        Assert.Equal(400, Assert.Throws<SipGatewayLineRoutingException>(() => options.ResolveOutboundLine(null)).StatusCode);
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() => options.ResolveOutboundLine("private-module")).StatusCode);
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() => options.ResolveOutboundLine("missing-module")).StatusCode);
        Assert.Equal("module-b", (options with { DefaultOutboundLineId = "module-b" }).ResolveOutboundLine(null));
        Assert.Equal("module-a", (options with { DefaultOutboundLineId = "module-b" }).ResolveOutboundLine("module-a"));
        Assert.Throws<ArgumentException>(() => (options with { DefaultOutboundLineId = "private-module" }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { LineRoutes = new() { ["bad\r\nline"] = new() } }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { LineRoutes = new() { ["module-a"] = new() { IncomingTarget = "1001\r\nInjected: yes" } } }).Validate());
    }

    [Fact]
    public async Task DifferentModulesInviteTheirOwnInternalTargetsAndCleanupStaysIndependent()
    {
        using var peer = Socket(); var options = Options(peer); var controller = new Controller();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync();
        Assert.True(gateway.RoutesIncomingCall("module-a"));
        Assert.True(gateway.RoutesIncomingCall("module-b"));
        Assert.False(gateway.RoutesIncomingCall("private-module"));
        Assert.False(gateway.RoutesIncomingCall("unknown"));
        Assert.False(gateway.RoutesIncomingCall(null));
        ((IDictionary<string, SipGatewayLineRoute>)gateway.Status.LineRoutes).Clear();
        Assert.True(gateway.RoutesIncomingCall("module-a"));
        // Mutating the original configuration cannot change the running gateway.
        options.LineRoutes["module-a"] = new() { Enabled = false };
        Assert.True(gateway.RoutesIncomingCall("module-a"));
        controller.Incoming("private-module");
        controller.Incoming("module-a");
        var first = await Receive(peer);
        Assert.Equal("module-a", first.GetHeader("X-VoWin-Line"));
        Assert.Contains("sip:1001@", first.RequestUri);
        await Send(peer, first.CreateResponse(180, "Ringing"), gateway.Status.LocalEndPoint!);
        controller.Incoming("module-b");
        var second = await Receive(peer);
        Assert.Equal("module-b", second.GetHeader("X-VoWin-Line"));
        Assert.Contains("sip:1002@", second.RequestUri);
        await Send(peer, second.CreateResponse(180, "Ringing"), gateway.Status.LocalEndPoint!);
        await Send(peer, first.CreateResponse(486, "Busy Here"), gateway.Status.LocalEndPoint!);
        Assert.Equal("ACK", (await Receive(peer)).Method);
        await WaitUntil(() => controller.Hangups.Contains("module-a"));
        Assert.DoesNotContain("module-b", controller.Hangups);
        Assert.DoesNotContain("private-module", controller.Hangups);
        await Send(peer, second.CreateResponse(486, "Busy Here"), gateway.Status.LocalEndPoint!);
        Assert.Equal("ACK", (await Receive(peer)).Method);
        await WaitUntil(() => controller.Hangups.Contains("module-b"));
    }

    [Theory]
    [InlineData(null, null, 400, null)]
    [InlineData("private-module", null, 403, null)]
    [InlineData("missing", "module-b", 403, null)]
    [InlineData(null, "module-b", 503, "module-b")]
    [InlineData("module-a", "module-b", 503, "module-a")]
    public async Task AuthenticatedOutboundRequestsEnforceModuleSelectionBeforeCallingTheController(string? header, string? defaultLine, int finalStatus, string? expectedLine)
    {
        using var peer = Socket(); using var media = Socket();
        var options = Options(peer) with { DefaultOutboundLineId = defaultLine };
        var controller = new Controller();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync();
        var request = new SipMessage { IsRequest = true, Method = "INVITE", RequestUri = "sip:12345@vowin.local" };
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{Endpoint(peer).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("From", "<sip:gateway@vowin.local>;tag=local-phone");
        request.SetHeader("To", "<sip:12345@vowin.local>");
        request.SetHeader("Call-ID", Guid.NewGuid().ToString("N"));
        request.SetHeader("CSeq", "1 INVITE");
        if (header is not null) request.SetHeader("X-VoWin-Line", header);
        request.SetHeader("Content-Type", "application/sdp");
        request.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {Endpoint(media).Port} RTP/AVP 8\r\n";
        var destination = gateway.Status.LocalEndPoint!;
        await Send(peer, request, destination);
        var challenge = await Receive(peer); Assert.Equal(407, challenge.StatusCode);
        var inbound = options.Peer!.InboundAuth!;
        var proofPeer = options.Peer with
        {
            OutboundAuth = new(inbound.Username, inbound.Password, options.Realm) { AllowedAlgorithms = [inbound.Algorithm] }
        };
        request.SetHeader("Proxy-Authorization", SipPeerDigest.Create(challenge.GetHeader("Proxy-Authenticate")!, request, proofPeer)!);
        request.SetHeader("CSeq", "2 INVITE");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{Endpoint(peer).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await Send(peer, request, destination);
        SipMessage response;
        do { response = await Receive(peer); } while (response.StatusCode < 200);
        Assert.Equal(finalStatus, response.StatusCode);
        if (expectedLine is null) Assert.Empty(controller.DialedLines);
        else Assert.Equal(expectedLine, Assert.Single(controller.DialedLines));
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, cancellation.Token);
    }
    private sealed class Controller : ISipGatewayCallController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall;
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public ConcurrentQueue<string?> DialedLines { get; } = new();
        public ConcurrentQueue<string?> Hangups { get; } = new();
        public void Incoming(string line) => IncomingCall?.Invoke(this, new("same-carrier-call-id", "12345", slotId: line));
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct)
        { DialedLines.Enqueue(slotId); throw new InvalidOperationException("Synthetic carrier stays offline; no hardware or external call."); }
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task HangupAsync(string? slotId, CancellationToken ct) { Hangups.Enqueue(slotId); return Task.CompletedTask; }
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
