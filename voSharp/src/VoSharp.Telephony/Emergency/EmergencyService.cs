namespace VoSharp.Telephony.Emergency;

public static class EmergencyService
{
    private static readonly HashSet<string> StandardEmergencyNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        "112", // Global standard / GSM
        "911", // US / Canada
        "110", // China Police / Japan Police
        "119", // China Fire / Japan Fire
        "120", // China Medical
        "122", // China Traffic
        "999", // UK
        "000", // Australia
        "08"   // Special
    };

    public static bool IsEmergencyNumber(string number)
    {
        number = number.Trim().TrimStart('+');
        return StandardEmergencyNumbers.Contains(number);
    }
}
