namespace VoSharp.Telephony.VoWifi;

public class VoWifiStateChangedEventArgs : EventArgs
{
    public VoWifiState OldState { get; }
    public VoWifiState NewState { get; }
    public string? EpdgFqdn { get; }
    public string? AssignedIp { get; }
    public string? PcscfIp { get; }
    public string? LastError { get; }
    public string? SlotId { get; }

    public VoWifiStateChangedEventArgs(
        VoWifiState oldState,
        VoWifiState newState,
        string? epdgFqdn = null,
        string? assignedIp = null,
        string? pcscfIp = null,
        string? lastError = null,
        string? slotId = null)
    {
        OldState = oldState;
        NewState = newState;
        EpdgFqdn = epdgFqdn;
        AssignedIp = assignedIp;
        PcscfIp = pcscfIp;
        LastError = lastError;
        SlotId = slotId;
    }
}

public class ImsSessionChangedEventArgs : EventArgs
{
    public VoWifiImsSessionInfo? ImsInfo { get; }
    public string RegistrationState { get; }
    public string? SlotId { get; }

    public ImsSessionChangedEventArgs(VoWifiImsSessionInfo? imsInfo, string registrationState, string? slotId = null)
    {
        ImsInfo = imsInfo;
        RegistrationState = registrationState;
        SlotId = slotId;
    }
}
