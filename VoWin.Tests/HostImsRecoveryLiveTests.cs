using System.IO;
using System.Reflection;
using System.Windows.Threading;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;
using VoSharp.Telephony;
using VoWin.Services;
using Xunit.Abstractions;

namespace VoWin.Tests;

[Collection("Desktop modem lab")]
public sealed class HostImsRecoveryLiveTests(ITestOutputHelper output)
{
    [HostImsRecoveryLiveFact]
    public async Task ExplicitRefreshFailureRebuildsDesktopHostImsAndManualStopPreventsRestart()
    {
        Assert.True(VoKernelService.HasAdministratorToken());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await RunOnDispatcherAsync(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "VoWin explicit Host IMS recovery lab" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(10));
    }

    private async Task RunOnDispatcherAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VoWin-host-recovery-lab-" + Guid.NewGuid().ToString("N"));
        var preferences = new PreferenceDatabaseService(Path.Combine(directory, "preferences.db"));
        await preferences.InitializeAsync();
        var settings = new TelephonySettingsStore(Path.Combine(directory, "telephony.json"));
        await settings.UpdateAsync(value => value.AutoDiscoverModems = false);
        await using var service = new VoKernelService(preferences, settings, Path.Combine(directory, "recovery.log"));
        await using var slot = new ModemSlot("host-recovery-live", Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6",
            identityHistory: new SimIdentityHistoryStore(Path.Combine(directory, "identities.json")));
        Assert.True(await slot.AttachAsync(initializeSms: false));
        Assert.True(service.Kernel.Pool.TryAddSlot(slot));
        await service.SaveTelephonyPreferencesAsync(slot.Id, new(TelephonyMode.HostIms, false, false, TelephonyMode.HostIms));
        try
        {
            await service.EnableTelephonyModeAsync(slot.Id, TelephonyMode.HostIms);
            var original = slot.GetHostImsRegistrationStatus();
            Assert.True(original.IsRegistered, original.LastError);
            var instance = service.GetModuleInstance(slot.Id);
            Assert.NotNull(instance.HostRecovery);
            var clientField = typeof(ModemSlot).GetField("_hostImsRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalClient = Assert.IsType<HostImsRegistrationClient>(clientField.GetValue(slot));
            output.WriteLine("Initial real carrier registration accepted. Injecting an OWNER refresh-failure signal, not a carrier SIP 500 response; no call, SMS or microphone.");
            var fallback = (Task)typeof(ModemSlot).GetMethod("RestoreHostImsAfterRefreshFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(slot, [originalClient, "Controlled owner refresh-failure signal for explicit recovery lab."])!;
            await fallback.WaitAsync(TimeSpan.FromMinutes(3));
            var until = DateTime.UtcNow.AddMinutes(4);
            while (instance.HostRecoveryStatus?.LastRecoveredAtUtc is null &&
                   instance.HostRecoveryStatus?.IsPaused != true && DateTime.UtcNow < until)
                await Task.Delay(250);
            var restored = slot.GetHostImsRegistrationStatus();
            Assert.True(restored.IsRegistered, instance.HostRecoveryStatus?.Message ?? restored.LastError);
            Assert.False(instance.HostRecoveryStatus!.IsPaused);
            Assert.Equal(1, instance.HostRecoveryStatus.Attempts);
            Assert.NotNull(instance.HostRecoveryStatus.LastRecoveredAtUtc);
            Assert.True(restored.RegisteredAtUtc > original.RegisteredAtUtc);
            Assert.NotSame(originalClient, clientField.GetValue(slot));
            Assert.False(originalClient.IsRegistered);
            Assert.Equal(TelephonyMode.HostIms, slot.CellularImsMode);
            Assert.True(service.GetTelephonyModeStatus(slot.Id).IsEnabled);
            output.WriteLine($"Automatic Host IMS rebuild accepted: attempts={instance.HostRecoveryStatus.Attempts}; new registered-at={restored.RegisteredAtUtc:O}; PPP active={slot.GetHostImsPppBearerStatus().IsActive}; old client invalidated=True.");
            var worker = instance.HostRecovery!;
            await service.StopTelephonyModeAsync(slot.Id);
            Assert.True(worker.Completion.IsCompletedSuccessfully);
            Assert.Null(instance.HostRecovery);
            Assert.False(slot.GetHostImsRegistrationStatus().IsRegistered);
            Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
            Assert.True(slot.GetHostImsRegistrationStatus().RemoteDeregistered);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.False(slot.GetHostImsRegistrationStatus().IsRegistered);
            Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
            output.WriteLine("Manual desktop stop completed, remote de-registration confirmed; no restart during 5-second observation. Modem recovery requested; no calls or SMS sent.");
        }
        finally { await service.StopTelephonyModeAsync(slot.Id); }
    }
}

internal sealed class HostImsRecoveryLiveFactAttribute : FactAttribute
{
    public HostImsRecoveryLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_HOST_IMS_RECOVERY") != "1" ||
            Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED") != "1")
            Skip = "Explicit administrator registration/recovery lab only; never places a call or sends SMS.";
    }
}
