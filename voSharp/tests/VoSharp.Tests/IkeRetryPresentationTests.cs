using VoSharp.Ike;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class IkeRetryPresentationTests
{
    private static readonly SimIdentity Sim = SimIdentity.FromImsiAndIccid("234150123456789", "8944100000000000051", mncLength: 2);
    private static readonly IkeRetryTarget Target = new("epdg.example", "23415");
    private sealed class Clock : IIkeRetryClock
    {
        public long Ticks = TimeSpan.FromHours(1).Ticks;
        public IkeRetryClockStamp Read() => new(new string('a', 64), Ticks);
    }
    private sealed class CountingStore : IIkeRetryJournalStore
    {
        private readonly MemoryIkeRetryJournalStore _inner = new();
        public int Reads;
        public Task<string?> LoadAsync(CancellationToken ct) { Interlocked.Increment(ref Reads); return _inner.LoadAsync(ct); }
        public Task<bool> CompareExchangeAsync(string? expected, string replacement, CancellationToken ct) => _inner.CompareExchangeAsync(expected, replacement, ct);
    }
    private static VoWifiManager Manager(IkeRetryGate gate) => new(
        epdgHistory: new EpdgConnectionHistoryStore(Path.Combine(Path.GetTempPath(), "vowin-ike-presentation-" + Guid.NewGuid().ToString("N"), "history.json")),
        ikeRetry: gate, retryDeviceId: "presentation-reader") { EpdgDiscovery = new(ManualOnly: true) };
    private static async Task Seed(IkeRetryGate gate, byte timer)
    {
        var card = await gate.PrepareVerifiedCardAsync("presentation-reader", Sim.Iccid); var token = await gate.BeginAsync(card, Target);
        await gate.ObserveAsync(card, Target, token, "192.0.2.9", new(10500, IkeRejectionScope.Epdg, IkeBackoffTimer.Decode([1, timer])), true);
        await gate.CompleteAsync(card, token);
    }

    [Fact]
    public async Task CachedObservationIsBoundToTheExactCardAndDoesNotReadTheStore()
    {
        var store = new CountingStore(); var gate = new IkeRetryGate(store, new Clock()); await Seed(gate, 0xAA);
        using var manager = Manager(gate);
        await Assert.ThrowsAsync<IkeRetryGateException>(() => manager.PreflightIkeRetryAsync(Sim, Target.Host));
        var reads = store.Reads;
        Assert.Same(manager.IkeRetryStatus, manager.GetIkeRetryStatusFor(Sim.Iccid));
        Assert.Null(manager.GetIkeRetryStatusFor("8944100000000000052"));
        Assert.Null(manager.GetIkeRetryStatusFor(null)); Assert.Null(manager.GetIkeRetryStatusFor(""));
        for (var i = 0; i < 100; i++) Assert.NotNull(manager.GetIkeRetryStatusFor(Sim.Iccid));
        Assert.Equal(reads, store.Reads);
    }

    [Fact]
    public async Task ManualStopClearsPresentationButNeverClearsTheDurableRestriction()
    {
        var gate = new IkeRetryGate(new MemoryIkeRetryJournalStore(), new Clock()); await Seed(gate, 0xE0);
        using var manager = Manager(gate);
        Assert.False(await manager.StartVoWifiAsync(Sim, Target.Host));
        Assert.True(manager.GetIkeRetryStatusFor(Sim.Iccid)!.UntilCardEvent);
        await manager.StopVoWifiAsync();
        Assert.Null(manager.IkeRetryStatus); Assert.Null(manager.GetIkeRetryStatusFor(Sim.Iccid));
        var blocked = await Assert.ThrowsAsync<IkeRetryGateException>(() => gate.InspectAsync(Sim.Iccid, Target));
        Assert.True(blocked.Status!.UntilCardEvent);
    }

    [Fact]
    public async Task CachedDurationIsNotPermissionAndOnlyARealPreflightClearsAnExpiredObservation()
    {
        var clock = new Clock(); var gate = new IkeRetryGate(new MemoryIkeRetryJournalStore(), clock); await Seed(gate, 0xAA);
        using var manager = Manager(gate);
        await Assert.ThrowsAsync<IkeRetryGateException>(() => manager.PreflightIkeRetryAsync(Sim, Target.Host));
        clock.Ticks += TimeSpan.FromMinutes(10).Ticks;
        Assert.Equal(TimeSpan.FromMinutes(10), manager.GetIkeRetryStatusFor(Sim.Iccid)!.Remaining);
        await manager.PreflightIkeRetryAsync(Sim, Target.Host);
        Assert.Null(manager.GetIkeRetryStatusFor(Sim.Iccid));
    }

    [Fact]
    public async Task StoreFailureKeepsItsTypeAndCardWithoutHardwareOrDns()
    {
        using var manager = Manager(new(new UnavailableIkeRetryJournalStore(), new Clock()));
        Assert.False(await manager.StartVoWifiAsync(Sim, Target.Host));
        Assert.Equal("store-unavailable", manager.GetIkeRetryStatusFor(Sim.Iccid)!.Source);
        Assert.Null(manager.GetIkeRetryStatusFor("8944100000000000052"));
        Assert.True(manager.AutomaticRecoveryPaused);
        await manager.StopVoWifiAsync();
    }
}
