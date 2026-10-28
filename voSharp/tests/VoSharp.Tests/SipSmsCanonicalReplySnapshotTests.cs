using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;

namespace VoSharp.Tests;

// Real gateway/loopback MESSAGE tests with the existing explicitly memory-only
// journal and synthetic bound controller. These are not SQLite, modem or carrier
// proofs. Reflection keeps the preceding API compiling for an assertion RED.
public sealed partial class SipServerFeatureTests
{
    private const string CanonicalSnapshotNational = "15550000001";
    private const string CanonicalSnapshotOldNumber = "+8615550000001";
    private const string CanonicalSnapshotNewNumber = "+6315550000001";

    private static SipGatewayOptions CanonicalSnapshotOptions(ForwardJournal journal, string country = "86")
    {
        var options = ForwardOptions(journal) with { DefaultOutboundLineId = "modem-b" };
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with
        {
            NormalizeE164 = true, CountryCode = country, NationalNumberLength = 11,
            Prefix = "81", ForwardIncomingSms = true, AllowOutgoingSms = true
        };
        // An original-line reply must not run this unrelated selection/rewrite.
        options.DialRules.Add(new() { Prefix = "155", LineId = "modem-b", StripPrefix = true, Prepend = "+199" });
        return options;
    }

    private static string CanonicalSnapshotCapture(SipGateway gateway, string? line, string sender)
    {
        var method = typeof(SipGateway).GetMethod("CaptureSmsReplyNumber", BindingFlags.Instance | BindingFlags.Public,
            binder: null, types: [typeof(string), typeof(string)], modifiers: null);
        Assert.True(method is not null, "An explicit admission-time canonical SMS reply capture API must exist.");
        Assert.NotNull(method);
        Assert.Equal(typeof(string), method.ReturnType);
        return Assert.IsType<string>(method.Invoke(gateway, [line, sender]));
    }

    private static PropertyInfo CanonicalSnapshotReplyProperty()
    {
        var property = typeof(SipSmsForwardPayload).GetProperty("ReplyNumber", BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, "The original MT payload must retain an explicit canonical ReplyNumber snapshot.");
        Assert.NotNull(property);
        Assert.Equal(typeof(string), property.PropertyType);
        Assert.NotNull(property.SetMethod);
        Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        return property;
    }

    private static SipSmsForwardPayload CanonicalSnapshotPayload(string? reply, string? binding = null,
        string sender = CanonicalSnapshotNational, string text = "synthetic original canonical SMS")
    {
        var payload = new SipSmsForwardPayload("modem-a", binding ?? TestSmsBinding("modem-a"), sender, text);
        CanonicalSnapshotReplyProperty().SetValue(payload, reply);
        return payload;
    }

    private static string CanonicalSnapshotFromUser(SipMessage message, string originalSender)
    {
        var from = message.GetHeader("From") ?? "";
        Assert.StartsWith("\"" + originalSender + "\" <sip:", from);
        var user = Regex.Match(from, @"<sip:([^@>]+)@").Groups[1].Value;
        Assert.NotEmpty(user);
        return user;
    }

    private static async Task<SipMessage> CanonicalSnapshotDeliver(SipGateway gateway, ForwardJournal journal,
        UdpClient receiver, string id, string originalSender, string originalBody)
    {
        gateway.NotifySmsInboxCommitted();
        var message = await Receive(receiver);
        Assert.Equal("MESSAGE", message.Method);
        Assert.Equal(originalBody, message.Body);
        _ = CanonicalSnapshotFromUser(message, originalSender);
        await Send(receiver, message.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(id) == SipSmsForwardState.Accepted);
        return message;
    }

    private static async Task CanonicalSnapshotSendReply(UdpClient phone, SipGateway gateway, string alias, int expected,
        string body = "synthetic original-card canonical reply")
    {
        await Authenticate(phone, Text(alias, body), "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(expected, (await Receive(phone)).StatusCode);
    }

    [Theory]
    [InlineData("modem-a", "+15551234567", true, "+15551234567")]
    [InlineData("modem-a", CanonicalSnapshotNational, true, CanonicalSnapshotOldNumber)]
    [InlineData("MODEM-A", CanonicalSnapshotNational, true, CanonicalSnapshotOldNumber)]
    [InlineData("modem-a", CanonicalSnapshotNational, false, "")]
    [InlineData(null, CanonicalSnapshotNational, true, "")]
    [InlineData("missing-line", CanonicalSnapshotNational, true, "")]
    [InlineData("modem-a", "1555000000", true, "")]
    [InlineData("modem-a", "12345", true, "")]
    [InlineData("modem-a", "SYNTHETIC-BANK", true, "")]
    [InlineData("modem-a", "15550000001 ", true, "")]
    [InlineData("modem-a", "１５５５００００００１", true, "")]
    [InlineData("modem-a", "+01551234567", true, "")]
    [InlineData("modem-a", "+123456789012345", true, "+123456789012345")]
    [InlineData("modem-a", "+1234567890123456", true, "")]
    [InlineData("modem-a", "+15551234567\n", true, "")]
    [InlineData("modem-a", "+15551234567 ", true, "")]
    public async Task CanonicalSnapshotCaptureUsesOnlyExactAdmissionLineAndStrictSender(string? line, string sender,
        bool normalize, string expected)
    {
        var journal = new ForwardJournal();
        var options = CanonicalSnapshotOptions(journal);
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with { NormalizeE164 = normalize };
        await using var gateway = new SipGateway(options, new Controller());
        await gateway.StartAsync();
        Assert.Equal(expected, CanonicalSnapshotCapture(gateway, line, sender));
        Assert.Equal(0, journal.Claims);
    }

    [Fact]
    public void CanonicalSnapshotPayloadDistinguishesLegacyNullReadOnlyAndCanonicalNumberWithoutChangingV1()
    {
        var payload = new SipSmsForwardPayload("modem-a", TestSmsBinding("modem-a"), CanonicalSnapshotNational,
            "synthetic fingerprint source");
        var property = CanonicalSnapshotReplyProperty();
        Assert.Null(property.GetValue(payload));
        var oldFields = new[] { "sip-sms-forward-payload-v1", payload.LineId, payload.SimBinding!, payload.Sender, payload.Text };
        var oldFrame = string.Concat(oldFields.Select(value => Encoding.UTF8.GetByteCount(value)
            .ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value));
        var oldFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldFrame))).ToLowerInvariant();
        Assert.Equal(oldFingerprint, payload.Fingerprint());

        var readOnly = payload with { };
        property.SetValue(readOnly, "");
        var canonical = payload with { };
        property.SetValue(canonical, CanonicalSnapshotOldNumber);
        var changed = canonical with { };
        property.SetValue(changed, CanonicalSnapshotNewNumber);
        Assert.Equal(oldFingerprint, payload.Fingerprint());
        Assert.NotEqual(oldFingerprint, readOnly.Fingerprint());
        Assert.NotEqual(readOnly.Fingerprint(), canonical.Fingerprint());
        Assert.NotEqual(canonical.Fingerprint(), changed.Fingerprint());
        Assert.Equal(canonical.Fingerprint(), (canonical with { }).Fingerprint());
        Assert.DoesNotContain(CanonicalSnapshotNational, canonical.ToString());
        Assert.DoesNotContain(CanonicalSnapshotOldNumber, canonical.ToString());
    }

    [Fact]
    public async Task CanonicalSnapshotQueuedUnder86Survives63GatewayRestartAndFreshSourceUses63()
    {
        var journal = new ForwardJournal();
        string originalId;
        var originalBody = "synthetic SMS admitted under the original +86 policy";
        await using (var original = new SipGateway(CanonicalSnapshotOptions(journal), new Controller()))
        {
            await original.StartAsync();
            var recipient = original.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice");
            var captured = CanonicalSnapshotCapture(original, "modem-a", CanonicalSnapshotNational);
            Assert.Equal(CanonicalSnapshotOldNumber, captured);
            originalId = journal.Add(recipient, CanonicalSnapshotPayload(captured, text: originalBody));
            await ForwardUntil(() => journal.Scans >= 2);
            Assert.Equal(SipSmsForwardState.Queued, journal.State(originalId));
            Assert.Equal(0, journal.Claims);
        }

        var controller = new Controller();
        await using var restarted = new SipGateway(CanonicalSnapshotOptions(journal, "63"), controller);
        await restarted.StartAsync();
        using var alice = Socket();
        await Register(alice, "alice", restarted.Status.LocalEndPoint!);
        var oldMessage = await CanonicalSnapshotDeliver(restarted, journal, alice, originalId,
            CanonicalSnapshotNational, originalBody);
        var oldAlias = CanonicalSnapshotFromUser(oldMessage, CanonicalSnapshotNational);
        Assert.True(SipGateway.TryParseSmsReplyUser(oldAlias, out _, out var oldBinding, out var oldNumber));
        Assert.Equal(TestSmsBinding("modem-a"), oldBinding);
        Assert.Equal(CanonicalSnapshotOldNumber, oldNumber);
        await CanonicalSnapshotSendReply(alice, restarted, oldAlias, 202);
        Assert.Equal(1, controller.SmsCount);
        Assert.Equal("modem-a", controller.LastLine);
        Assert.Equal(CanonicalSnapshotOldNumber, controller.LastNumber);

        var freshReply = CanonicalSnapshotCapture(restarted, "modem-a", CanonicalSnapshotNational);
        Assert.Equal(CanonicalSnapshotNewNumber, freshReply);
        var freshBody = "synthetic distinct SMS admitted under the new +63 policy";
        var freshId = journal.Add(restarted.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"),
            CanonicalSnapshotPayload(freshReply, text: freshBody));
        var freshMessage = await CanonicalSnapshotDeliver(restarted, journal, alice, freshId,
            CanonicalSnapshotNational, freshBody);
        var freshAlias = CanonicalSnapshotFromUser(freshMessage, CanonicalSnapshotNational);
        Assert.True(SipGateway.TryParseSmsReplyUser(freshAlias, out _, out var freshBinding, out var freshNumber));
        Assert.Equal(oldBinding, freshBinding);
        Assert.Equal(CanonicalSnapshotNewNumber, freshNumber);
        await CanonicalSnapshotSendReply(alice, restarted, freshAlias, 202, "synthetic new-policy canonical reply");
        Assert.Equal(2, controller.SmsCount);
        Assert.Equal("modem-a", controller.LastLine);
        Assert.Equal(CanonicalSnapshotNewNumber, controller.LastNumber);
        Assert.Equal(0, controller.DialCount);
        Assert.Equal(2, journal.Claims);
        await NoForwardDatagram(alice);
    }

    [Theory]
    [InlineData(CanonicalSnapshotNational, false)]
    [InlineData("+15551234567", true)]
    public async Task CanonicalSnapshotLegacyNullPayloadNeverReinterpretsNationalSender(string sender, bool canReply)
    {
        var journal = new ForwardJournal();
        var controller = new Controller();
        await using var gateway = new SipGateway(CanonicalSnapshotOptions(journal, "63"), controller);
        await gateway.StartAsync(); using var alice = Socket();
        await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var payload = CanonicalSnapshotPayload(null, sender: sender);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"), payload);
        var message = await CanonicalSnapshotDeliver(gateway, journal, alice, id, sender, payload.Text);
        var alias = CanonicalSnapshotFromUser(message, sender);
        if (canReply)
        {
            Assert.True(SipGateway.TryParseSmsReplyUser(alias, out _, out _, out var number));
            Assert.Equal(sender, number);
        }
        else
        {
            Assert.StartsWith("sms-ro-", alias);
            Assert.False(SipGateway.TryParseSmsReplyUser(alias, out _, out _, out _));
        }
        await CanonicalSnapshotSendReply(alice, gateway, alias, canReply ? 202 : 403);
        Assert.Equal(canReply ? 1 : 0, controller.SmsCount);
        if (canReply) Assert.Equal(sender, controller.LastNumber);
    }

    [Fact]
    public async Task CanonicalSnapshotBareNationalBoundAliasCannotAcquireTheCurrentCountryPolicy()
    {
        var controller = new Controller();
        await using var gateway = new SipGateway(CanonicalSnapshotOptions(new ForwardJournal(), "63"), controller);
        await gateway.StartAsync(); using var alice = Socket();
        // The old public codec can round-trip a raw number, but that does not
        // prove which country's destination was intended when it was received.
        var oldAlias = SipGateway.SmsReplyUser("modem-a", CanonicalSnapshotNational, TestSmsBinding("modem-a"));
        Assert.True(SipGateway.TryParseSmsReplyUser(oldAlias, out _, out _, out var raw));
        Assert.Equal(CanonicalSnapshotNational, raw);
        await CanonicalSnapshotSendReply(alice, gateway, oldAlias, 403);
        Assert.Equal(0, controller.SmsCount);
        Assert.Equal(0, controller.DialCount);
    }

    [Theory]
    [InlineData("explicit-read-only")]
    [InlineData("unknown-original-sim")]
    public async Task CanonicalSnapshotReadOnlyDecisionOrUnknownSimCannotGainReplyPermission(string source)
    {
        var journal = new ForwardJournal();
        var controller = new Controller();
        await using var gateway = new SipGateway(CanonicalSnapshotOptions(journal), controller);
        await gateway.StartAsync(); using var alice = Socket();
        await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var payload = CanonicalSnapshotPayload(source == "explicit-read-only" ? "" : CanonicalSnapshotOldNumber);
        if (source == "unknown-original-sim") payload = payload with { SimBinding = null };
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"), payload);
        var message = await CanonicalSnapshotDeliver(gateway, journal, alice, id, payload.Sender, payload.Text);
        var alias = CanonicalSnapshotFromUser(message, payload.Sender);
        Assert.StartsWith("sms-ro-", alias);
        Assert.False(SipGateway.TryParseSmsReplyUser(alias, out _, out _, out _));
        await CanonicalSnapshotSendReply(alice, gateway, alias, 403);
        Assert.Equal(0, controller.SmsCount);
        Assert.Equal(0, controller.DialCount);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("line")]
    [InlineData("account")]
    [InlineData("wrong-original-sim")]
    public async Task CanonicalSnapshotCanonicalReplyDoesNotBypassCurrentPaidGrantsOrOriginalSim(string disabled)
    {
        var journal = new ForwardJournal();
        var options = CanonicalSnapshotOptions(journal);
        if (disabled == "global") options = options with { AllowOutgoingSms = false };
        if (disabled == "line") options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with { AllowOutgoingSms = false };
        if (disabled == "account") options.ExtensionPolicies["alice"] = new() { AllowSms = false };
        var controller = new Controller();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var binding = TestSmsBinding(disabled == "wrong-original-sim" ? "modem-b" : "modem-a");
        var payload = CanonicalSnapshotPayload(CanonicalSnapshotOldNumber, binding);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"), payload);
        var message = await CanonicalSnapshotDeliver(gateway, journal, alice, id, payload.Sender, payload.Text);
        var alias = CanonicalSnapshotFromUser(message, payload.Sender);
        Assert.True(SipGateway.TryParseSmsReplyUser(alias, out _, out var actualBinding, out var number));
        Assert.Equal(binding, actualBinding);
        Assert.Equal(CanonicalSnapshotOldNumber, number);
        await CanonicalSnapshotSendReply(alice, gateway, alias, 403);
        Assert.Equal(0, controller.SmsCount);
        Assert.Equal(0, controller.DialCount);
    }
}
