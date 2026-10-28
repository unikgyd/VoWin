using VoSharp.Kernel.SipGateway;

namespace VoWin.ViewModels.Pages;

/// <summary>Null lists retain legacy scope; an explicit empty list grants no line.</summary>
public partial class SipPolicyEditor : ObservableObject
{
    public SipPolicyEditor(SipExtensionPolicy? policy = null) { if (policy != null) ApplyPolicy(policy); }
    private SipExtensionPolicy _source = new() { AllowCalls = false };
    [ObservableProperty] private bool _allowCalls;
    [ObservableProperty] private bool _allowSms;
    [ObservableProperty] private bool _allowInternalCalls = true;
    [ObservableProperty] private bool _allowMessages = true;
    [ObservableProperty] private string? _defaultLineId;
    [ObservableProperty] private string _outgoingLines = "*";
    [ObservableProperty] private string _smsSendLines = "*";

    internal void ApplyPolicy(SipExtensionPolicy policy)
    {
        _source = policy;
        AllowCalls = policy.AllowCalls; AllowSms = policy.AllowSms;
        AllowInternalCalls = policy.AllowInternalCalls ?? true; AllowMessages = policy.AllowMessages ?? true;
        DefaultLineId = policy.DefaultLineId;
        OutgoingLines = FormatList(policy.OutgoingLines); SmsSendLines = FormatList(policy.SmsSendLines);
    }

    internal SipExtensionPolicy SnapshotPolicy() => new()
    {
        AllowCalls = AllowCalls, AllowSms = AllowSms, DefaultLineId = DefaultLineId,
        AllowInternalCalls = _source.AllowInternalCalls is null && AllowInternalCalls ? null : AllowInternalCalls,
        AllowMessages = _source.AllowMessages is null && AllowMessages ? null : AllowMessages,
        OutgoingLines = ParseList(OutgoingLines), SmsSendLines = ParseList(SmsSendLines)
    };

    internal static string FormatList(IReadOnlyList<string>? values) => values is null ? "*" : string.Join(", ", values);
    // Validation of membership, duplicates, limits and token syntax belongs to
    // SipGatewayOptions. Never discard unknown entries or broaden to all lines.
    internal static List<string>? ParseList(string value) => value.Trim() == "*" ? null :
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
