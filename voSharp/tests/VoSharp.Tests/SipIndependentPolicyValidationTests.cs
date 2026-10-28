using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoSharp.Kernel.SipGateway;

namespace VoSharp.Tests;

public sealed partial class SipServerFeatureTests
{
    private static SipGatewayOptions IndependentPolicyValidationOptions()
    {
        using var peer = Socket();
        return Options() with { Peer = RuntimePeer(peer) };
    }

    private static SipGatewayOptions IndependentPolicyValidationSetList(SipGatewayOptions options,
        string role, IReadOnlyList<string>? values)
    {
        switch (role)
        {
            case "local-call":
                options.ExtensionPolicies["alice"] = new() { OutgoingLines = values }; break;
            case "local-sms":
                options.ExtensionPolicies["alice"] = new() { SmsSendLines = values }; break;
            case "peer-call":
                options = options with { Peer = options.Peer! with { OutgoingLines = values } }; break;
            case "peer-sms":
                options = options with { Peer = options.Peer! with { SmsSendLines = values } }; break;
            case "incoming-call":
                options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with { LocalIncomingExtensions = values }; break;
            case "incoming-sms":
                options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with { LocalSmsRecipients = values }; break;
            default: throw new ArgumentOutOfRangeException(nameof(role));
        }
        return options;
    }

    [Fact]
    public void IndependentPolicyValidationDeepCloneSeparatesEveryNewList()
    {
        using var peer = Socket();
        var localCalls = new List<string> { "modem-a" };
        var localSms = new List<string> { "modem-b" };
        var peerCalls = new List<string> { "modem-b" };
        var peerSms = new List<string> { "modem-a" };
        var incomingCalls = new List<string> { "alice" };
        var incomingSms = new List<string> { "bob" };
        var source = Options() with { Peer = RuntimePeer(peer) with { OutgoingLines = peerCalls, SmsSendLines = peerSms } };
        source.ExtensionPolicies["alice"] = new() { OutgoingLines = localCalls, SmsSendLines = localSms };
        source.LineRoutes["modem-a"] = source.LineRoutes["modem-a"] with
        { LocalIncomingExtensions = incomingCalls, LocalSmsRecipients = incomingSms };
        source.Validate();
        var copy = source.DeepClone();
        localCalls.Clear(); localSms.Clear(); peerCalls.Clear(); peerSms.Clear(); incomingCalls.Clear(); incomingSms.Clear();
        Assert.Equal(new[] { "modem-a" }, copy.ExtensionPolicies["alice"].OutgoingLines);
        Assert.Equal(new[] { "modem-b" }, copy.ExtensionPolicies["alice"].SmsSendLines);
        Assert.Equal(new[] { "modem-b" }, copy.Peer!.OutgoingLines);
        Assert.Equal(new[] { "modem-a" }, copy.Peer.SmsSendLines);
        Assert.Equal(new[] { "alice" }, copy.LineRoutes["modem-a"].LocalIncomingExtensions);
        Assert.Equal(new[] { "bob" }, copy.LineRoutes["modem-a"].LocalSmsRecipients);
        copy.Validate();
    }

    [Fact]
    public async Task IndependentPolicyValidationRunningSnapshotAndStatusOwnTheirIncomingLists()
    {
        var calls = new List<string> { "alice" }; var sms = new List<string> { "bob" };
        var source = Options();
        source.LineRoutes["modem-a"] = source.LineRoutes["modem-a"] with
        { LocalIncomingExtensions = calls, LocalSmsRecipients = sms };
        await using var gateway = new SipGateway(source, new IndependentPolicyController());
        calls[0] = "bob"; sms[0] = "alice";
        var first = gateway.Status.LineRoutes["modem-a"];
        Assert.Equal(new[] { "alice" }, first.LocalIncomingExtensions);
        Assert.Equal(new[] { "bob" }, first.LocalSmsRecipients);
        ((IList<string>)first.LocalIncomingExtensions!)[0] = "900";
        ((IList<string>)first.LocalSmsRecipients!)[0] = "900";
        Assert.Equal(new[] { "alice" }, gateway.Status.LineRoutes["modem-a"].LocalIncomingExtensions);
        Assert.Equal(new[] { "bob" }, gateway.Status.LineRoutes["modem-a"].LocalSmsRecipients);
    }

    [Theory]
    [InlineData("local-call")]
    [InlineData("local-sms")]
    [InlineData("peer-call")]
    [InlineData("peer-sms")]
    [InlineData("incoming-call")]
    [InlineData("incoming-sms")]
    public void IndependentPolicyValidationListsRejectDuplicateUnknownMalformedAndNullReferences(string role)
    {
        var incoming = role.StartsWith("incoming", StringComparison.Ordinal);
        var valid = incoming ? "alice" : "modem-a";
        foreach (var values in new IReadOnlyList<string>[]
        {
            new[] { valid, valid.ToUpperInvariant() },
            new[] { incoming ? "unknown-account" : "unknown-line" },
            new[] { valid + "\r\n" },
            new string[] { null! },
            new[] { "" }
        })
        {
            var options = IndependentPolicyValidationSetList(IndependentPolicyValidationOptions(), role, values);
            Assert.Throws<ArgumentException>(options.Validate);
        }
        IndependentPolicyValidationSetList(IndependentPolicyValidationOptions(), role, null).Validate();
        IndependentPolicyValidationSetList(IndependentPolicyValidationOptions(), role, Array.Empty<string>()).Validate();
        IndependentPolicyValidationSetList(IndependentPolicyValidationOptions(), role, new[] { valid.ToUpperInvariant() }).Validate();
    }

    [Theory]
    [InlineData("local-call")]
    [InlineData("local-sms")]
    [InlineData("peer-call")]
    [InlineData("peer-sms")]
    [InlineData("incoming-call")]
    [InlineData("incoming-sms")]
    public void IndependentPolicyValidationListsAcceptExactlySixtyFourAndRejectTheNextEntry(string role)
    {
        using var peer = Socket();
        var lines = Enumerable.Range(0, 64).Select(index => "line-" + index).ToArray();
        var users = Enumerable.Range(0, 64).Select(index => "account-" + index).ToArray();
        var options = Options() with
        {
            Peer = RuntimePeer(peer), DefaultOutboundLineId = null,
            Accounts = users.ToDictionary(user => user, user => "synthetic-password-" + user),
            LineRoutes = lines.ToDictionary(line => line, _ => new SipGatewayLineRoute())
        };
        var incoming = role.StartsWith("incoming", StringComparison.Ordinal);
        var accepted = incoming ? users : lines;
        // Use the same collection slot as the helper, without manufacturing a
        // 65th global account/line that would fail an unrelated global bound.
        options.Accounts.Add("alice", "test-alice");
        options.Accounts.Remove(users[^1]);
        if (incoming) accepted = users[..^1].Append("alice").ToArray();
        options.LineRoutes["modem-a"] = new(); options.LineRoutes.Remove(lines[^1]);
        if (!incoming) accepted = lines[..^1].Append("modem-a").ToArray();
        options = IndependentPolicyValidationSetList(options, role, accepted);
        options.Validate();
        Assert.Equal(64, accepted.Length);
        var oversized = accepted.Append(incoming ? "overflow-account" : "overflow-line").ToArray();
        // No 65 unique valid members can exist under the global 64 bound. The
        // explicit list-count guard is evaluated before member validation.
        options = IndependentPolicyValidationSetList(options, role, oversized);
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void IndependentPolicyValidationNullFieldsPreserveTheExactLegacyJsonShape()
    {
        Assert.Equal("{\"AllowCalls\":true,\"AllowSms\":false,\"DefaultLineId\":null}",
            JsonSerializer.Serialize(new SipExtensionPolicy()));
        Assert.Equal("{\"Enabled\":true,\"AllowIncomingCalls\":true,\"AllowOutgoingCalls\":true,\"IncomingTarget\":null}",
            JsonSerializer.Serialize(new SipGatewayLineRoute()));
        using var peerSocket = Socket(); var peer = RuntimePeer(peerSocket);
        var json = new JsonSerializerOptions(); json.Converters.Add(new IndependentPolicyValidationEndpointConverter());
        var legacyPeer = new
        {
            peer.EndPoint, peer.IncomingTarget, peer.Username, peer.InboundAuth,
            peer.OutboundAuth, peer.AllowOutboundCalls, peer.AllowOutboundMessages
        };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(legacyPeer, json), JsonSerializer.SerializeToUtf8Bytes(peer, json));
        var options = ForwardPeerOptions(new ForwardJournal(), peerSocket);
        Assert.Equal(SipSmsRecipientAuthority.Peer(options, "600"), SipSmsRecipientAuthority.Peer(options.DeepClone(), "600"));
        Assert.Equal(SipSmsRecipientAuthority.Local(options, "alice"), SipSmsRecipientAuthority.Local(options.DeepClone(), "alice"));
    }

    [Fact]
    public void IndependentPolicyValidationExplicitFalseAndEmptyListsAreNotDroppedFromJson()
    {
        var policy = new SipExtensionPolicy
        { AllowInternalCalls = false, AllowMessages = false, OutgoingLines = [], SmsSendLines = [] };
        var json = JsonSerializer.Serialize(policy);
        Assert.Contains("\"AllowInternalCalls\":false", json); Assert.Contains("\"AllowMessages\":false", json);
        Assert.Contains("\"OutgoingLines\":[]", json); Assert.Contains("\"SmsSendLines\":[]", json);
        var route = new SipGatewayLineRoute
        { LocalIncomingExtensions = [], LocalSmsRecipients = [], ForwardCallsToPeer = false, ForwardSmsToPeer = false };
        json = JsonSerializer.Serialize(route);
        Assert.Contains("\"LocalIncomingExtensions\":[]", json); Assert.Contains("\"LocalSmsRecipients\":[]", json);
        Assert.Contains("\"ForwardCallsToPeer\":false", json); Assert.Contains("\"ForwardSmsToPeer\":false", json);
    }

    [Theory]
    [InlineData(false, "no-peer")]
    [InlineData(false, "null-target")]
    [InlineData(false, "empty-target")]
    [InlineData(false, "explicit-target")]
    [InlineData(true, "no-peer")]
    [InlineData(true, "null-target")]
    [InlineData(true, "empty-target")]
    [InlineData(true, "explicit-target")]
    public void IndependentPolicyValidationExplicitPeerForwardingRequiresItsOwnPeerAndRouteTarget(bool sms, string control)
    {
        var options = IndependentPolicyValidationOptions();
        if (control == "no-peer") options = options with { Peer = null };
        var target = control == "null-target" ? null : control == "empty-target" ? "" : "explicit-target";
        options.LineRoutes["modem-a"] = sms
            ? new() { ForwardSmsToPeer = true, SmsTarget = target }
            : new() { ForwardCallsToPeer = true, IncomingTarget = target };
        if (control == "explicit-target") options.Validate();
        else Assert.Throws<ArgumentException>(options.Validate);
        options.LineRoutes["modem-a"] = sms ? new() { ForwardSmsToPeer = null } : new() { ForwardCallsToPeer = null };
        options.Validate(); // Legacy null still permits the existing fallback.
    }

    [Fact]
    public void IndependentPolicyValidationFinalLineAclIsCaseInsensitiveButNeverFallsBack()
    {
        var options = Options();
        options.ExtensionPolicies["alice"] = new()
        {
            AllowCalls = true, AllowSms = true, OutgoingLines = ["MODEM-A"], SmsSendLines = ["MODEM-A"],
            AllowInternalCalls = false, AllowMessages = false
        };
        options.Validate();
        Assert.Equal("modem-a", options.ResolveDestination("+15551234567", "modem-a", "ALICE").Line);
        Assert.Equal("MODEM-A", options.ResolveDestination("+15551234567", "MODEM-A", "ALICE", sms: true).Line);
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() =>
            options.ResolveDestination("+15551234567", "modem-b", "alice")).StatusCode);
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() =>
            options.ResolveDestination("+15551234567", "modem-b", "alice", sms: true)).StatusCode);
    }

    [Fact]
    public void IndependentPolicyValidationNullRetainsLegacyAutomaticSelectionButEmptyDoesNot()
    {
        var options = Options() with { RequireConfiguredLines = false, LineRoutes = new(), DefaultOutboundLineId = null };
        options.ExtensionPolicies["alice"] = new() { AllowCalls = true, AllowSms = true };
        options.Validate();
        Assert.Null(options.ResolveDestination("+15551234567", null, "alice").Line);
        Assert.Null(options.ResolveDestination("+15551234567", null, "alice", sms: true).Line);
        options.ExtensionPolicies["alice"] = new() { AllowCalls = true, AllowSms = true, OutgoingLines = [], SmsSendLines = [] };
        options.Validate();
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() => options.ResolveDestination("+15551234567", null, "alice")).StatusCode);
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() => options.ResolveDestination("+15551234567", null, "alice", sms: true)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentPolicyValidationSameNamedLocalAndPeerHaveSeparateRateBudgets(bool exhaustPeer)
    {
        using var peer = Socket(); using var local = Socket();
        var options = Options() with { Peer = RuntimePeer(peer) };
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); var endpoint = gateway.Status.LocalEndPoint!;
        Assert.Equal("alice", options.Peer!.InboundAuth!.Username);
        Assert.True(options.Accounts.ContainsKey("alice"));
        async Task<int> Submit(bool fromPeer)
        {
            // A valid authenticated but unroutable target consumes only the
            // principal's ordinary budget, without a paid or internal send.
            var request = Text("unknown-target", "synthetic principal rate check");
            if (fromPeer) await IndependentPolicyAuthenticatePeer(peer, request, options, endpoint);
            else await Authenticate(local, request, "alice", endpoint);
            return (await Final(fromPeer ? peer : local)).StatusCode;
        }
        for (var index = 0; index < 20; index++) Assert.Equal(404, await Submit(exhaustPeer));
        Assert.Equal(503, await Submit(exhaustPeer));
        Assert.Equal(404, await Submit(!exhaustPeer));
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    private sealed class IndependentPolicyValidationEndpointConverter : JsonConverter<IPEndPoint>
    {
        public override IPEndPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
