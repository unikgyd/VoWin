using System.ComponentModel;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Kernel.SipGateway;
using VoSharp.Telephony.VoWifi;

namespace VoWin.ViewModels.Pages;

public sealed partial class SipModuleRouteItem : ObservableObject
{
    private ModemSlot? _slot;
    public string SlotId { get; }
    public string Title => _slot?.DisplayTitle ?? $"离线线路 {SlotId}";
    public string LineSummary => $"线路 {SlotId}" + (_slot is null ? "" : $" · {_slot.PortName}");
    public string ConnectionSummary => _slot is null ? "模组未连接；保留规则，不使用其他模组替代。" :
        $"出站接入：{SelectedAccessName} · Host IMS {(_slot.GetHostImsRegistrationStatus().IsRegistered ? "已注册" : "未注册")} · VoWiFi {(_slot.VoWifi.State == VoWifiState.ImsRegistered ? "已注册" : "未注册")}";
    private string SelectedAccessName => (_slot?.PreferredTelephonyMode is null ? null :
        _slot.PreferredTelephonyMode == TelephonyMode.VoWifi ? TelephonyMode.VoWifi : _slot.CellularImsMode ?? _slot.PreferredTelephonyMode) switch
        { TelephonyMode.VoWifi => "VoWiFi", TelephonyMode.HostIms => "Host IMS", TelephonyMode.ModemBridge => "模组桥接", _ => "该模组的默认接入" };
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _allowIncomingCalls = true;
    [ObservableProperty] private bool _allowOutgoingCalls = true;
    [ObservableProperty] private string _incomingTarget = string.Empty;
    [ObservableProperty] private bool _forwardIncomingSms;
    [ObservableProperty] private bool _allowOutgoingSms;
    [ObservableProperty] private string _smsTarget = string.Empty;
    [ObservableProperty] private string _localIncomingTarget = string.Empty;
    [ObservableProperty] private string _localSmsTarget = string.Empty;
    [ObservableProperty] private string _localIncomingExtensions = "*";
    [ObservableProperty] private string _localSmsRecipients = "*";
    [ObservableProperty] private bool? _forwardCallsToPeer;
    [ObservableProperty] private bool? _forwardSmsToPeer;
    [ObservableProperty] private string _prefix = string.Empty;
    [ObservableProperty] private string _countryCode = string.Empty;
    [ObservableProperty] private int _nationalNumberLength;
    [ObservableProperty] private bool _normalizeE164;

    public SipModuleRouteItem(string slotId, SipGatewayLineRoute? route = null)
    {
        SlotId = slotId;
        if (route is not null)
        {
            Enabled = route.Enabled; AllowIncomingCalls = route.AllowIncomingCalls;
            AllowOutgoingCalls = route.AllowOutgoingCalls; IncomingTarget = route.IncomingTarget ?? "";
            ForwardIncomingSms = route.ForwardIncomingSms; AllowOutgoingSms = route.AllowOutgoingSms; SmsTarget = route.SmsTarget ?? "";
            LocalIncomingTarget = route.LocalIncomingTarget ?? ""; LocalSmsTarget = route.LocalSmsTarget ?? "";
            LocalIncomingExtensions = SipPolicyEditor.FormatList(route.LocalIncomingExtensions);
            LocalSmsRecipients = SipPolicyEditor.FormatList(route.LocalSmsRecipients);
            ForwardCallsToPeer = route.ForwardCallsToPeer; ForwardSmsToPeer = route.ForwardSmsToPeer;
            Prefix = route.Prefix ?? ""; CountryCode = route.CountryCode ?? "";
            NationalNumberLength = route.NationalNumberLength; NormalizeE164 = route.NormalizeE164;
        }
    }

    internal void BindSlot(ModemSlot? slot)
    {
        if (!ReferenceEquals(slot, _slot))
        {
            if (_slot is not null) { _slot.PropertyChanged -= OnSlotChanged; _slot.VoWifi.StateChanged -= OnWifiChanged; }
            _slot = slot;
            if (_slot is not null) { _slot.PropertyChanged += OnSlotChanged; _slot.VoWifi.StateChanged += OnWifiChanged; }
        }
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(LineSummary)); OnPropertyChanged(nameof(ConnectionSummary));
    }
    private void OnSlotChanged(object? sender, PropertyChangedEventArgs e) => BindSlot(_slot);
    private void OnWifiChanged(object? sender, VoWifiStateChangedEventArgs e) => OnPropertyChanged(nameof(ConnectionSummary));
    internal SipGatewayLineRoute Snapshot() => new()
    {
        Enabled = Enabled, AllowIncomingCalls = AllowIncomingCalls, AllowOutgoingCalls = AllowOutgoingCalls,
        IncomingTarget = string.IsNullOrWhiteSpace(IncomingTarget) ? null : IncomingTarget.Trim(),
        ForwardIncomingSms = ForwardIncomingSms, AllowOutgoingSms = AllowOutgoingSms,
        SmsTarget = string.IsNullOrWhiteSpace(SmsTarget) ? null : SmsTarget.Trim(),
        LocalIncomingTarget = string.IsNullOrWhiteSpace(LocalIncomingTarget) ? null : LocalIncomingTarget.Trim(),
        LocalSmsTarget = string.IsNullOrWhiteSpace(LocalSmsTarget) ? null : LocalSmsTarget.Trim(),
        LocalIncomingExtensions = SipPolicyEditor.ParseList(LocalIncomingExtensions),
        LocalSmsRecipients = SipPolicyEditor.ParseList(LocalSmsRecipients),
        ForwardCallsToPeer = ForwardCallsToPeer, ForwardSmsToPeer = ForwardSmsToPeer,
        Prefix = string.IsNullOrWhiteSpace(Prefix) ? null : Prefix.Trim(),
        CountryCode = string.IsNullOrWhiteSpace(CountryCode) ? null : CountryCode.Trim(),
        NationalNumberLength = NationalNumberLength, NormalizeE164 = NormalizeE164
    };
}

public sealed record SipOutboundLineChoice(string? SlotId, string Label)
{
    public string AccountLabel => SlotId == null ? "沿用号码规则 / 全局默认线路" : Label;
    public string RuleLabel => SlotId == null ? "请选择固定模组" : Label;
}
