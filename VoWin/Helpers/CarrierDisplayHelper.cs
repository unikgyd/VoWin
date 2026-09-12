using VoSharp.Sim;

namespace VoWin.Helpers;

/// <summary>
/// Chooses user-facing labels from live SIM metadata. No bundled carrier profile
/// or ICCID mapping participates in display or network identity selection.
/// </summary>
public static class CarrierDisplayHelper
{
    public static string GetOperatorDisplay(SimIdentity? sim)
    {
        if (!string.IsNullOrWhiteSpace(sim?.OperatorName))
            return sim.OperatorName;

        return !string.IsNullOrWhiteSpace(sim?.Mcc)
            ? $"PLMN: {sim.Mcc}-{sim.Mnc}"
            : "未知运营商";
    }

    public static string GetCountryDisplay(SimIdentity? sim)
    {
        var imsiCountry = MccCountryHelper.FindByMcc(sim?.Mcc);
        if (imsiCountry != null)
            return $"{imsiCountry.Flag} {imsiCountry.Name} ({imsiCountry.Code})";

        return !string.IsNullOrWhiteSpace(sim?.Mcc) ? $"MCC: {sim.Mcc}" : "--";
    }

    /// <summary>Retail label reported by the live SIM, without a bundled lookup table.</summary>
    public static string GetCardProfileDisplay(SimIdentity? sim)
    {
        if (sim == null) return "未读取 SIM";
        var country = MccCountryHelper.FindByMcc(sim.Mcc);
        var label = GetOperatorDisplay(sim);
        return country == null ? label : $"{label} · {country.Flag} {country.Name}";
    }

    /// <summary>Actual IMSI home PLMN used for network authentication.</summary>
    public static string GetImsiHomeDisplay(SimIdentity? sim)
    {
        var country = MccCountryHelper.FindByMcc(sim?.Mcc);
        var plmn = !string.IsNullOrWhiteSpace(sim?.Mcc) ? $"{sim.Mcc}-{sim.Mnc}" : null;

        if (country != null)
            return string.IsNullOrWhiteSpace(plmn)
                ? $"{country.Flag} {country.Name}"
                : $"{country.Flag} {country.Name} · PLMN {plmn}";

        return plmn ?? "--";
    }
}
