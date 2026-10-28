using System.IO;
using Microsoft.Data.Sqlite;
using System.Reflection;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;
using VoWin.Models;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class TelephonyModeServiceTests : IAsyncLifetime
{
    [Fact]
    public async Task DesktopDisposalMarksEverySlotForExitAndRejectsNewModemWork()
    {
        var first = AddSlot("exit-test-a");
        var second = AddSlot("exit-test-b");
        await _service.DisposeAsync();
        var exit = typeof(ModemSlot).GetField("_applicationExitRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(1, exit.GetValue(first));
        Assert.Equal(1, exit.GetValue(second));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _service.Kernel.Pool.AddOrUpdateSlotAsync("COM999"));
    }

    [Fact]
    public async Task ShutdownUsbReenumerationCannotRestartDiscoveryOrCancelADisposedSource()
    {
        await _service.DisposeAsync();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
        {
            _service.OnUsbDeviceInserted();
            _service.OnUsbDeviceRemoved();
        })));
        Assert.Null(typeof(VoKernelService).GetField("_usbDebounceCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service));
        Assert.Empty(_service.Kernel.Pool.Slots);
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoWin-modes-tests-" + Guid.NewGuid().ToString("N"));
    private VoKernelService _service = null!;

    public async Task InitializeAsync()
    {
        var preferences = new PreferenceDatabaseService(Path.Combine(_directory, "preferences.db"));
        await preferences.InitializeAsync();
        _service = new(preferences, new(Path.Combine(_directory, "telephony.json")),
            Path.Combine(_directory, "test.log"));
    }

    private ModemSlot AddSlot(string? iccid = "test-sim-a")
    {
        var id = Guid.NewGuid().ToString("N");
        // Each independent mock modem needs a distinct control port, just like
        // the two physical USB devices; duplicate port ownership is rejected.
        var slot = new ModemSlot(id, $"OFFLINE-TEST-PORT-{id}");
        if (iccid != null)
            typeof(ModemSlot).GetProperty(nameof(ModemSlot.Sim))!.SetValue(slot,
                SimIdentity.FromImsiAndIccid("460110000000001", iccid));
        Assert.True(_service.Kernel.Pool.TryAddSlot(slot));
        return slot;
    }

    private ModemSlot AddClosedFakeDriverSlot(string iccid = "test-sim-a")
    {
        var slot = AddSlot(iccid);
        typeof(ModemSlot).GetProperty(nameof(slot.Modem))!.SetValue(slot, new VoSharp.Modem.ModemDriver(slot.PortName));
        typeof(ModemSlot).GetProperty(nameof(slot.State))!.SetValue(slot, SlotState.Online);
        return slot;
    }

    [Fact]
    public async Task DiscoveryPreferencesApplyOnlyToTheirSimAndResetAfterCardChange()
    {
        var a = AddSlot("epdg-a"); var b = AddSlot("epdg-b");
        await _service.UpdateTelephonyPreferencesAsync(a.Id, preferences => preferences with
        { VoWifiDiscovery = new("EPDG.Example.test", "515-66", true) });
        await _service.ApplyVoWifiDiscoveryPreferencesAsync(a);
        await _service.ApplyVoWifiDiscoveryPreferencesAsync(b);
        Assert.Equal("epdg.example.test", a.VoWifi.UserEpdgConfiguration!.PreferredAddresses);
        Assert.Equal("51566", a.VoWifi.UserEpdgConfiguration!.AdditionalPlmns);
        Assert.Null(b.VoWifi.UserEpdgConfiguration);
        typeof(ModemSlot).GetProperty(nameof(a.Sim))!.SetValue(a,
            SimIdentity.FromImsiAndIccid("204041234567890", "epdg-new-card"));
        Assert.Null(a.VoWifi.UserEpdgConfiguration);
        await _service.ApplyVoWifiDiscoveryPreferencesAsync(a);
        Assert.Null(a.VoWifi.UserEpdgConfiguration);
    }

    [Fact]
    public async Task InvalidDiscoveryUpdatePreservesStoredPreferences()
    {
        var slot = AddSlot();
        await _service.UpdateTelephonyPreferencesAsync(slot.Id, preferences => preferences with
        { VoWifiDiscovery = new("192.0.2.10", ManualOnly: true) });
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateTelephonyPreferencesAsync(slot.Id, preferences =>
            preferences with { VoWifiDiscovery = new("https://secret@example.test/path") }));
        Assert.Equal("192.0.2.10", (await _service.GetTelephonyPreferencesAsync(slot.Id)).VoWifiDiscovery!.PreferredAddresses);
    }

    private async Task<SystemStatusViewModel> DiscoveryEditorFor(ModemSlot slot)
    {
        typeof(VoKernelService).GetProperty(nameof(VoKernelService.IsInitialScanRunning))!.SetValue(_service, false);
        var model = new SystemStatusViewModel(_service);
        Assert.Same(slot, model.SelectedSlot);
        // Wait for real editor readiness, as the UI does. Faking the loaded
        // ICCID let the constructor's pending read overwrite test input.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!model.CanEditVoWifiDiscovery) await Task.Delay(5, deadline.Token);
        return model;
    }

    [Fact]
    public async Task PlmnGeneratorShowsPaddedMncAndRequiresExplicitSave()
    {
        var slot = AddSlot(); var model = await DiscoveryEditorFor(slot);
        Assert.Contains("mcc460.pub.3gppnetwork.org", model.VoWifiGeneratedEpdgPreview);
        model.VoWifiAdditionalPlmns = "515-66, 20404, 310-260";
        Assert.Equal("epdg.epc.mnc066.mcc515.pub.3gppnetwork.org\nepdg.epc.mnc004.mcc204.pub.3gppnetwork.org\nepdg.epc.mnc260.mcc310.pub.3gppnetwork.org", model.VoWifiGeneratedEpdgPreview);
        model.GenerateVoWifiEpdgCommand.Execute(null);
        Assert.Equal(model.VoWifiGeneratedEpdgPreview, model.VoWifiPreferredEpdgs);
        model.GenerateVoWifiEpdgCommand.Execute(null); // No duplicate addresses.
        Assert.Equal(3, model.VoWifiPreferredEpdgs.Split('\n').Length);
        Assert.Null((await _service.GetTelephonyPreferencesAsync(slot.Id)).VoWifiDiscovery);
        Assert.Null(slot.VoWifi.UserEpdgConfiguration);
        Assert.Contains("点击保存", model.VoWifiDiscoverySettingsMessage);
    }

    [Fact]
    public async Task InvalidPlmnOrStaleSimCannotOverwriteGeneratedAddressFields()
    {
        var slot = AddSlot(); var model = await DiscoveryEditorFor(slot);
        model.VoWifiPreferredEpdgs = "existing.example.test";
        model.VoWifiAdditionalPlmns = "not-a-plmn";
        Assert.Contains("PLMN 格式", model.VoWifiGeneratedEpdgPreview);
        model.GenerateVoWifiEpdgCommand.Execute(null);
        Assert.Equal("existing.example.test", model.VoWifiPreferredEpdgs);
        model.VoWifiAdditionalPlmns = "515-66";
        typeof(ModemSlot).GetProperty(nameof(slot.Sim))!.SetValue(slot,
            SimIdentity.FromImsiAndIccid("204041234567890", "new-card"));
        model.GenerateVoWifiEpdgCommand.Execute(null);
        Assert.Equal("existing.example.test", model.VoWifiPreferredEpdgs);
    }

    [Fact]
    public async Task CurrentEpdgShowsTheSelectedAddressAndDoesNotClaimAStoppedSessionIsLive()
    {
        var slot = AddSlot(); var model = await DiscoveryEditorFor(slot);
        Assert.Contains("尚未选择", model.VoWifiCurrentEpdg);
        var manager = typeof(VoSharp.Telephony.VoWifi.VoWifiManager);
        manager.GetProperty(nameof(slot.VoWifi.EpdgInfo))!.SetValue(slot.VoWifi,
            new VoSharp.Telephony.VoWifi.EpdgResolutionResult("epdg.example.test", "ims.example.test", "test", "sip:test",
                [System.Net.IPAddress.Parse("192.0.2.20")], HomeMcc: "460", HomeMnc: "11"));
        manager.GetProperty(nameof(slot.VoWifi.State))!.SetValue(slot.VoWifi, VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered);
        Assert.Contains("正在使用：epdg.example.test", model.VoWifiCurrentEpdg);
        Assert.Contains("192.0.2.20 · 认证归属 460-11", model.VoWifiCurrentEpdg);
        manager.GetProperty(nameof(slot.VoWifi.State))!.SetValue(slot.VoWifi, VoSharp.Telephony.VoWifi.VoWifiState.Disconnected);
        Assert.Contains("最近尝试", model.VoWifiCurrentEpdg);
        Assert.DoesNotContain("正在使用", model.VoWifiCurrentEpdg);
    }

    [Fact]
    public async Task DiscoveryEditorSaveAndResetDoNotDropExistingModeOrAutoConnectIntent()
    {
        var slot = AddSlot();
        await _service.SaveTelephonyPreferencesAsync(slot.Id, new(TelephonyMode.HostIms, false, true));
        var model = await DiscoveryEditorFor(slot);
        model.VoWifiPreferredEpdgs = "EPDG.EXAMPLE.TEST,192.0.2.10";
        model.VoWifiAdditionalPlmns = "515-66";
        model.VoWifiManualDiscoveryOnly = true;
        Assert.True(model.CanEditVoWifiDiscovery);
        await model.SaveVoWifiDiscoveryCommand.ExecuteAsync(null);
        var saved = await _service.GetTelephonyPreferencesAsync(slot.Id);
        Assert.Equal("epdg.example.test\n192.0.2.10", saved.VoWifiDiscovery!.PreferredAddresses);
        Assert.Equal(TelephonyMode.HostIms, saved.Mode); Assert.False(saved.AutoConnect); Assert.True(saved.AutoConnectWifi);
        Assert.Null(slot.VoWifi.UserEpdgConfiguration); // Save does not change an established/recovering session.
        await model.ResetVoWifiDiscoveryCommand.ExecuteAsync(null);
        var reset = await _service.GetTelephonyPreferencesAsync(slot.Id);
        Assert.Null(reset.VoWifiDiscovery);
        Assert.Equal(TelephonyMode.HostIms, reset.Mode); Assert.False(reset.AutoConnect); Assert.True(reset.AutoConnectWifi);
    }

    [Fact]
    public async Task DiscoveryEditorCannotWriteStaleFieldsAfterSimChange()
    {
        var slot = AddSlot(); var model = await DiscoveryEditorFor(slot);
        model.VoWifiPreferredEpdgs = "192.0.2.10";
        typeof(ModemSlot).GetProperty(nameof(slot.Sim))!.SetValue(slot,
            SimIdentity.FromImsiAndIccid("204041234567890", "new-card"));
        Assert.False(model.CanEditVoWifiDiscovery);
        await model.SaveVoWifiDiscoveryCommand.ExecuteAsync(null);
        Assert.Null((await _service.GetTelephonyPreferencesAsync(slot.Id)).VoWifiDiscovery);
    }

    [Fact]
    public void GatewayStatusReadsOnlyItsExplicitCachedSlotWithoutCreatingAnOperation()
    {
        var a = AddSlot(); var b = AddSlot("test-sim-b");
        a.CellularImsMode = TelephonyMode.HostIms; b.CellularImsMode = TelephonyMode.ModemBridge;
        var instanceA = _service.GetModuleInstance(a.Id);
        Assert.Null(instanceA.CellularStatus);
        var snapshotA = _service.GetGatewayStatus(a.Id);
        var snapshotB = _service.GetGatewayStatus(b.Id);
        Assert.Equal("Host IMS", snapshotA.Cellular.Label);
        Assert.Equal("模组语音桥接", snapshotB.Cellular.Label);
        Assert.Contains("SIM 资料已缓存", snapshotA.Device.Value);
        Assert.Equal("未选择设备", _service.GetGatewayStatus("removed-module").Device.Value);
        Assert.Equal("未选择设备", _service.GetGatewayStatus(null).Device.Value);
        Assert.Null(instanceA.CellularStatus);
        Assert.False(a.Modem?.IsOpen == true); Assert.False(b.Modem?.IsOpen == true);
    }

    [Fact]
    public void GatewayStatusPrefersCoreFailureAndDropsSupersededForegroundFailure()
    {
        var slot = AddSlot();
        _service.GetModuleInstance(slot.Id).WifiError = "generic foreground failure";
        var managerType = typeof(VoSharp.Telephony.VoWifi.VoWifiManager);
        managerType.GetProperty(nameof(slot.VoWifi.LastError))!.SetValue(slot.VoWifi, "specific core failure");
        Assert.Equal("specific core failure", _service.GetGatewayStatus(slot.Id).Wifi.Detail);
        managerType.GetProperty(nameof(slot.VoWifi.LastError))!.SetValue(slot.VoWifi, null);
        managerType.GetProperty(nameof(slot.VoWifi.State))!.SetValue(slot.VoWifi,
            VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered);
        Assert.DoesNotContain("foreground failure", _service.GetGatewayStatus(slot.Id).Wifi.Detail);
        Assert.Equal("设备已断开", _service.GetGatewayStatus(slot.Id).Wifi.Value); // Cached registration still isn't online.
    }

    [Fact]
    public void ModuleTileDoesNotHideAnAutomaticRecoveryBehindItsOldForegroundError()
    {
        var slot = AddSlot();
        var instance = _service.GetModuleInstance(slot.Id);
        instance.WifiError = "previous timeout";
        typeof(VoSharp.Telephony.VoWifi.VoWifiManager).GetProperty(nameof(slot.VoWifi.State))!.SetValue(slot.VoWifi,
            VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistering);
        Assert.Contains("连接中", instance.WifiSummary);
        typeof(VoSharp.Telephony.VoWifi.VoWifiManager).GetProperty(nameof(slot.VoWifi.State))!.SetValue(slot.VoWifi,
            VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered);
        Assert.Equal("VoWiFi · 已注册", instance.WifiSummary);
        Assert.DoesNotContain("失败", instance.WifiSummary);
    }

    [Fact]
    public async Task WaitingRecoveryCanBeStoppedEvenWhenTheOriginalSimIsUnavailable()
    {
        var slot = AddSlot(iccid: null);
        var model = new SystemStatusViewModel(_service);
        typeof(SystemStatusViewModel).GetField("_selectedSlot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, slot);
        typeof(VoKernelService).GetProperty(nameof(VoKernelService.IsInitialScanRunning))!.SetValue(_service, false);
        typeof(VoSharp.Telephony.VoWifi.VoWifiManager).GetField("_keepOnlineRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot.VoWifi, 1); // Saved intent only: no recovery worker or socket is started.
        Assert.True(model.IsWifiConnecting);
        Assert.True(model.CanUseVoWifi);
        Assert.Equal("停止自动重连", model.WifiActionText);
        Assert.Equal(_service.GetGatewayStatus(slot.Id).Wifi.InlineText, model.WifiStatusText);
        await model.ToggleIndependentVoWifiCommand.ExecuteAsync(null);
        Assert.False(slot.VoWifi.KeepOnlineRequested);
        Assert.False(model.IsWifiConnecting);
    }

    [Fact]
    public async Task RemoteStatusUsesTheSameGatewayLayersWithoutOperatingTheHardware()
    {
        var slot = AddSlot(); slot.CellularImsMode = TelephonyMode.HostIms;
        Assert.True(_service.Kernel.Pool.SelectSlot(slot.Id));
        var processor = new VoWin.Services.RemoteControl.RemoteCommandProcessor(_service);
        var status = await processor.ExecuteAsync("状态", CancellationToken.None);
        Assert.Contains("Host IMS", status); Assert.Contains("控制通道", status);
        Assert.Contains("VoWiFi", status); Assert.Contains("SIP", status);
        Assert.Contains("缓存", status);
        var list = await processor.ExecuteAsync("卡片", CancellationToken.None);
        Assert.Contains("Host IMS", list); Assert.Contains("未连接", list);
        Assert.False(slot.Modem?.IsOpen == true);
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("reject")]
    public async Task DisconnectedCallTargetErrorsAreNotSwallowedByDesktopService(string operation)
    {
        var other = AddSlot();
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            if (operation == "answer") await _service.AnswerAsync("disconnected-module");
            else await _service.RejectAsync("disconnected-module");
        });
        Assert.Null(other.Calls.ActiveCall);
        Assert.Null(_service.Kernel.ActiveCall);
    }

    [Fact]
    public async Task UsbAudioReservationsAndCancellationAreIndependentPerModule()
    {
        var a = AddSlot(); var b = AddSlot("test-sim-b");
        var containerA = Guid.NewGuid(); var containerB = Guid.NewGuid();
        var reserve = typeof(VoKernelService).GetMethod("ReserveCellularMedia", BindingFlags.Instance | BindingFlags.NonPublic)!;
        reserve.Invoke(_service, [a.Id, containerA]); reserve.Invoke(_service, [b.Id, containerB]);
        var reservations = (System.Collections.Concurrent.ConcurrentDictionary<string, CellularSipReservation>)typeof(VoKernelService)
            .GetField("_cellularSipReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
        var contexts = (System.Collections.Concurrent.ConcurrentDictionary<string, CellularModuleMediaContext>)typeof(VoKernelService)
            .GetField("_cellularMedia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
        var mediaA = contexts[a.Id] = new(a.Id, a.PortName, containerA);
        var mediaB = contexts[b.Id] = new(b.Id, b.PortName, containerB);
        var tokenA = mediaA.Begin(reservations[a.Id].OwnerId, CancellationToken.None);
        var tokenB = mediaB.Begin(reservations[b.Id].OwnerId, CancellationToken.None);
        Assert.Throws<TargetInvocationException>(() => reserve.Invoke(_service, ["alias-of-a", containerA]));
        await mediaA.Gate.WaitAsync();
        Task release;
        try
        {
            release = _service.ReleaseAsync(a.Id);
            Assert.False(release.IsCompleted); Assert.True(tokenA.IsCancellationRequested);
            Assert.False(tokenB.IsCancellationRequested); Assert.True(_service.IsReserved(b.Id));
            Assert.Same(release, _service.ReleaseAsync(a.Id));
            Assert.Throws<TargetInvocationException>(() => reserve.Invoke(_service, [a.Id, containerA]));
        }
        finally { mediaA.Gate.Release(); }
        await release.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(_service.IsReserved(a.Id)); Assert.True(_service.IsReserved(b.Id));
        reserve.Invoke(_service, [a.Id, containerA]);
        await _service.ReleaseAsync(a.Id);
        Assert.False(tokenB.IsCancellationRequested);
        await _service.ReleaseAsync(b.Id);
    }

    [Fact]
    public async Task OldAudioCleanupCannotCancelANewCallOnTheSameModule()
    {
        var slot = AddSlot(); var media = new CellularModuleMediaContext(slot.Id, slot.PortName, Guid.NewGuid());
        var contexts = (System.Collections.Concurrent.ConcurrentDictionary<string, CellularModuleMediaContext>)typeof(VoKernelService)
            .GetField("_cellularMedia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
        contexts[slot.Id] = media;
        media.Begin("old-call", CancellationToken.None); media.End("old-call");
        var token = media.Begin("new-call", CancellationToken.None);
        var stop = typeof(VoKernelService).GetMethod("StopCellularModuleAudioAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)stop.Invoke(_service, [slot.Id, "old-call"])!).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(token.IsCancellationRequested); Assert.True(media.IsOwner("new-call"));
        media.End("new-call");
    }

    [Fact]
    public async Task AutomaticWifiRestoreRechecksIntentAfterWaitingForCellularControl()
    {
        var slot = AddSlot(); var instance = _service.GetModuleInstance(slot.Id);
        await instance.ControlGate.WaitAsync();
        var acquired = true;
        try
        {
            var start = typeof(VoKernelService).GetMethod("StartVoWifiWithIntentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = (Task<bool>)start.Invoke(_service, [slot.Id, (long?)0])!;
            Assert.True(instance.WifiBusy);
            typeof(VoKernelService).GetMethod("AdvanceVoWifiIntent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_service, [slot.Id]);
            instance.ControlGate.Release(); acquired = false;
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(instance.WifiBusy);
            Assert.Equal(VoSharp.Telephony.VoWifi.VoWifiState.Disconnected, slot.VoWifi.State);
        }
        finally { if (acquired) instance.ControlGate.Release(); }
    }

    [Fact]
    public async Task ConcurrentIndependentSettingsDoNotOverwriteEachOther()
    {
        var slot = AddSlot();
        await _service.SaveTelephonyPreferencesAsync(slot.Id, new(TelephonyMode.HostIms, false, false));
        await Task.WhenAll(
            _service.UpdateTelephonyPreferencesAsync(slot.Id, current => current with { AutoConnect = true }),
            _service.UpdateTelephonyPreferencesAsync(slot.Id, current => current with { VoWifiAutoConnect = true }),
            _service.SetPreferredTelephonyRouteAsync(slot.Id, TelephonyMode.VoWifi));
        var saved = await _service.GetTelephonyPreferencesAsync(slot.Id);
        Assert.True(saved.AutoConnectCellular); Assert.True(saved.AutoConnectWifi);
        Assert.Equal(TelephonyMode.HostIms, saved.CellularMode);
        Assert.Equal(TelephonyMode.VoWifi, saved.DefaultRoute);
    }

    [Fact]
    public async Task EachModuleOwnsIndependentGatesStatusAndRoute()
    {
        var first = AddSlot(); var second = AddSlot("test-sim-b");
        var a = _service.GetModuleInstance(first.Id); var b = _service.GetModuleInstance(second.Id);
        Assert.Same(a, _service.GetModuleInstance(first.Id));
        Assert.NotSame(a, b);
        await a.ControlGate.WaitAsync();
        try
        {
            Assert.True(await b.ControlGate.WaitAsync(TimeSpan.FromMilliseconds(200)));
            b.ControlGate.Release();
            a.WifiBusy = true; a.WifiError = "first only";
            Assert.False(b.WifiBusy); Assert.Null(b.WifiError);
            await _service.SetPreferredTelephonyRouteAsync(second.Id, TelephonyMode.VoWifi);
            Assert.Equal(TelephonyMode.VoWifi, second.PreferredTelephonyMode);
            Assert.NotEqual(TelephonyMode.VoWifi, first.PreferredTelephonyMode);
        }
        finally { a.ControlGate.Release(); }
    }

    [Fact]
    public async Task HangupCancelsPreparingDialEvenWhileDialCommandIsPending()
    {
        var slot = AddSlot();
        typeof(VoSharp.Telephony.VoWifi.VoWifiManager).GetProperty("State")!.SetValue(slot.VoWifi,
            VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered);
        var instance = _service.GetModuleInstance(slot.Id);
        await instance.ControlGate.WaitAsync();
        try
        {
            var model = new PhoneViewModel(_service) { SelectedSlot = slot, PhoneNumber = "10086" };
            var dial = model.DialCommand.ExecuteAsync(null);
            Assert.True(model.IsCallOperationPending);
            Assert.False(dial.IsCompleted);
            // Switching the selection must not redirect hangup to a different module.
            model.SelectedSlot = AddSlot("other-sim");
            await model.HangupCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
            await dial.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(model.IsCallOperationPending);
            Assert.False(model.IsHangupOperationPending);
            Assert.Null(model.CallActionError);
            Assert.Equal(CallPhase.Ended, model.CurrentPhase);
            Assert.Null(slot.Calls.ActiveCall);
        }
        finally { instance.ControlGate.Release(); }
    }

    [Fact]
    public async Task StoppingModuleImsDoesNotStopIndependentWifiRegistration()
    {
        var slot = AddSlot();
        slot.CellularImsMode = TelephonyMode.ModemBridge;
        slot.TelephonyEnabled = true;
        typeof(VoSharp.Telephony.VoWifi.VoWifiManager).GetProperty("State")!.SetValue(slot.VoWifi,
            VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered);
        await _service.StopTelephonyModeAsync(slot.Id);
        Assert.False(slot.TelephonyEnabled);
        Assert.Equal(VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered, slot.VoWifi.State);
    }

    [Fact]
    public async Task ControlPreferenceIsGlobalPersistentAndDoesNotRestartIms()
    {
        var first = AddSlot();
        first.PreferredTelephonyMode = TelephonyMode.HostIms;
        first.TelephonyEnabled = true;
        Assert.Equal(VoSharp.Modem.ModemControlPreference.QmiPreferred, (await _service.GetTelephonySettingsAsync()).ControlPreference);
        await _service.SetModemControlPreferenceAsync(VoSharp.Modem.ModemControlPreference.AtOnly);
        Assert.Equal(VoSharp.Modem.ModemControlPreference.AtOnly, first.ControlPreference);
        Assert.Equal(TelephonyMode.HostIms, first.PreferredTelephonyMode);
        Assert.True(first.TelephonyEnabled);
        var second = AddSlot("second");
        Assert.Equal(VoSharp.Modem.ModemControlPreference.AtOnly, second.ControlPreference);
        Assert.Equal(VoSharp.Modem.ModemControlPreference.AtOnly, (await _service.GetTelephonySettingsAsync()).ControlPreference);
        await _service.SetModemControlPreferenceAsync(VoSharp.Modem.ModemControlPreference.QmiPreferred);
        Assert.Equal(VoSharp.Modem.ModemControlPreference.QmiPreferred, first.ControlPreference);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.SetModemControlPreferenceAsync((VoSharp.Modem.ModemControlPreference)42));
    }

    [Fact]
    public async Task SettingsSaveCommandPersistsControlChoiceAndDiscoveryTogether()
    {
        var model = new SettingsViewModel(_service)
        {
            SelectedControlPreference = VoSharp.Modem.ModemControlPreference.AtOnly,
            AutoConnectKnownModem = false
        };
        await model.SaveStartupSettingsCommand.ExecuteAsync(null);
        var saved = await _service.GetTelephonySettingsAsync();
        Assert.Equal(VoSharp.Modem.ModemControlPreference.AtOnly, saved.ControlPreference);
        Assert.False(saved.AutoDiscoverModems);
        Assert.Equal("已保存", model.TelephonySettingsStatus);
        Assert.Empty(_service.Kernel.Pool.Slots); // Saving settings does not start discovery/IMS.
        model.SelectedControlPreference = VoSharp.Modem.ModemControlPreference.QmiPreferred;
        await model.SaveStartupSettingsCommand.ExecuteAsync(null);
        saved = await _service.GetTelephonySettingsAsync();
        Assert.Equal(VoSharp.Modem.ModemControlPreference.QmiPreferred, saved.ControlPreference);
        Assert.False(saved.AutoDiscoverModems);
    }

    [Fact]
    public async Task SipModuleRulesPersistIndependentlyWithoutChangingImsModesOrStartingSIP()
    {
        var first = AddSlot(); var second = AddSlot("test-sim-b");
        first.CellularImsMode = TelephonyMode.HostIms; first.PreferredTelephonyMode = TelephonyMode.HostIms;
        second.CellularImsMode = TelephonyMode.ModemBridge; second.PreferredTelephonyMode = TelephonyMode.VoWifi;
        second.TelephonyEnabled = false;
        var path = Path.Combine(_directory, "sip-module-routes.json");
        var model = new SettingsViewModel(_service, new(path)) { SipGatewayPassword = "never-save-route-secret" };
        model.RefreshSipModuleRoutesCore();
        var firstRule = model.SipModuleRoutes.Single(row => row.SlotId == first.Id);
        var secondRule = model.SipModuleRoutes.Single(row => row.SlotId == second.Id);
        firstRule.Enabled = false; firstRule.IncomingTarget = "1001";
        secondRule.AllowOutgoingCalls = false; secondRule.IncomingTarget = "1002";
        await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
        var reloaded = new SettingsViewModel(_service, new(path));
        await reloaded.LoadSipSettingsAsync();
        Assert.False(reloaded.SipModuleRoutes.Single(row => row.SlotId == first.Id).Enabled);
        Assert.Equal("1001", reloaded.SipModuleRoutes.Single(row => row.SlotId == first.Id).IncomingTarget);
        Assert.True(reloaded.SipModuleRoutes.Single(row => row.SlotId == second.Id).Enabled);
        Assert.False(reloaded.SipModuleRoutes.Single(row => row.SlotId == second.Id).AllowOutgoingCalls);
        Assert.Equal("1002", reloaded.SipModuleRoutes.Single(row => row.SlotId == second.Id).IncomingTarget);
        Assert.Equal(TelephonyMode.HostIms, first.CellularImsMode);
        Assert.Equal(TelephonyMode.VoWifi, second.PreferredTelephonyMode);
        Assert.False(second.TelephonyEnabled);
        Assert.Null(_service.Kernel.LocalSipGateway);
        Assert.DoesNotContain("never-save-route-secret", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SipDefaultModuleDoesNotFollowUiSelectionOrDisappearIntoAnotherLine()
    {
        var first = AddSlot(); var second = AddSlot("test-sim-b");
        var model = new SettingsViewModel(_service)
        { SipGatewayBindAddress = "127.0.0.1", SipGatewayExtension = "local-test", SipGatewayPassword = "local-test-secret" };
        model.RefreshSipModuleRoutesCore();
        model.SelectedSipOutboundLine = model.SipOutboundLineChoices.Single(choice => choice.SlotId == first.Id);
        Assert.True(_service.Kernel.Pool.SelectSlot(second.Id));
        var options = model.BuildSipGatewayOptions();
        Assert.True(options.RequireConfiguredLines);
        Assert.Equal(first.Id, options.DefaultOutboundLineId);
        await _service.Kernel.Pool.RemoveSlotAsync(first.Id);
        model.RefreshSipModuleRoutesCore();
        Assert.Equal(first.Id, model.SelectedSipOutboundLine!.SlotId);
        Assert.Contains("未连接", model.SipModuleRoutes.Single(row => row.SlotId == first.Id).ConnectionSummary);
        Assert.Equal(first.Id, model.BuildSipGatewayOptions().DefaultOutboundLineId);
        model.SipModuleRoutes.Single(row => row.SlotId == first.Id).Enabled = false;
        Assert.Throws<ArgumentException>(() => model.BuildSipGatewayOptions());
        Assert.True(model.SipModuleRoutes.Single(row => row.SlotId == second.Id).Enabled);
        Assert.Null(_service.Kernel.LocalSipGateway);
    }

    [Fact]
    public async Task SipPreferencesSaveAndReloadNeverEnableListenerOrPersistPassword()
    {
        var path = Path.Combine(_directory, "sip.json");
        var store = new SipGatewaySettingsStore(path);
        var model = new SettingsViewModel(_service, store)
        {
            SipGatewayUseTrunk = true, SipGatewayServerAddress = "10.66.66.10",
            SipGatewayBindAddress = "10.66.66.20", SipGatewayPassword = "never-persist-this-secret",
            SipGatewayIncomingTarget = "601"
        };
        Assert.False(model.IsSipGatewayRunning);
        Assert.False(model.SipGatewayAllowOutboundCalls);
        await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
        Assert.Null(_service.Kernel.LocalSipGateway);
        Assert.Empty(_service.Kernel.Pool.Slots);
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("never-persist-this-secret", json);
        Assert.DoesNotContain("Password", json);
        Assert.DoesNotContain("\"WasEnabled\"", json);
        var reloaded = new SettingsViewModel(_service, store);
        await reloaded.LoadSipSettingsAsync();
        Assert.True(reloaded.SipGatewayUseTrunk);
        Assert.Equal("10.66.66.10", reloaded.SipGatewayServerAddress);
        Assert.Equal("601", reloaded.SipGatewayIncomingTarget);
        Assert.Empty(reloaded.SipGatewayPassword);
        Assert.False(reloaded.IsSipGatewayRunning);
        Assert.Null(_service.Kernel.LocalSipGateway);
    }

    [Fact]
    public async Task SipDeploymentImportRemembersProtectedCredentialsButNeverStartsAListener()
    {
        var path = Path.Combine(_directory, "deployment.json");
        var settings = new SipGatewayPreferences { UseTrunk = true, BindAddress = "127.0.0.1", ServerAddress = "127.0.0.1" };
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new SipGatewayDeploymentProfile(settings, "test-deployment-secret")));
        var savedPath = Path.Combine(_directory, "saved-sip.json");
        var model = new SettingsViewModel(_service, new(savedPath));
        await model.LoadSipDeploymentAsync(path);
        Assert.True(model.SipGatewayUseTrunk);
        Assert.Empty(model.SipGatewayPassword);
        Assert.False(model.SipLocalRegistrarEnabled);
        Assert.Empty(model.SipPeerInboundPassword); Assert.Empty(model.SipPeerOutboundPassword);
        Assert.False(model.SipGatewayAllowOutboundCalls);
        Assert.Null(_service.Kernel.LocalSipGateway);
        Assert.True(model.SipGatewayRestoreOnStartup);
        Assert.True(File.Exists(savedPath + ".protected"));
        Assert.False((await new SipGatewaySettingsStore(savedPath).LoadAsync()).WasEnabled);
        Assert.DoesNotContain("test-deployment-secret", await File.ReadAllTextAsync(savedPath));
        await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
        Assert.DoesNotContain("test-deployment-secret", await File.ReadAllTextAsync(savedPath));
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new SipGatewayDeploymentProfile(settings with { ServerAddress = "8.8.8.8" }, "invalid-test-secret")));
        await model.LoadSipDeploymentAsync(path); // Invalid public settings remain editable, but cannot start.
        Assert.Equal("8.8.8.8", model.SipGatewayServerAddress);
        Assert.Empty(model.SipGatewayPassword);
        Assert.Throws<ArgumentException>(() => model.BuildSipGatewayOptions());
        Assert.Null(_service.Kernel.LocalSipGateway);
    }

    [Fact]
    public void SipLocalAddressSelectionDoesNotStartSIPOrUseAStaleDisconnectedInterface()
    {
        var model = new SettingsViewModel(_service) { SipGatewayServerAddress = "127.0.0.1", SipGatewayBindAddress = "192.0.2.1" };
        model.DetectSipBindAddressCommand.Execute(null);
        Assert.Equal("127.0.0.1", model.SipGatewayBindAddress);
        Assert.Null(_service.Kernel.LocalSipGateway);
        Assert.False(model.IsSipGatewayRunning);
    }

    [Fact]
    public async Task ExplicitMissingModemRefreshCannotFallBackToAnotherModule()
    {
        var slot = AddSlot();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.RefreshMetricsAsync("removed"));
        Assert.Same(slot, _service.Kernel.Pool.ActiveSlot);
    }

    [Fact]
    public void ModuleTitlesUseTheDetectedModelButPreserveCustomNamesAndCardNicknames()
    {
        var slot = AddSlot();
        typeof(ModemSlot).GetProperty(nameof(slot.DeviceModel))!.SetValue(slot, "EC20-CE");
        var instance = _service.GetModuleInstance(slot.Id);
        Assert.Equal("EC20-CE", instance.Title);
        Assert.Contains("EC20-CE", slot.DisplayTitle);
        slot.Name = "Study gateway";
        Assert.Equal("Study gateway", instance.Title);
        slot.CardNickname = "My card";
        Assert.Equal("My card", instance.Title);
    }

    [Fact]
    public async Task SipSettingsExplicitEnableDisableWithoutSimLeavesTelephonyModesUntouched()
    {
        var slot = AddSlot(); slot.PreferredTelephonyMode = TelephonyMode.HostIms;
        using var peer = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int port;
        using (var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var model = new SettingsViewModel(_service, new(Path.Combine(_directory, "sip.json")))
        {
            SipGatewayBindAddress = "127.0.0.1", SipGatewayPort = port, SipGatewayUseTrunk = true,
            SipGatewayServerAddress = "127.0.0.1", SipGatewayServerPort = ((System.Net.IPEndPoint)peer.Client.LocalEndPoint!).Port,
            SipLocalRegistrarEnabled = false, SipGatewayTrunkUsername = "peer-in", SipGatewayIncomingTarget = "test-ring",
            SipPeerOutboundUsername = "peer-out", SipPeerExpectedRealm = "test-pbx.local",
            SipPeerInboundPassword = "test-in-secret", SipPeerOutboundPassword = "test-out-secret"
        };
        await model.ToggleSipGatewayCommand.ExecuteAsync(null);
        Assert.True(model.IsSipGatewayRunning);
        Assert.False(model.CanEditSipGateway);
        Assert.Contains("中继已配置（未验证）", model.SipGatewayStatus);
        Assert.False(slot.HasCellularCall);
        await model.ToggleSipGatewayCommand.ExecuteAsync(null);
        Assert.False(model.IsSipGatewayRunning);
        Assert.True(model.CanEditSipGateway);
        Assert.Null(_service.Kernel.LocalSipGateway);
        Assert.Equal(TelephonyMode.HostIms, slot.PreferredTelephonyMode);
    }

    [Fact]
    public void SipSettingsRejectPublicServerAndMissingPasswordBeforeStarting()
    {
        var model = new SettingsViewModel(_service, new(Path.Combine(_directory, "sip.json")))
        {
            SipGatewayBindAddress = "127.0.0.1", SipGatewayUseTrunk = true,
            SipGatewayServerAddress = "8.8.8.8", SipLocalRegistrarEnabled = false,
            SipGatewayTrunkUsername = "peer-in", SipGatewayIncomingTarget = "test-ring",
            SipPeerOutboundUsername = "peer-out", SipPeerExpectedRealm = "test-pbx.local",
            SipPeerInboundPassword = "test-in-secret", SipPeerOutboundPassword = "test-out-secret"
        };
        Assert.Throws<ArgumentException>(() => model.BuildSipGatewayOptions());
        model.SipGatewayServerAddress = "10.66.66.10";
        model.SipPeerOutboundPassword = "";
        Assert.Throws<ArgumentException>(() => model.BuildSipGatewayOptions());
        Assert.Null(_service.Kernel.LocalSipGateway);
    }

    [Fact]
    public async Task DetailedDiagnosticsPersistPreferenceWithoutChangingModesAndExportWithoutSim()
    {
        await _service.SetDetailedDiagnosticsAsync(true);
        Assert.True(_service.DetailedDiagnosticsEnabled);
        Assert.True((await _service.GetTelephonySettingsAsync()).DetailedDiagnosticsEnabled);
        var output = Path.Combine(_directory, "support.zip");
        await _service.ExportDiagnosticBundleAsync(output);
        using var archive = System.IO.Compression.ZipFile.OpenRead(output);
        using var state = new StreamReader(archive.GetEntry("state.txt")!.Open());
        var text = await state.ReadToEndAsync();
        Assert.Contains("Cached state only", text);
        Assert.Empty(_service.Kernel.Pool.Slots);
        await _service.SetDetailedDiagnosticsAsync(false);
        Assert.False((await _service.GetTelephonySettingsAsync()).DetailedDiagnosticsEnabled);
        Assert.True((await _service.GetTelephonySettingsAsync()).AutoDiscoverModems);
    }

    [Fact]
    public async Task MigratesLegacyWifiSelectionWithoutChangingOtherSims()
    {
        var first = AddSlot();
        var second = AddSlot("test-sim-b");
        await _service.Preferences.SaveSimPreferenceAsync(new() { Iccid = first.Sim!.Iccid, DefaultVoWifi = true });
        Assert.Equal(TelephonyMode.VoWifi, (await _service.GetTelephonyPreferencesAsync(first.Id)).Mode);
        Assert.Equal(TelephonyMode.ModemBridge, (await _service.GetTelephonyPreferencesAsync(second.Id)).Mode);
        await _service.SaveTelephonyPreferencesAsync(first.Id, new(TelephonyMode.HostIms, false));
        Assert.Equal(new(TelephonyMode.HostIms, false), await _service.GetTelephonyPreferencesAsync(first.Id));
        Assert.Equal(TelephonyMode.ModemBridge, (await _service.GetTelephonyPreferencesAsync(second.Id)).Mode);
    }

    [Fact]
    public async Task SelectingDraftModeDoesNotChangeRunningRoute()
    {
        var slot = AddSlot();
        slot.PreferredTelephonyMode = TelephonyMode.ModemBridge;
        await _service.SaveTelephonyPreferencesAsync(slot.Id, new(TelephonyMode.HostIms));
        Assert.Equal(TelephonyMode.ModemBridge, slot.PreferredTelephonyMode);
    }

    [Fact]
    public async Task SimReplacementDoesNotInheritHostPreference()
    {
        var slot = AddSlot();
        await _service.SaveTelephonyPreferencesAsync(slot.Id, new(TelephonyMode.HostIms));
        typeof(ModemSlot).GetProperty(nameof(ModemSlot.Sim))!.SetValue(slot,
            SimIdentity.FromImsiAndIccid("460110000000002", "test-sim-c"));
        Assert.Equal(TelephonyMode.ModemBridge, (await _service.GetTelephonyPreferencesAsync(slot.Id)).Mode);
    }

    [Fact]
    public async Task NoSimActivationFailsWithoutMutatingRoute()
    {
        var slot = AddSlot(null);
        slot.PreferredTelephonyMode = TelephonyMode.ModemBridge;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.EnableTelephonyModeAsync(slot.Id, TelephonyMode.HostIms));
        Assert.Equal(TelephonyMode.ModemBridge, slot.PreferredTelephonyMode);
        Assert.False(_service.GetTelephonyModeStatus(slot.Id).IsBusy);
    }

    [Fact]
    public async Task MissingSlotFailsPromptlyOnRepeatedAttempts()
    {
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.EnableTelephonyModeAsync("missing", TelephonyMode.HostIms).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task StoppingDisablesRoutingWithoutTurningOffCellularRadio()
    {
        var slot = AddSlot();
        slot.PreferredTelephonyMode = TelephonyMode.ModemBridge;
        await _service.StopTelephonyModeAsync(slot.Id);
        Assert.False(slot.TelephonyEnabled);
        Assert.False(slot.IsFlightMode);
        Assert.False(_service.GetTelephonyModeStatus(slot.Id).IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.Kernel.DialAsync("10086", slot.Id));
    }

    [Fact]
    public async Task HotplugCleanupDoesNotDisposeAModemWithAnActivePoolLease()
    {
        var protectedSlot = AddSlot();
        var missingSlot = AddSlot("test-missing-sim");
        foreach (var item in new[] { protectedSlot, missingSlot })
        {
            typeof(ModemSlot).GetProperty(nameof(item.Modem))!.SetValue(item, new VoSharp.Modem.ModemDriver(item.PortName));
            typeof(ModemSlot).GetProperty(nameof(item.State))!.SetValue(item, SlotState.Online);
        }
        var driver = protectedSlot.Modem;
        var lease = await _service.Kernel.Pool.AcquireSlotLeaseAsync(protectedSlot.Id);
        Assert.NotNull(lease);
        try
        {
            Assert.True(await _service.DetachMissingModemsAsync(new HashSet<string>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Same(driver, protectedSlot.Modem);
            Assert.Equal(SlotState.Online, protectedSlot.State);
            Assert.Null(missingSlot.Modem);
            Assert.Equal(SlotState.Offline, missingSlot.State);
        }
        finally { await lease.DisposeAsync(); }
    }

    [Fact]
    public async Task HotplugCleanupDoesNotDisposeAModemDuringAnInstanceControlOperation()
    {
        var protectedSlot = AddSlot();
        var missingSlot = AddSlot("test-missing-sim");
        foreach (var item in new[] { protectedSlot, missingSlot })
        {
            typeof(ModemSlot).GetProperty(nameof(item.Modem))!.SetValue(item, new VoSharp.Modem.ModemDriver(item.PortName));
            typeof(ModemSlot).GetProperty(nameof(item.State))!.SetValue(item, SlotState.Online);
        }
        var driver = protectedSlot.Modem;
        var gate = _service.GetModuleInstance(protectedSlot.Id).ControlGate;
        await gate.WaitAsync();
        try
        {
            Assert.True(await _service.DetachMissingModemsAsync(new HashSet<string>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Same(driver, protectedSlot.Modem);
            Assert.Equal(SlotState.Online, protectedSlot.State);
            Assert.Null(missingSlot.Modem);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task HotplugCleanupRechecksCurrentPortsBeforeClosingAReinsertedDevice()
    {
        var slot = AddClosedFakeDriverSlot();
        var driver = slot.Modem;
        var reads = 0;
        Assert.False(await _service.DetachMissingModemsAsync(new HashSet<string>(), CancellationToken.None, () =>
        {
            reads++;
            return new HashSet<string>([slot.PortName.ToLowerInvariant()], StringComparer.OrdinalIgnoreCase);
        }));
        Assert.Equal(1, reads);
        Assert.Same(driver, slot.Modem);
        Assert.Equal(SlotState.Online, slot.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredHotplugCleanupUsesTheNewInventoryAfterLeaseRelease(bool reinserted)
    {
        var slot = AddClosedFakeDriverSlot();
        var driver = slot.Modem;
        var lease = await _service.Kernel.Pool.AcquireSlotLeaseAsync(slot.Id);
        Assert.NotNull(lease);
        try
        {
            Assert.True(await _service.DetachMissingModemsAsync(new HashSet<string>(), CancellationToken.None));
            Assert.Same(driver, slot.Modem);
        }
        finally { await lease.DisposeAsync(); }
        Assert.False(await _service.DetachMissingModemsAsync(new HashSet<string>(), CancellationToken.None,
            () => reinserted ? new HashSet<string>([slot.PortName], StringComparer.OrdinalIgnoreCase) : new HashSet<string>()));
        if (reinserted)
        {
            Assert.Same(driver, slot.Modem);
            Assert.Equal(SlotState.Online, slot.State);
        }
        else
        {
            Assert.Null(slot.Modem);
            Assert.Equal(SlotState.Offline, slot.State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HotplugInventoryFailureOrCancellationPreservesDriverAndReleasesAllGates(bool canceled)
    {
        var slot = AddClosedFakeDriverSlot();
        var driver = slot.Modem;
        using var cancellation = new CancellationTokenSource();
        Func<IReadOnlySet<string>> readPorts = () =>
        {
            if (!canceled) throw new IOException("Synthetic port inventory failure");
            cancellation.Cancel();
            return new HashSet<string>();
        };
        if (canceled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.DetachMissingModemsAsync(new HashSet<string>(), cancellation.Token, readPorts));
        else
            await Assert.ThrowsAsync<IOException>(() => _service.DetachMissingModemsAsync(new HashSet<string>(), cancellation.Token, readPorts));
        Assert.Same(driver, slot.Modem);
        var lease = await _service.Kernel.Pool.AcquireSlotLeaseAsync(slot.Id, timeoutMs: 0);
        Assert.NotNull(lease);
        await lease.DisposeAsync();
        var control = _service.GetModuleInstance(slot.Id).ControlGate;
        Assert.True(control.Wait(0));
        control.Release();
    }

    [Fact]
    public async Task HotplugCleanupDefersADirectHardwareProfileOperationWithoutBlockingOtherDevices()
    {
        var slot = AddClosedFakeDriverSlot();
        var missing = AddClosedFakeDriverSlot("test-other-sim");
        var driver = slot.Modem;
        var gate = (SemaphoreSlim)typeof(ModemSlot).GetField("_profileSwitchGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot)!;
        await gate.WaitAsync();
        try
        {
            Assert.True(await _service.DetachMissingModemsAsync(new HashSet<string>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Same(driver, slot.Modem);
            Assert.Null(missing.Modem);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task HotplugCleanupAfterServiceDisposalCannotAcquireADisposedPoolGate()
    {
        await _service.DisposeAsync();
        Assert.False(await _service.DetachMissingModemsAsync(new HashSet<string>(), CancellationToken.None));
    }

    [Fact]
    public async Task HotplugRemovalProtectsOnlyItsTransitionOwnerAndReleasesAnotherMissingModem()
    {
        var slot = AddSlot(); var other = AddSlot("test-other-sim");
        foreach (var item in new[] { slot, other })
        {
            typeof(ModemSlot).GetProperty(nameof(item.Modem))!.SetValue(item, new VoSharp.Modem.ModemDriver(item.PortName));
            typeof(ModemSlot).GetProperty(nameof(item.State))!.SetValue(item, SlotState.Online);
        }
        var protectedDriver = slot.Modem;
        var transition = typeof(ModemSlot).GetField("_hostImsPppTransition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        transition.SetValue(slot, 1);
        try
        {
            await _service.DetachMissingModemsAsync(new HashSet<string>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(protectedDriver, slot.Modem);
            Assert.Equal(SlotState.Online, slot.State);
            Assert.NotNull(slot.Sim);
            Assert.Null(other.Modem);
            Assert.Equal(SlotState.Offline, other.State);
        }
        finally { transition.SetValue(slot, 0); }
    }

    [Fact]
    public async Task CanceledHotplugCleanupCannotTouchModemOwnership()
    {
        var slot = AddSlot();
        var transition = typeof(ModemSlot).GetField("_hostImsPppTransition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        transition.SetValue(slot, 1);
        using var cancellation = new CancellationTokenSource();
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.DetachMissingModemsAsync(new HashSet<string>(), cancellation.Token));
            Assert.Equal(1, transition.GetValue(slot));
            Assert.NotNull(slot.Sim);
        }
        finally { transition.SetValue(slot, 0); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OfflineModemOffersRescanInsteadOfFailingHostActivation(bool retainedSim)
    {
        var model = new SystemStatusViewModel(_service);
        var slot = AddSlot(retainedSim ? "test-retained-sim" : null);
        typeof(SystemStatusViewModel).GetField("_selectedSlot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, slot);
        typeof(VoKernelService).GetProperty(nameof(VoKernelService.IsInitialScanRunning))!.SetValue(_service, false);
        model.SelectedTelephonyMode = model.TelephonyModes.Single(option => option.Mode == TelephonyMode.HostIms);
        Assert.True(model.NeedsModemReconnect);
        Assert.True(model.CanExecuteTelephonyAction);
        Assert.Equal("重新扫描", model.TelephonyActionText);
        Assert.Equal("模组连接已断开", model.TelephonyHeadline);
        Assert.Equal("请重新扫描设备。", model.TelephonyDetail);
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task HostRecoveryCanBeCanceledFromTheUiEvenWithoutASim()
    {
        var slot = AddSlot(iccid: null);
        slot.CellularImsMode = TelephonyMode.HostIms;
        var instance = _service.GetModuleInstance(slot.Id);
        instance.HostRecovery = new HostImsRecoveryController(() => new(false, true, false, false, false, false, true,
                HostImsLossKind.RegistrationRefreshFailed), _ => Task.CompletedTask,
            _ => throw new InvalidOperationException("A missing SIM must never start a hardware attempt."),
            delay: (_, ct) => Task.Delay(5, ct));
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!instance.HostRecovery.Status.IsWaiting && DateTime.UtcNow < timeout) await Task.Delay(5);
        var model = new SystemStatusViewModel(_service);
        typeof(SystemStatusViewModel).GetField("_selectedSlot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, slot);
        typeof(VoKernelService).GetProperty(nameof(VoKernelService.IsInitialScanRunning))!.SetValue(_service, false);
        Assert.True(model.IsHostImsRecovering);
        Assert.True(model.CanExecuteTelephonyAction);
        Assert.Equal("停止自动恢复", model.TelephonyActionText);
        await model.ExecutePrimaryActionCommand.ExecuteAsync(null);
        await instance.HostRecovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(model.IsHostImsRecovering);
        Assert.False(instance.HostRecovery.Status.KeepOnlineRequested);
        Assert.Null(slot.Modem);
    }
}
