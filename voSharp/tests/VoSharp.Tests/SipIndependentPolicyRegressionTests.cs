using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;

namespace VoSharp.Tests;

// Behavioral RED uses the old public record types. Unknown future properties
// are deliberately supplied through JSON, so an old build compiles and must
// fail on actual routing/recipient behavior, never on a missing C# property.
public sealed partial class SipServerFeatureTests
{
    private static T IndependentPolicyJson<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("Synthetic policy JSON was not an object.");

    private static SipGatewayPeer IndependentPolicyPeer(UdpClient peer, string policyJson)
    {
        // The existing record has two public constructors. Supply only its
        // ordinary typed factory; standard JSON handling still ignores old
        // unknown fields and assigns the real properties once implemented.
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(SipGatewayPeer))
                info.CreateObject = () => new SipGatewayPeer(End(peer), "600",
                    new SipPeerInboundCredentials("peer-in", "independent-inbound-test-secret") { Algorithm = "MD5" },
                    new SipPeerOutboundCredentials("peer-out", "independent-outbound-test-secret", "peer.test")
                    { AllowedAlgorithms = ["MD5"] })
                { AllowOutboundCalls = true, AllowOutboundMessages = true };
        });
        return JsonSerializer.Deserialize<SipGatewayPeer>(policyJson,
            new JsonSerializerOptions { TypeInfoResolver = resolver })
            ?? throw new InvalidDataException("Synthetic peer policy JSON was not an object.");
    }

    private static async Task IndependentPolicyAuthenticatePeer(UdpClient peer, SipMessage request,
        SipGatewayOptions options, IPEndPoint endpoint)
    {
        await Send(peer, request, endpoint);
        var challenge = await Receive(peer);
        Assert.Equal(407, challenge.StatusCode);
        var inbound = options.Peer!.InboundAuth!;
        var proof = options.Peer with
        {
            OutboundAuth = new(inbound.Username, inbound.Password, options.Realm)
            { AllowedAlgorithms = [inbound.Algorithm] }
        };
        request.SetHeader("Proxy-Authorization", SipPeerDigest.Create(
            challenge.GetHeader("Proxy-Authenticate")!, request, proof)!);
        request.SetHeader("CSeq", $"2 {request.Method}");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{End(peer).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await Send(peer, request, endpoint);
    }

    private static SipMessage IndependentPolicyInvite(string target)
    {
        var request = Request("INVITE", "alice", target);
        request.SetHeader("Content-Type", "application/sdp");
        request.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        return request;
    }

    private static async Task<SipMessage?> IndependentPolicyObserve(UdpClient receiver, IPEndPoint endpoint,
        int milliseconds = 400)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        try
        {
            var message = SipMessage.Parse((await receiver.ReceiveAsync(timeout.Token)).Buffer);
            Assert.True(message.IsRequest, "The synthetic receiver must observe an actual business request.");
            if (message.Method == "MESSAGE")
                await Send(receiver, message.CreateResponse(200, "OK"), endpoint);
            else if (message.Method == "INVITE")
            {
                var declined = message.CreateResponse(486, "Synthetic Busy");
                declined.SetHeader("To", message.GetHeader("To") + ";tag=policy-receiver");
                await Send(receiver, declined, endpoint);
            }
            else Assert.Fail("Unexpected synthetic business request method.");
            return message;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return null; }
    }

    private static SipGatewayOptions IndependentPolicyPaidOptions(bool sms, string selection)
    {
        var options = Options();
        options.LineRoutes["modem-b"] = new() { AllowOutgoingCalls = true, AllowOutgoingSms = true };
        if (selection == "default") options = options with { DefaultOutboundLineId = "modem-b" };
        if (selection == "rule") options = options with
        { DialRules = [new() { Prefix = "71", StripPrefix = true, Prepend = "+", LineId = "modem-b" }] };
        return options;
    }

    private static SipMessage IndependentPolicyPaidRequest(bool sms, string selection)
    {
        var target = selection == "rule" ? "7115551234567" : "+15551234567";
        var request = sms ? Text(target, "synthetic independent policy SMS") : IndependentPolicyInvite(target);
        if (selection == "header") request.SetHeader("X-VoWin-Line", "modem-b");
        return request;
    }

    [Theory]
    [InlineData("alice", false)]
    [InlineData("bob", false)]
    [InlineData("alice", true)]
    [InlineData("bob", true)]
    public async Task IndependentPolicyInternalMessagesRequireSenderAndReceiverConsent(string account, bool allowed)
    {
        var options = Options();
        options.ExtensionPolicies[account] = IndependentPolicyJson<SipExtensionPolicy>(
            JsonSerializer.Serialize(new { AllowMessages = allowed }));
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync();
        using var alice = Socket(); using var bob = Socket();
        var endpoint = gateway.Status.LocalEndPoint!;
        await Register(bob, "bob", endpoint);
        var observed = IndependentPolicyObserve(bob, endpoint);
        await Authenticate(alice, Text("bob", "synthetic internal text"), "alice", endpoint);
        var response = await Final(alice);
        var forwarded = await observed;
        Assert.Equal(allowed ? 200 : 403, response.StatusCode);
        if (allowed)
        {
            Assert.NotNull(forwarded); Assert.Equal("synthetic internal text", forwarded.Body);
            Assert.Contains("sip:alice@", forwarded.GetHeader("From"));
        }
        else Assert.Null(forwarded);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Theory]
    [InlineData("alice", false)]
    [InlineData("bob", false)]
    [InlineData("alice", true)]
    [InlineData("bob", true)]
    public async Task IndependentPolicyInternalInvitesRequireSenderAndReceiverConsent(string account, bool allowed)
    {
        var options = Options() with { AllowCarrierCalls = false, LineRoutes = new(), DefaultOutboundLineId = null };
        options.ExtensionPolicies[account] = IndependentPolicyJson<SipExtensionPolicy>(
            JsonSerializer.Serialize(new { AllowInternalCalls = allowed }));
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller) { InviteTransactionTimeout = TimeSpan.FromSeconds(1) };
        await gateway.StartAsync();
        using var alice = Socket(); using var bob = Socket();
        var endpoint = gateway.Status.LocalEndPoint!;
        await Register(bob, "bob", endpoint);
        var observed = IndependentPolicyObserve(bob, endpoint);
        await Authenticate(alice, IndependentPolicyInvite("bob"), "alice", endpoint);
        var response = await Final(alice);
        var forwarded = await observed;
        Assert.Equal(allowed ? 480 : 403, response.StatusCode);
        if (allowed) { Assert.NotNull(forwarded); Assert.Equal("INVITE", forwarded.Method); }
        else Assert.Null(forwarded);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Theory]
    [InlineData(false, "header", false)]
    [InlineData(false, "default", false)]
    [InlineData(false, "rule", false)]
    [InlineData(true, "header", false)]
    [InlineData(true, "default", false)]
    [InlineData(true, "rule", false)]
    [InlineData(false, "header", true)]
    [InlineData(true, "header", true)]
    public async Task IndependentPolicyLocalPaidLineListsCheckTheFinalSelection(bool sms, string selection, bool allowed)
    {
        var options = IndependentPolicyPaidOptions(sms, selection);
        var grant = new[] { allowed ? "modem-b" : "modem-a" };
        options.ExtensionPolicies["alice"] = IndependentPolicyJson<SipExtensionPolicy>(JsonSerializer.Serialize(new
        {
            AllowCalls = true, AllowSms = true,
            OutgoingLines = sms ? new[] { "modem-b" } : grant,
            SmsSendLines = sms ? grant : new[] { "modem-b" }
        }));
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        await Authenticate(alice, IndependentPolicyPaidRequest(sms, selection), "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(allowed ? (sms ? 202 : 503) : 403, (await Final(alice)).StatusCode);
        Assert.Equal(allowed && sms ? 1 : 0, controller.SmsCount);
        Assert.Equal(allowed && !sms ? 1 : 0, controller.DialCount);
        if (allowed) Assert.Equal("modem-b", controller.LastLine);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IndependentPolicyExplicitEmptyPaidListsDenyEveryLine(bool sms, bool automatic)
    {
        var options = IndependentPolicyPaidOptions(sms, "header");
        if (automatic) options = options with { RequireConfiguredLines = false, LineRoutes = new(), DefaultOutboundLineId = null };
        options.ExtensionPolicies["alice"] = IndependentPolicyJson<SipExtensionPolicy>(
            "{\"AllowCalls\":true,\"AllowSms\":true,\"OutgoingLines\":[],\"SmsSendLines\":[]}");
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        await Authenticate(alice, IndependentPolicyPaidRequest(sms, automatic ? "automatic" : "header"), "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(403, (await Final(alice)).StatusCode);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IndependentPolicyPeerPaidListsDoNotBorrowTheLocalPrincipal(bool sms, bool allowed)
    {
        using var peer = Socket();
        var options = IndependentPolicyPaidOptions(sms, "header");
        // A SAME-NAMED local account's broad grant cannot stand in for the
        // independently authenticated fixed peer principal.
        options.Accounts.Add("peer-in", "test-peer-in");
        options.ExtensionPolicies["peer-in"] = IndependentPolicyJson<SipExtensionPolicy>(
            "{\"AllowCalls\":true,\"AllowSms\":true,\"OutgoingLines\":[\"modem-b\"],\"SmsSendLines\":[\"modem-b\"]}");
        var grant = new[] { allowed ? "modem-b" : "modem-a" };
        options = options with { Peer = IndependentPolicyPeer(peer, JsonSerializer.Serialize(new
        { OutgoingLines = sms ? new[] { "modem-b" } : grant, SmsSendLines = sms ? grant : new[] { "modem-b" } })) };
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync();
        await IndependentPolicyAuthenticatePeer(peer, IndependentPolicyPaidRequest(sms, "header"), options, gateway.Status.LocalEndPoint!);
        Assert.Equal(allowed ? (sms ? 202 : 503) : 403, (await Final(peer)).StatusCode);
        Assert.Equal(allowed && sms ? 1 : 0, controller.SmsCount);
        Assert.Equal(allowed && !sms ? 1 : 0, controller.DialCount);
        if (allowed) Assert.Equal("modem-b", controller.LastLine);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task IndependentPolicyBoundReplyKeepsItsOriginalPaidLineDespiteNumericAccountCollision(bool fromPeer, bool allowed, bool collision)
    {
        using var peer = Socket(); using var alice = Socket(); using var numeric = Socket();
        var options = IndependentPolicyPaidOptions(true, "header");
        var grant = new[] { allowed ? "modem-b" : "modem-a" };
        options.ExtensionPolicies["alice"] = IndependentPolicyJson<SipExtensionPolicy>(JsonSerializer.Serialize(new
        { AllowSms = true, SmsSendLines = grant }));
        if (fromPeer) options = options with { Peer = IndependentPolicyPeer(peer, JsonSerializer.Serialize(new { SmsSendLines = grant })) };
        if (collision) options.Accounts.Add("15551234567", "test-15551234567");
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        if (collision) await Register(numeric, "15551234567", endpoint);
        var observer = collision ? IndependentPolicyObserve(numeric, endpoint) : Task.FromResult<SipMessage?>(null);
        var sender = collision ? "15551234567" : "+15551234567";
        var alias = SipGateway.SmsReplyUser("modem-b", sender, TestSmsBinding("modem-b"));
        var request = Text(alias, "synthetic reply stays on its original SIM");
        if (fromPeer) await IndependentPolicyAuthenticatePeer(peer, request, options, endpoint);
        else await Authenticate(alice, request, "alice", endpoint);
        var response = await Final(fromPeer ? peer : alice);
        var internalCopy = await observer;
        // A bare-national legacy alias proves no receive-time country. Even
        // an authorized sender cannot promote it using today's normalization,
        // nor may it fall through to the identically named local account.
        var replyAllowed = allowed && !collision;
        Assert.Equal(replyAllowed ? 202 : 403, response.StatusCode);
        Assert.Null(internalCopy);
        Assert.Equal(replyAllowed ? 1 : 0, controller.SmsCount);
        Assert.Equal(replyAllowed ? 1 : 0, controller.BoundSmsCount);
        Assert.Equal(0, controller.DialCount);
        if (replyAllowed) Assert.Equal("modem-b", controller.LastLine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentPolicyIncomingLocalCallListOverridesTheLegacyAllRecipients(bool selectBob)
    {
        using var peer = Socket(); using var alice = Socket(); using var bob = Socket();
        var options = Options() with { Peer = IndependentPolicyPeer(peer, "{}") };
        options.LineRoutes["modem-a"] = IndependentPolicyJson<SipGatewayLineRoute>(JsonSerializer.Serialize(new
        { AllowIncomingCalls = true, LocalIncomingExtensions = selectBob ? new[] { "bob" } : Array.Empty<string>(), ForwardCallsToPeer = false }));
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller) { InviteTransactionTimeout = TimeSpan.FromMilliseconds(800) };
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        await Register(alice, "alice", endpoint); await Register(bob, "bob", endpoint);
        var observedAlice = IndependentPolicyObserve(alice, endpoint);
        var observedBob = IndependentPolicyObserve(bob, endpoint);
        var observedPeer = IndependentPolicyObserve(peer, endpoint);
        controller.Incoming("modem-a");
        var results = await Task.WhenAll(observedAlice, observedBob, observedPeer);
        Assert.Null(results[0]); Assert.Null(results[2]);
        if (selectBob) { Assert.NotNull(results[1]); Assert.Equal("INVITE", results[1]!.Method); }
        else Assert.Null(results[1]);
        Assert.Equal(0, controller.DialCount); Assert.Equal(0, controller.SmsCount);
    }

    [Theory]
    [InlineData("peer")]
    [InlineData("line")]
    [InlineData("allowed")]
    public async Task IndependentPolicyIncomingPeerCallDirectionIsSeparateFromLocalAndOutgoingGrants(string control)
    {
        using var peer = Socket();
        var options = Options() with
        {
            Accounts = new(),
            Peer = IndependentPolicyPeer(peer, JsonSerializer.Serialize(new { AllowIncomingCalls = control != "peer" }))
        };
        options.LineRoutes["modem-a"] = IndependentPolicyJson<SipGatewayLineRoute>(JsonSerializer.Serialize(new
        { AllowIncomingCalls = true, IncomingTarget = "600", LocalIncomingExtensions = Array.Empty<string>(), ForwardCallsToPeer = control != "line" }));
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller) { InviteTransactionTimeout = TimeSpan.FromMilliseconds(800) };
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        var observed = IndependentPolicyObserve(peer, endpoint);
        controller.Incoming("modem-a");
        var forwarded = await observed;
        if (control == "allowed") { Assert.NotNull(forwarded); Assert.Contains("sip:600@", forwarded.RequestUri); }
        else Assert.Null(forwarded);
        Assert.Equal(0, controller.DialCount); Assert.Equal(0, controller.SmsCount);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("local")]
    [InlineData("peer-only")]
    [InlineData("peer-off")]
    [InlineData("line-off")]
    public async Task IndependentPolicyDurableMtRecipientsSeparateExplicitListsAndPeerDirections(string control)
    {
        using var peer = Socket();
        var journal = new ForwardJournal();
        var options = ForwardOptions(journal) with
        {
            PeerCredentialEpoch = new('e', 32),
            Peer = IndependentPolicyPeer(peer, JsonSerializer.Serialize(new { AllowIncomingMessages = control is not ("peer-off" or "none") }))
        };
        options.ExtensionPolicies["bob"] = IndependentPolicyJson<SipExtensionPolicy>("{\"AllowSms\":false,\"AllowMessages\":false}");
        options.LineRoutes["modem-a"] = IndependentPolicyJson<SipGatewayLineRoute>(JsonSerializer.Serialize(new
        {
            ForwardIncomingSms = true, SmsTarget = "sms-in",
            LocalSmsRecipients = control == "local" ? new[] { "bob" } : Array.Empty<string>(),
            ForwardSmsToPeer = control is "peer-only" or "peer-off"
        }));
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync();
        var recipients = gateway.CaptureSmsForwardRecipients("modem-a");
        if (control == "local")
        {
            var only = Assert.Single(recipients);
            Assert.Equal(SipSmsForwardRecipientKind.Local, only.Kind); Assert.Equal("bob", only.User);
        }
        else if (control == "peer-only")
        {
            var only = Assert.Single(recipients);
            Assert.Equal(SipSmsForwardRecipientKind.Peer, only.Kind); Assert.Equal("sms-in", only.User);
        }
        else Assert.Empty(recipients);
        Assert.Equal(0, journal.Claims);
        Assert.Equal(0, controller.DialCount); Assert.Equal(0, controller.SmsCount);
        await NoForwardDatagram(peer);
    }

    private sealed class IndependentPolicyController : ISipGatewayCallController, ISipGatewaySimBoundMessageController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall;
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public event EventHandler<SmsReceivedEventArgs>? SmsReceived { add { } remove { } }
        public int DialCount, SmsCount, BoundSmsCount;
        public string? LastLine;
        public void Incoming(string line) => IncomingCall?.Invoke(this,
            new(Guid.NewGuid().ToString("N"), "+15551234567", slotId: line));
        public string? GetSmsSimBinding(string slotId) => TestSmsBinding(slotId);
        public Task<SmsSubmitResult> SendSmsForSimAsync(string number, string text, string slotId, string expectedBinding, CancellationToken ct)
        {
            Assert.Equal(TestSmsBinding(slotId), expectedBinding);
            Interlocked.Increment(ref BoundSmsCount);
            return SendSmsAsync(number, text, slotId, ct);
        }
        public Task<SmsSubmitResult> SendSmsAsync(string number, string text, string? slotId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref SmsCount); LastLine = slotId;
            return Task.FromResult(new SmsSubmitResult(number, text, SmsEncoding.Ucs2, null,
                1, 1, 1, true, "accepted", [], DateTime.UtcNow));
        }
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref DialCount); LastLine = slotId;
            throw new NotSupportedException("Synthetic carrier remains offline; no external call is made.");
        }
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task HangupAsync(string? slotId, CancellationToken ct) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
