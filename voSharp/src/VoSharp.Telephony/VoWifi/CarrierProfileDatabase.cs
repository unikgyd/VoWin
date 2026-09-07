using System.Net;
using System.Text.RegularExpressions;

namespace VoSharp.Telephony.VoWifi;

public record CarrierProfile(
    string Id,
    string Name,
    string[] HomePlmns,
    string[]? IccidPrefixes,
    string[]? Spns,
    string EpdgHostname,
    IkeProposalSuite PreferredSuite,
    string Apn,
    string? SmsCenter,
    string[]? KnownEpdgIps = null
);

public static class CarrierProfileDatabase
{
    private static readonly List<CarrierProfile> BuiltinProfiles = new()
    {
        // 1. Philippines
        new CarrierProfile(
            Id: "dito-ph",
            Name: "DITO Philippines",
            HomePlmns: new[] { "51566", "51505" },
            IccidPrefixes: new[] { "896366" },
            Spns: new[] { "DITO", "DITO Telecommunity" },
            EpdgHostname: "epdg.epc.mnc066.mcc515.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Legacy, // DITO requires SHA1 / MODP-1024
            Apn: "ims",
            SmsCenter: "+639910000000",
            KnownEpdgIps: new[] { "131.226.72.137", "131.226.73.137" }
        ),
        new CarrierProfile(
            Id: "globe-ph",
            Name: "Globe Philippines",
            HomePlmns: new[] { "51502" },
            IccidPrefixes: new[] { "896302" },
            Spns: new[] { "Globe", "Globe Telecom" },
            EpdgHostname: "epdg.epc.mnc002.mcc515.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+639170000130"
        ),
        new CarrierProfile(
            Id: "smart-ph",
            Name: "Smart Philippines",
            HomePlmns: new[] { "51503" },
            IccidPrefixes: new[] { "896303" },
            Spns: new[] { "Smart", "Smart Communications" },
            EpdgHostname: "epdg.epc.mnc003.mcc515.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+639180000101"
        ),

        // 2. UK & Europe
        new CarrierProfile(
            Id: "ctexcel-uk",
            Name: "CTExcel UK",
            HomePlmns: new[] { "23432", "23430", "23433" },
            IccidPrefixes: new[] { "894430", "894432", "894411" },
            Spns: new[] { "CTExcel", "CTExcel UK", "China Telecom UK" },
            EpdgHostname: "epdg.epc.mnc030.mcc234.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+447802000332"
        ),
        new CarrierProfile(
            Id: "ee-uk",
            Name: "EE UK",
            HomePlmns: new[] { "23430", "23433", "23434" },
            IccidPrefixes: new[] { "894430" },
            Spns: new[] { "EE" },
            EpdgHostname: "epdg.epc.mnc030.mcc234.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+447958879879"
        ),
        new CarrierProfile(
            Id: "vodafone-uk",
            Name: "Vodafone UK",
            HomePlmns: new[] { "23415", "23407", "23477", "23489", "23491", "23492" },
            IccidPrefixes: new[] { "894415" },
            Spns: new[] { "Vodafone", "Vodafone UK" },
            EpdgHostname: "epdg.epc.mnc015.mcc234.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Legacy,
            Apn: "ims",
            SmsCenter: "+447785016005"
        ),
        new CarrierProfile(
            Id: "o2-uk",
            Name: "O2 UK",
            HomePlmns: new[] { "23410", "23402", "23411" },
            IccidPrefixes: new[] { "894410" },
            Spns: new[] { "O2", "O2 - UK", "giffgaff" },
            EpdgHostname: "epdg.epc.mnc010.mcc234.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+447802000332"
        ),
        new CarrierProfile(
            Id: "three-uk",
            Name: "Three UK",
            HomePlmns: new[] { "23420", "23494" },
            IccidPrefixes: new[] { "894420" },
            Spns: new[] { "3", "3 UK", "Three" },
            EpdgHostname: "epdg.epc.mnc020.mcc234.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+447782000008"
        ),

        // 3. China Mainland
        new CarrierProfile(
            Id: "china-mobile",
            Name: "China Mobile",
            HomePlmns: new[] { "46000", "46002", "46007", "46008" },
            IccidPrefixes: new[] { "898600", "898602", "898607" },
            Spns: new[] { "CMCC", "China Mobile", "中国移动" },
            EpdgHostname: "epdg.epc.mnc000.mcc460.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+8613800100500"
        ),
        new CarrierProfile(
            Id: "china-unicom",
            Name: "China Unicom",
            HomePlmns: new[] { "46001", "46006", "46009" },
            IccidPrefixes: new[] { "898601", "898606", "898609" },
            Spns: new[] { "CUCC", "China Unicom", "中国联通" },
            EpdgHostname: "epdg.epc.mnc001.mcc460.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+8613010112500"
        ),
        new CarrierProfile(
            Id: "china-telecom",
            Name: "China Telecom",
            HomePlmns: new[] { "46003", "46005", "46011" },
            IccidPrefixes: new[] { "898603", "898611" },
            Spns: new[] { "CTCC", "China Telecom", "中国电信" },
            EpdgHostname: "epdg.epc.mnc011.mcc460.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+8618900000000"
        ),
        new CarrierProfile(
            Id: "china-broadnet",
            Name: "China Broadnet",
            HomePlmns: new[] { "46015" },
            IccidPrefixes: new[] { "898615" },
            Spns: new[] { "CBN", "China Broadnet", "中国广电" },
            EpdgHostname: "epdg.epc.mnc015.mcc460.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+8619200000000"
        ),

        // 4. United States
        new CarrierProfile(
            Id: "tmobile-us",
            Name: "T-Mobile USA",
            HomePlmns: new[] { "310260", "310240", "310200", "310210" },
            IccidPrefixes: new[] { "8901260" },
            Spns: new[] { "T-Mobile" },
            EpdgHostname: "epdg.epc.mnc260.mcc310.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Modern,
            Apn: "ims",
            SmsCenter: "+12063130004"
        ),
        new CarrierProfile(
            Id: "att-us",
            Name: "AT&T USA",
            HomePlmns: new[] { "310280", "310410", "310150" },
            IccidPrefixes: new[] { "8901410", "8901280" },
            Spns: new[] { "AT&T" },
            EpdgHostname: "epdg.epc.att.net",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+13123149810"
        ),
        new CarrierProfile(
            Id: "verizon-us",
            Name: "Verizon Wireless",
            HomePlmns: new[] { "311480", "310012", "311270" },
            IccidPrefixes: new[] { "891480" },
            Spns: new[] { "Verizon" },
            EpdgHostname: "wo.vzwwo.com",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+19085599999"
        ),

        // 5. Hong Kong & Taiwan
        new CarrierProfile(
            Id: "cmhk-hk",
            Name: "CMHK Hong Kong",
            HomePlmns: new[] { "45412", "45413" },
            IccidPrefixes: new[] { "8985212" },
            Spns: new[] { "CMHK" },
            EpdgHostname: "epdg.epc.mnc012.mcc454.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+85292000000"
        ),
        new CarrierProfile(
            Id: "csl-hk",
            Name: "CSL Hong Kong",
            HomePlmns: new[] { "45400", "45402", "45418", "45419" },
            IccidPrefixes: new[] { "8985200" },
            Spns: new[] { "csl", "1O1O", "Club Sim" },
            EpdgHostname: "epdg.epc.mnc000.mcc454.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+85290000000"
        ),
        new CarrierProfile(
            Id: "chunghwa-tw",
            Name: "Chunghwa Telecom",
            HomePlmns: new[] { "46692" },
            IccidPrefixes: new[] { "8988692" },
            Spns: new[] { "Chunghwa", "中華電信" },
            EpdgHostname: "epdg.epc.mnc092.mcc466.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+886932000000"
        ),

        // 6. Japan
        new CarrierProfile(
            Id: "softbank-jp",
            Name: "SoftBank Japan",
            HomePlmns: new[] { "44020" },
            IccidPrefixes: new[] { "898120" },
            Spns: new[] { "SoftBank", "LINEMO", "Y!mobile" },
            EpdgHostname: "epdg.epc.mnc020.mcc440.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+819066519000"
        ),
        new CarrierProfile(
            Id: "docomo-jp",
            Name: "NTT Docomo Japan",
            HomePlmns: new[] { "44010" },
            IccidPrefixes: new[] { "898110" },
            Spns: new[] { "NTT DOCOMO", "ahamo" },
            EpdgHostname: "epdg.epc.mnc010.mcc440.pub.3gppnetwork.org",
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: "+81903101652"
        )
    };

    public static CarrierProfile? FindProfile(string imsi, string? iccid = null, string? opName = null)
    {
        var cleanImsi = Regex.Replace(imsi, @"\D", "");
        var cleanIccid = iccid != null ? Regex.Replace(iccid, @"\D", "") : string.Empty;
        var cleanOp = opName?.Trim() ?? string.Empty;

        CarrierProfile? bestMatch = null;
        int highestScore = 0;

        foreach (var profile in BuiltinProfiles)
        {
            int score = 0;

            // 1. SPN matching (highest priority: 1000 for exact, 500 for substring)
            if (!string.IsNullOrEmpty(cleanOp) && profile.Spns != null)
            {
                if (profile.Spns.Any(s => s.Equals(cleanOp, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 1000;
                }
                else if (profile.Spns.Any(s => cleanOp.Contains(s, StringComparison.OrdinalIgnoreCase) || s.Contains(cleanOp, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 500;
                }
            }

            // 2. ICCID prefix matching (weight: 100 + prefix length * 10)
            if (!string.IsNullOrEmpty(cleanIccid) && profile.IccidPrefixes != null)
            {
                var matchingIccid = profile.IccidPrefixes
                    .Where(pfx => cleanIccid.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(pfx => pfx.Length)
                    .FirstOrDefault();

                if (matchingIccid != null)
                {
                    score += 100 + matchingIccid.Length * 10;
                }
            }

            // 3. PLMN matching (weight: 10 + prefix length)
            if (cleanImsi.Length >= 5)
            {
                var matchingPlmn = profile.HomePlmns
                    .Where(plmn => cleanImsi.StartsWith(plmn, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(plmn => plmn.Length)
                    .FirstOrDefault();

                if (matchingPlmn != null)
                {
                    score += 10 + matchingPlmn.Length;
                }
            }

            if (score > highestScore)
            {
                highestScore = score;
                bestMatch = profile;
            }
        }

        return bestMatch;
    }
}
