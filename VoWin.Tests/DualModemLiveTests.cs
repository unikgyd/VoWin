using System.IO;
using System.Windows.Threading;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;
using VoWin.Services;
using Xunit.Abstractions;
using VoSharp.Common.Events;
using VoSharp.Modem;
using System.Text.RegularExpressions;
using System.Reflection;
using VoSharp.Sip;

namespace VoWin.Tests;

[Collection("Desktop modem lab")]
public sealed class DualModemLiveTests(ITestOutputHelper output)
{
    [DualModemLiveFact]
    public Task PhilippineAutomaticRecoveryRebuildsRealRegistrationWithoutRestartingChineseHostIms() =>
        RunOnDispatcherAsync(() => RunAsync(exerciseWifiRecovery: true));

    [DualModemLiveFact]
    public Task DesktopConfiguredPhilippineRegistrationRefreshUsesTheCurrentCardPreferences() =>
        RunOnDispatcherAsync(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "VoWin-dito-refresh-lab-" + Guid.NewGuid().ToString("N"));
            output.WriteLine($"Lab evidence directory={directory}");
            var preferences = new PreferenceDatabaseService();
            await preferences.InitializeAsync();
            await using var service = new VoKernelService(preferences, diagnosticLogPath: Path.Combine(directory, "desktop-refresh.log"));
            await using var slot = new ModemSlot("com10", Environment.GetEnvironmentVariable("VOWIN_PH_AT_PORT") ?? "COM10",
                eventBus: service.Kernel.EventBus);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            Assert.True(await slot.AttachAsync(deadline.Token, initializeSms: false));
            Assert.Equal("515", slot.Sim?.Mcc);
            Assert.True(service.Kernel.Pool.TryAddSlot(slot));
            var saved = await preferences.GetSimPreferenceAsync(slot.Sim!.Iccid);
            var reportedImsi = slot.Sim.Imsi;
            var completedRenewals = false;
            output.WriteLine($"Current-card preference: present={saved is not null}; saved IMSI equals reported={saved?.Imsi == reportedImsi}; cellular-data preference={(saved is null ? "unspecified" : saved.DefaultCellularData.ToString())}");
            try
            {
                var started = await service.StartVoWifiAsync(slot.Id);
                output.WriteLine(DiagnosticLogRedactor.Redact($"Desktop VoWiFi start={started}; state={slot.VoWifi.State}; stage={slot.VoWifi.LastFailureStage}; error={slot.VoWifi.LastError ?? "none"}"));
                if (!started && slot.VoWifi.KeepOnlineRequested && !slot.VoWifi.AutomaticRecoveryPaused)
                {
                    var recovery = Assert.IsAssignableFrom<Task>(typeof(VoWifiManager).GetProperty("RecoveryCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot.VoWifi));
                    output.WriteLine($"Waiting on the same automatic recovery task; live={!recovery.IsCompleted}. No manual restart.");
                    await recovery.WaitAsync(TimeSpan.FromMinutes(4), deadline.Token);
                    started = slot.VoWifi.State == VoWifiState.ImsRegistered;
                    output.WriteLine($"Recovery result={slot.VoWifi.State}; tile={service.GetModuleInstance(slot.Id).WifiSummary}");
                }
                output.WriteLine($"Identity selection: saved override active={slot.VoWifiIdentityOverride is not null}; IMS identity equals reported={slot.VoWifi.EpdgInfo?.Impi.Split('@')[0] == reportedImsi}; IMS identity equals saved={slot.VoWifi.EpdgInfo?.Impi.Split('@')[0] == saved?.Imsi}");
                Assert.True(started, slot.VoWifi.LastError);
                await Task.Delay(2500, deadline.Token);
                output.WriteLine($"Initial health result={slot.VoWifi.GetDiagnosticInfo().LastProbeResult}");
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var session = Assert.IsType<SipRegisterSession>(typeof(VoWifiManager).GetField("_registerSession", flags)!.GetValue(slot.VoWifi));
                var authenticate = Assert.IsType<Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>>>(
                    typeof(VoWifiManager).GetField("_deregisterAkaProvider", flags)!.GetValue(slot.VoWifi));
                await session.StopRefreshingAsync();
                var previous = session.LastResult!.RegisteredAt;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    var refreshed = await session.RegisterAsync(authenticate, deadline.Token);
                    Assert.True(refreshed.RegisteredAt > previous);
                    previous = refreshed.RegisteredAt;
                    output.WriteLine($"Real desktop-configured same-flow REGISTER refresh {attempt}/3 confirmed; granted expiry={refreshed.ExpiresSeconds}s.");
                }
                var scheduled = new TaskCompletionSource<SipRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.RegistrationRefreshed += (_, result) => scheduled.TrySetResult(result);
                // Shorten ONLY this in-memory test scheduler's deadline. The carrier's
                // binding remains at its granted 3600 s; no persisted preference changes.
                typeof(SipRegisterSession).GetProperty(nameof(SipRegisterSession.LastResult))!.SetValue(session,
                    session.LastResult! with { ExpiresSeconds = 2 });
                session.StartRefreshing(authenticate);
                var automatic = await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(40), deadline.Token);
                await session.StopRefreshingAsync();
                Assert.True(automatic.RegisteredAt > previous);
                Assert.True(automatic.ExpiresSeconds > 2);
                Assert.Equal(automatic.RegisteredAt, slot.VoWifi.ImsInfo!.RegisteredAt);
                output.WriteLine("Real automatic scheduler refresh confirmed (accelerated local deadline, not a natural one-hour expiry). No call, SMS or desktop audio.");
                completedRenewals = true;
            }
            finally
            {
                await service.StopVoWifiAsync(slot.Id);
                output.WriteLine($"Cleanup: remote removal confirmed={slot.VoWifi.RemoteDeregistered}; reason={slot.VoWifi.LastDeregistrationError ?? "none"}");
                if (completedRenewals) Assert.True(slot.VoWifi.RemoteDeregistered, slot.VoWifi.LastDeregistrationError);
            }
        });

    private static async Task RunOnDispatcherAsync(Func<Task> work)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await work(); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "VoWin desktop registration lab" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromMinutes(12));
    }

    [DualModemLiveFact]
    public async Task PhilippineRegistrationRefreshRemainsResponsiveAfterOptionsRejection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await using var slot = new ModemSlot("com10", Environment.GetEnvironmentVariable("VOWIN_PH_AT_PORT") ?? "COM10");
        Assert.True(await slot.AttachAsync(deadline.Token, initializeSms: false));
        Assert.Equal("515", slot.Sim?.Mcc);
        try
        {
            var started = await slot.StartVoWifiAsync(ct: deadline.Token);
            output.WriteLine(DiagnosticLogRedactor.Redact($"Initial VoWiFi start={started}; state={slot.VoWifi.State}; stage={slot.VoWifi.LastFailureStage}; error={slot.VoWifi.LastError ?? "none"}"));
            Assert.True(started, slot.VoWifi.LastError);
            await Task.Delay(2500, deadline.Token); // First automatic OPTIONS should have received its policy response.
            var diagnostic = slot.VoWifi.GetDiagnosticInfo();
            output.WriteLine($"Initial registration={slot.VoWifi.State}; last health result={diagnostic.LastProbeResult}");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var session = Assert.IsType<SipRegisterSession>(typeof(VoWifiManager).GetField("_registerSession", flags)!.GetValue(slot.VoWifi));
            var authenticate = Assert.IsType<Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>>>(
                typeof(VoWifiManager).GetField("_deregisterAkaProvider", flags)!.GetValue(slot.VoWifi));
            await session.StopRefreshingAsync();
            var previous = session.LastResult!.RegisteredAt;
            var refreshed = await session.RegisterAsync(authenticate, deadline.Token);
            Assert.True(refreshed.RegisteredAt > previous);
            output.WriteLine($"Real same-flow REGISTER refresh confirmed; granted expiry={refreshed.ExpiresSeconds}s. No call, SMS or desktop audio.");
        }
        finally
        {
            await slot.StopVoWifiAsync();
            output.WriteLine($"Cleanup: remote removal confirmed={slot.VoWifi.RemoteDeregistered}; reason={slot.VoWifi.LastDeregistrationError ?? "none"}");
        }
    }

    [DualModemLiveFact]
    public async Task PhilippineReadinessAcrossOrdinaryStartupOperations()
    {
        await using var slot = new ModemSlot("com10", Environment.GetEnvironmentVariable("VOWIN_PH_AT_PORT") ?? "COM10");
        Assert.True(await slot.AttachAsync(initializeSms: false));
        Assert.Equal("515", slot.Sim?.Mcc);
        var expectedIccid = slot.Sim!.Iccid;
        var aka = Assert.IsType<Ec25AkaProvider>(slot.Aka);
        async Task ReadyAsync(string stage)
        {
            var result = await aka.VerifyUsimReadyAsync(expectedIccid, requireCardIccid: false);
            output.WriteLine($"{stage}: ready={result.Ready}; card-view={result.UsedCardIccid}; failure={result.Failure ?? "none"}");
            Assert.True(result.Ready, stage + ": " + result.Failure);
        }
        async Task PinReadyAsync(string stage)
        {
            // Do not select ADF.USIM between stages: that would repair file
            // selection state and hide the ordinary startup sequence's failure.
            await Task.Delay(2000);
            var response = await slot.Modem!.SendRawAtCommandAsync("AT+CPIN?", 2000);
            var ready = response.Success && response.Lines.Any(line => line.Contains("+CPIN: READY", StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"{stage}: CPIN ready={ready}; error={response.ErrorCode ?? "none"}");
            Assert.True(ready, stage + ": " + response.ErrorCode);
        }
        await ReadyAsync("after attach");
        var euicc = await slot.ProbeEuiccAsync();
        output.WriteLine($"eUICC capability={euicc.Capability}");
        await PinReadyAsync("after read-only eUICC inventory");
        Assert.False(string.IsNullOrWhiteSpace(await slot.GetEuiccEidAsync()));
        await PinReadyAsync("after EID page read");
        _ = await slot.GetEuiccProfilesAsync();
        await PinReadyAsync("after profile page read");
        var roamingApplied = await slot.SetDataRoamingEnabledAsync(false);
        output.WriteLine($"default roaming policy confirmed={roamingApplied}");
        await PinReadyAsync("after roaming preference");
        var dataApplied = await slot.SetCellularDataEnabledAsync(true);
        output.WriteLine($"default packet attach confirmed={dataApplied}");
        await PinReadyAsync("after packet attach preference");
        await slot.RefreshMetricsAsync();
        await PinReadyAsync("after telemetry refresh");
        await ReadyAsync("final identity verification");
        output.WriteLine("No profile switch, AKA, IMS registration, call or SMS was performed.");
    }

    [DualModemConfigureLiveFact]
    public async Task ExplicitlySaveVerifiedChineseHostImsAndPhilippineVoWifiStartupPreferences()
    {
        var exportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Lab", "vowin-client.json");
        var sipProfile = await SipGatewayDeploymentProfile.ReadAsync(exportPath);
        await using var china = new ModemSlot("com6", "COM6");
        await using var philippines = new ModemSlot("com10", "COM10");
        Assert.True(await china.AttachAsync(initializeSms: false));
        Assert.True(await philippines.AttachAsync(initializeSms: false));
        Assert.Equal("460", china.Sim?.Mcc); Assert.Equal("515", philippines.Sim?.Mcc);
        Assert.True(china.Sim!.IsHomePlmnAuthoritative); Assert.True(philippines.Sim!.IsHomePlmnAuthoritative);
        Assert.NotNull(china.UsbContainerId); Assert.NotEqual(china.UsbContainerId, philippines.UsbContainerId);
        var store = new TelephonySettingsStore();
        await store.UpdateAsync(settings =>
        {
            settings.AutoDiscoverModems = true;
            settings.Sims[china.Sim.Iccid] = new(TelephonyMode.HostIms, true, false, TelephonyMode.HostIms);
            settings.Sims[philippines.Sim.Iccid] = new(TelephonyMode.ModemBridge, false, true, TelephonyMode.VoWifi);
        });
        await new SipGatewaySettingsStore().SaveAsync(sipProfile.Settings); // Non-secret values only; SIP remains optional/off.
        var saved = await store.LoadAsync();
        Assert.True(saved.Sims[china.Sim.Iccid].AutoConnectCellular);
        Assert.True(saved.Sims[philippines.Sim.Iccid].AutoConnectWifi);
        Assert.False(saved.Sims[philippines.Sim.Iccid].AutoConnectCellular);
        output.WriteLine("Saved per-verified-SIM startup roles: China=Host IMS, Philippines=VoWiFi. Other SIM preferences retained. SIP server fields saved, no listener started or password persisted; no carrier registration, call or SMS.");
    }

    [DualModemLiveFact]
    public async Task PhilippineModemReadOnlyStartupTraceOmitsSubscriberIdentifiers()
    {
        await using var modem = new ModemDriver(Environment.GetEnvironmentVariable("VOWIN_PH_AT_PORT") ?? "COM10");
        modem.Open();
        Assert.True(await modem.PingAsync());
        foreach (var command in new[] { "AT+CFUN?", "AT+QGMR", "AT+GSN", "AT+CGMM", "AT+CIMI", "AT+QCCID", "AT+CPIN?" })
        {
            var response = await modem.SendRawAtCommandAsync(command, 3000);
            var safe = Regex.Replace(string.Join(" / ", response.Lines), @"\d{14,22}", "<identifier>");
            output.WriteLine($"{command}: success={response.Success}; error={response.ErrorCode}; lines={safe}");
        }
        var imsi = await modem.GetImsiAsync();
        var iccid = await modem.GetIccidAsync();
        output.WriteLine($"Parsed subscriber identity: IMSI present={imsi.Length >= 14}; ICCID present={iccid.Length >= 18}; QMI active={modem.IsQmiActive}; fallback={modem.LastQmiFallbackReason}");
        Assert.True(imsi.Length >= 14);
        Assert.True(iccid.Length >= 18);
    }

    [DualModemLiveFact]
    public async Task ChineseHostImsAndPhilippineVoWifiRemainIndependentDuringDiscoveryAndStop()
    {
        Assert.True(VoKernelService.HasAdministratorToken());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await RunAsync(); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "VoWin explicit dual-modem registration lab" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromMinutes(12));
    }

    private async Task RunAsync(bool exerciseWifiRecovery = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VoWin-dual-modem-lab-" + Guid.NewGuid().ToString("N"));
        output.WriteLine($"Lab evidence directory={directory}");
        var preferences = new PreferenceDatabaseService(Path.Combine(directory, "preferences.db"));
        await preferences.InitializeAsync();
        var settings = new TelephonySettingsStore(Path.Combine(directory, "telephony.json"));
        await settings.UpdateAsync(value => value.AutoDiscoverModems = false);
        await using var service = new VoKernelService(preferences, settings, Path.Combine(directory, "dual-modem.log"));
        using var simLogs = service.Kernel.EventBus.Subscribe(EventTopics.SystemLog, message =>
        {
            if (message.Source == "SIM") output.WriteLine(DiagnosticLogRedactor.Redact($"SIM: {message.Payload}"));
            return Task.CompletedTask;
        });
        await using var china = new ModemSlot("com6", Environment.GetEnvironmentVariable("VOWIN_CN_AT_PORT") ?? "COM6",
            eventBus: service.Kernel.EventBus,
            identityHistory: new SimIdentityHistoryStore(Path.Combine(directory, "cn-identity.json")));
        await using var philippines = new ModemSlot("com10", Environment.GetEnvironmentVariable("VOWIN_PH_AT_PORT") ?? "COM10",
            eventBus: service.Kernel.EventBus,
            identityHistory: new SimIdentityHistoryStore(Path.Combine(directory, "ph-identity.json")));
        Assert.True(await china.AttachAsync(initializeSms: false));
        Assert.True(await philippines.AttachAsync(initializeSms: false));
        Assert.Equal("460", china.Sim?.Mcc);
        Assert.Equal("515", philippines.Sim?.Mcc);
        Assert.NotNull(china.UsbContainerId);
        Assert.NotNull(philippines.UsbContainerId);
        Assert.NotEqual(china.UsbContainerId, philippines.UsbContainerId);
        Assert.True(service.Kernel.Pool.TryAddSlot(china));
        Assert.True(service.Kernel.Pool.TryAddSlot(philippines));
        await service.SaveTelephonyPreferencesAsync(china.Id, new(TelephonyMode.HostIms, false, false, TelephonyMode.HostIms));
        await service.SaveTelephonyPreferencesAsync(philippines.Id, new(TelephonyMode.ModemBridge, false, false, TelephonyMode.VoWifi));
        china.PreferredTelephonyMode = TelephonyMode.HostIms;
        philippines.PreferredTelephonyMode = TelephonyMode.VoWifi;
        var wifiRegistered = false;
        string? wifiError = null;
        try
        {
            var wifiTask = service.StartVoWifiAsync(philippines.Id);
            try { wifiRegistered = await wifiTask.WaitAsync(TimeSpan.FromMinutes(4)); }
            catch (TimeoutException)
            {
                await service.StopVoWifiAsync(philippines.Id);
                try { await wifiTask; } catch (OperationCanceledException) { }
                wifiError = "Initial VoWiFi observation deadline elapsed; canceled and awaited the same operation.";
            }
            wifiError ??= philippines.VoWifi.LastError;
            if (!wifiRegistered && philippines.VoWifi.KeepOnlineRequested && !philippines.VoWifi.AutomaticRecoveryPaused)
            {
                var initialRecovery = GetRecoveryTask(philippines.VoWifi);
                output.WriteLine($"Initial registration not accepted; observing the same automatic recovery task, live={!initialRecovery.IsCompleted}.");
                await initialRecovery.WaitAsync(TimeSpan.FromMinutes(4));
                wifiRegistered = philippines.VoWifi.State == VoWifiState.ImsRegistered;
                wifiError = philippines.VoWifi.LastError;
            }
            output.WriteLine(DiagnosticLogRedactor.Redact($"Philippine VoWiFi result={wifiRegistered}; state={philippines.VoWifi.State}; reason={wifiError ?? "none"}."));
            if (!wifiRegistered) await service.StopVoWifiAsync(philippines.Id);
            var phDriver = philippines.Modem;
            var phIdentity = philippines.Sim;
            var phManager = philippines.VoWifi;
            await service.EnableTelephonyModeAsync(china.Id, TelephonyMode.HostIms);
            Assert.True(china.GetHostImsRegistrationStatus().IsRegistered);
            Assert.Same(phDriver, philippines.Modem);
            Assert.Same(phIdentity, philippines.Sim);
            Assert.Same(phManager, philippines.VoWifi);
            output.WriteLine("Chinese real Host IMS registered; the Philippine modem and SIM ownership were retained. No call, SMS or microphone.");
            if (exerciseWifiRecovery)
            {
                Assert.True(wifiRegistered, wifiError);
                Assert.Null(china.Calls.ActiveCall);
                Assert.Null(philippines.Calls.ActiveCall);
                Assert.False(service.GetMicrophoneUplinkStatus().HasSession);
                Assert.False(service.GetSipGatewayStatus() is { IsRunning: true });
                var registeredAt = china.GetHostImsRegistrationStatus().RegisteredAtUtc;
                var hostRegistration = typeof(ModemSlot).GetField("_hostImsRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(china);
                var previousTunnel = philippines.VoWifi.IkeInitiatorSpi;
                Assert.NotEqual(0ul, previousTunnel);
                var instance = service.GetModuleInstance(philippines.Id);
                instance.WifiError = "Synthetic foreground timeout before recovery";
                // Only the failure trigger is simulated. The manager's actual
                // worker performs cleanup, card readiness, new IKE/EAP-AKA and
                // real carrier REGISTER. No transport is mocked or manually restarted.
                typeof(VoWifiManager).GetMethod("QueueAutomaticRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(philippines.VoWifi, ["Lab simulated VoWiFi liveness failure"]);
                var recovery = GetRecoveryTask(philippines.VoWifi);
                output.WriteLine($"Controlled health-failure trigger queued the actual recovery worker; live={!recovery.IsCompleted}.");
                Assert.False(recovery.IsCompleted);
                await recovery.WaitAsync(TimeSpan.FromMinutes(4));
                Assert.Equal(VoWifiState.ImsRegistered, philippines.VoWifi.State);
                Assert.NotEqual(0ul, philippines.VoWifi.IkeInitiatorSpi);
                Assert.NotEqual(previousTunnel, philippines.VoWifi.IkeInitiatorSpi);
                Assert.True(philippines.VoWifi.KeepOnlineRequested);
                Assert.False(philippines.VoWifi.AutomaticRecoveryPaused);
                Assert.Same(phDriver, philippines.Modem);
                Assert.Same(phIdentity, philippines.Sim);
                Assert.Same(phManager, philippines.VoWifi);
                Assert.True(china.GetHostImsRegistrationStatus().IsRegistered);
                Assert.Equal(registeredAt, china.GetHostImsRegistrationStatus().RegisteredAtUtc);
                Assert.Same(hostRegistration, typeof(ModemSlot).GetField("_hostImsRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(china));
                Assert.Equal("VoWiFi · 已注册", instance.WifiSummary);
                Assert.Null(instance.WifiError);
                Assert.Null(china.Calls.ActiveCall);
                Assert.Null(philippines.Calls.ActiveCall);
                Assert.False(service.GetMicrophoneUplinkStatus().HasSession);
                output.WriteLine("Actual automatic recovery completed with a different real IKE tunnel and accepted carrier IMS registration; Chinese registration/client unchanged; UI foreground error cleared. The failure was simulated, not a natural outage or initial REGISTER timeout.");
            }
            var found = await service.DiscoverSlotsAsync();
            Assert.Contains(found, slot => ReferenceEquals(slot, china));
            Assert.Contains(found, slot => ReferenceEquals(slot, philippines));
            Assert.Equal(2, service.Kernel.Pool.Slots.Values.Count(slot => !slot.IsPcscReader));
            Assert.Same(phDriver, philippines.Modem);
            Assert.True(china.GetHostImsRegistrationStatus().IsRegistered);
            if (wifiRegistered) Assert.Equal(VoWifiState.ImsRegistered, philippines.VoWifi.State);
            output.WriteLine("Discovery with an active Host IMS owner retained both physical modules without reopening a sibling port.");
            await Task.Delay(TimeSpan.FromSeconds(30));
            Assert.True(china.GetHostImsRegistrationStatus().IsRegistered);
            await service.StopTelephonyModeAsync(china.Id);
            Assert.False(china.GetHostImsPppBearerStatus().IsActive);
            Assert.True(china.GetHostImsRegistrationStatus().RemoteDeregistered);
            Assert.Same(phDriver, philippines.Modem);
            if (wifiRegistered) Assert.Equal(VoWifiState.ImsRegistered, philippines.VoWifi.State);
            output.WriteLine("Stopping Chinese Host IMS confirmed remote deregistration and did not stop the other modem's VoWiFi.");
            Assert.True(wifiRegistered, wifiError ?? "Philippine VoWiFi did not register.");
        }
        finally
        {
            await service.StopVoWifiAsync(philippines.Id);
            output.WriteLine(DiagnosticLogRedactor.Redact($"Philippine cleanup: remote removal confirmed={philippines.VoWifi.RemoteDeregistered}; reason={philippines.VoWifi.LastDeregistrationError ?? "none"}."));
            await service.StopTelephonyModeAsync(china.Id);
        }
    }

    private static Task GetRecoveryTask(VoWifiManager manager) =>
        Assert.IsAssignableFrom<Task>(typeof(VoWifiManager).GetProperty("RecoveryCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager));
}

internal sealed class DualModemLiveFactAttribute : FactAttribute
{
    public DualModemLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_DUAL_MODEM") != "1" ||
            Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED") != "1")
            Skip = "Explicit two-modem carrier registration lab only; never places a call or sends SMS.";
    }
}

internal sealed class DualModemConfigureLiveFactAttribute : FactAttribute
{
    public DualModemConfigureLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_WRITE_DUAL_MODEM_PREFERENCES") != "1")
            Skip = "Explicit user-authorized write of current verified SIM startup roles only.";
    }
}
