using System.Net;
using System.Text.RegularExpressions;

namespace VoSharp.Telephony.VoWifi;

public record EpdgResolutionResult(
    string Fqdn,
    string ImsDomain,
    string Impi,
    string Impu,
    IPAddress[] IpAddresses,
    string? MatchedCarrier = null,
    IkeProposalSuite PreferredSuite = IkeProposalSuite.Standard,
    string? Apn = null,
    string? SmsCenter = null
);

public static class EpdgResolver
{
    /// <summary>
    /// Constructs 3GPP standard ePDG FQDN and IMS identities from IMSI.
    /// Standard 3GPP TS 23.003 format: epdg.epc.mnc<MNC3>.mcc<MCC3>.pub.3gppnetwork.org
    /// </summary>
    public static string BuildEpdgDomain(string mcc, string mnc)
    {
        var mncPadded = mnc.Length == 2 ? $"0{mnc}" : mnc;
        var mccPadded = mcc.Length == 2 ? $"0{mcc}" : mcc;
        return $"epdg.epc.mnc{mncPadded}.mcc{mccPadded}.pub.3gppnetwork.org";
    }

    public static string BuildImsDomain(string mcc, string mnc)
    {
        var mncPadded = mnc.Length == 2 ? $"0{mnc}" : mnc;
        var mccPadded = mcc.Length == 2 ? $"0{mcc}" : mcc;
        return $"ims.mnc{mncPadded}.mcc{mccPadded}.3gppnetwork.org";
    }

    public static async Task<EpdgResolutionResult> ResolveAsync(
        string imsi,
        string? customEpdg = null,
        string? iccid = null,
        string? opName = null,
        CancellationToken ct = default)
    {
        var cleanImsi = Regex.Replace(imsi, @"\D", "");
        if (cleanImsi.Length < 5)
            throw new ArgumentException("Invalid IMSI length for VoWiFi ePDG resolution", nameof(imsi));

        var mcc = cleanImsi[..3];
        // Standard 2-digit vs 3-digit MNC partition (North American Plan uses 3-digit MNC)
        var mnc = cleanImsi.Length >= 6 && (mcc is "302" or "310" or "311" or "312" or "313" or "314" or "315" or "316" or "334") 
            ? cleanImsi.Substring(3, 3) 
            : cleanImsi.Substring(3, 2);

        // 1. Query global carrier profile database for optimal carrier matching
        var matchedProfile = CarrierProfileDatabase.FindProfile(cleanImsi, iccid, opName);

        string fqdn;
        IkeProposalSuite preferredSuite;
        string? apn;
        string? smsCenter;
        string? carrierName = matchedProfile?.Name;

        if (!string.IsNullOrWhiteSpace(customEpdg))
        {
            fqdn = customEpdg.Trim();
            preferredSuite = matchedProfile?.PreferredSuite ?? IkeProposalSuite.Standard;
            apn = matchedProfile?.Apn ?? "ims";
            smsCenter = matchedProfile?.SmsCenter;
        }
        else if (matchedProfile != null)
        {
            fqdn = matchedProfile.EpdgHostname;
            preferredSuite = matchedProfile.PreferredSuite;
            apn = matchedProfile.Apn;
            smsCenter = matchedProfile.SmsCenter;
        }
        else
        {
            fqdn = BuildEpdgDomain(mcc, mnc);
            preferredSuite = IkeProposalSuite.Standard;
            apn = "ims";
            smsCenter = null;
        }

        var imsDomain = BuildImsDomain(mcc, mnc);
        var impi = $"{cleanImsi}@{imsDomain}";
        var impu = $"sip:{cleanImsi}@{imsDomain}";

        IPAddress[] addrs = Array.Empty<IPAddress>();
        try
        {
            addrs = await Dns.GetHostAddressesAsync(fqdn, ct).ConfigureAwait(false);
        }
        catch
        {
        }

        // If DNS failed or returned dummy loopback (e.g. DNS pollution/NXDOMAIN), fallback to verified carrier ePDG gateway IPs
        if (addrs.Length == 0 || addrs.All(IPAddress.IsLoopback))
        {
            if (matchedProfile?.KnownEpdgIps != null && matchedProfile.KnownEpdgIps.Length > 0)
            {
                addrs = matchedProfile.KnownEpdgIps.Select(IPAddress.Parse).ToArray();
            }
        }

        return new EpdgResolutionResult(
            Fqdn: fqdn,
            ImsDomain: imsDomain,
            Impi: impi,
            Impu: impu,
            IpAddresses: addrs,
            MatchedCarrier: carrierName,
            PreferredSuite: preferredSuite,
            Apn: apn,
            SmsCenter: smsCenter
        );
    }
}
