using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.ViewModels.Pages;

public sealed record TelephonyModeOption(TelephonyMode Mode, string Title, string Subtitle, string Description);

public partial class SystemStatusViewModel
{
    public IReadOnlyList<TelephonyModeOption> TelephonyModes { get; } = new[]
    {
        new TelephonyModeOption(TelephonyMode.ModemBridge, "模块 IMS", "模组处理协议", "USB 双向语音桥接。"),
        new TelephonyModeOption(TelephonyMode.HostIms, "Host IMS", "Windows 处理协议", "使用蜂窝 IMS，通话与短信由 Windows 接管。"),
        new TelephonyModeOption(TelephonyMode.VoWifi, "VoWiFi", "通过互联网接入", "Windows 建立 IMS 隧道，需运营商支持。")
    };
    public IEnumerable<TelephonyModeOption> CellularModes => TelephonyModes.Where(option => option.Mode != TelephonyMode.VoWifi);
    public IReadOnlyList<TelephonyModeOption> OutgoingRoutes { get; } = new[]
    {
        new TelephonyModeOption(TelephonyMode.ModemBridge, "蜂窝线路", "", ""),
        new TelephonyModeOption(TelephonyMode.VoWifi, "VoWiFi", "", "")
    };
    public IReadOnlyList<ModuleManagementInstance> ModuleInstances => Slots.Select(slot => _kernelService.GetModuleInstance(slot.Id)).ToArray();
    public ModuleManagementInstance? SelectedModuleInstance
    {
        get
        {
            var selected = SelectedSlot;
            if (selected is null) return null;
            try { return _kernelService.GetModuleInstance(selected.Id); }
            catch (InvalidOperationException) { return null; } // Hotplug can retain an old selection for one UI refresh.
        }
        set { if (value != null && !ReferenceEquals(value.Slot, SelectedSlot)) SelectedSlot = value.Slot; }
    }
    [ObservableProperty] private bool _autoConnectVoWifi;
    [ObservableProperty] private TelephonyModeOption? _selectedOutgoingRoute;
    public bool CanUseCellular => SelectedSlot is { IsPcscReader: false };
    public bool IsSelectedSlotInCall => SelectedSlot?.Calls.ActiveCall != null || SelectedSlot?.HasCellularCall == true;
    public bool CanUseVoWifi => (HasCurrentSim || IsWifiConnecting) && !IsSelectedSlotInCall && !_kernelService.IsInitialScanRunning;
    public bool CanChangeWifiPreference => HasCurrentSim && !IsSelectedSlotInCall && !IsWifiConnecting;
    public bool IsWifiConnecting => SelectedModuleInstance?.WifiBusy == true ||
        SelectedSlot?.VoWifi is { KeepOnlineRequested: true, AutomaticRecoveryPaused: false, State: not VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered } ||
        SelectedSlot?.VoWifi.State is not (null or VoSharp.Telephony.VoWifi.VoWifiState.Disconnected or
            VoSharp.Telephony.VoWifi.VoWifiState.Failed or VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered);
    public string WifiActionText => IsWifiConnecting
        ? SelectedModuleInstance?.WifiBusy != true && SelectedSlot?.VoWifi.State is
            VoSharp.Telephony.VoWifi.VoWifiState.Disconnected or VoSharp.Telephony.VoWifi.VoWifiState.Failed
                ? "停止自动重连" : "取消连接"
        : SelectedSlot?.VoWifi.State == VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered ? "断开 VoWiFi"
        : SelectedIkeRetry is { UntilCardEvent: true } ? "重新检查"
        : SelectedSlot?.VoWifi.AutomaticRecoveryPaused == true ? "重试 VoWiFi" : "连接 VoWiFi";
    public GatewayStatusItem WifiStatusIndicator => _kernelService.GetGatewayStatus(SelectedSlot?.Id).Wifi;
    private IkeRetryStatus? SelectedIkeRetry => SelectedSlot is { } slot ? slot.VoWifi.GetIkeRetryStatusFor(slot.Sim?.Iccid) : null;
    public string WifiActionHint => SelectedIkeRetry is not null
        ? "重新核对当前卡的重试限制；不会清空记录。等待期间可停止自动重连。"
        : "只操作当前模组的 VoWiFi，不影响其他模组或蜂窝线路。";
    public string WifiStatusText => _kernelService.GetGatewayStatus(SelectedSlot?.Id).Wifi.InlineText;
    public string WifiStatusDetail => _kernelService.GetGatewayStatus(SelectedSlot?.Id).Wifi.Detail;

    partial void OnAutoConnectVoWifiChanged(bool value)
    {
        if (!_loadingTelephonyPreferences && SelectedSlot is { } slot && HasCurrentSim) _ = SaveWifiPreferenceAsync(slot, value);
    }
    private async Task SaveWifiPreferenceAsync(ModemSlot slot, bool value)
    {
        try
        {
            await _kernelService.UpdateTelephonyPreferencesAsync(slot.Id, preferences => preferences with { VoWifiAutoConnect = value });
        }
        catch (Exception ex) { if (ReferenceEquals(slot, SelectedSlot)) TelephonyOperationMessage = ex.Message; }
    }
    partial void OnSelectedOutgoingRouteChanged(TelephonyModeOption? value)
    {
        if (!_loadingTelephonyPreferences && SelectedSlot is { } slot && HasCurrentSim && value != null)
            _ = SaveOutgoingRouteAsync(slot, value.Mode);
    }
    private async Task SaveOutgoingRouteAsync(ModemSlot slot, TelephonyMode route)
    {
        try { await _kernelService.SetPreferredTelephonyRouteAsync(slot.Id, route); }
        catch (Exception ex) { if (ReferenceEquals(slot, SelectedSlot)) TelephonyOperationMessage = ex.Message; }
    }
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleIndependentVoWifiAsync()
    {
        if (!CanUseVoWifi || SelectedSlot is not { } slot) return;
        try
        {
            if (IsWifiConnecting || slot.VoWifi.State == VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered)
                await _kernelService.StopVoWifiAsync(slot.Id);
            else await _kernelService.StartVoWifiAsync(slot.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ReferenceEquals(slot, SelectedSlot)) TelephonyOperationMessage = ex.Message; }
        finally { NotifyAll(); }
    }

    [ObservableProperty] private TelephonyModeOption? _selectedTelephonyMode;
    [ObservableProperty] private bool _autoConnectTelephony;
    [ObservableProperty] private string _telephonyOperationMessage = string.Empty;
    private bool _loadingTelephonyPreferences;
    private int _telephonyLoadVersion;
    private string? _loadedTelephonyIccid;

    private TelephonyModeStatus? TelephonyStatus
    {
        get
        {
            try { return SelectedSlot == null ? null : _kernelService.GetTelephonyModeStatus(SelectedSlot.Id); }
            catch { return null; }
        }
    }

    public bool IsHostImsMode => SelectedTelephonyMode?.Mode == TelephonyMode.HostIms;
    public bool IsVoWifiMode => SelectedTelephonyMode?.Mode == TelephonyMode.VoWifi;
    public bool IsTelephonyWorking => TelephonyStatus?.IsEnabled == true;
    public bool IsHostImsRecovering => SelectedModuleInstance?.HostRecoveryStatus is { KeepOnlineRequested: true, IsWaiting: true };
    public bool IsTelephonyBusy => IsBusy || IsHostImsPppBusy || IsHostImsRegistrationBusy || IsHostImsProbeRunning ||
        _kernelService.IsInitialScanRunning || TelephonyStatus?.IsBusy == true;
    public bool CanEditRadioSettings => !IsTelephonyBusy && SelectedSlot?.IsHostImsPppTransitionOrActive != true &&
        !IsSelectedSlotInCall;
    public bool CanChooseTelephonyMode => !IsTelephonyBusy && !IsSelectedSlotInCall && CanUseCellular;
    public bool CanChangeAutoConnect => CanChooseTelephonyMode && HasCurrentSim;
    public bool NeedsModemReconnect => !IsHostImsRecovering && SelectedSlot is { IsPcscReader: false, Modem: not { IsOpen: true } } &&
        !SelectedSlot.IsHostImsPppTransitionOrActive &&
        !(IsTelephonyWorking && TelephonyStatus?.Mode == SelectedTelephonyMode?.Mode);
    public bool CanExecuteTelephonyAction => (!_kernelService.IsInitialScanRunning && (!IsTelephonyBusy || TelephonyStatus?.CanCancel == true)) && !IsSelectedSlotInCall &&
        (IsHostImsRecovering || SelectedSlot == null || NeedsModemReconnect || (HasCurrentSim && SelectedTelephonyMode != null &&
            (!SelectedSlot.IsPcscReader || IsVoWifiMode)));
    public string TelephonyActionText => _kernelService.IsInitialScanRunning ? "正在检测…"
        : IsHostImsRecovering ? "停止自动恢复"
        : TelephonyStatus?.CanCancel == true ? "取消连接"
        : SelectedSlot == null ? "扫描设备"
        : IsTelephonyBusy ? "正在处理…"
        : NeedsModemReconnect ? "重新扫描"
        : IsTelephonyWorking && TelephonyStatus?.Mode == SelectedTelephonyMode?.Mode ? "停止蜂窝 IMS"
        : IsTelephonyWorking ? "切换蜂窝 IMS" : "启用蜂窝 IMS";
    public string TelephonyHeadline => _kernelService.IsInitialScanRunning ? "正在检测通信设备…"
        : SelectedSlot == null ? "连接设备"
        : IsTelephonyBusy ? TelephonyStatus?.Message ?? "正在处理…"
        : NeedsModemReconnect ? "模组连接已断开"
        : !HasCurrentSim ? "等待 SIM 就绪"
        : IsTelephonyWorking ? $"{TelephonyModes.First(option => option.Mode == TelephonyStatus!.Mode).Title} 已启用"
        : "选择工作方式，启用通信";
    public string TelephonyDetail => !string.IsNullOrWhiteSpace(TelephonyOperationMessage) ? TelephonyOperationMessage
        : _kernelService.IsInitialScanRunning ? _kernelService.InitialScanStatus
        : SelectedSlot == null ? "插入模组或读卡器。"
        : NeedsModemReconnect && !IsTelephonyBusy ? "请重新扫描设备。"
        : !HasCurrentSim ? "请插入 SIM；如果卡片设置了 PIN，请先解锁。"
        : SelectedSlot.IsPcscReader && !IsVoWifiMode ? "此设备是读卡器，仅支持 VoWiFi；蜂窝模式需要通信模组。"
        : TelephonyStatus?.Message ?? string.Empty;
    public string TelephonyUsageHint => SelectedSlot == null ? "插入设备后扫描。"
        : NeedsModemReconnect && !IsTelephonyBusy ? "设备已断开，请重新扫描。"
        : !HasCurrentSim && !IsTelephonyBusy ? "请插入 SIM 或解锁 PIN。"
        : !string.IsNullOrWhiteSpace(TelephonyOperationMessage)
            ? TelephonyOperationMessage.Length <= 64 ? TelephonyOperationMessage : "操作结果请查看详情。"
        : SelectedSlot.IsPcscReader ? "读卡器仅支持下方 VoWiFi。"
        : TelephonyStatus?.HasError == true ? "连接失败，请重试或查看详情。" : string.Empty;
    public string TelephonyRouteText => IsTelephonyWorking
        ? $"通话 / 短信 / SIP 网关 → {TelephonyModes.First(option => option.Mode == TelephonyStatus!.Mode).Title}"
        : "通话与短信线路未启用";
    public bool ShowAdministratorRestart => (IsHostImsMode || BundledRuntime.HasModemDrivers) && !VoKernelService.HasAdministratorToken();
    public bool ShowManualHostImsSetup => IsHostImsMode && !BundledRuntime.IsAvailable;
    partial void OnIsBusyChanged(bool value) => NotifyAll();
    public bool HasBundledModemDrivers => BundledRuntime.HasModemDrivers;
    public bool CanInstallBundledDrivers => HasBundledModemDrivers && CanChooseTelephonyMode &&
        !_kernelService.Slots.Any(slot => _kernelService.GetTelephonyModeStatus(slot.Id).IsEnabled);
    public string RuntimeDependencySummary => BundledRuntime.IsAvailable
        ? ".NET 10 · 通信组件已内置"
        : "开发版 · 通信组件未打包";
    public string RuntimeDriverSummary => HasBundledModemDrivers
        ? "已附带 Quectel 驱动；安装前请停止通信。"
        : "模组驱动需另行安装。";

    [RelayCommand]
    private async Task InstallBundledDriversAsync()
    {
        if (!CanInstallBundledDrivers) return;
        IsBusy = true;
        TelephonyOperationMessage = "正在请求 Windows 安装内置签名驱动…";
        try { TelephonyOperationMessage = await BundledDriverInstaller.InstallAsync(); }
        catch (Exception ex) { TelephonyOperationMessage = ex.Message; }
        finally { IsBusy = false; NotifyAll(); }
    }

    [RelayCommand]
    private void OpenRuntimeNotices()
    {
        try
        {
            if (BundledRuntime.Find("THIRD-PARTY-NOTICES.txt") is { } path)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else TelephonyOperationMessage = "源码开发版的第三方组件说明位于 packaging/THIRD-PARTY-NOTICES.txt。";
        }
        catch (Exception ex) { TelephonyOperationMessage = $"无法打开组件说明：{ex.Message}"; }
    }

    partial void OnSelectedTelephonyModeChanged(TelephonyModeOption? value)
    {
        TelephonyOperationMessage = string.Empty;
        NotifyAll();
    }

    partial void OnAutoConnectTelephonyChanged(bool value)
    {
        if (!_loadingTelephonyPreferences && SelectedSlot is { } slot && HasCurrentSim)
            _ = SaveAutoConnectAsync(slot, value);
    }

    private async Task SaveAutoConnectAsync(ModemSlot slot, bool value)
    {
        try
        {
            await _kernelService.UpdateTelephonyPreferencesAsync(slot.Id, preferences => preferences with {
                Mode = preferences.CellularMode, AutoConnect = value,
                VoWifiAutoConnect = preferences.AutoConnectWifi, PreferredRoute = preferences.DefaultRoute });
        }
        catch (Exception ex) { TelephonyOperationMessage = $"自动连接设置未保存：{ex.Message}"; }
    }

    private async Task LoadTelephonyPreferencesAsync(ModemSlot slot)
    {
        var version = Interlocked.Increment(ref _telephonyLoadVersion);
        var iccid = slot.Sim?.Iccid;
        try
        {
            var preferences = await _kernelService.GetTelephonyPreferencesAsync(slot.Id);
            var setup = await _kernelService.GetTelephonySettingsAsync();
            if (version != Volatile.Read(ref _telephonyLoadVersion) || !ReferenceEquals(slot, SelectedSlot) ||
                !string.Equals(iccid, slot.Sim?.Iccid, StringComparison.Ordinal)) return;
            _loadingTelephonyPreferences = true;
            SelectedTelephonyMode = TelephonyModes.First(option => option.Mode == preferences.CellularMode);
            AutoConnectTelephony = preferences.AutoConnectCellular;
            AutoConnectVoWifi = preferences.AutoConnectWifi;
            LoadVoWifiDiscoveryEditor(preferences);
            SelectedOutgoingRoute = OutgoingRoutes.First(option => option.Mode == (preferences.DefaultRoute == TelephonyMode.VoWifi ? TelephonyMode.VoWifi : TelephonyMode.ModemBridge));
            if (!string.IsNullOrWhiteSpace(setup.WinDivertDllPath)) HostImsWinDivertDllPath = setup.WinDivertDllPath;
            if (!string.IsNullOrWhiteSpace(setup.HandoffScriptPath) &&
                System.IO.Path.GetExtension(setup.HandoffScriptPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                HostImsPppScriptPath = setup.HandoffScriptPath;
            if (!string.IsNullOrWhiteSpace(setup.RasHelperPath)) HostImsRasProbePath = setup.RasHelperPath;
            _loadedTelephonyIccid = iccid;
            NotifyAll();
        }
        catch (Exception ex) { TelephonyOperationMessage = $"读取通信设置失败：{ex.Message}"; }
        finally { _loadingTelephonyPreferences = false; }
    }

    private async Task ExecuteTelephonyActionAsync()
    {
        if (!CanExecuteTelephonyAction) return;
        if (SelectedSlot is { } connectingSlot && TelephonyStatus?.IsBusy == true)
        {
            _kernelService.CancelTelephonyModeConnection(connectingSlot.Id);
            TelephonyOperationMessage = IsHostImsRecovering ? "正在停止 Host IMS 自动恢复…" : "正在取消连接并恢复模组状态…";
            return;
        }
        if (SelectedSlot == null || NeedsModemReconnect)
        {
            IsBusy = true;
            TelephonyOperationMessage = "正在扫描通信设备…";
            try
            {
                await _kernelService.DiscoverSlotsAsync();
                SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();
                TelephonyOperationMessage = SelectedSlot == null || NeedsModemReconnect
                    ? "未检测到在线设备，请检查 USB 连接和模组驱动。" : string.Empty;
            }
            catch (Exception ex) { TelephonyOperationMessage = $"设备扫描失败：{ex.Message}"; }
            finally { IsBusy = false; NotifyAll(); }
            return;
        }
        var slot = SelectedSlot;
        var mode = SelectedTelephonyMode!.Mode;
        var shouldStop = IsTelephonyWorking && TelephonyStatus?.Mode == mode;
        TelephonyOperationMessage = string.Empty;
        try
        {
            if (shouldStop) await _kernelService.StopTelephonyModeAsync(slot.Id);
            else
            {
                var setup = await _kernelService.GetTelephonySettingsAsync();
                await _kernelService.SaveTelephonySetupAsync(setup.AutoDiscoverModems,
                    HostImsWinDivertDllPath, HostImsPppScriptPath, HostImsRasProbePath);
                await _kernelService.EnableTelephonyModeAsync(slot.Id, mode);
            }
            await LoadSimSwitchesAsync(slot);
        }
        catch (OperationCanceledException) { if (ReferenceEquals(slot, SelectedSlot)) TelephonyOperationMessage = "连接已取消。"; }
        catch (Exception ex) { if (ReferenceEquals(slot, SelectedSlot)) TelephonyOperationMessage = ex.Message; }
        finally { NotifyAll(); }
    }

    [RelayCommand]
    private async Task ConfigureHostImsAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WinDivert 组件|WinDivert.dll", Title = "选择 x64 WinDivert.dll（同目录需有 WinDivert64.sys）"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var setup = await _kernelService.GetTelephonySettingsAsync();
            await _kernelService.SaveTelephonySetupAsync(setup.AutoDiscoverModems, dialog.FileName,
                HostImsPppScriptPath, HostImsRasProbePath);
            HostImsWinDivertDllPath = dialog.FileName;
            TelephonyOperationMessage = "Host IMS 组件位置已记住，点击启用通信即可连接。";
        }
        catch (Exception ex) { TelephonyOperationMessage = $"组件配置未保存：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task RestartAsAdministratorAsync()
    {
        if (!CanChooseTelephonyMode) return;
        try
        {
            if (SelectedSlot != null && SelectedTelephonyMode != null && HasCurrentSim)
                await _kernelService.SaveTelephonyPreferencesAsync(SelectedSlot.Id,
                    new(SelectedTelephonyMode.Mode, AutoConnectTelephony));
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("未找到应用程序路径。");
            Process.Start(new ProcessStartInfo(executable, $"--wait-for-exit {Environment.ProcessId}")
                { UseShellExecute = true, Verb = "runas" });
            if (Application.Current is App app) await app.ShutdownGracefullyAsync();
            else Application.Current.Shutdown();
        }
        catch (Exception ex) { TelephonyOperationMessage = $"未能以管理员身份重启：{ex.Message}"; }
    }
}
