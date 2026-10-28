using VoSharp.Kernel.SipGateway;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;

namespace VoSharp.Tests;

public sealed partial class SipServerFeatureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DeploymentProjectionLineIngressAcceptsConfiguredPrefixCanonicalAndLongNationalUsers(bool sms, bool longNational)
    {
        var options = DeploymentProjectionLineOptions();
        var target = "81+639171234567";
        var expected = "+639171234567";
        if (longNational)
        {
            options.LineRoutes["modem-b"] = DeploymentProjectionRoute("12345678", "1", 14);
            target = "1234567823456789012345";
            expected = "+123456789012345";
            Assert.True(target.Length > 20);
        }
        var controller = new DeploymentProjectionIngressController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        var request = sms ? Text(target, "synthetic bounded prefix ingress") : IndependentPolicyInvite(target);
        await Authenticate(alice, request, "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(sms ? 202 : 503, (await Final(alice)).StatusCode);
        Assert.Equal(sms ? 1 : 0, controller.SmsCount); Assert.Equal(sms ? 0 : 1, controller.DialCount);
        Assert.Equal("modem-b", controller.LastLine); Assert.Equal(expected, controller.LastNumber);
    }

    [Theory]
    [InlineData(false, "99+639171234567")]
    [InlineData(true, "99+639171234567")]
    [InlineData(false, "81+01234567")]
    [InlineData(true, "81+01234567")]
    [InlineData(false, "81+1234567890123456")]
    [InlineData(true, "81+1234567890123456")]
    [InlineData(false, "81+63917x")]
    [InlineData(true, "81+63917x")]
    [InlineData(false, "9999999923456789012345")]
    [InlineData(true, "9999999923456789012345")]
    public async Task DeploymentProjectionLineIngressRejectsUnknownPrefixesAndMalformedUsersWithoutBackendWork(bool sms, string target)
    {
        var options = DeploymentProjectionLineOptions();
        var controller = new DeploymentProjectionIngressController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        var request = sms ? Text(target, "synthetic invalid prefix ingress") : IndependentPolicyInvite(target);
        await Authenticate(alice, request, "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(sms ? 404 : 484, (await Final(alice)).StatusCode);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
        Assert.Null(controller.LastLine); Assert.Null(controller.LastNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentProjectionLineIngressFramingDoesNotAuthorizeAPrefixLineOutsideThePrincipalAcl(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        options.ExtensionPolicies["alice"] = new()
        { AllowCalls = true, AllowSms = true, OutgoingLines = ["modem-a"], SmsSendLines = ["modem-a"] };
        var controller = new DeploymentProjectionIngressController();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(); using var alice = Socket();
        var request = sms ? Text("81+639171234567", "synthetic denied prefix ingress") : IndependentPolicyInvite("81+639171234567");
        await Authenticate(alice, request, "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(403, (await Final(alice)).StatusCode);
        Assert.Equal(0, controller.SmsCount); Assert.Equal(0, controller.DialCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineIngressEnvelopeIsBoundedAndDoesNotExpandAnUnconfiguredLegacyPrefix(bool sms)
    {
        var legacy = Options();
        Assert.False(legacy.IsPlausibleCarrierDestination("81+639171234567", sms));
        Assert.True(legacy.IsPlausibleCarrierDestination("+15551234567", sms));
        Assert.Equal(!sms, legacy.IsPlausibleCarrierDestination("*123#", sms));
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-b"] = DeploymentProjectionRoute("12345678", "1", 14);
        Assert.True(options.IsPlausibleCarrierDestination("12345678+123456789012345", sms));
        Assert.False(options.IsPlausibleCarrierDestination("12345678+1234567890123456", sms));
        Assert.False(options.IsPlausibleCarrierDestination("12345678+123456789012345\n", sms));
    }

    private sealed class DeploymentProjectionIngressController : ISipGatewayCallController, ISipGatewaySimBoundMessageController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public event EventHandler<SmsReceivedEventArgs>? SmsReceived { add { } remove { } }
        public int SmsCount, DialCount;
        public string? LastLine, LastNumber;
        public string? GetSmsSimBinding(string slotId) => TestSmsBinding(slotId);
        public Task<SmsSubmitResult> SendSmsForSimAsync(string number, string text, string slotId, string expectedBinding, CancellationToken ct)
        {
            Assert.Equal(GetSmsSimBinding(slotId), expectedBinding);
            return SendSmsAsync(number, text, slotId, ct);
        }
        public Task<SmsSubmitResult> SendSmsAsync(string number, string text, string? slotId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref SmsCount); LastLine = slotId; LastNumber = number;
            return Task.FromResult(new SmsSubmitResult(number, text, SmsEncoding.Ucs2, null,
                1, 1, 1, true, "accepted", [], DateTime.UtcNow));
        }
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref DialCount); LastLine = slotId; LastNumber = number;
            throw new NotSupportedException("Synthetic carrier stays offline; no external call is made.");
        }
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task HangupAsync(string? slotId, CancellationToken ct) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
