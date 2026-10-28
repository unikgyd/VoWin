using System.Collections.ObjectModel;
using VoWin.Services;
using VoSharp.Kernel.SipGateway;

namespace VoWin.ViewModels.Pages;

public partial class SettingsViewModel
{
    private bool _restoringSipRecipientIdentity;
    private string _sipMainRecipientEpoch = Guid.NewGuid().ToString("N");
    private string _sipPeerRecipientEpoch = Guid.NewGuid().ToString("N");
    partial void OnSipGatewayPasswordChanged(string value) { if (!_restoringSipRecipientIdentity) _sipMainRecipientEpoch = Guid.NewGuid().ToString("N"); }
    partial void OnSipGatewayExtensionChanged(string value) { if (!_restoringSipRecipientIdentity) _sipMainRecipientEpoch = Guid.NewGuid().ToString("N"); }
    partial void OnSipPeerInboundPasswordChanged(string value) => RotatePeerRecipientIdentity();
    partial void OnSipPeerOutboundPasswordChanged(string value) => RotatePeerRecipientIdentity();
    partial void OnSipPeerInboundAlgorithmChanged(string value) => RotatePeerRecipientIdentity();
    partial void OnSipPeerAllowMd5Changed(bool value) => RotatePeerRecipientIdentity();
    partial void OnSipPeerAllowSha256Changed(bool value) => RotatePeerRecipientIdentity();
    partial void OnSipGatewayIncomingTargetChanged(string value) => RotatePeerRecipientIdentity();
    private void RotatePeerRecipientIdentity() { if (!_restoringSipRecipientIdentity) _sipPeerRecipientEpoch = Guid.NewGuid().ToString("N"); }
    [RelayCommand] private void ResetSipMainRecipientIdentity()
    {
        if (!CanEditSipGateway) return;
        _sipMainRecipientEpoch = Guid.NewGuid().ToString("N"); RecipientResetNotice();
    }
    [RelayCommand] private void ResetSipPeerRecipientIdentity()
    {
        if (!CanEditSipGateway) return;
        _sipPeerRecipientEpoch = Guid.NewGuid().ToString("N"); RecipientResetNotice();
    }
    [RelayCommand] private void ResetSipExtensionRecipientIdentity(SipExtensionItem? row)
    {
        if (!CanEditSipGateway || row == null || !SipExtensions.Contains(row)) return;
        row.ResetRecipientIdentity(); RecipientResetNotice();
    }
    private void RecipientResetNotice() => SipGatewayStatus = "接收身份已重置；保存后旧身份的待投短信不再转发，密码保持不变。";
    private void ResetAllSipRecipientIdentities()
    {
        _sipMainRecipientEpoch = Guid.NewGuid().ToString("N"); _sipPeerRecipientEpoch = Guid.NewGuid().ToString("N");
        foreach (var row in SipExtensions) row.ResetRecipientIdentity();
    }
    private Dictionary<string, string> SnapshotSipRecipientEpochs()
    {
        var epochs = SipExtensions.ToDictionary(row => row.Username.Trim(), row => row.CredentialEpoch, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(SipGatewayExtension)) epochs.Add(SipGatewayExtension.Trim(), _sipMainRecipientEpoch);
        return epochs;
    }
    private void ApplySipRecipientEpochs(SipGatewayPreferences settings)
    {
        string? Epoch(string username) => settings.LocalAccountEpochs?.FirstOrDefault(pair => pair.Key.Equals(username, StringComparison.OrdinalIgnoreCase)).Value;
        _sipMainRecipientEpoch = Epoch(SipGatewayExtension.Trim()) is { } epoch && SipSmsRecipientAuthority.IsValidEpoch(epoch) ? epoch : Guid.NewGuid().ToString("N");
        _sipPeerRecipientEpoch = SipSmsRecipientAuthority.IsValidEpoch(settings.PeerCredentialEpoch) ? settings.PeerCredentialEpoch! : Guid.NewGuid().ToString("N");
        foreach (var row in SipExtensions) row.RestoreRecipientIdentity(Epoch(row.Username.Trim()));
    }
    private async Task<SipGatewayPreferences> PersistSipConnectionAsync(bool enabled)
    {
        var settings = await _sipSettings.PrepareConnectionAsync(SnapshotSipSettings(), SnapshotSipCredentials(), enabled);
        ApplySipRecipientEpochs(settings);
        return settings;
    }
    [ObservableProperty] private bool _sipLocalRegistrarEnabled = true;
    [ObservableProperty] private string _sipIdentityDomain = "";
    [ObservableProperty] private string _sipTrustedNetworks = "*";
    [ObservableProperty] private string _sipLocalRegistrarAlgorithm = "MD5";
    private string? _sipSourceLocalRegistrarAlgorithm;
    private string? SnapshotLocalRegistrarAlgorithm() =>
        _sipSourceLocalRegistrarAlgorithm is null && SipLocalRegistrarAlgorithm == "MD5" ? null : SipLocalRegistrarAlgorithm;
    [ObservableProperty] private string _sipPeerInboundPassword = "";
    [ObservableProperty] private string _sipPeerOutboundPassword = "";
    [ObservableProperty] private string _sipPeerOutboundUsername = "";
    [ObservableProperty] private string _sipPeerExpectedRealm = "";
    [ObservableProperty] private string _sipPeerInboundAlgorithm = "SHA-256";
    [ObservableProperty] private bool _sipPeerAllowMd5;
    [ObservableProperty] private bool _sipPeerAllowSha256 = true;
    private string _legacyTrunkPassword = ""; // Migration candidate only; never used for either typed role.
    public IReadOnlyList<string> SipPeerInboundAlgorithms { get; } = ["SHA-256", "MD5"];
    public Visibility SipLocalVisibility => SipLocalRegistrarEnabled ? Visibility.Visible : Visibility.Collapsed;
    partial void OnSipLocalRegistrarEnabledChanged(bool value) => OnPropertyChanged(nameof(SipLocalVisibility));
    partial void OnSipPeerExpectedRealmChanged(string value) => SipPeerOutboundPassword = "";
    partial void OnSipPeerOutboundUsernameChanged(string value) => SipPeerOutboundPassword = "";
    partial void OnSipGatewayTrunkUsernameChanged(string value) => SipPeerInboundPassword = "";
    partial void OnSipGatewayRealmChanged(string value)
    {
        SipPeerInboundPassword = "";
        if (!_restoringSipRecipientIdentity) _sipMainRecipientEpoch = Guid.NewGuid().ToString("N");
        RotatePeerRecipientIdentity();
    }
    partial void OnSipGatewayServerAddressChanged(string value) => ClearSipPeerPasswords();
    partial void OnSipGatewayServerPortChanged(int value) => ClearSipPeerPasswords();
    private void ClearSipPeerPasswords() { SipPeerInboundPassword = ""; SipPeerOutboundPassword = ""; }

    private SipGatewayCredentials SnapshotSipCredentials() => new(SipGatewayPassword, SnapshotSipPasswords(),
        SipPeerInboundPassword, SipPeerOutboundPassword, _legacyTrunkPassword);
    private void ApplySipCredentials(SipGatewayCredentials credentials)
    {
        _restoringSipRecipientIdentity = true;
        try
        {
            SipGatewayPassword = credentials.MainPassword;
            ApplySipPasswords(credentials.Extensions ?? new());
            SipPeerInboundPassword = credentials.PeerInboundPassword;
            SipPeerOutboundPassword = credentials.PeerOutboundPassword;
            _legacyTrunkPassword = credentials.LegacyTrunkPassword;
        }
        finally { _restoringSipRecipientIdentity = false; }
    }
    [ObservableProperty] private bool _sipMessagesEnabled;
    [ObservableProperty] private bool _sipForwardIncomingSms;
    [ObservableProperty] private bool _sipAllowOutgoingSms;
    private bool _sipHasPrimaryPolicy;
    // No legacy policy means the global paid-SMS switch is the only gate.
    public SipPolicyEditor SipPrimaryPolicy { get; } = new(new() { AllowSms = true });
    [ObservableProperty] private bool? _sipPeerAllowOutboundCalls;
    [ObservableProperty] private bool? _sipPeerAllowOutboundMessages;
    [ObservableProperty] private bool? _sipPeerAllowIncomingCalls;
    [ObservableProperty] private bool? _sipPeerAllowIncomingMessages;
    [ObservableProperty] private string _sipPeerOutgoingLines = "*";
    [ObservableProperty] private string _sipPeerSmsSendLines = "*";
    private SipExtensionPolicy? SnapshotPrimaryPolicy()
    {
        var policy = SipPrimaryPolicy.SnapshotPolicy();
        return !_sipHasPrimaryPolicy && policy.AllowCalls && policy.AllowSms && policy.AllowInternalCalls is null && policy.AllowMessages is null &&
            policy.DefaultLineId is null && policy.OutgoingLines is null && policy.SmsSendLines is null ? null : policy;
    }
    public ObservableCollection<SipExtensionItem> SipExtensions { get; } = new();
    public ObservableCollection<SipDialRuleItem> SipDialRules { get; } = new();
    public ObservableCollection<string> SipRegisteredDevices { get; } = new();

    [RelayCommand] private void AddSipExtension() { if (CanEditSipGateway) SipExtensions.Add(new()); }
    [RelayCommand] private void RemoveSipExtension(SipExtensionItem? row) { if (CanEditSipGateway && row != null) SipExtensions.Remove(row); }
    [RelayCommand] private void AddSipDialRule() { if (CanEditSipGateway) SipDialRules.Add(new()); }
    [RelayCommand] private void RemoveSipDialRule(SipDialRuleItem? row) { if (CanEditSipGateway && row != null) SipDialRules.Remove(row); }

    private Dictionary<string, string> SnapshotSipPasswords() => SipExtensions.ToDictionary(row => row.Username.Trim(), row => row.Password, StringComparer.OrdinalIgnoreCase);
    private void ApplySipPasswords(Dictionary<string, string> passwords)
    {
        foreach (var row in SipExtensions) row.RestoreRecipientIdentity(row.CredentialEpoch, passwords.GetValueOrDefault(row.Username) ?? "");
    }
    private void ApplySipServerSettings(SipGatewayPreferences settings)
    {
        SipIdentityDomain = settings.IdentityDomain ?? "";
        SipTrustedNetworks = SipPolicyEditor.FormatList(settings.TrustedNetworks);
        _sipSourceLocalRegistrarAlgorithm = settings.LocalRegistrarAlgorithm;
        SipLocalRegistrarAlgorithm = settings.LocalRegistrarAlgorithm ?? "MD5";
        _sipHasPrimaryPolicy = settings.PrimaryPolicy != null;
        SipPrimaryPolicy.ApplyPolicy(settings.PrimaryPolicy ?? new() { AllowSms = true });
        SipPeerAllowOutboundCalls = settings.PeerAllowOutboundCalls; SipPeerAllowOutboundMessages = settings.PeerAllowOutboundMessages;
        SipPeerAllowIncomingCalls = settings.PeerAllowIncomingCalls; SipPeerAllowIncomingMessages = settings.PeerAllowIncomingMessages;
        SipPeerOutgoingLines = SipPolicyEditor.FormatList(settings.PeerOutgoingLines);
        SipPeerSmsSendLines = SipPolicyEditor.FormatList(settings.PeerSmsSendLines);
        SipLocalRegistrarEnabled = settings.HasLocalRegistrar;
        SipPeerOutboundUsername = settings.PeerOutboundUsername ?? "";
        SipPeerExpectedRealm = settings.PeerExpectedRealm ?? "";
        SipPeerInboundAlgorithm = settings.PeerInboundAlgorithm ?? "SHA-256";
        SipPeerAllowMd5 = settings.PeerAllowedAlgorithms?.Contains("MD5", StringComparer.OrdinalIgnoreCase) == true;
        SipPeerAllowSha256 = settings.PeerAllowedAlgorithms is null || settings.PeerAllowedAlgorithms.Contains("SHA-256", StringComparer.OrdinalIgnoreCase);
        SipMessagesEnabled = settings.MessagesEnabled; SipForwardIncomingSms = settings.ForwardIncomingSms; SipAllowOutgoingSms = settings.AllowOutgoingSms;
        SipExtensions.Clear();
        foreach (var extension in settings.Extensions ?? [])
        {
            var row = new SipExtensionItem { Username = extension.Username };
            row.ApplyPolicy(extension.Policy);
            SipExtensions.Add(row);
        }
        SipDialRules.Clear();
        foreach (var rule in settings.DialRules ?? [])
            SipDialRules.Add(new() { Prefix = rule.Prefix, TotalLength = rule.TotalLength,
                StripPrefix = rule.StripPrefix, Prepend = rule.Prepend, LineId = rule.LineId });
        ApplySipRecipientEpochs(settings);
    }
}
