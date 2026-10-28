using System.Reflection;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using Xunit;

namespace VoSharp.Tests;

public class HostImsHotplugLifecycleTests
{
    private static ModemSlot MakeSlot(string id, SlotState state = SlotState.Online)
    {
        // Separate pool entries represent different serial functions, even
        // when the fake drivers and retained physical identities are shared.
        var slot = new ModemSlot(id, $"TEST-{id}");
        typeof(ModemSlot).GetProperty(nameof(ModemSlot.State))!.SetValue(slot, state);
        typeof(ModemSlot).GetProperty(nameof(ModemSlot.Modem))!.SetValue(slot, new ModemDriver("COM999"));
        typeof(ModemSlot).GetProperty(nameof(ModemSlot.Sim))!.SetValue(slot,
            SimIdentity.FromImsiAndIccid("460110000000001", "test-hotplug-sim"));
        typeof(ModemSlot).GetProperty(nameof(ModemSlot.Imei))!.SetValue(slot, "test-same-physical-modem");
        return slot;
    }

    private static void SetTransition(ModemSlot slot, bool active) =>
        typeof(ModemSlot).GetField("_hostImsPppTransition", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, active ? 1 : 0);

    [Fact]
    public async Task ExpectedUsbRestartPreservesDriverAndVerifiedSimThenRealUnplugDetaches()
    {
        await using var slot = MakeSlot("reboot");
        var driver = slot.Modem;
        var sim = slot.Sim;
        SetTransition(slot, true);
        try
        {
            await slot.DetachHardwareAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(driver, slot.Modem);
            Assert.Same(sim, slot.Sim);
            Assert.Equal(SlotState.Online, slot.State);
        }
        finally { SetTransition(slot, false); }
        await slot.DetachHardwareAsync();
        Assert.Null(slot.Modem);
        Assert.Same(sim, slot.Sim); // independent VoWiFi keeps its cached identity
        Assert.Equal(SlotState.Offline, slot.State);
    }

    [Fact]
    public async Task DetachDoesNotWaitBehindHandoffAndReplayStaleRemoval()
    {
        await using var slot = MakeSlot("gate-race");
        var driver = slot.Modem;
        var gate = (SemaphoreSlim)typeof(ModemSlot).GetField("_profileSwitchGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot)!;
        await gate.WaitAsync();
        try
        {
            await slot.DetachHardwareAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(driver, slot.Modem);
        }
        finally { gate.Release(); }
        Assert.Same(driver, slot.Modem);
        await slot.DetachHardwareAsync();
        Assert.Null(slot.Modem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveSiblingWinsOverRetainedOfflineIdentityRegardlessOfPortOrder(bool reverse)
    {
        await using var pool = new ModemPool();
        var stale = MakeSlot("com6", SlotState.Offline);
        var live = MakeSlot("com9");
        Assert.True(pool.TryAddSlot(stale));
        Assert.True(pool.TryAddSlot(live));
        var discovered = await pool.DeduplicateDiscoveredSlotsAsync(reverse ? [live, stale] : [stale, live]);
        Assert.Same(live, Assert.Single(discovered));
        Assert.Same(live, pool.ActiveSlot);
        Assert.False(pool.Slots.ContainsKey(stale.Id));
        Assert.True(pool.Slots.ContainsKey(live.Id));
    }

    [Fact]
    public async Task DuplicateCandidateCannotDisposeTheSelectedSlot()
    {
        await using var pool = new ModemPool();
        var slot = MakeSlot("same-candidate");
        Assert.True(pool.TryAddSlot(slot));
        Assert.Same(slot, Assert.Single(await pool.DeduplicateDiscoveredSlotsAsync([slot, slot])));
        Assert.Same(slot, pool.ActiveSlot);
    }

    [Fact]
    public async Task ScanDuringManagedRestartDoesNotProbeSerialOrSmartCardDevices()
    {
        await using var pool = new ModemPool();
        var owned = MakeSlot("owned");
        Assert.True(pool.TryAddSlot(owned));
        SetTransition(owned, true);
        try
        {
            var slots = await pool.DiscoverAndEnrichAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(owned, Assert.Single(slots));
            Assert.False(owned.Modem!.IsOpen); // COM999 was never opened
        }
        finally { SetTransition(owned, false); }
    }

    [Fact]
    public async Task SerialSessionDisposalIsIdempotentAndCannotBeReopened()
    {
        var session = new AtSession("COM999");
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => session.Open());
    }
}
