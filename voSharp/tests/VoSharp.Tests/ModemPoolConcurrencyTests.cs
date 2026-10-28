using VoSharp.Common.Events;
using VoSharp.Kernel.Pool;
using Xunit;

namespace VoSharp.Tests;

// The injected attachment never opens a serial port, reads a SIM or starts IMS.
public class ModemPoolConcurrencyTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConcurrentAddsWithTheSameIdShareOneSlotAndSerializeAttachment()
    {
        var entered = Signal();
        var resume = Signal();
        var count = 0;
        var concurrent = 0;
        var maximum = 0;
        await using var bus = new AsyncEventBus();
        await using var pool = new ModemPool(bus, async (_, ct) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref concurrent));
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.SetResult();
                await resume.Task.WaitAsync(ct);
            }
            Interlocked.Decrement(ref concurrent);
            return true;
        });
        var first = pool.AddOrUpdateSlotAsync("COM9001", slotId: "line-a");
        await entered.Task.WaitAsync(Deadline);
        var second = pool.AddOrUpdateSlotAsync("com9001", slotId: "LINE-A");
        try { Assert.False(second.IsCompleted); }
        finally { resume.TrySetResult(); }
        Assert.Same(await first.WaitAsync(Deadline), await second.WaitAsync(Deadline));
        Assert.Single(pool.Slots);
        Assert.Equal(2, count);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task DifferentIdsCannotOwnTheSamePortIncludingCaseVariants()
    {
        var entered = Signal();
        var resume = Signal();
        var count = 0;
        await using var pool = new ModemPool(null, async (_, ct) =>
        {
            Interlocked.Increment(ref count);
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return true;
        });
        var first = pool.AddOrUpdateSlotAsync("COM9001", slotId: "a");
        await entered.Task.WaitAsync(Deadline);
        var alias = pool.AddOrUpdateSlotAsync("com9001", slotId: "b");
        try { Assert.False(alias.IsCompleted); }
        finally { resume.TrySetResult(); }
        var original = await first.WaitAsync(Deadline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => alias.WaitAsync(Deadline));
        Assert.Same(original, Assert.Single(pool.Slots).Value);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PortDiscoveryConvergesOnAnInFlightManuallyNamedSlot()
    {
        var entered = Signal();
        var resume = Signal();
        await using var pool = new ModemPool(null, async (_, ct) =>
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return true;
        });
        var manual = pool.AddOrUpdateSlotAsync("COM9001", name: "My modem", slotId: "custom-line");
        await entered.Task.WaitAsync(Deadline);
        var automatic = pool.AddOrUpdateSlotAsync("com9001");
        try { Assert.False(automatic.IsCompleted); }
        finally { resume.TrySetResult(); }
        var original = await manual.WaitAsync(Deadline);
        Assert.Same(original, await automatic.WaitAsync(Deadline));
        Assert.Equal("custom-line", original.Id);
        Assert.Equal("My modem", original.Name);
        Assert.Single(pool.Slots);
    }

    [Fact]
    public async Task IndependentModemsAttachInParallel()
    {
        var entered = Signal();
        var resume = Signal();
        await using var pool = new ModemPool(null, async (slot, ct) =>
        {
            if (slot.PortName == "COM9001")
            {
                entered.TrySetResult();
                await resume.Task.WaitAsync(ct);
            }
            return true;
        });
        var slow = pool.AddOrUpdateSlotAsync("COM9001");
        await entered.Task.WaitAsync(Deadline);
        try
        {
            var other = await pool.AddOrUpdateSlotAsync("COM9002").WaitAsync(Deadline);
            Assert.Equal("com9002", other.Id);
            Assert.False(slow.IsCompleted);
        }
        finally { resume.TrySetResult(); }
        await slow.WaitAsync(Deadline);
        Assert.Equal(2, pool.Slots.Count);
    }

    [Fact]
    public async Task SynchronousInsertionCannotOverwriteAnInFlightAttachment()
    {
        var entered = Signal();
        var resume = Signal();
        await using var pool = new ModemPool(null, async (_, ct) =>
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return true;
        });
        await using var sameId = new ModemSlot("a", "COM9001");
        await using var samePort = new ModemSlot("b", "com9001");
        var add = pool.AddOrUpdateSlotAsync("COM9001", slotId: "a");
        await entered.Task.WaitAsync(Deadline);
        try
        {
            Assert.False(pool.TryAddSlot(sameId));
            Assert.False(pool.TryAddSlot(samePort));
        }
        finally { resume.TrySetResult(); }
        Assert.Same(await add.WaitAsync(Deadline), Assert.Single(pool.Slots).Value);
    }

    [Fact]
    public async Task RemoveWaitsForAttachmentAndDoesNotLoseTheNewSlot()
    {
        var entered = Signal();
        var resume = Signal();
        await using var pool = new ModemPool(null, async (_, ct) =>
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return true;
        });
        var add = pool.AddOrUpdateSlotAsync("COM9001", slotId: "a");
        await entered.Task.WaitAsync(Deadline);
        var remove = pool.RemoveSlotAsync("a");
        try { Assert.False(remove.IsCompleted); }
        finally { resume.TrySetResult(); }
        await add.WaitAsync(Deadline);
        Assert.True(await remove.WaitAsync(Deadline));
        Assert.Empty(pool.Slots);
        Assert.Null(pool.ActiveSlotId);
    }

    [Fact]
    public async Task RemovingALeasedSlotKeepsItVisibleUntilTheLeaseDrains()
    {
        await using var pool = new ModemPool();
        var slot = new ModemSlot("a", "COM9001");
        Assert.True(pool.TryAddSlot(slot));
        var lease = await pool.AcquireSlotLeaseAsync("a");
        Assert.NotNull(lease);
        var remove = pool.RemoveSlotAsync("a");
        try
        {
            Assert.False(remove.IsCompleted);
            Assert.Same(slot, pool.Slots["a"]);
        }
        finally { await lease.DisposeAsync(); }
        Assert.True(await remove.WaitAsync(Deadline));
        await lease.DisposeAsync(); // Idempotent, cannot over-release the gate.
    }

    [Fact]
    public async Task RemovalDoesNotDestroyAnActiveLeaseAfterTheOldThreeSecondTimeout()
    {
        await using var pool = new ModemPool();
        var slot = new ModemSlot("a", "COM9001");
        Assert.True(pool.TryAddSlot(slot));
        var lease = await pool.AcquireSlotLeaseAsync("a");
        Assert.NotNull(lease);
        var remove = pool.RemoveSlotAsync("a");
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(3200));
            Assert.False(remove.IsCompleted);
            Assert.Same(slot, pool.Slots["a"]);
            Assert.Null(await pool.AcquireSlotLeaseAsync("a", timeoutMs: 0));
        }
        finally { await lease.DisposeAsync(); }
        Assert.True(await remove.WaitAsync(Deadline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateAddsDoNotReattachBusyModemsAndPortMovesAreRejected(bool move)
    {
        var attachments = 0;
        await using var pool = new ModemPool(null, (_, _) =>
        {
            Interlocked.Increment(ref attachments);
            return Task.FromResult(true);
        });
        var slot = new ModemSlot("a", "COM9001");
        typeof(ModemSlot).GetProperty(nameof(ModemSlot.State))!.SetValue(slot, SlotState.Busy);
        Assert.True(pool.TryAddSlot(slot));
        if (move)
            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AddOrUpdateSlotAsync("COM9002", slotId: "a"));
        else
            Assert.Same(slot, await pool.AddOrUpdateSlotAsync("com9001", slotId: "a"));
        Assert.Equal(0, attachments);
        Assert.Equal("COM9001", slot.PortName);
        Assert.Equal(SlotState.Busy, slot.State);
    }

    [Fact]
    public async Task CancelledRemovalDoesNotDisposeTheLeasedSlotOrReleaseItsGate()
    {
        await using var pool = new ModemPool();
        var slot = new ModemSlot("a", "COM9001");
        Assert.True(pool.TryAddSlot(slot));
        var lease = await pool.AcquireSlotLeaseAsync("a");
        Assert.NotNull(lease);
        using var cancellation = new CancellationTokenSource();
        var remove = pool.RemoveSlotAsync("a", cancellation.Token);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remove.WaitAsync(Deadline));
            Assert.Same(slot, pool.Slots["a"]);
            Assert.Null(await pool.AcquireSlotLeaseAsync("a", timeoutMs: 0));
        }
        finally { await lease.DisposeAsync(); }
        Assert.True(await pool.RemoveSlotAsync("a").WaitAsync(Deadline));
    }

    [Fact]
    public async Task ALeaseQueuedBeforeRemovalCannotLeaseTheRemovedInstance()
    {
        await using var pool = new ModemPool();
        Assert.True(pool.TryAddSlot(new ModemSlot("a", "COM9001")));
        var lease = await pool.AcquireSlotLeaseAsync("a");
        Assert.NotNull(lease);
        var remove = pool.RemoveSlotAsync("a");
        var queued = pool.AcquireSlotLeaseAsync("a");
        await lease.DisposeAsync();
        Assert.True(await remove.WaitAsync(Deadline));
        var staleLease = await queued.WaitAsync(Deadline);
        if (staleLease is not null) await staleLease.DisposeAsync();
        Assert.Null(staleLease);
    }

    [Fact]
    public async Task MovingAPortReservesBothOldAndNewPortsUntilAttachmentFinishes()
    {
        var entered = Signal();
        var resume = Signal();
        await using var pool = new ModemPool(null, async (_, ct) =>
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return true;
        });
        var original = new ModemSlot("a", "COM9001");
        Assert.True(pool.TryAddSlot(original));
        await using var oldAlias = new ModemSlot("b", "COM9001");
        await using var newAlias = new ModemSlot("c", "COM9002");
        var move = pool.AddOrUpdateSlotAsync("COM9002", slotId: "a");
        await entered.Task.WaitAsync(Deadline);
        try
        {
            Assert.False(pool.TryAddSlot(oldAlias));
            Assert.False(pool.TryAddSlot(newAlias));
        }
        finally { resume.TrySetResult(); }
        Assert.Same(original, await move.WaitAsync(Deadline));
        Assert.Equal("COM9002", original.PortName);
    }

    [Fact]
    public async Task CancellationReleasesPartiallyAcquiredGates()
    {
        var entered = Signal();
        var resume = Signal();
        await using var pool = new ModemPool(null, async (_, ct) =>
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return true;
        });
        var add = pool.AddOrUpdateSlotAsync("COM9001", slotId: "a");
        await entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var blocked = pool.AddOrUpdateSlotAsync("COM9001", slotId: "b", ct: cancellation.Token);
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(Deadline)); }
        finally { resume.TrySetResult(); }
        await add.WaitAsync(Deadline);
        Assert.Equal("b", (await pool.AddOrUpdateSlotAsync("COM9002", slotId: "b").WaitAsync(Deadline)).Id);
    }

    [Fact]
    public async Task ShutdownWaitsForInFlightAttachmentAndRejectsNewMutations()
    {
        var entered = Signal();
        var resume = Signal();
        var pool = new ModemPool(null, async (_, ct) =>
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return true;
        });
        await using var rejected = new ModemSlot("b", "COM9002");
        var add = pool.AddOrUpdateSlotAsync("COM9001");
        await entered.Task.WaitAsync(Deadline);
        var stop = pool.DisposeAsync().AsTask();
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.Same(stop, pool.DisposeAsync().AsTask());
            Assert.Throws<ObjectDisposedException>(() => pool.TryAddSlot(rejected));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AddOrUpdateSlotAsync("COM9002"));
        }
        finally { resume.TrySetResult(); }
        await add.WaitAsync(Deadline);
        await stop.WaitAsync(Deadline);
        Assert.Empty(pool.Slots);
        Assert.Null(pool.ActiveSlotId);
    }

    [Fact]
    public async Task ShutdownDrainsOutstandingLeasesBeforeDisposingTheirGates()
    {
        var pool = new ModemPool();
        Assert.True(pool.TryAddSlot(new ModemSlot("a", "COM9001")));
        var lease = await pool.AcquireSlotLeaseAsync("a");
        Assert.NotNull(lease);
        var stop = pool.DisposeAsync().AsTask();
        try { Assert.False(stop.IsCompleted); }
        finally { await lease.DisposeAsync(); }
        await stop.WaitAsync(Deadline);
        await lease.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireSlotLeaseAsync("a"));
    }
}
