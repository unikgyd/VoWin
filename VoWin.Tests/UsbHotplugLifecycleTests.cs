using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoWin.Services;

namespace VoWin.Tests;

// Complete callbacks include UI synchronization. Use the production dispatcher
// dependency with a pumping STA thread; fake drivers never open a serial port.
public sealed class UsbHotplugLifecycleTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoWin-usb-callback-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource _uiStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher _ui = null!;
    private VoKernelService _service = null!;

    public async Task InitializeAsync()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                ready.TrySetResult(dispatcher);
                Dispatcher.Run();
                _uiStopped.TrySetResult();
            }
            catch (Exception ex) { ready.TrySetException(ex); _uiStopped.TrySetException(ex); }
        }) { IsBackground = true, Name = "VoWin hotplug callback test UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _ui = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var preferences = new PreferenceDatabaseService(Path.Combine(_directory, "preferences.db"));
        await preferences.InitializeAsync();
        _service = new(preferences, new(Path.Combine(_directory, "telephony.json")),
            Path.Combine(_directory, "test.log"), uiDispatcher: _ui);
    }

    private ModemSlot AddClosedFakeDriverSlot()
    {
        var id = Guid.NewGuid().ToString("N");
        var slot = new ModemSlot(id, $"OFFLINE-USB-TEST-{id}");
        typeof(ModemSlot).GetProperty(nameof(slot.Modem))!.SetValue(slot, new ModemDriver(slot.PortName));
        typeof(ModemSlot).GetProperty(nameof(slot.State))!.SetValue(slot, SlotState.Online);
        Assert.True(_service.Kernel.Pool.TryAddSlot(slot));
        Assert.False(slot.Modem!.IsOpen);
        return slot;
    }

    private Task StartRemovalCallback(CancellationToken token) =>
        Assert.IsAssignableFrom<Task>(typeof(VoKernelService).GetMethod("HandleUsbDeviceRemovedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_service, [token]));

    private static Task ObserveOffline(ModemSlot slot)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModemSlot.State) && slot.State == SlotState.Offline) signal.TrySetResult();
        };
        return signal.Task;
    }

    [Fact]
    public async Task RemovalCallbackRetriesBusyDeviceAfterTheOtherDeviceWasReleased()
    {
        var slot = AddClosedFakeDriverSlot();
        var other = AddClosedFakeDriverSlot();
        var driver = slot.Modem;
        var otherDetached = ObserveOffline(other);
        var protectedDetached = ObserveOffline(slot);
        var lease = await _service.Kernel.Pool.AcquireSlotLeaseAsync(slot.Id);
        Assert.NotNull(lease);
        using var cancellation = new CancellationTokenSource();
        var callback = StartRemovalCallback(cancellation.Token);
        try
        {
            await otherDetached.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Same(driver, slot.Modem);
            Assert.False(callback.IsCompleted);
        }
        finally { await lease.DisposeAsync(); }
        await protectedDetached.WaitAsync(TimeSpan.FromSeconds(3));
        await callback.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(slot.Modem);
    }

    [Fact]
    public async Task CancelingDeferredCallbackDoesNotReplayDetachAfterLeaseRelease()
    {
        var slot = AddClosedFakeDriverSlot();
        var other = AddClosedFakeDriverSlot();
        var driver = slot.Modem;
        var otherDetached = ObserveOffline(other);
        var lease = await _service.Kernel.Pool.AcquireSlotLeaseAsync(slot.Id);
        Assert.NotNull(lease);
        using var cancellation = new CancellationTokenSource();
        var callback = StartRemovalCallback(cancellation.Token);
        try
        {
            await otherDetached.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await callback.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await lease.DisposeAsync(); }
        Assert.Same(driver, slot.Modem);
        Assert.Equal(SlotState.Online, slot.State);
    }

    [Fact]
    public async Task ShutdownEndsDeferredCallbackBeforePoolGatesAreDisposed()
    {
        var slot = AddClosedFakeDriverSlot();
        var other = AddClosedFakeDriverSlot();
        var otherDetached = ObserveOffline(other);
        var lease = await _service.Kernel.Pool.AcquireSlotLeaseAsync(slot.Id);
        Assert.NotNull(lease);
        var callback = StartRemovalCallback(CancellationToken.None);
        Task? shutdown = null;
        try
        {
            await otherDetached.WaitAsync(TimeSpan.FromSeconds(3));
            shutdown = _service.DisposeAsync().AsTask();
            await callback.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await lease.DisposeAsync();
            if (shutdown is not null) await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.NotNull(shutdown);
        Assert.Empty(_service.Kernel.Pool.Slots);
    }

    public async Task DisposeAsync()
    {
        if (_service is not null) await _service.DisposeAsync();
        if (_ui is not null) _ui.BeginInvokeShutdown(DispatcherPriority.Background);
        await _uiStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
