using System.Text.Json;
using VoSharp.Kernel.SipGateway;

namespace VoSharp.Tests;

// Behavioral RED deliberately uses only the existing record/API. The old
// product ignores the future JSON route fields, then fails on real selection
// or number assertions rather than failing to compile on missing properties.
public sealed partial class SipServerFeatureTests
{
    private static SipGatewayLineRoute DeploymentProjectionRoute(string prefix, string country, int length) =>
        JsonSerializer.Deserialize<SipGatewayLineRoute>(JsonSerializer.Serialize(new
        {
            Enabled = true, AllowIncomingCalls = true, AllowOutgoingCalls = true,
            ForwardIncomingSms = true, AllowOutgoingSms = true,
            Prefix = prefix, CountryCode = country, NationalNumberLength = length, NormalizeE164 = true
        }))!;

    private static SipGatewayOptions DeploymentProjectionLineOptions()
    {
        var options = Options();
        options.LineRoutes["modem-a"] = DeploymentProjectionRoute("8", "86", 11);
        options.LineRoutes["modem-b"] = DeploymentProjectionRoute("81", "63", 10);
        options.ExtensionPolicies["alice"] = new()
        {
            AllowCalls = true, AllowSms = true, AllowMessages = true,
            OutgoingLines = ["modem-a", "modem-b"], SmsSendLines = ["modem-a", "modem-b"]
        };
        options.Validate();
        return options;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineLongestPrefixIsSelectedAndStrippedBeforeNationalNormalization(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        var result = options.ResolveDestination("819171234567", null, "alice", sms);
        Assert.Equal("modem-b", result.Line);
        Assert.Equal("+639171234567", result.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineDefaultLineNormalizesItsOwnNationalNumber(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        var result = options.ResolveDestination("13800138000", null, "alice", sms);
        Assert.Equal("modem-a", result.Line);
        Assert.Equal("+8613800138000", result.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineExplicitLineNormalizesAfterSelection(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        var result = options.ResolveDestination("9171234567", "modem-b", "alice", sms);
        Assert.Equal("modem-b", result.Line);
        Assert.Equal("+639171234567", result.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineExplicitSelectionDoesNotReinterpretANationalNumberAsAnotherLinePrefix(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-a"] = DeploymentProjectionRoute("8", "86", 12);
        // Canonical replies still cannot be stolen by a later selection rule.
        options.DialRules.Insert(0, new() { Prefix = "+86", LineId = "modem-b" });
        var result = options.ResolveDestination("819171234567", "modem-a", "alice", sms);
        Assert.Equal("modem-a", result.Line);
        Assert.Equal("+86819171234567", result.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLinePrefixCannotFallbackToAPermittedDefaultWhenItsSelectedLineIsDenied(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        options.ExtensionPolicies["alice"] = new()
        {
            AllowCalls = true, AllowSms = true,
            OutgoingLines = ["modem-a"], SmsSendLines = ["modem-a"]
        };
        var failure = Assert.Throws<SipGatewayLineRoutingException>(() =>
            options.ResolveDestination("819171234567", null, "alice", sms));
        Assert.Equal(403, failure.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineVoiceAndSmsListsRemainIndependentAfterPrefixSelection(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        options.ExtensionPolicies["alice"] = new()
        {
            AllowCalls = true, AllowSms = true,
            OutgoingLines = ["modem-a"], SmsSendLines = ["modem-b"]
        };
        if (!sms)
        {
            Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() =>
                options.ResolveDestination("819171234567", null, "alice")).StatusCode);
            return;
        }
        (string Number, string? Line) result = ("", null);
        var failure = Record.Exception(() =>
        {
            result = options.ResolveDestination("819171234567", null, "alice", sms: true);
        });
        Assert.Null(failure);
        Assert.Equal("modem-b", result.Line); Assert.Equal("+639171234567", result.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineAlreadyInternationalNumberIsNotRebasedToItsDefaultCountry(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        var result = options.ResolveDestination("+639171234567", null, "alice", sms);
        Assert.Equal("modem-a", result.Line); Assert.Equal("+639171234567", result.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentProjectionLineActualPaidRequestUsesTheSelectedPrefixLine(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        var controller = new IndependentPolicyController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        var request = sms ? Text("819171234567", "synthetic projected SMS") : IndependentPolicyInvite("819171234567");
        await Authenticate(alice, request, "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(sms ? 202 : 503, (await Final(alice)).StatusCode);
        Assert.Equal(sms ? 1 : 0, controller.SmsCount); Assert.Equal(sms ? 0 : 1, controller.DialCount);
        Assert.Equal("modem-b", controller.LastLine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentProjectionLineBoundSmsReplyNeverSelectsAnotherPrefixLine(bool permitted)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-a"] = DeploymentProjectionRoute("8", "86", 12);
        options.ExtensionPolicies["alice"] = new()
        {
            AllowCalls = true, AllowSms = true,
            OutgoingLines = ["modem-a", "modem-b"],
            SmsSendLines = permitted ? ["modem-a"] : ["modem-b"]
        };
        var controller = new Controller();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        var original = "819171234567";
        // National aliases are no longer paid destinations: capture their
        // country interpretation at receive time, not when the reply arrives.
        var captured = gateway.CaptureSmsReplyNumber("modem-a", original);
        Assert.Equal("+86819171234567", captured);
        var alias = SipGateway.SmsReplyUser("modem-a", captured, TestSmsBinding("modem-a"));
        await Authenticate(alice, Text(alias, "synthetic original-card reply"), "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(permitted ? 202 : 403, (await Final(alice)).StatusCode);
        Assert.Equal(permitted ? 1 : 0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
        if (permitted)
        {
            Assert.Equal("modem-a", controller.LastLine);
            Assert.Equal("+86819171234567", controller.LastNumber);
        }
    }

    [Fact]
    public async Task DeploymentProjectionLineKnownInternalAccountIsNotRewrittenAsACarrierPrefix()
    {
        var options = DeploymentProjectionLineOptions();
        options.Accounts["81"] = "test-81";
        options.ExtensionPolicies["81"] = new() { AllowMessages = true };
        var controller = new Controller();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket(); using var receiver = Socket();
        var endpoint = gateway.Status.LocalEndPoint!;
        await Register(receiver, "81", endpoint);
        var forwarded = IndependentPolicyObserve(receiver, endpoint);
        await Authenticate(alice, Text("81", "synthetic internal numeric account"), "alice", endpoint);
        Assert.Equal(200, (await Final(alice)).StatusCode);
        Assert.Equal("synthetic internal numeric account", (await forwarded)!.Body);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }
}
