namespace VoSharp.Sim;

public record SimIdentity(
    string Imsi,
    string Iccid,
    string Mcc,
    string Mnc,
    string OperatorName,
    string? PhoneNumber = null
)
{
    public static SimIdentity FromImsiAndIccid(
        string imsi,
        string iccid,
        string? opName = null,
        string? phoneNumber = null)
    {
        imsi = imsi.Trim();
        iccid = iccid.Trim();
        var mcc = imsi.Length >= 3 ? imsi[..3] : "";
        var mnc = imsi.Length >= 5 ? imsi.Substring(3, Math.Min(2, imsi.Length - 3)) : "";
        return new SimIdentity(imsi, iccid, mcc, mnc, opName ?? GuessOperator(mcc, mnc), phoneNumber);
    }

    private static string GuessOperator(string mcc, string mnc)
    {
        if (mcc == "460")
        {
            return mnc switch
            {
                "00" or "02" or "07" or "08" => "China Mobile",
                "01" or "06" or "09" => "China Unicom",
                "03" or "05" or "11" => "China Telecom",
                "15" => "China Broadnet",
                _ => "China Operator"
            };
        }
        return $"{mcc}{mnc}";
    }
}
