using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;

namespace VoWin.ViewModels.Pages;

public partial class SystemStatusViewModel
{
    [ObservableProperty] private string _voWifiPreferredEpdgs = string.Empty;
    [ObservableProperty] private string _voWifiAdditionalPlmns = string.Empty;
    [ObservableProperty] private bool _voWifiManualDiscoveryOnly;
    [ObservableProperty] private bool _isSavingVoWifiDiscovery;
    [ObservableProperty] private string _voWifiDiscoverySettingsMessage = string.Empty;

    public bool CanEditVoWifiDiscovery => CanChangeWifiPreference && !IsSavingVoWifiDiscovery &&
        !_loadingTelephonyPreferences && !_kernelService.IsInitialScanRunning &&
        SelectedSlot?.Sim?.Iccid is { Length: > 0 } iccid && iccid == _loadedTelephonyIccid;

    public string VoWifiDiscoveryIdentitySummary => SelectedSlot?.Sim is { } sim
        ? $"当前归属候选 {sim.Mcc}-{sim.Mnc} · {SelectedSlot.HasImsiPlmnConflict switch { true => "检测到身份 / PLMN 变化，将保留同卡回退候选", false => "使用 SIM 归属与等价归属网络，不盲用驻留网" }}"
        : "选择已识别 SIM 的模组后，可保存这张卡专用的发现配置。";

    public string VoWifiCurrentEpdg => SelectedSlot?.VoWifi.State == VoWifiState.ResolvingEpdg
        ? "ePDG：正在解析候选地址…"
        : SelectedSlot?.VoWifi.EpdgInfo is { } epdg
        ? $"{(SelectedSlot.VoWifi.State == VoWifiState.ImsRegistered ? "正在使用" : IsWifiConnecting ? "正在尝试" : "最近尝试")}：{epdg.Fqdn}\n{epdg.IpAddresses.FirstOrDefault()?.ToString() ?? "尚未解析到 IP"} · 认证归属 {epdg.HomeMcc}-{epdg.HomeMnc}"
        : "ePDG：尚未选择地址";

    public string VoWifiNetworkStack => SelectedSlot?.VoWifi.NetworkPathStatus ?? "网络协议栈：连接时自动检测 IPv4 / IPv6";

    public string VoWifiGeneratedEpdgPreview
    {
        get
        {
            try { return string.Join('\n', GetGeneratedEpdgDomains()); }
            catch (ArgumentException) { return "PLMN 格式应为 MCC-MNC，或连续 5 / 6 位数字。"; }
        }
    }

    partial void OnVoWifiAdditionalPlmnsChanged(string value) => OnPropertyChanged(nameof(VoWifiGeneratedEpdgPreview));

    private string[] GetGeneratedEpdgDomains()
    {
        var normalized = new EpdgUserConfiguration(AdditionalPlmns: VoWifiAdditionalPlmns).Normalize();
        if (!string.IsNullOrWhiteSpace(normalized.AdditionalPlmns))
            return normalized.AdditionalPlmns.Split('\n').Select(plmn => EpdgResolver.BuildEpdgDomain(plmn[..3], plmn[3..])).ToArray();
        return SelectedSlot?.Sim is { } sim ? EpdgResolver.BuildHomePlmnCandidates(sim)
            .Select(home => EpdgResolver.BuildEpdgDomain(home.Mcc, home.Mnc)).ToArray() : [];
    }

    [RelayCommand]
    private void GenerateVoWifiEpdg()
    {
        if (!CanEditVoWifiDiscovery) return;
        try
        {
            var existing = new EpdgUserConfiguration(PreferredAddresses: VoWifiPreferredEpdgs).Normalize().PreferredAddresses;
            VoWifiPreferredEpdgs = new EpdgUserConfiguration(string.Join('\n', GetGeneratedEpdgDomains().Prepend(existing ?? ""))).Normalize().PreferredAddresses!;
            VoWifiDiscoverySettingsMessage = "已按补充 PLMN（留空则按 SIM 归属候选）生成地址；点击保存后下次连接生效。";
        }
        catch (ArgumentException) { VoWifiDiscoverySettingsMessage = "未生成：请检查 PLMN / 地址格式，地址总数不能超过 16。"; }
    }

    private void LoadVoWifiDiscoveryEditor(SimTelephonyPreferences preferences)
    {
        VoWifiPreferredEpdgs = preferences.VoWifiDiscovery?.PreferredAddresses ?? string.Empty;
        VoWifiAdditionalPlmns = preferences.VoWifiDiscovery?.AdditionalPlmns ?? string.Empty;
        VoWifiManualDiscoveryOnly = preferences.VoWifiDiscovery?.ManualOnly == true;
        VoWifiDiscoverySettingsMessage = string.Empty;
    }

    private void ClearVoWifiDiscoveryEditor()
    {
        _loadedTelephonyIccid = null;
        LoadVoWifiDiscoveryEditor(new());
    }

    [RelayCommand]
    private Task SaveVoWifiDiscoveryAsync() => SaveVoWifiDiscoveryCoreAsync(reset: false);

    [RelayCommand]
    private Task ResetVoWifiDiscoveryAsync() => SaveVoWifiDiscoveryCoreAsync(reset: true);

    private async Task SaveVoWifiDiscoveryCoreAsync(bool reset)
    {
        if (!CanEditVoWifiDiscovery || SelectedSlot is not { } slot || slot.Sim?.Iccid is not { } iccid) return;
        EpdgUserConfiguration? configuration;
        try
        {
            configuration = reset ? null : new EpdgUserConfiguration(VoWifiPreferredEpdgs, VoWifiAdditionalPlmns,
                VoWifiManualDiscoveryOnly).Normalize();
        }
        catch (ArgumentException)
        {
            VoWifiDiscoverySettingsMessage = "未保存：地址请填域名或可用 IP，不含协议、端口、路径；PLMN 填 5 / 6 位数字（可带 MCC-MNC 连字符）。每项最多 16 个，仅手动模式不能留空。";
            return;
        }
        IsSavingVoWifiDiscovery = true;
        NotifyAll();
        try
        {
            await _kernelService.UpdateTelephonyPreferencesAsync(slot.Id, preferences =>
            {
                if (slot.Sim?.Iccid != iccid) throw new InvalidOperationException("SIM 已改变，未保存旧卡配置。");
                return preferences with { VoWifiDiscovery = configuration };
            });
            if (!ReferenceEquals(SelectedSlot, slot) || slot.Sim?.Iccid != iccid) return;
            VoWifiPreferredEpdgs = configuration?.PreferredAddresses ?? string.Empty;
            VoWifiAdditionalPlmns = configuration?.AdditionalPlmns ?? string.Empty;
            VoWifiManualDiscoveryOnly = configuration?.ManualOnly == true;
            VoWifiDiscoverySettingsMessage = (reset ? "已恢复此卡的自动发现。" : "已保存此 SIM 的发现配置。") +
                "下次连接 VoWiFi 时生效，当前连接不会中断。";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(SelectedSlot, slot) && slot.Sim?.Iccid == iccid)
                VoWifiDiscoverySettingsMessage = $"发现配置未保存：{ex.Message}";
        }
        finally { IsSavingVoWifiDiscovery = false; NotifyAll(); }
    }
}
