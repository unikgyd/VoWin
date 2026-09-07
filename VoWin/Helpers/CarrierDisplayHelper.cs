using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoWin.Helpers;

/// <summary>
/// Chooses user-facing carrier labels without changing the IMSI identity that is
/// supplied to the network.  Some travel/hosted SIMs carry an IMSI allocated by
/// a different operator or country than their retail carrier.
/// </summary>
public static class CarrierDisplayHelper
{
    public static string GetOperatorDisplay(SimIdentity? sim)
    {
        var profile = FindProfile(sim);
        if (profile != null)
            return profile.Name;

        if (!string.IsNullOrWhiteSpace(sim?.OperatorName))
            return sim.OperatorName;

        return !string.IsNullOrWhiteSpace(sim?.Mcc)
            ? $"PLMN: {sim.Mcc}-{sim.Mnc}"
            : "未知运营商";
    }

    public static string GetCountryDisplay(SimIdentity? sim)
    {
        var imsiCountry = MccCountryHelper.FindByMcc(sim?.Mcc);
        var carrierCountry = FindCarrierCountry(FindProfile(sim));

        if (carrierCountry != null)
        {
            var carrierDisplay = $"{carrierCountry.Flag} {carrierCountry.Name} ({carrierCountry.Code})";
            if (imsiCountry != null && !string.Equals(carrierCountry.Code, imsiCountry.Code, StringComparison.OrdinalIgnoreCase))
            {
                return $"{carrierDisplay} · IMSI 归属 {imsiCountry.Flag} {imsiCountry.Name} ({imsiCountry.Code})";
            }

            return carrierDisplay;
        }

        if (imsiCountry != null)
            return $"{imsiCountry.Flag} {imsiCountry.Name} ({imsiCountry.Code})";

        return !string.IsNullOrWhiteSpace(sim?.Mcc) ? $"MCC: {sim.Mcc}" : "--";
    }

    /// <summary>Retail/card-profile identity inferred from ICCID and SPN data.</summary>
    public static string GetCardProfileDisplay(SimIdentity? sim)
    {
        var profile = FindProfile(sim);
        var country = FindCarrierCountry(profile);
        if (profile != null && country != null)
            return $"{profile.Name} · {country.Flag} {country.Name}";

        return profile?.Name ?? "未识别卡配置";
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

    private static CountryMeta? FindCarrierCountry(CarrierProfile? profile) =>
        profile?.HomePlmns
            .Select(plmn => plmn.Length >= 3 ? plmn[..3] : plmn)
            .Select(MccCountryHelper.FindByMcc)
            .FirstOrDefault(country => country != null);

    private static CarrierProfile? FindProfile(SimIdentity? sim) =>
        sim == null || string.IsNullOrWhiteSpace(sim.Imsi)
            ? null
            : CarrierProfileDatabase.FindProfile(sim.Imsi, sim.Iccid, sim.OperatorName);
}
