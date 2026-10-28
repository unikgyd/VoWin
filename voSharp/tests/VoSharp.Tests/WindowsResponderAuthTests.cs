using VoSharp.Common.Aka;
using VoSharp.Ike;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class WindowsResponderAuthTests
{
    private sealed class Sim : IAkaProvider
    {
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default) => throw new InvalidOperationException();
    }
    private static IkeSessionResult Failure() => new(false, null, null, [], 0, 0, null, null, null, null, null, null, null, false, "synthetic");

    [Fact]
    public async Task ActualBackendRetainsSelectedNameWhenRestartingForAkaPrime()
    {
        var seen = new List<string>();
        using var backend = new CSharpIkeBackend
        {
            ResponderAuthentication = new("epdg.example"),
            EstablishSession = (request, _) =>
            {
                seen.Add(request.ResponderAuthentication!.ServerName);
                return Task.FromResult(seen.Count == 1 ? Failure() with { RequestedEapType = EapType.AkaPrime }
                    : Failure() with { InitialResponderAuthVerified = true });
            }
        };
        var result = await backend.StartTunnelAsync(SimIdentity.FromImsiAndIccid("999990123456789", "synthetic", mncLength: 2), new Sim(), "192.0.2.5");
        Assert.Equal(new[] { "epdg.example", "epdg.example" }, seen);
        Assert.True(result.InitialResponderAuthVerified);
        Assert.False(result.FinalAuthVerified);
    }

    [Fact]
    public async Task EarlierInitialAuthenticationDoesNotAuthenticateLaterFamilyAttempt()
    {
        var attempts = 0;
        using var backend = new CSharpIkeBackend
        {
            EstablishSession = (_, _) => Task.FromResult(++attempts == 1 ? Failure() with
            { RetryableAddressFamilyFailure = true, PreEapFamilyFailure = true, InitialResponderAuthVerified = true }
            : Failure())
        };
        var result = await backend.StartTunnelAsync(SimIdentity.FromImsiAndIccid("999990123456789", "synthetic", mncLength: 2), new Sim(), "192.0.2.5");
        Assert.Equal(2, attempts); Assert.False(result.InitialResponderAuthVerified);
    }

    [Fact]
    public async Task LiteralIpFallbackNeverGuessesAnIdentityFromTheSim()
    {
        using var backend = new CSharpIkeBackend
        {
            EstablishSession = (request, _) => { Assert.Equal("192.0.2.5", request.ResponderAuthentication!.ServerName); return Task.FromResult(Failure()); }
        };
        await backend.StartTunnelAsync(SimIdentity.FromImsiAndIccid("999990123456789", "synthetic", mncLength: 2), new Sim(), "192.0.2.5");
    }
}
