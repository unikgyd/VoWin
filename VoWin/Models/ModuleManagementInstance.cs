using System.ComponentModel;
using VoSharp.Common.Events;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using VoWin.Services;

namespace VoWin.Models;

/// <summary>One hardware slot, one isolated control context. Selecting another tile never transfers ownership.</summary>
public sealed class ModuleManagementInstance : INotifyPropertyChanged, IDisposable
{
    public ModemSlot Slot { get; }
    public string Id => Slot.Id;
    public string Title => Slot.CardNickname ?? Slot.DeviceTitle;
    public string Port => Slot.PortName;
    public string LineSummary => $"线路 {Id} · {Port}";
    public string SimSummary => Slot.Sim is not { } sim ? "未识别 SIM" :
        (sim.Mcc is "460" or "461" ? "中国 · " : sim.Mcc == "515" ? "菲律宾 · " : "") + sim.OperatorName;
    public string IdentitySummary => Slot.IsPcscReader ? "PC/SC 鉴权源" :
        Slot.UsbContainerId is not null ? "USB 物理绑定已确认" : "USB 物理绑定待确认";
    internal SemaphoreSlim ControlGate { get; } = new(1, 1);
    internal SemaphoreSlim WifiGate { get; } = new(1, 1);
    internal CancellationTokenSource? CellularCancellation;
    internal CancellationTokenSource? WifiCancellation;
    internal TelephonyModeStatus? CellularStatus;
    internal bool WifiBusy;
    internal string? WifiError;
    internal HostImsRecoveryController? HostRecovery;
    public HostImsRecoveryStatus? HostRecoveryStatus => Volatile.Read(ref HostRecovery)?.Status;
    public string CellularSummary
    {
        get
        {
            var operation = CellularStatus;
            var recovery = HostRecoveryStatus;
            return operation is { IsBusy: true } ? operation.Message
                : recovery is { IsWaiting: true } or { IsPaused: true } ? recovery.Message
                : operation?.Message ?? (!Slot.TelephonyEnabled ? "蜂窝 · 已停止"
                    : Slot.CellularImsMode == TelephonyMode.HostIms
                        ? Slot.GetHostImsRegistrationStatus().IsRegistered ? "Host IMS · 已注册" : "Host IMS · 未连接"
                        : Slot.Modem?.IsOpen == true ? "模组语音桥接 · 已启用（IMS 未核验）" : "模块 · 未连接");
        }
    }
    public string WifiSummary => Slot.VoWifi.State == VoWifiState.ImsRegistered && !Slot.VoWifi.AutomaticRecoveryPaused ? "VoWiFi · 已注册"
        : Slot.VoWifi.KeepOnlineRequested && Slot.VoWifi.GetIkeRetryStatusFor(Slot.Sim?.Iccid) is { } retry
            ? WifiIkeRetryPresentation.Create(retry, recoveryPaused: Slot.VoWifi.AutomaticRecoveryPaused).InlineText
        : WifiBusy ? "VoWiFi · 连接中"
        : Slot.VoWifi.State is >= VoWifiState.ResolvingEpdg and <= VoWifiState.ImsRegistering
            ? Slot.VoWifi.KeepOnlineRequested ? "VoWiFi · 自动连接中" : "VoWiFi · 连接中"
        : Slot.VoWifi.AutomaticRecoveryPaused ? "VoWiFi · 自动连接已暂停"
        : Slot.VoWifi.KeepOnlineRequested ? "VoWiFi · 等待自动重连"
        : WifiError != null || Slot.VoWifi.State == VoWifiState.Failed ? "VoWiFi · 连接失败" : "VoWiFi · 未连接";
    public string CallSummary => Slot.Calls.ActiveCall?.State is { } state ? $"通话 · {state}"
        : Slot.HasCellularCall ? "模块通话中" : "空闲";
    public event PropertyChangedEventHandler? PropertyChanged;
    public ModuleManagementInstance(ModemSlot slot)
    {
        Slot = slot;
        Slot.PropertyChanged += OnSlotChanged;
        Slot.VoWifi.StateChanged += OnWifiChanged;
        Slot.Calls.CallStateChanged += OnCallChanged;
        Slot.HostImsManualStopRequested += OnHostImsManualStop;
    }
    private void OnSlotChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void OnWifiChanged(object? sender, VoWifiStateChangedEventArgs e)
    {
        if (e.NewState == VoWifiState.ImsRegistered) WifiError = null;
        Refresh();
    }
    private void OnCallChanged(object? sender, CallStateChangedEventArgs e) => Refresh();
    private void OnHostImsManualStop() => HostRecovery?.Cancel();
    internal void Refresh() => PropertyChanged?.Invoke(this, new(null));
    public void Dispose()
    {
        try { CellularCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        try { WifiCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        HostRecovery?.Cancel();
        Slot.PropertyChanged -= OnSlotChanged;
        Slot.VoWifi.StateChanged -= OnWifiChanged;
        Slot.Calls.CallStateChanged -= OnCallChanged;
        Slot.HostImsManualStopRequested -= OnHostImsManualStop;
        // Operations already holding the gates unwind before their context can be collected.
    }
}
