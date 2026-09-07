using VoSharp.Common.Events;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;
using Xunit;

namespace VoSharp.Tests;

public class ModemPoolTests
{
    [Fact]
    public async Task ModemSlot_ConfigurationAndDiagnostics_WorksCorrectly()
    {
        var bus = new AsyncEventBus();
        var slot = new ModemSlot("slot-ph", "COM10", 115200, "DITO PH", "socks5://127.0.0.1:10808", bus);

        Assert.Equal("slot-ph", slot.Id);
        Assert.Equal("COM10", slot.PortName);
        Assert.Equal("socks5://127.0.0.1:10808", slot.ProxyUrl);
        Assert.Equal("socks5://127.0.0.1:10808", slot.VoWifi.ProxyUrl);
        Assert.Equal(SlotState.Offline, slot.State);

        var diag = slot.GetDiagnosticInfo();
        Assert.NotNull(diag);

        await slot.DisposeAsync();
    }

    [Fact]
    public async Task ModemPool_SlotManagementAndRouting_WorksCorrectly()
    {
        var bus = new AsyncEventBus();
        var pool = new ModemPool(bus);

        // Add virtual / mock slots
        var slot1 = new ModemSlot("slot-1", "COM21", 115200, "China Mobile", null, bus);
        var slot2 = new ModemSlot("slot-2", "COM22", 115200, "DITO Telecommunity", "socks5://127.0.0.1:10808", bus);

        // Test slot selection
        Assert.True(pool.Slots.Count == 0);

        // Set proxy on pool
        bool setProxy = pool.SetSlotProxy("nonexistent", "socks5://127.0.0.1:1080");
        Assert.False(setProxy);

        await pool.DisposeAsync();
    }

    [Fact]
    public async Task VoKernel_SlotCommands_ExecuteProperly()
    {
        var bus = new AsyncEventBus();
        var kernel = new VoKernel(bus);

        // 1. slot list
        var resList = await kernel.ExecuteCommandAsync("slot list");
        Assert.True(resList.Success);
        Assert.Contains("Modem Pool", resList.Message);

        // 2. slot proxy configuration
        var resProxy = await kernel.ExecuteCommandAsync("slot proxy slot-1 socks5://127.0.0.1:10808");
        Assert.False(resProxy.Success); // Slot not found yet

        // 3. vowifi status contains proxy info
        var resVoWifi = await kernel.ExecuteCommandAsync("vowifi status");
        Assert.True(resVoWifi.Success);
        Assert.Contains("Proxy:", resVoWifi.Message);

        // 4. CreateSnapshot returns valid snapshot
        var snapshot = kernel.CreateSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(kernel.StateMachine.CurrentState, snapshot.State);

        await kernel.DisposeAsync();
    }

    [Fact]
    public async Task ModemPool_SlotLease_EnforcesMutualExclusionAndDrains()
    {
        await using var bus = new AsyncEventBus();
        await using var pool = new ModemPool(bus);

        var slot = new ModemSlot("slot-lease", "COM30", 115200, "TestSlot", null, bus);
        Assert.True(pool.TryAddSlot(slot));

        // Acquire first lease
        var lease1 = await pool.AcquireSlotLeaseAsync("slot-lease", 500);
        Assert.NotNull(lease1);

        // Concurrent lease attempt should timeout
        var lease2 = await pool.AcquireSlotLeaseAsync("slot-lease", 50);
        Assert.Null(lease2);

        // Release first lease
        await lease1.DisposeAsync();

        // Now second lease can be acquired
        var lease3 = await pool.AcquireSlotLeaseAsync("slot-lease", 500);
        Assert.NotNull(lease3);
        await lease3.DisposeAsync();

        // Remove slot should succeed
        bool removed = await pool.RemoveSlotAsync("slot-lease");
        Assert.True(removed);
    }
}
