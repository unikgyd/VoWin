using VoSharp.Kernel.SipGateway;
using VoWin.Services;

namespace VoWin.ViewModels.Pages;

public sealed partial class SipExtensionItem : SipPolicyEditor
{
    private bool _restoringIdentity;
    internal string CredentialEpoch { get; private set; } = Guid.NewGuid().ToString("N");
    partial void OnUsernameChanged(string value) { if (!_restoringIdentity) ResetRecipientIdentity(); }
    partial void OnPasswordChanged(string value) { if (!_restoringIdentity) ResetRecipientIdentity(); }
    internal void ResetRecipientIdentity() => CredentialEpoch = Guid.NewGuid().ToString("N");
    internal void RestoreRecipientIdentity(string? epoch, string? password = null)
    {
        _restoringIdentity = true;
        try
        {
            if (password != null) Password = password;
            CredentialEpoch = SipSmsRecipientAuthority.IsValidEpoch(epoch) ? epoch! : Guid.NewGuid().ToString("N");
        }
        finally { _restoringIdentity = false; }
    }
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    internal SipExtensionSettings Snapshot() => new(Username.Trim(), SnapshotPolicy());
    public override string ToString() => "SIP extension (credentials omitted)";
}

public sealed partial class SipDialRuleItem : ObservableObject
{
    [ObservableProperty] private string _prefix = "";
    [ObservableProperty] private int _totalLength;
    [ObservableProperty] private bool _stripPrefix;
    [ObservableProperty] private string _prepend = "";
    [ObservableProperty] private string? _lineId;
    internal SipDialRule Snapshot() => new() { Prefix = Prefix.Trim(), TotalLength = TotalLength,
        StripPrefix = StripPrefix, Prepend = Prepend.Trim(), LineId = LineId ?? "" };
}
