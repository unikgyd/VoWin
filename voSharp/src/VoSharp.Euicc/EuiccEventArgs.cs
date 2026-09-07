using VoSharp.Euicc.Models;

namespace VoSharp.Euicc;

public class EuiccOperationEventArgs : EventArgs
{
    public string Action { get; }
    public string? TargetIccidOrAid { get; }
    public string? Nickname { get; }
    public bool Success { get; }
    public string? ErrorMessage { get; }

    public EuiccOperationEventArgs(
        string action,
        string? targetIccidOrAid = null,
        string? nickname = null,
        bool success = true,
        string? errorMessage = null)
    {
        Action = action;
        TargetIccidOrAid = targetIccidOrAid;
        Nickname = nickname;
        Success = success;
        ErrorMessage = errorMessage;
    }
}

public class EuiccProfilesChangedEventArgs : EventArgs
{
    public IReadOnlyList<Profile> Profiles { get; }

    public EuiccProfilesChangedEventArgs(IReadOnlyList<Profile> profiles)
    {
        Profiles = profiles;
    }
}
