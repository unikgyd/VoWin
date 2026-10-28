using System.Reflection;
using VoSharp.Common.Aka;
using VoSharp.Ike;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class WindowsIkeRetryProofTests
{
    private sealed class Sim : IAkaProvider
    {
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default) => throw new InvalidOperationException();
    }
    private static IkeSessionResult Failure() => new(false, null, null, [], 0, 0, null, null, null, null, null, null, null, false, "synthetic");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualBackendKeepsAuthenticationHistoryAcrossFamilies(bool authenticated)
    {
        var starts = 0;
        using var backend = new CSharpIkeBackend
        {
            EstablishSession = (_, _) => Task.FromResult(++starts == 1
                ? Failure() with { RetryableAddressFamilyFailure = true, PreEapFamilyFailure = !authenticated, FinalAuthVerified = authenticated, FailureCleanupComplete = true }
                : Failure() with { RetryableIdentityRejection = true, FailureCleanupComplete = true })
        };
        var result = await backend.StartTunnelAsync(SimIdentity.FromImsiAndIccid("001010123456789", "synthetic", mncLength: 2), new Sim(), "127.0.0.1");
        Assert.Equal(2, starts);
        Assert.Equal(!authenticated, result.RetryableIdentityRejection);
        Assert.Equal(authenticated, result.FinalAuthVerified);
    }

    [Fact]
    public async Task LaterInitTimeoutCannotDowngradeTheEarlierAcceptedSuite()
    {
        var starts = 0;
        using var backend = new CSharpIkeBackend
        {
            EstablishSession = (_, _) => Task.FromResult(++starts == 1
                ? Failure() with { RetryableAddressFamilyFailure = true, PreEapFamilyFailure = true, FailureCleanupComplete = true }
                : Failure() with { RetryableInitialTimeout = true, FailureCleanupComplete = true })
        };
        var result = await backend.StartTunnelAsync(SimIdentity.FromImsiAndIccid("001010123456789", "synthetic", mncLength: 2), new Sim(), "127.0.0.1");
        Assert.Equal(2, starts);
        Assert.False(result.RetryableInitialTimeout);
    }

    [Theory]
    [InlineData("IsPreAkaRejection", "EAP-AKA authentication rejected before AKA challenge response after local failure")]
    [InlineData("IsInitialIkeTimeout", "IKE_AUTH failed; previous IKE_SA_INIT timed out")]
    [InlineData("IsProposalNegotiationFailure", "final AUTH failed with unsupported suite / DH group")]
    [InlineData("IsProposalNegotiationFailure", "CHILD_SA NO_PROPOSAL_CHOSEN")]
    public void DiagnosticTextAloneCannotAuthorizeRetry(string name, string message)
    {
        var method = typeof(VoWifiManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = new IkeBackendResult(false, null, null, [], 0, 0, message);
        object argument = method.GetParameters()[0].ParameterType == typeof(string) ? message : result;
        Assert.False((bool)method.Invoke(null, [argument])!);
    }
}
