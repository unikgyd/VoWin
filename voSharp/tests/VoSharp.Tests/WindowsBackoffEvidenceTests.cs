using VoSharp.Common.Aka;
using VoSharp.Ike;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class WindowsBackoffEvidenceTests
{
    private sealed class Sim : IAkaProvider
    {
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default) => throw new InvalidOperationException();
    }
    private static IkeSessionResult Failure() => new(false, null, null, [], 0, 0, null, null, null, null, null, null, null, false, "synthetic");

    [Fact]
    public async Task ActualBackendForwardsStructuredRejectionRatherThanParsingText()
    {
        var proof = new IkeNetworkRejection(10500, IkeRejectionScope.Epdg, IkeBackoffTimer.Decode([1, 0xAA]));
        var attempts = 0;
        using var backend = new CSharpIkeBackend
        {
            EstablishSession = (_, _) => { attempts++; return Task.FromResult(Failure() with { NetworkRejection = proof, InitialResponderAuthVerified = true }); }
        };
        var result = await backend.StartTunnelAsync(SimIdentity.FromImsiAndIccid("999990123456789", "synthetic", mncLength: 2), new Sim(), "192.0.2.5");
        Assert.Same(proof, result.NetworkRejection); Assert.Equal(1, attempts);
        Assert.False(result.RetryableIdentityRejection); Assert.False(result.RetryableTransportFailure);
    }

    [Fact]
    public async Task EarlierFamilyAuthenticationCannotManufactureCurrentNetworkEvidence()
    {
        var attempts = 0;
        using var backend = new CSharpIkeBackend
        {
            EstablishSession = (_, _) => Task.FromResult(++attempts == 1 ? Failure() with
            { RetryableAddressFamilyFailure = true, PreEapFamilyFailure = true, InitialResponderAuthVerified = true }
            : Failure() with { ErrorMessage = "NETWORK_FAILURE BACKOFF_TIMER=does not expire" })
        };
        var result = await backend.StartTunnelAsync(SimIdentity.FromImsiAndIccid("999990123456789", "synthetic", mncLength: 2), new Sim(), "192.0.2.5");
        Assert.Equal(2, attempts); Assert.False(result.InitialResponderAuthVerified); Assert.Null(result.NetworkRejection);
    }
}
