using System.Net;
using Microsoft.Data.Sqlite;
using VoSharp.Common.Aka;
using VoSharp.Ike;
using VoSharp.Kernel.Persistence;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class FinalRejectionOwnershipReviewTests
{
    private static readonly SimIdentity Identity = SimIdentity.FromImsiAndIccid("234150123456789", "8944100000000000001", mncLength: 2);
    private sealed class Aka : IAkaProvider
    {
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default) => throw new InvalidOperationException("No hardware AKA in a synthetic ownership test.");
    }
    private sealed class Dns : IEpdgDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.9") });
    }
    private sealed class Clock : IIkeRetryClock
    {
        public IkeRetryClockStamp Read() => new(new string('a', 64), TimeSpan.FromHours(1).Ticks);
    }
    private static IkeSessionResult Failure() => new(false, null, null, [], 0, 0, null, null, null, null, null, null, null, false, "synthetic ownership failure", FailureCleanupComplete: true);
    private static readonly IkeNetworkRejection Proof = new(10500, IkeRejectionScope.Epdg, IkeBackoffTimer.Decode([1, 1]));
    private static IkeSessionResult Earlier(string previous) => Failure() with
    { RetryableAddressFamilyFailure = true, FinalAuthVerified = previous == "final", InitialResponderAuthVerified = previous == "initial" };
    private static IkeSessionResult Current(string current) => Failure() with
    { FinalAuthVerified = current == "final", InitialResponderAuthVerified = current == "initial", NetworkRejection = current == "no-proof" ? null : Proof };

    [Theory]
    [InlineData("final", "unverified", false)]
    [InlineData("initial", "unverified", false)]
    [InlineData("none", "unverified", false)]
    [InlineData("final", "final", true)]
    [InlineData("final", "initial", true)]
    [InlineData("none", "final", true)]
    [InlineData("none", "initial", true)]
    [InlineData("final", "no-proof", false)]
    public async Task ActualBackendSeparatesCurrentRejectionFromHistoricalAuthentication(string previous, string current, bool wantProof)
    {
        var attempts = 0;
        using var backend = new CSharpIkeBackend
        {
            EstablishSession = (_, _) => Task.FromResult(++attempts == 1 && previous != "none" ? Earlier(previous) : Current(current))
        };
        var result = await backend.StartTunnelAsync(Identity, new Aka(), "192.0.2.9");
        Assert.False(result.Success); Assert.Equal(previous == "none" ? 1 : 2, attempts);
        Assert.Equal(previous == "final" || current == "final", result.FinalAuthVerified); // Keep historical home-binding evidence.
        Assert.Equal(current == "initial", result.InitialResponderAuthVerified);
        Assert.True((result.NetworkRejection != null) == wantProof,
            $"current unauthenticated rejection escaped backend: previous={previous}, current={current}");
        if (wantProof) Assert.Same(Proof, result.NetworkRejection);
        Assert.False(result.RetryableAddressFamilyFailure); Assert.False(result.RetryableIdentityRejection);
        Assert.False(result.RetryableTransportFailure);
    }

    [Fact]
    public async Task ActualManagerCannotPersistACurrentUnverifiedProofUsingAnEarlierFinalAUTH()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-final-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "journal.db") }.ToString();
        var gate = new IkeRetryGate(new SqliteIkeRetryJournalStore(connection), new Clock());
        var attempts = 0;
        using (var manager = new VoWifiManager(epdgHistory: new EpdgConnectionHistoryStore(Path.Combine(directory, "history.json")), ikeRetry: gate, retryDeviceId: "synthetic-owner-reader")
        { AkaProvider = new Aka(), DeviceImei = "356938035643809", EpdgDiscovery = new(ManualOnly: true, DnsResolver: new Dns()) })
        {
            manager.IkeBackendFactory = context => new CSharpIkeBackend
            {
                BeforeInitialSend = context.Permit,
                ObserveNetworkRejection = context.Observe,
                EstablishSession = async (request, ct) =>
                {
                    if (request.BeforeInitialSend != null) await request.BeforeInitialSend(IPAddress.Parse("192.0.2.9"), ct);
                    return ++attempts == 1 ? Earlier("final") : Current("unverified");
                }
            };
            Assert.False(await manager.StartVoWifiAsync(Identity, customEpdg: "epdg.example", proxyUrl: "socks5://127.0.0.1:9"));
            await manager.StopVoWifiAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(2, attempts);
        var reopened = new IkeRetryGate(new SqliteIkeRetryJournalStore(connection), new Clock());
        var error = await Record.ExceptionAsync(() => reopened.InspectAsync(Identity.Iccid, new IkeRetryTarget("epdg.example", null)));
        Assert.True(error == null, $"old final AUTH authorized current unverified rejection: {error}");
    }
}
