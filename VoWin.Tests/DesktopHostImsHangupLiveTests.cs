using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoWin.Services;
using VoWin.ViewModels.Pages;
using Xunit.Abstractions;

namespace VoWin.Tests;

[CollectionDefinition("Desktop modem lab", DisableParallelization = true)]
public sealed class DesktopModemLabCollection;

[Collection("Desktop modem lab")]
public sealed class DesktopHostImsHangupLiveTests(ITestOutputHelper output)
{
    [DesktopHostImsDialFact]
    [Trait("Lab", "DesktopHostImsDial")]
    public Task LiveDesktopDialCommandCanHangupWhileCarrierInviteIsPending() => RunLabAsync(requireConnected: false);

    [DesktopHostImsConnectedFact]
    [Trait("Lab", "DesktopHostImsConnected")]
    public Task LiveDesktopHangupEndsAnAnsweredCarrierCall() => RunLabAsync(requireConnected: true);

    private async Task RunLabAsync(bool requireConnected)
    {
        Assert.True(VoKernelService.HasAdministratorToken(), "This explicit modem lab needs an administrator test process.");
        var target = Environment.GetEnvironmentVariable("VOWIN_TEST_HOST_IMS_DIAL_TARGET")!;
        Assert.Matches(@"^\+?[1-9][0-9]{7,14}$", target);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await RunOnDispatcherAsync(target, requireConnected); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "VoWin explicit desktop IMS lab" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(8));
    }

    private async Task RunOnDispatcherAsync(string target, bool requireConnected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VoWin-desktop-ims-lab-" + Guid.NewGuid().ToString("N"));
        var preferences = new PreferenceDatabaseService(Path.Combine(directory, "preferences.db"));
        await preferences.InitializeAsync();
        var settings = new TelephonySettingsStore(Path.Combine(directory, "telephony.json"));
        await settings.UpdateAsync(s => s.AutoDiscoverModems = false);
        await using var service = new VoKernelService(preferences, settings, Path.Combine(directory, "desktop-lab.log"));
        await using var slot = new ModemSlot("desktop-host-ims-lab", Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6");
        Assert.True(await slot.AttachAsync(initializeSms: false));
        // Add only the explicitly selected, already verified modem. Do not scan
        // other ports, read the user's preferences, or enable automatic paths.
        Assert.True(service.Kernel.Pool.TryAddSlot(slot));
        await service.SaveTelephonyPreferencesAsync(slot.Id, new(TelephonyMode.HostIms, false, false, TelephonyMode.HostIms));
        var model = new PhoneViewModel(service) { SelectedSlot = slot, PhoneNumber = target };
        try
        {
            await service.EnableTelephonyModeAsync(slot.Id, TelephonyMode.HostIms);
            Assert.True(slot.GetHostImsRegistrationStatus().IsRegistered);
            // This test verifies signaling/hangup only, not listening or speech.
            slot.Calls.ExternalMediaBridgeEnabled = true;
            var dialing = model.DialCommand.ExecuteAsync(null);
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(requireConnected ? 60 : 20) &&
                   (requireConnected ? slot.Calls.State != CallState.Active : slot.Calls.State is not (CallState.Ringing or CallState.Active)) && !dialing.IsCompleted)
                await Task.Delay(100);
            Assert.NotNull(slot.Calls.ActiveCall);
            if (requireConnected)
            {
                Assert.Equal(CallState.Active, slot.Calls.State);
                output.WriteLine("Carrier answered: Active/200 OK. Keeping this signaling-only call for 20 seconds before desktop hangup.");
                await Task.Delay(TimeSpan.FromSeconds(20));
                Assert.Equal(CallState.Active, slot.Calls.State);
            }
            output.WriteLine($"Before desktop hangup: carrier call={slot.Calls.State}; dial command pending={model.IsCallOperationPending}.");
            var hangup = Stopwatch.StartNew();
            await model.HangupCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(15));
            await dialing.WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Null(model.CallActionError);
            Assert.Null(slot.Calls.ActiveCall);
            Assert.False(model.IsCallOperationPending);
            Assert.False(model.IsHangupOperationPending);
            wait.Restart();
            while (slot.GetHostImsPppBearerStatus().MediaRouteCount != 0 && wait.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(100);
            Assert.Equal(0, slot.GetHostImsPppBearerStatus().MediaRouteCount);
            Assert.True(slot.GetHostImsRegistrationStatus().IsRegistered);
            output.WriteLine($"Desktop hangup returned; dial reader finished; media routes=0; registration retained. Total hangup/reader wait={hangup.Elapsed.TotalSeconds:F1}s.");
        }
        finally
        {
            try
            {
                if (slot.Calls.ActiveCall != null) await service.HangupAsync(slot.Id);
                await service.StopTelephonyModeAsync(slot.Id);
                output.WriteLine($"After stop: PPP active={slot.GetHostImsPppBearerStatus().IsActive}; remote deregistered={slot.GetHostImsRegistrationStatus().RemoteDeregistered}.");
            }
            finally { slot.Calls.ExternalMediaBridgeEnabled = false; }
        }
    }
}

internal sealed class DesktopHostImsConnectedFactAttribute : DesktopHostImsDialFactAttribute
{
    public DesktopHostImsConnectedFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_HOST_IMS_CONNECTED_CALL") != "1")
            Skip = "Explicit answered-call lab only; coordinate availability with the approved target first.";
    }
}

internal class DesktopHostImsDialFactAttribute : FactAttribute
{
    public DesktopHostImsDialFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_HOST_IMS_DIAL_APPROVED") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOWIN_TEST_HOST_IMS_DIAL_TARGET")))
            Skip = "Explicit administrator carrier-call lab only; requires an approved target and protected owner/WinDivert paths.";
    }
}
