using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;

namespace VoSharp.Tests;

[Collection("SIP loopback sockets")]
public sealed partial class SipServerFeatureTests
{
    private static UdpClient Socket() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static IPEndPoint End(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;
    private static int Port() { using var socket = Socket(); return End(socket).Port; }
    private static async Task<SipMessage> Receive(UdpClient socket) => SipMessage.Parse((await socket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(4))).Buffer);
    private static Task Send(UdpClient socket, SipMessage message, IPEndPoint destination) => socket.SendAsync(message.ToBytes(), destination).AsTask();
    private static SipGatewayOptions Options() => new()
    {
        BindAddress = IPAddress.Loopback, SipPort = Port(), Realm = "lab.local",
        Accounts = new() { ["alice"] = "test-alice", ["bob"] = "test-bob", ["900"] = "test-offline" },
        RequireConfiguredLines = true, DefaultOutboundLineId = "modem-a",
        MessagesEnabled = true, ForwardIncomingSms = true, AllowOutgoingSms = true,
        SmsOperationStore = new OperationStore(),
        LineRoutes = new() { ["modem-a"] = new() { ForwardIncomingSms = true, AllowOutgoingSms = true },
            ["modem-b"] = new() { AllowIncomingCalls = false, ForwardIncomingSms = false, AllowOutgoingSms = false } }
    };

    private static SipMessage Request(string method, string user, string target)
    {
        var message = new SipMessage { IsRequest = true, Method = method, RequestUri = $"sip:{target}@lab.local" };
        message.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:5099;branch=z9hG4bK{Guid.NewGuid():N};rport");
        message.SetHeader("From", $"<sip:{user}@lab.local>;tag=source");
        message.SetHeader("To", $"<sip:{target}@lab.local>");
        message.SetHeader("Call-ID", Guid.NewGuid().ToString("N"));
        message.SetHeader("CSeq", $"1 {method}");
        return message;
    }

    private static async Task Authenticate(UdpClient phone, SipMessage request, string user, IPEndPoint gateway)
    {
        request.Headers.Remove("Authorization"); request.Headers.Remove("Proxy-Authorization");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{End(phone).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await Send(phone, request, gateway);
        var challenge = await Receive(phone);
        Assert.Contains(challenge.StatusCode, new[] { 401, 407 });
        var authorization = SipPeerDigest.Create(challenge.GetHeader(challenge.StatusCode == 401 ? "WWW-Authenticate" : "Proxy-Authenticate")!,
            request, new(gateway, user,
                new SipPeerInboundCredentials(user, "test-" + user),
                new SipPeerOutboundCredentials(user, "test-" + user, "lab.local") { AllowedAlgorithms = ["MD5"] }));
        request.SetHeader(challenge.StatusCode == 401 ? "Authorization" : "Proxy-Authorization", authorization!);
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{End(phone).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await Send(phone, request, gateway);
    }

    private static async Task Register(UdpClient phone, string user, IPEndPoint gateway)
    {
        var request = Request("REGISTER", user, user);
        request.SetHeader("Contact", $"<sip:{user}@127.0.0.1:{End(phone).Port}>");
        request.SetHeader("Expires", "300");
        await Authenticate(phone, request, user, gateway);
        Assert.Equal(200, (await Receive(phone)).StatusCode);
    }
    private static SipMessage Text(string target, string text = "Hello 短信 🌍")
    {
        var request = Request("MESSAGE", "spoofed", target);
        request.SetHeader("Content-Type", "text/plain;charset=UTF-8");
        request.RawBody = Encoding.UTF8.GetBytes(text);
        return request;
    }

    [Fact]
    public async Task LocalTextUsesAuthenticatedIdentityAndUtf8WithoutSms()
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options(), controller);
        await gateway.StartAsync(); using var alice = Socket(); using var bob = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        await Register(bob, "bob", endpoint);
        var request = Text("bob"); await Authenticate(alice, request, "alice", endpoint);
        var forwarded = await Receive(bob);
        Assert.Equal("MESSAGE", forwarded.Method); Assert.Contains("sip:alice@", forwarded.GetHeader("From"));
        Assert.Equal("Hello 短信 🌍", Encoding.UTF8.GetString(forwarded.RawBody!));
        Assert.True(forwarded.ToBytes().Length <= 1300);
        await Send(bob, forwarded.CreateResponse(200, "OK"), endpoint);
        Assert.Equal(200, (await Receive(alice)).StatusCode);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public async Task LocalTextIgnoresWrongFromTagBeforeAcceptingRealResponse()
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options(), controller);
        await gateway.StartAsync(); using var alice = Socket(); using var bob = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        await Register(bob, "bob", endpoint);
        await Authenticate(alice, Text("bob"), "alice", endpoint);
        var forwarded = await Receive(bob);
        var forged = forwarded.CreateResponse(200, "OK");
        forged.SetHeader("From", Regex.Replace(forged.GetHeader("From")!, @";tag=[^;\s]+", ";tag=wrong-dialog"));
        await Send(bob, forged, endpoint);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(80)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await alice.ReceiveAsync(deadline.Token));
        await Send(bob, forwarded.CreateResponse(200, "OK"), endpoint);
        Assert.Equal(200, (await Receive(alice)).StatusCode);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public async Task LocalTextIgnoresDuplicateToTagsBeforeAcceptingRealResponse()
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options(), controller);
        await gateway.StartAsync(); using var alice = Socket(); using var bob = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        await Register(bob, "bob", endpoint);
        await Authenticate(alice, Text("bob"), "alice", endpoint);
        var forwarded = await Receive(bob);
        var forged = forwarded.CreateResponse(200, "OK");
        forged.SetHeader("To", forged.GetHeader("To") + ";tag=ambiguous;tag=ambiguous");
        await Send(bob, forged, endpoint);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(80)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await alice.ReceiveAsync(deadline.Token));
        await Send(bob, forwarded.CreateResponse(200, "OK"), endpoint);
        Assert.Equal(200, (await Receive(alice)).StatusCode);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public async Task SmsWithoutDurableAdmissionFailsBeforeTransport()
    {
        var controller = new Controller();
        await using var gateway = new SipGateway(Options() with { SmsOperationStore = null }, controller);
        await gateway.StartAsync(); using var phone = Socket();
        await Authenticate(phone, Text("+15551234567"), "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(503, (await Receive(phone)).StatusCode);
        Assert.Equal(0, controller.SmsCount);
    }

    [Fact]
    public async Task SmsSubmissionUsesExplicitLineAndDeduplicatesNewBranchesWithoutRecharging()
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options(), controller);
        await gateway.StartAsync(); using var alice = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        var request = Text("+15551234567"); request.SetHeader("X-VoWin-Line", "modem-a");
        await Authenticate(alice, request, "alice", endpoint); Assert.Equal(202, (await Receive(alice)).StatusCode);
        Assert.Equal("modem-a", controller.LastLine); Assert.Equal("+15551234567", controller.LastNumber);
        Assert.Equal("Hello 短信 🌍", controller.LastText);
        await Send(alice, request, endpoint); Assert.Equal(202, (await Receive(alice)).StatusCode);
        await Authenticate(alice, request, "alice", endpoint); Assert.Equal(202, (await Receive(alice)).StatusCode);
        Assert.Equal(1, controller.SmsCount);
        request.RawBody = Encoding.UTF8.GetBytes("changed transaction");
        await Authenticate(alice, request, "alice", endpoint); Assert.Equal(400, (await Receive(alice)).StatusCode);
        Assert.Equal(1, controller.SmsCount);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("line")]
    [InlineData("account")]
    public async Task EverySmsPermissionLayerFailsClosed(string disabled)
    {
        var options = Options();
        if (disabled == "global") options = options with { AllowOutgoingSms = false };
        if (disabled == "line") options.LineRoutes["modem-a"] = new() { AllowOutgoingSms = false };
        if (disabled == "account") options.ExtensionPolicies["alice"] = new() { AllowSms = false };
        var controller = new Controller(); await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        await Authenticate(alice, Text("+15551234567"), "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(403, (await Receive(alice)).StatusCode); Assert.Equal(0, controller.SmsCount);
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("sequence")]
    public async Task MessageRetryIgnoresNetworkDomainAndCanonicalSequence(string retry)
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options(), controller);
        await gateway.StartAsync(); using var alice = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        var request = Text("+15551234567"); request.SetHeader("X-VoWin-Line", "modem-a");
        await Authenticate(alice, request, "alice", endpoint);
        Assert.Equal(202, (await Receive(alice)).StatusCode);
        if (retry == "domain") request.RequestUri = "sip:+15551234567@changed-vpn.invalid";
        else request.SetHeader("CSeq", "0001 MESSAGE");
        await Authenticate(alice, request, "alice", endpoint);
        Assert.Equal(202, (await Receive(alice)).StatusCode);
        Assert.Equal(1, controller.SmsCount);
        request.RawBody = Encoding.UTF8.GetBytes("changed payload");
        await Authenticate(alice, request, "alice", endpoint);
        Assert.Equal(400, (await Receive(alice)).StatusCode);
        Assert.Equal(1, controller.SmsCount);
    }

    [Theory]
    [InlineData("submitted_parts_unreported", 202)]
    [InlineData("unknown", 503)]
    [InlineData("failed", 503)]
    public async Task AggregateModemSubmissionDoesNotInventPartCountsOrRetryUnknownResults(string status, int expected)
    {
        var controller = new Controller { AggregateStatus = status };
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync();
        using var alice = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        var request = Text("+15551234567");
        await Authenticate(alice, request, "alice", endpoint);
        var response = await Receive(alice); Assert.Equal(expected, response.StatusCode);
        if (status == "unknown") Assert.Contains("Do Not Retry", response.ReasonPhrase);
        await Authenticate(alice, request, "alice", endpoint); Assert.Equal(expected, (await Receive(alice)).StatusCode);
        Assert.Equal(1, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public async Task DisabledMessagesRejectWithoutContactingCarrier()
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options() with { MessagesEnabled = false }, controller);
        await gateway.StartAsync(); using var phone = Socket();
        await Send(phone, Text("+15551234567"), gateway.Status.LocalEndPoint!);
        Assert.Equal(403, (await Receive(phone)).StatusCode); Assert.Equal(0, controller.SmsCount);
    }

    [Theory]
    [InlineData("application/vnd.3gpp.sms", 415)]
    [InlineData("text/html", 415)]
    [InlineData("text/plain;charset=iso-8859-1", 415)]
    [InlineData("text/plain", 413)]
    public async Task UnsupportedContentAndOversizeNeverBecomeSms(string contentType, int expected)
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options(), controller);
        await gateway.StartAsync(); using var phone = Socket(); var request = Text("+15551234567", new string('x', 1025));
        request.SetHeader("Content-Type", contentType);
        await Authenticate(phone, request, "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(expected, (await Receive(phone)).StatusCode); Assert.Equal(0, controller.SmsCount);
    }

    [Fact]
    public async Task IncomingSmsTargetsItsConfiguredExtensionSegmentsUnicodeAndDeduplicates()
    {
        var options = Options(); options.LineRoutes["modem-a"] = new() { ForwardIncomingSms = true, SmsTarget = "bob" };
        var controller = new Controller(); await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var bob = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        await Register(bob, "bob", endpoint);
        var text = string.Concat(Enumerable.Repeat("测试短信🌍", 90));
        var message = new SmsMessage(1, SmsStatus.Unread, "+15550001111", text, DateTime.UtcNow, Id: "unique-inbox-id");
        controller.Receive(message, "modem-b"); // This SIM has not consented to forwarding.
        controller.Receive(message, "modem-a");
        var chunks = SipGateway.SplitMessageText(text);
        var restored = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            var forwarded = await Receive(bob);
            Assert.Equal("MESSAGE", forwarded.Method); Assert.Equal("modem-a", forwarded.GetHeader("X-VoWin-Line"));
            Assert.Contains("\"+15550001111\"", forwarded.GetHeader("From"));
            Assert.Contains("sip:" + SipGateway.SmsReplyUser("modem-a", "+15550001111", TestSmsBinding("modem-a")) + "@", forwarded.GetHeader("From"));
            Assert.True(forwarded.ToBytes().Length <= 1300);
            var part = Encoding.UTF8.GetString(forwarded.RawBody!);
            Assert.StartsWith($"[{i + 1}/{chunks.Count}] ", part);
            restored.Append(part[(part.IndexOf(']') + 2)..]);
            await Send(bob, forwarded.CreateResponse(200, "OK"), endpoint);
        }
        Assert.Equal(text, restored.ToString());
        controller.Receive(message, "modem-a");
        using var noDuplicate = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await bob.ReceiveAsync(noDuplicate.Token));
        Assert.Equal(0, controller.SmsCount);
    }

    [Fact]
    public async Task OfflineLocalExtensionCannotFallThroughToAnySim()
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options(), controller);
        await gateway.StartAsync(); using var alice = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        await Authenticate(alice, Text("900"), "alice", endpoint);
        Assert.Equal(480, (await Receive(alice)).StatusCode);
        var invite = Request("INVITE", "alice", "900");
        invite.SetHeader("Content-Type", "application/sdp");
        invite.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        await Authenticate(alice, invite, "alice", endpoint);
        Assert.Equal(480, (await Receive(alice)).StatusCode);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public void RoutingRulesAreGenericAndExplicitAccountOrHeaderSelectionWins()
    {
        var options = Options() with { DialRules = [new() { Prefix = "71", StripPrefix = true, Prepend = "+", LineId = "modem-b" }] };
        options.Validate();
        Assert.Equal(("+441234567890", "modem-b"), options.ResolveDestination("71441234567890", null, "alice"));
        Assert.Equal(("71441234567890", "modem-a"), options.ResolveDestination("71441234567890", "modem-a", "alice"));
        options.ExtensionPolicies["alice"] = new() { DefaultLineId = "modem-a", AllowSms = true };
        Assert.Equal(("71441234567890", "modem-a"), options.ResolveDestination("71441234567890", null, "alice"));
        Assert.Throws<ArgumentException>(() => (options with { DialRules = [new() { Prefix = "x\r\n", LineId = "modem-a" }] }).Validate());
    }

    private static async Task<SipMessage> Final(UdpClient socket)
    {
        SipMessage message;
        do { message = await Receive(socket); } while (!message.IsRequest && message.StatusCode < 200);
        return message;
    }

    [Fact]
    public async Task StandaloneExtensionsForkFirstAnswerBridgePcmAndByeWithoutAnyModem()
    {
        var options = Options() with { LineRoutes = new(), DefaultOutboundLineId = null };
        var controller = new Controller(); await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        using var alice = Socket(); using var bob = Socket(); using var bobOtherDevice = Socket();
        using var aliceRtp = Socket(); using var bobRtp = Socket();
        await Register(alice, "alice", endpoint); await Register(bob, "bob", endpoint); await Register(bobOtherDevice, "bob", endpoint);
        var invite = Request("INVITE", "spoofed", "bob");
        invite.SetHeader("Content-Type", "application/sdp");
        invite.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {End(aliceRtp).Port} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        await Authenticate(alice, invite, "alice", endpoint);
        var offered = await Receive(bob); var other = await Receive(bobOtherDevice);
        Assert.Contains("sip:alice@", offered.GetHeader("From"));
        var answered = offered.CreateResponse(200, "OK");
        answered.SetHeader("To", offered.GetHeader("To") + ";tag=bob-answer");
        answered.SetHeader("Contact", $"<sip:bob@127.0.0.1:{End(bob).Port}>");
        answered.SetHeader("Content-Type", "application/sdp");
        answered.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {End(bobRtp).Port} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        await Send(bob, answered, endpoint);
        Assert.Equal("ACK", (await Receive(bob)).Method);
        var accepted = await Final(alice); Assert.Equal(200, accepted.StatusCode);
        var ack = Request("ACK", "alice", "gateway");
        ack.SetHeader("From", invite.GetHeader("From")!); ack.SetHeader("To", accepted.GetHeader("To")!);
        ack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!); ack.SetHeader("CSeq", "1 ACK");
        await Send(alice, ack, endpoint);
        var cancel = await Receive(bobOtherDevice); Assert.Equal("CANCEL", cancel.Method);
        await Send(bobOtherDevice, cancel.CreateResponse(200, "OK"), endpoint);
        var terminated = other.CreateResponse(487, "Request Terminated"); terminated.SetHeader("To", other.GetHeader("To") + ";tag=other");
        await Send(bobOtherDevice, terminated, endpoint); Assert.Equal("ACK", (await Receive(bobOtherDevice)).Method);
        Assert.Equal(1, gateway.Status.ActiveDialogs);
        var packet = new byte[172]; packet[0] = 0x80; packet[1] = 8; packet[11] = 1; Array.Fill(packet, (byte)0xFA, 12, 160);
        int MediaPort(SipMessage message) => int.Parse(Regex.Match(message.Body, @"m=audio (\d+)").Groups[1].Value);
        await aliceRtp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, MediaPort(accepted)));
        await bobRtp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, MediaPort(offered)));
        async Task Speech(UdpClient socket)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                var media = await socket.ReceiveAsync(deadline.Token);
                if (media.Buffer.Length > 12 && media.Buffer.AsSpan(12).Contains((byte)0xFA)) return;
            }
        }
        await Task.WhenAll(Speech(aliceRtp), Speech(bobRtp));
        var bye = Request("BYE", "bob", "gateway");
        bye.SetHeader("From", answered.GetHeader("To")!); bye.SetHeader("To", offered.GetHeader("From")!);
        bye.SetHeader("Call-ID", offered.GetHeader("Call-ID")!); bye.SetHeader("CSeq", "3 BYE");
        await Send(bob, bye, endpoint); Assert.Equal(200, (await Receive(bob)).StatusCode);
        var callerBye = await Receive(alice); Assert.Equal("BYE", callerBye.Method);
        await Send(alice, callerBye.CreateResponse(200, "OK"), endpoint);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (gateway.Status.ActiveDialogs != 0) await Task.Delay(10, cleanup.Token);
        Assert.Equal(0, controller.DialCount); Assert.Equal(0, controller.SmsCount);
    }

    [Fact]
    public async Task StandaloneCallerCancelTerminatesBothPendingLegsWithoutCarrierTraffic()
    {
        var controller = new Controller(); await using var gateway = new SipGateway(Options(), controller);
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        using var alice = Socket(); using var bob = Socket(); await Register(bob, "bob", endpoint);
        var invite = Request("INVITE", "alice", "bob"); invite.SetHeader("Content-Type", "application/sdp");
        invite.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        await Authenticate(alice, invite, "alice", endpoint); var offered = await Receive(bob);
        var cancel = Request("CANCEL", "alice", "bob");
        foreach (var name in new[] { "Via", "From", "To", "Call-ID" }) cancel.SetHeader(name, invite.GetHeader(name)!);
        cancel.SetHeader("CSeq", "1 CANCEL"); await Send(alice, cancel, endpoint);
        // A provisional INVITE response can still be queued when CANCEL is
        // sent. Match the two final transactions, not a packet-order assumption.
        var finals = new[] { await Final(alice), await Final(alice) };
        Assert.Contains(finals, response => response.StatusCode == 200 && response.GetHeader("CSeq") == "1 CANCEL" && response.GetHeader("Call-ID") == invite.GetHeader("Call-ID"));
        Assert.Contains(finals, response => response.StatusCode == 487 && response.GetHeader("CSeq") == "1 INVITE" && response.GetHeader("Call-ID") == invite.GetHeader("Call-ID"));
        var downstreamCancel = await Receive(bob); Assert.Equal("CANCEL", downstreamCancel.Method);
        await Send(bob, downstreamCancel.CreateResponse(200, "OK"), endpoint);
        var ended = offered.CreateResponse(487, "Request Terminated"); ended.SetHeader("To", offered.GetHeader("To") + ";tag=bob");
        await Send(bob, ended, endpoint); Assert.Equal("ACK", (await Receive(bob)).Method);
        Assert.Equal(0, gateway.Status.ActiveDialogs); Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public async Task BusySmsSubmissionOnOneModemDoesNotBlockAnotherModem()
    {
        var options = Options(); options.LineRoutes["modem-b"] = new() { AllowOutgoingSms = true };
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new Controller { BeforeSend = async line =>
        { if (line == "modem-a") { blocked.TrySetResult(); await release.Task; } } };
        await using var gateway = new SipGateway(options, controller); await gateway.StartAsync();
        using var alice = Socket(); using var bob = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        var first = Text("+15551234567"); first.SetHeader("X-VoWin-Line", "modem-a");
        await Authenticate(alice, first, "alice", endpoint); await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var second = Text("+15551234568"); second.SetHeader("X-VoWin-Line", "modem-b");
            await Authenticate(bob, second, "bob", endpoint); Assert.Equal(202, (await Receive(bob)).StatusCode);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(202, (await Receive(alice)).StatusCode); Assert.Equal(2, controller.SmsCount);
    }

    [Fact]
    public async Task RegistrarSupportsAuthenticatedQueryAndWildcardRemovalOfAllContacts()
    {
        await using var gateway = new SipGateway(Options(), new Controller()); await gateway.StartAsync();
        using var alice = Socket(); using var extra = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        await Register(alice, "alice", endpoint); await Register(extra, "alice", endpoint);
        var query = Request("REGISTER", "alice", "alice"); await Authenticate(alice, query, "alice", endpoint);
        var registered = await Receive(alice); Assert.Equal(200, registered.StatusCode); Assert.Equal(2, registered.Headers["Contact"].Count);
        var remove = Request("REGISTER", "alice", "alice"); remove.SetHeader("Contact", "*"); remove.SetHeader("Expires", "0");
        await Authenticate(alice, remove, "alice", endpoint); Assert.Equal(200, (await Receive(alice)).StatusCode);
        Assert.Empty(gateway.Status.Registrations);
    }

    [Theory]
    [InlineData(true, 202)]
    [InlineData(false, 403)]
    public async Task ReplyToForwardedSmsUsesOriginalSimNotGlobalDefault(bool enabled, int expected)
    {
        var options = Options(); options.LineRoutes["modem-b"] = new() { AllowOutgoingSms = enabled };
        var controller = new Controller(); await using var gateway = new SipGateway(options, controller); await gateway.StartAsync();
        using var alice = Socket(); var target = SipGateway.SmsReplyUser("modem-b", "+15551234567", TestSmsBinding("modem-b"));
        await Authenticate(alice, Text(target), "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(expected, (await Receive(alice)).StatusCode);
        if (enabled) { Assert.Equal("modem-b", controller.LastLine); Assert.Equal("+15551234567", controller.LastNumber); }
        else Assert.Equal(0, controller.SmsCount);
    }

    private static string TestSmsBinding(string line) => SmsSubscriptionIdentity.ForIccid(
        line.Equals("modem-a", StringComparison.OrdinalIgnoreCase) ? "8900000000000000001" : "8900000000000000002")!;

    private sealed class Controller : ISipGatewayCallController, ISipGatewaySimBoundMessageController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public event EventHandler<SmsReceivedEventArgs>? SmsReceived;
        public int SmsCount, DialCount;
        public Func<string?, Task>? BeforeSend;
        public string? LastLine, LastNumber, LastText;
        public string? AggregateStatus;
        public void Receive(SmsMessage message, string slot) => SmsReceived?.Invoke(this, new(message, slot, GetSmsSimBinding(slot)));
        public string? GetSmsSimBinding(string slotId) => TestSmsBinding(slotId);
        public Task<SmsSubmitResult> SendSmsForSimAsync(string number, string text, string slotId, string expectedBinding, CancellationToken ct)
        {
            Assert.Equal(GetSmsSimBinding(slotId), expectedBinding);
            return SendSmsAsync(number, text, slotId, ct);
        }
        public async Task<SmsSubmitResult> SendSmsAsync(string number, string text, string? slotId, CancellationToken ct)
        {
            Interlocked.Increment(ref SmsCount); LastNumber = number; LastText = text; LastLine = slotId;
            if (BeforeSend != null) await BeforeSend(slotId);
            if (AggregateStatus != null) return new(number, text, SmsEncoding.Unknown, null, 0, 0, 0, false, AggregateStatus, [], DateTime.UtcNow);
            return new SmsSubmitResult(number, text, SmsEncoding.Ucs2, null, 1, 1, 1, true, "accepted", [], DateTime.UtcNow);
        }
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct)
        { Interlocked.Increment(ref DialCount); throw new NotSupportedException(); }
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task HangupAsync(string? slotId, CancellationToken ct) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
