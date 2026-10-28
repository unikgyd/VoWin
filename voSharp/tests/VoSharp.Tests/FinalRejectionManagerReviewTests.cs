using System.Net;
using Microsoft.Data.Sqlite;
using VoSharp.Common.Aka;
using VoSharp.Ike;
using VoSharp.Kernel.Persistence;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class FinalRejectionManagerReviewTests
{
    private sealed class Clock : IIkeRetryClock
    {
        public IkeRetryClockStamp Read() => new(new string('a', 64), TimeSpan.FromHours(1).Ticks);
    }
    private sealed class Aka : IAkaProvider
    {
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default) => throw new InvalidOperationException("No hardware AKA in a synthetic backend test.");
    }
    private sealed class Dns : IEpdgDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.9") });
    }
    private sealed class Backend(IkeBackendContext context, string mode) : IIkeBackend
    {
        public EspTunnel? EspTunnel => null;
        public IkeTransport? Transport => null;
        public Task ProbeLivenessAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopTunnelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        public async Task<IkeBackendResult> StartTunnelAsync(SimIdentity sim, IAkaProvider akaProvider, string epdgIp,
            string apn = "ims", string? fallbackPcscf = null, IkeProposalSuite suite = IkeProposalSuite.Auto,
            string? proxyUrl = null, string? imei = null, Action<string>? diagnosticLog = null,
            CancellationToken ct = default, bool forceNatt = false, IPAddress? localAddress = null)
        {
            var peer = IPAddress.Parse(epdgIp);
            await context.Permit(peer, ct);
            var proof = new IkeNetworkRejection(10500, IkeRejectionScope.Epdg, IkeBackoffTimer.Decode([1, 1]));
            if (mode == "callback-plus-result") await context.Observe(proof, peer, ct);
            return new(false, null, null, [], 0, 0, "synthetic NETWORK_FAILURE BACKOFF_TIMER",
                FinalAuthVerified: mode is "late-proof" or "late-no-proof" or "callback-plus-result",
                InitialResponderAuthVerified: mode == "initial-proof",
                NetworkRejection: mode is "text-only" or "late-no-proof" ? null : proof);
        }
    }

    [Theory]
    [InlineData("late-proof", true)]
    [InlineData("initial-proof", true)]
    [InlineData("unverified-proof", false)]
    [InlineData("text-only", false)]
    [InlineData("late-no-proof", false)]
    [InlineData("callback-plus-result", true)]
    public async Task ActualManagerPersistsOnlyAuthenticatedReturnedProof(string mode, bool wantRestriction)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-final-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "journal.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = database }.ToString();
        var identity = SimIdentity.FromImsiAndIccid("234150123456789", "8944100000000000001", mncLength: 2);
        var gate = new IkeRetryGate(new SqliteIkeRetryJournalStore(connection), new Clock());
        using (var manager = new VoWifiManager(epdgHistory: new EpdgConnectionHistoryStore(Path.Combine(directory, "history.json")), ikeRetry: gate, retryDeviceId: "synthetic-reader")
        { AkaProvider = new Aka(), DeviceImei = "356938035643809", EpdgDiscovery = new(ManualOnly: true, DnsResolver: new Dns()) })
        {
            manager.IkeBackendFactory = context => new Backend(context, mode);
            Assert.False(await manager.StartVoWifiAsync(identity, customEpdg: "epdg.example", proxyUrl: "socks5://127.0.0.1:9"));
            await manager.StopVoWifiAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        // Reopen the actual file-backed store after owner cleanup, not merely
        // a presentation flag or in-memory observation on the original manager.
        var reopened = new IkeRetryGate(new SqliteIkeRetryJournalStore(connection), new Clock());
        var error = await Record.ExceptionAsync(() => reopened.InspectAsync(identity.Iccid, new IkeRetryTarget("epdg.example", null)));
        Assert.True((error is IkeRetryGateException { Failure: IkeRetryFailure.Blocked }) == wantRestriction,
            $"actual Manager lost late final proof or invented a restriction for {mode}: {error}");
        if (wantRestriction)
        {
            var blocked = Assert.IsType<IkeRetryGateException>(error);
            Assert.Equal(TimeSpan.FromMinutes(10), blocked.Status!.Remaining);
            Assert.Equal((ushort)10500, blocked.Status.Cause);
        }
        // Synthetic temporary data remains available for inspection; no user
        // Preferences database, device, modem, SIM, media or network is opened.
    }
}
