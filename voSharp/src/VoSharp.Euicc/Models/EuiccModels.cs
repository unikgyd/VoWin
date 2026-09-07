namespace VoSharp.Euicc.Models;

public enum ProfileState
{
    Disabled = 0,
    Enabled = 1
}

public enum ProfileClass
{
    Test = 0,
    Provisioning = 1,
    Operational = 2
}

public class Profile
{
    public string ICCID { get; set; } = string.Empty;
    public string ISDPAID { get; set; } = string.Empty;
    public ProfileState State { get; set; } = ProfileState.Disabled;
    public string? Nickname { get; set; }
    public string? ServiceProviderName { get; set; }
    public string? ProviderName => ServiceProviderName ?? ProfileName;
    public string? ProfileName { get; set; }
    public ProfileClass ProfileClass { get; set; } = ProfileClass.Operational;
    public int IconType { get; set; }

    public override string ToString()
    {
        var name = !string.IsNullOrWhiteSpace(Nickname) ? Nickname : (!string.IsNullOrWhiteSpace(ProfileName) ? ProfileName : "Unnamed");
        var sp = !string.IsNullOrWhiteSpace(ServiceProviderName) ? $" ({ServiceProviderName})" : "";
        return $"[{State}] ICCID={ICCID} | {name}{sp}";
    }
}

public class EuiccInfo
{
    public string EID { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? FirmwareVer { get; set; }
    public int FreeNvramBytes { get; set; }
    public string? DefaultSmdp { get; set; }
    public string? RootDs { get; set; }
}
