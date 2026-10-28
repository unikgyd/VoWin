using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;

namespace VoWin.Services;

public partial class VoKernelService
{
    private readonly TelephonySettingsStore _telephonySettingsStore = new();
    private TelephonySettings _telephonySettings = new();
    private readonly ConcurrentDictionary<string, ModuleManagementInstance> _moduleInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _restoredTelephonyCards = new();
    public ModuleManagementInstance GetModuleInstance(string slotId)
    {
        lock (_moduleInstances)
        {
            if (_moduleInstances.TryGetValue(slotId, out var existing)) return existing;
            var instance = new ModuleManagementInstance(RequireTelephonySlot(slotId));
            instance.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new(nameof(GetModuleInstance)));
            _moduleInstances[slotId] = instance;
            return instance;
        }
    }

    public void CancelTelephonyModeConnection(string slotId)
    {
        if (_moduleInstances.TryGetValue(slotId, out var recoveryInstance)) recoveryInstance.HostRecovery?.Cancel();
        if (_moduleInstances.TryGetValue(slotId, out var instance) && instance.CellularCancellation is { } cancellation)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private ModemSlot RequireTelephonySlot(string slotId) => Kernel.Pool.Slots.TryGetValue(slotId, out var slot)
        ? slot : throw new InvalidOperationException("所选设备已断开，请重新选择设备。");

    private bool IsTelephonyTransitioning(string slotId) =>
        _moduleInstances.TryGetValue(slotId, out var instance) && instance.CellularStatus?.IsBusy == true;

    public Task<TelephonySettings> GetTelephonySettingsAsync() => _telephonySettingsStore.LoadAsync();

    public async Task SetModemControlPreferenceAsync(ModemControlPreference preference)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        await _telephonySettingsStore.UpdateAsync(settings => settings.ControlPreference = preference).ConfigureAwait(false);
        // Do not dispose endpoints or rebuild connected IMS/AKA providers. New reads and new
        // authentication-provider selection follow this preference; in-flight challenges stay pinned.
        Kernel.Pool.ControlPreference = preference;
        AddLog("INFO", "Modem", $"Control preference={preference}; established IMS sessions retained.");
    }

    public async Task SaveTelephonySetupAsync(bool autoDiscover, string? winDivertDll,
        string? handoffScript = null, string? rasHelper = null)
    {
        await _telephonySettingsStore.UpdateAsync(settings =>
        {
            settings.AutoDiscoverModems = autoDiscover;
            settings.WinDivertDllPath = winDivertDll?.Trim();
            if (!string.IsNullOrWhiteSpace(handoffScript)) settings.HandoffScriptPath = handoffScript.Trim();
            if (!string.IsNullOrWhiteSpace(rasHelper)) settings.RasHelperPath = rasHelper.Trim();
        }).ConfigureAwait(false);
        _telephonySettings = await _telephonySettingsStore.LoadAsync().ConfigureAwait(false);
    }

    public async Task<SimTelephonyPreferences> GetTelephonyPreferencesAsync(string slotId)
    {
        var slot = RequireTelephonySlot(slotId);
        var iccid = slot.Sim?.Iccid;
        if (string.IsNullOrWhiteSpace(iccid)) return new(slot.IsPcscReader ? TelephonyMode.VoWifi : TelephonyMode.ModemBridge, false);
        var settings = await _telephonySettingsStore.LoadAsync().ConfigureAwait(false);
        if (settings.Sims.TryGetValue(iccid, out var saved)) return saved;
        var legacy = await Preferences.GetSimPreferenceAsync(iccid).ConfigureAwait(false);
        return new(legacy?.DefaultVoWifi == true || slot.IsPcscReader ? TelephonyMode.VoWifi : TelephonyMode.ModemBridge,
            legacy?.DefaultVoWifi == true || (!slot.IsPcscReader && legacy?.DefaultFlightMode != true));
    }

    public async Task SaveTelephonyPreferencesAsync(string slotId, SimTelephonyPreferences preferences)
    {
        var iccid = RequireTelephonySlot(slotId).Sim?.Iccid;
        if (string.IsNullOrWhiteSpace(iccid)) throw new InvalidOperationException("请先插入并解锁 SIM。");
        if (!Enum.IsDefined(preferences.Mode) || preferences.PreferredRoute is { } route && !Enum.IsDefined(route))
            throw new ArgumentOutOfRangeException(nameof(preferences));
        preferences = preferences with { VoWifiDiscovery = preferences.VoWifiDiscovery?.Normalize() };
        await _telephonySettingsStore.UpdateAsync(settings => settings.Sims[iccid] = preferences).ConfigureAwait(false);
    }

    public async Task SetPreferredTelephonyRouteAsync(string slotId, TelephonyMode route)
    {
        if (!Enum.IsDefined(route)) throw new ArgumentOutOfRangeException(nameof(route));
        var slot = RequireTelephonySlot(slotId);
        await UpdateTelephonyPreferencesAsync(slotId, preferences => preferences with { PreferredRoute = route }).ConfigureAwait(false);
        slot.PreferredTelephonyMode = route;
        GetModuleInstance(slotId).Refresh();
    }

    internal async Task ApplyVoWifiDiscoveryPreferencesAsync(ModemSlot slot)
    {
        var iccid = slot.Sim?.Iccid;
        if (string.IsNullOrWhiteSpace(iccid) || !ReferenceEquals(RequireTelephonySlot(slot.Id), slot))
            throw new InvalidOperationException("SIM 或模组已改变，请重新选择后连接 VoWiFi。");
        var preferences = await GetTelephonyPreferencesAsync(slot.Id).ConfigureAwait(false);
        if (!ReferenceEquals(RequireTelephonySlot(slot.Id), slot) || slot.Sim?.Iccid != iccid)
            throw new InvalidOperationException("SIM 已改变，请重新连接 VoWiFi。");
        slot.VoWifi.UserEpdgConfiguration = preferences.VoWifiDiscovery?.Normalize();
    }

    public async Task UpdateTelephonyPreferencesAsync(string slotId, Func<SimTelephonyPreferences, SimTelephonyPreferences> update)
    {
        var slot = RequireTelephonySlot(slotId);
        var iccid = slot.Sim?.Iccid;
        if (string.IsNullOrWhiteSpace(iccid)) throw new InvalidOperationException("请先插入并解锁 SIM。");
        var fallback = await GetTelephonyPreferencesAsync(slotId).ConfigureAwait(false);
        await _telephonySettingsStore.UpdateAsync(settings =>
        {
            if (!string.Equals(slot.Sim?.Iccid, iccid, StringComparison.Ordinal))
                throw new InvalidOperationException("SIM 已改变，未保存上一张卡的设置。");
            var preferences = update(settings.Sims.GetValueOrDefault(iccid) ?? fallback);
            if (!Enum.IsDefined(preferences.Mode) || preferences.PreferredRoute is { } route && !Enum.IsDefined(route))
                throw new ArgumentOutOfRangeException(nameof(update));
            preferences = preferences with { VoWifiDiscovery = preferences.VoWifiDiscovery?.Normalize() };
            settings.Sims[iccid] = preferences;
        }).ConfigureAwait(false);
    }

    public TelephonyModeStatus GetTelephonyModeStatus(string slotId)
    {
        var slot = RequireTelephonySlot(slotId);
        var mode = slot.CellularImsMode ?? (slot.PreferredTelephonyMode == TelephonyMode.HostIms ? TelephonyMode.HostIms : TelephonyMode.ModemBridge);
        var status = GetModuleInstance(slotId).CellularStatus;
        if (status?.IsBusy == true) return status;
        if (GetModuleInstance(slotId).HostRecoveryStatus is { } recovery)
        {
            if (recovery.IsWaiting) return new(TelephonyMode.HostIms, false, true, recovery.Message, CanCancel: true);
            if (recovery.IsPaused) return new(TelephonyMode.HostIms, false, false, recovery.Message, HasError: true);
        }
        if (status?.HasError == true) return status;
        if (!slot.TelephonyEnabled) return new(mode, false, false, "已停止 · 选择工作方式后启用");
        return mode switch
        {
            TelephonyMode.HostIms => slot.GetHostImsRegistrationStatus() is { IsRegistered: true } hostStatus
                ? new(mode, true, false, $"Windows IMS 已注册 · 短信接收{(hostStatus.SmsReceiveEnabled ? "已启用" : "未启用")}")
                : new(mode, false, false, slot.GetHostImsRegistrationStatus().LastError ?? "Windows IMS 未连接"),
            TelephonyMode.VoWifi => slot.VoWifi.State == VoWifiState.ImsRegistered
                ? new(mode, true, false, "VoWiFi 已注册 · 通话与短信可用")
                : new(mode, false, slot.VoWifi.State is not (VoWifiState.Disconnected or VoWifiState.Failed),
                    slot.VoWifi.State == VoWifiState.Failed ? "VoWiFi 连接失败 · 请检查网络或运营商支持" : "VoWiFi 未连接"),
            _ => new(mode, slot.Modem?.IsOpen == true && !slot.IsFlightMode, false,
                slot.Modem?.IsOpen == true && !slot.IsFlightMode ? "模块桥接已启用 · 网络注册状态见下方" : "模块未就绪 · 请检查设备或飞行模式")
        };
    }

    private void SetTelephonyStatus(ModemSlot slot, TelephonyMode mode, bool busy, string message, bool error = false)
    {
        var instance = GetModuleInstance(slot.Id);
        instance.CellularStatus = new(mode, false, busy, message, error,
            busy && instance.CellularCancellation != null);
        instance.Refresh();
        AddLog(error ? "ERROR" : "INFO", "Telephony", $"Slot={slot.Id}; mode={mode}; busy={busy}; {message}");
        PropertyChanged?.Invoke(this, new(nameof(GetTelephonyModeStatus)));
    }

    public static bool HasAdministratorToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task<(string Script, string Ras, string Dll)> ResolveHostImsSetupAsync(CancellationToken ct)
    {
        // Prefer built-in bearer helpers over stale research paths. A saved explicit
        // WinDivert replacement remains usable (LGPL relinking/replacement).
        string Resolve(string? saved, string env, string relative) => Environment.GetEnvironmentVariable(env)
            ?? (relative == "WinDivert.dll" && !string.IsNullOrWhiteSpace(saved) && File.Exists(saved) ? saved : null)
            ?? BundledRuntime.FindHostIms(relative)
            ?? (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved) ? saved
                : Path.Combine(AppContext.BaseDirectory, "host-ims", relative));
        // The legacy JSON field is retained, but old .ps1 settings never override the managed owner.
        var savedOwner = Path.GetExtension(_telephonySettings.HandoffScriptPath)?.Equals(".exe", StringComparison.OrdinalIgnoreCase) == true
            ? _telephonySettings.HandoffScriptPath : null;
        var script = Resolve(savedOwner, "VOWIN_HOST_IMS_PPP_OWNER", "VoSharp.HostIms.exe");
        var ras = script; // RAS now lives in the managed owner; no native RAS executable.
        var dll = Resolve(_telephonySettings.WinDivertDllPath, "VOWIN_HOST_IMS_WINDIVERT_DLL", "WinDivert.dll");
        if (!HasAdministratorToken())
            throw new InvalidOperationException("Host IMS 需要管理员权限，请点击「以管理员身份重启」。");
        if (!File.Exists(script) || !File.Exists(ras))
            throw new InvalidOperationException("缺少 Host IMS 承载组件，请重新安装完整发布版。");
        if (!File.Exists(dll) || !File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "WinDivert64.sys")))
            throw new InvalidOperationException("Host IMS 组件不完整，请重新安装完整发布版。");
        // Check before stopping any existing access or releasing the AT handle.
        // Old saved .exe paths can point at the pre-managed-RAS command protocol.
        await HostImsOwnerCompatibility.VerifyAsync(script, ct).ConfigureAwait(false);
        if (!VoKernel.TryCreateHostImsUserspaceBackend("1", dll, out var backend, out var error))
            throw new InvalidOperationException(error);
        // Factory construction only validates the configuration; the registration owns its own backend.
        (backend as IDisposable)?.Dispose();
        return (script, ras, dll);
    }

    public async Task EnableTelephonyModeAsync(string slotId, TelephonyMode mode)
    {
        if (mode == TelephonyMode.VoWifi) { await StartVoWifiAsync(slotId).ConfigureAwait(false); return; }
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var instance = GetModuleInstance(slotId);
        if (instance.Slot.HasCellularCall || instance.Slot.Calls.ActiveCall is not null)
            throw new InvalidOperationException("通话中不能切换工作方式，请先挂断。");
        await StopHostImsRecoveryAsync(instance).ConfigureAwait(false);
        await EnableTelephonyModeCoreAsync(slotId, mode).ConfigureAwait(false);
        if (mode == TelephonyMode.HostIms && instance.Slot.GetHostImsRegistrationStatus().IsRegistered)
            ArmHostImsRecovery(instance);
    }

    private async Task EnableTelephonyModeCoreAsync(string slotId, TelephonyMode mode,
        HostImsRecoveryIdentity? recoveryIdentity = null, CancellationToken recoveryToken = default)
    {
        var instance = GetModuleInstance(slotId);
        var gate = instance.ControlGate;
        var slot = RequireTelephonySlot(slotId);
        await gate.WaitAsync(recoveryToken).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(recoveryToken);
        instance.CellularCancellation = cancellation;
        var ct = cancellation.Token;
        var changed = false;
        var resumeWifi = false;
        var wifiIntent = GetVoWifiIntentVersion(slotId);
        var expectedIccid = slot.Sim?.Iccid;
        var diagnosticTimer = Stopwatch.StartNew();
        AddLog("INFO", "Telephony", $"Connection started; slot={slotId}; mode={mode}.");
        try
        {
            if (recoveryIdentity is not null) VerifyRecoveryIdentity(slot, recoveryIdentity, ct);
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (slot.HasCellularCall || slot.Calls.ActiveCall != null)
                throw new InvalidOperationException("通话中不能切换工作方式，请先挂断。");
            var iccid = slot.Sim?.Iccid;
            if (string.IsNullOrWhiteSpace(iccid)) throw new InvalidOperationException("请先插入并解锁 SIM。");
            if (slot.IsPcscReader && mode != TelephonyMode.VoWifi)
                throw new InvalidOperationException("读卡器没有蜂窝射频，请选择 VoWiFi 或连接蜂窝模组。");
            _telephonySettings = await _telephonySettingsStore.LoadAsync().ConfigureAwait(false);
            var host = mode == TelephonyMode.HostIms ? await ResolveHostImsSetupAsync(ct).ConfigureAwait(false) : default;
            if (recoveryIdentity is not null && slot.GetHostImsRegistrationStatus() is { IsRegistered: true, ExpiresAtUtc: { } liveExpiry } && liveExpiry > DateTime.UtcNow)
                return; // Another valid registration superseded the pending recovery before the control gate opened.
            if (slot.CellularImsMode == mode && GetTelephonyModeStatus(slotId).IsEnabled) return;
            if (mode == TelephonyMode.HostIms && recoveryIdentity is null) slot.PrepareExplicitHostImsRetry();
            var preferences = await GetTelephonyPreferencesAsync(slotId).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(slot.Sim?.Iccid, iccid, StringComparison.Ordinal))
                throw new InvalidOperationException("SIM 已改变，请重新选择工作方式。");
            if (recoveryIdentity is null)
                await UpdateTelephonyPreferencesAsync(slotId, current => current with { Mode = mode,
                    AutoConnect = current.AutoConnectCellular, VoWifiAutoConnect = current.AutoConnectWifi,
                    PreferredRoute = current.DefaultRoute }).ConfigureAwait(false);
            if (slot.HasCellularCall || slot.Calls.ActiveCall != null)
                throw new InvalidOperationException("线路刚收到通话，请挂断后再切换工作方式。");
            SetTelephonyStatus(slot, mode, true, "正在切换工作方式…");
            changed = true;
            slot.TelephonyEnabled = false;
            resumeWifi = (mode == TelephonyMode.HostIms || slot.IsHostImsPppTransitionOrActive) &&
                slot.VoWifi.State is not (VoWifiState.Disconnected or VoWifiState.Failed);
            if (resumeWifi) await Kernel.StopVoWifiAsync(slot.Id).ConfigureAwait(false);
            if (recoveryIdentity is null)
            {
                await Kernel.StopHostImsPppBearerAsync(slot.Id).ConfigureAwait(false);
                await Kernel.StopHostImsRegistrationAsync(slot.Id).ConfigureAwait(false);
            }
            else if (slot.HostImsRecoveryCleanupPending)
                throw new InvalidOperationException("旧 Host IMS 会话尚未清理完成，未开始自动重建。");
            slot.CellularImsMode = mode;
            slot.PreferredTelephonyMode ??= preferences.DefaultRoute;
            if (!string.Equals(slot.Sim?.Iccid, iccid, StringComparison.Ordinal))
                throw new InvalidOperationException("SIM 已改变，请重新选择工作方式。");

            if (mode == TelephonyMode.HostIms)
            {
                if (recoveryIdentity is not null)
                    await VerifyRecoveryHardwareAsync(slot, recoveryIdentity, ct).ConfigureAwait(false);
                SetTelephonyStatus(slot, mode, true, "正在移交 IMS 承载 · 模组将短暂重启…");
                var bearer = await StartHostImsPppBearerAsync(host.Script, host.Ras, true, slot.Id, ct).ConfigureAwait(false);
                if (!bearer.IsActive) throw new InvalidOperationException(bearer.LastError ?? "IMS 承载建立失败。");
                SetTelephonyStatus(slot, mode, true, "承载已就绪 · 正在自动检测端点并注册 Windows IMS…");
                if (recoveryIdentity is not null) VerifyRecoveryIdentity(slot, recoveryIdentity, ct);
                var registration = await StartHostImsRegistrationAsync(slot.Id, subscriberEgressApproved: true,
                    winDivertDll: host.Dll, cancellationToken: ct, enableSmsReceiver: true).ConfigureAwait(false);
                if (!registration.IsRegistered) throw new InvalidOperationException(registration.LastError ?? "Windows IMS 注册失败。");
            }
            else
            {
                SetTelephonyStatus(slot, mode, true, "正在启用模块蜂窝线路与 USB 语音桥接…");
                if (slot.IsFlightMode && !await SetFlightModeAsync(false, slot.Id).ConfigureAwait(false))
                    throw new InvalidOperationException("模组未确认退出飞行模式。");
                if (!await slot.SetCellularDataEnabledAsync(true, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("模组未确认蜂窝网络附着，请检查 SIM 和信号。");
                await PersistBridgeRadioPreferencesAsync(slot).ConfigureAwait(false);
                await slot.RefreshMetricsAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            slot.TelephonyEnabled = true;
            instance.CellularStatus = null;
            instance.Refresh();
        }
        catch (Exception ex)
        {
            if (changed)
            {
                slot.TelephonyEnabled = false;
                try
                {
                    if (recoveryIdentity is null)
                    {
                        await Kernel.StopHostImsPppBearerAsync(slot.Id).ConfigureAwait(false);
                        await Kernel.StopHostImsRegistrationAsync(slot.Id).ConfigureAwait(false);
                    }
                    else await slot.AbortHostImsRecoveryAttemptAsync().ConfigureAwait(false);
                }
                catch (Exception recovery) { AddLog("ERROR", "Telephony", $"恢复模组 IMS 失败：{recovery.Message}"); }
                SetTelephonyStatus(slot, mode, false,
                    ex is OperationCanceledException ? "连接已取消 · 已请求恢复模组 IMS" : ex.Message,
                    ex is not OperationCanceledException);
            }
            else if (!slot.TelephonyEnabled)
                SetTelephonyStatus(slot, mode, false, ex.Message, true);
            AddLog("WARN", "Telephony", ex.Message);
            if (ex is not OperationCanceledException)
                AddLog("ERROR", "Telephony", ex.ToString());
            throw;
        }
        finally
        {
            AddLog("INFO", "Telephony", $"Connection finished; slot={slotId}; mode={mode}; enabled={slot.TelephonyEnabled}; elapsed={diagnosticTimer.ElapsedMilliseconds}ms.");
            RecordDiagnosticState("Connection finished");
            instance.CellularCancellation = null;
            gate.Release();
            if (resumeWifi && GetVoWifiIntentVersion(slotId) == wifiIntent &&
                string.Equals(expectedIccid, slot.Sim?.Iccid, StringComparison.Ordinal))
            {
                async Task ResumeWifiAsync()
                {
                    try { await StartVoWifiWithIntentAsync(slotId, wifiIntent).ConfigureAwait(false); }
                    catch (Exception ex) { AddLog("WARN", "VoWiFi", $"蜂窝切换已结束，VoWiFi 恢复失败：{ex.Message}"); }
                }
                if (recoveryIdentity is null) await ResumeWifiAsync().ConfigureAwait(false);
                else _ = ResumeWifiAsync(); // Independent VoWiFi backoff must not strand a canceled Host recovery worker.
            }
        }
    }

    private async Task PersistBridgeRadioPreferencesAsync(ModemSlot slot)
    {
        var iccid = slot.Sim?.Iccid;
        if (string.IsNullOrWhiteSpace(iccid)) return;
        await _runtimePreferenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var preference = await Preferences.GetSimPreferenceAsync(iccid).ConfigureAwait(false) ?? new() { Iccid = iccid };
            preference.DefaultFlightMode = false;
            preference.DefaultCellularData = true;
            await Preferences.SaveSimPreferenceAsync(preference).ConfigureAwait(false);
        }
        finally { _runtimePreferenceGate.Release(); }
    }

    public async Task StopTelephonyModeAsync(string slotId)
    {
        var instance = GetModuleInstance(slotId);
        var gate = instance.ControlGate;
        var resumeWifi = false;
        var wifiIntent = GetVoWifiIntentVersion(slotId);
        var expectedIccid = instance.Slot.Sim?.Iccid;
        if (instance.Slot.HasCellularCall || instance.Slot.Calls.ActiveCall is not null)
            throw new InvalidOperationException("请先挂断当前通话，再停止通信。");
        await StopHostImsRecoveryAsync(instance).ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var slot = RequireTelephonySlot(slotId);
            if (slot.HasCellularCall || slot.Calls.ActiveCall != null)
                throw new InvalidOperationException("请先挂断当前通话，再停止通信。");
            var mode = slot.CellularImsMode ?? TelephonyMode.ModemBridge;
            slot.CellularImsMode = mode;
            SetTelephonyStatus(slot, mode, true, "正在停止通信并恢复模组 IMS…");
            slot.TelephonyEnabled = false;
            resumeWifi = slot.IsHostImsPppTransitionOrActive && slot.VoWifi.State is not (VoWifiState.Disconnected or VoWifiState.Failed);
            if (resumeWifi) await Kernel.StopVoWifiAsync(slot.Id).ConfigureAwait(false);
            await Kernel.StopHostImsPppBearerAsync(slot.Id).ConfigureAwait(false);
            await Kernel.StopHostImsRegistrationAsync(slot.Id).ConfigureAwait(false);
            instance.CellularStatus = null;
            instance.Refresh();
        }
        catch (Exception ex)
        {
            var slot = RequireTelephonySlot(slotId);
            SetTelephonyStatus(slot, slot.PreferredTelephonyMode ?? TelephonyMode.ModemBridge, false, ex.Message, true);
            throw;
        }
        finally
        {
            gate.Release();
            if (resumeWifi && GetVoWifiIntentVersion(slotId) == wifiIntent &&
                string.Equals(expectedIccid, instance.Slot.Sim?.Iccid, StringComparison.Ordinal))
            {
                try { await StartVoWifiWithIntentAsync(slotId, wifiIntent).ConfigureAwait(false); }
                catch (Exception ex) { AddLog("WARN", "VoWiFi", $"蜂窝停止后 VoWiFi 恢复失败：{ex.Message}"); }
            }
        }
    }

    private void QueueRestoreTelephonyMode(ModemSlot slot, SimTelephonyPreferences preferences)
    {
        if (Volatile.Read(ref _serviceDisposed) != 0) return;
        if (GetModuleInstance(slot.Id).HostRecoveryStatus?.KeepOnlineRequested == true) return;
        var iccid = slot.Sim?.Iccid;
        if (string.IsNullOrWhiteSpace(iccid) || IsTelephonyTransitioning(slot.Id)) return;
        if (!_restoredTelephonyCards.TryAdd(slot.Id + ":" + iccid, 0)) return;
        slot.TelephonyEnabled = false;
        if (!preferences.AutoConnectCellular && !preferences.AutoConnectWifi) return;
        var wifiIntent = GetVoWifiIntentVersion(slot.Id);
        _ = RestoreAsync();
        async Task RestoreAsync()
        {
            if (Volatile.Read(ref _serviceDisposed) == 0 && preferences.AutoConnectCellular && !slot.IsPcscReader && string.Equals(slot.Sim?.Iccid, iccid, StringComparison.Ordinal))
            {
                try { await EnableTelephonyModeAsync(slot.Id, preferences.CellularMode).ConfigureAwait(false); }
                catch (Exception ex) { AddLog("WARN", "Telephony", $"蜂窝自动连接未完成：{ex.Message}"); }
            }
            if (Volatile.Read(ref _serviceDisposed) == 0 && preferences.AutoConnectWifi && string.Equals(slot.Sim?.Iccid, iccid, StringComparison.Ordinal))
            {
                try { await StartVoWifiWithIntentAsync(slot.Id, wifiIntent).ConfigureAwait(false); }
                catch (Exception ex) { AddLog("WARN", "VoWiFi", $"VoWiFi 自动连接未完成：{ex.Message}"); }
            }
        }
    }
}
