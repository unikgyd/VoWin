namespace VoSharp.Sim;

public record SimIdentity(
    string Imsi,
    string Iccid,
    string Mcc,
    string Mnc,
    string OperatorName,
    string? PhoneNumber = null,
    bool IsHomePlmnAuthoritative = false,
    IReadOnlyList<string>? HomePlmns = null
)
{
    public static SimIdentity FromImsiAndIccid(
        string imsi,
        string iccid,
        string? opName = null,
        string? phoneNumber = null,
        int? mncLength = null,
        IReadOnlyList<string>? homePlmns = null)
    {
        imsi = imsi.Trim();
        iccid = iccid.Trim();
        var mcc = imsi.Length >= 3 ? imsi[..3] : "";
        var validatedMncLength = mncLength is 2 or 3 ? mncLength.Value : 2;
        var mnc = imsi.Length >= 3 + validatedMncLength
            ? imsi.Substring(3, validatedMncLength)
            : "";
        var operatorName = string.IsNullOrWhiteSpace(opName)
            ? $"PLMN {mcc}-{mnc}"
            : opName.Trim();
        return new SimIdentity(
            imsi,
            iccid,
            mcc,
            mnc,
            operatorName,
            phoneNumber,
            IsHomePlmnAuthoritative: mncLength is 2 or 3,
            HomePlmns: homePlmns?.Distinct(StringComparer.Ordinal).ToArray());
    }
}
