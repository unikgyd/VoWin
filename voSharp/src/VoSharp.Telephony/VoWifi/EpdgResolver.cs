using System.Net;
using System.Net.Sockets;
using VoSharp.Sim;

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
    string? SmsCenter = null,
    string HomeMcc = "",
    string HomeMnc = ""
);

/// <summary>
/// Standards-based ePDG discovery. All network identities are derived from the
/// active USIM; no ICCID, SPN, operator, gateway, or algorithm table is used.
/// </summary>
public static class EpdgResolver
{
    public static string BuildEpdgDomain(string mcc, string mnc) =>
        $"epdg.epc.mnc{NormalizeMnc(mnc)}.mcc{NormalizeMcc(mcc)}.pub.3gppnetwork.org";

    public static string BuildImsDomain(string mcc, string mnc) =>
        $"ims.mnc{NormalizeMnc(mnc)}.mcc{NormalizeMcc(mcc)}.3gppnetwork.org";

    /// <summary>
    /// Returns USIM-derived home-PLMN candidates. EF_AD makes the first and only
    /// candidate authoritative. If EF_AD was unavailable, both legal IMSI MNC
    /// lengths are returned and DNS decides which standard ePDG exists.
    /// </summary>
    public static IReadOnlyList<(string Mcc, string Mnc)> BuildHomePlmnCandidates(SimIdentity sim)
    {
        ArgumentNullException.ThrowIfNull(sim);
        var imsi = new string((sim.Imsi ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (imsi.Length is < 5 or > 16)
            throw new ArgumentException("Invalid IMSI length for VoWiFi home-PLMN discovery.", nameof(sim));

        var mcc = imsi[..3];
        var candidates = new List<(string Mcc, string Mnc)>();

        foreach (var plmn in sim.HomePlmns ?? Array.Empty<string>())
        {
            if (plmn.Length is 5 or 6 && plmn.All(char.IsAsciiDigit) &&
                imsi.StartsWith(plmn, StringComparison.Ordinal))
                AddCandidate(plmn[..3], plmn[3..]);
        }

        if (sim.Mnc.Length is 2 or 3 && imsi.StartsWith(mcc + sim.Mnc, StringComparison.Ordinal))
            AddCandidate(mcc, sim.Mnc);

        if (sim.IsHomePlmnAuthoritative && candidates.Count > 0)
        {
            return candidates;
        }

        AddCandidate(mcc, imsi.Substring(3, 2));
        if (imsi.Length >= 6)
            AddCandidate(mcc, imsi.Substring(3, 3));
        return candidates;

        void AddCandidate(string candidateMcc, string candidateMnc)
        {
            if (!candidates.Any(candidate => candidate.Mcc == candidateMcc && candidate.Mnc == candidateMnc))
                candidates.Add((candidateMcc, candidateMnc));
        }
    }

    public static (string Mcc, string Mnc) ResolveHomePlmn(SimIdentity sim) =>
        BuildHomePlmnCandidates(sim)[0];

    public static async Task<EpdgResolutionResult> ResolveAsync(
        SimIdentity sim,
        string? customEpdg = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sim);
        var cleanImsi = new string(sim.Imsi.Where(char.IsAsciiDigit).ToArray());
        var candidates = BuildHomePlmnCandidates(sim);
        var selected = candidates[0];
        var fqdn = string.IsNullOrWhiteSpace(customEpdg)
            ? BuildEpdgDomain(selected.Mcc, selected.Mnc)
            : customEpdg.Trim();
        var addresses = Array.Empty<IPAddress>();

        if (!string.IsNullOrWhiteSpace(customEpdg))
        {
            addresses = await ResolveAddressesAsync(fqdn, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var candidateFqdn = BuildEpdgDomain(candidate.Mcc, candidate.Mnc);
                var candidateAddresses = await ResolveAddressesAsync(candidateFqdn, ct).ConfigureAwait(false);
                if (candidateAddresses.Length == 0)
                    continue;

                selected = candidate;
                fqdn = candidateFqdn;
                addresses = candidateAddresses;
                break;
            }
        }

        var imsDomain = BuildImsDomain(selected.Mcc, selected.Mnc);
        var impi = $"{cleanImsi}@{imsDomain}";
        return new EpdgResolutionResult(
            Fqdn: fqdn,
            ImsDomain: imsDomain,
            Impi: impi,
            Impu: $"sip:{impi}",
            IpAddresses: addresses,
            MatchedCarrier: string.IsNullOrWhiteSpace(sim.OperatorName) ? null : sim.OperatorName.Trim(),
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: "ims",
            SmsCenter: null,
            HomeMcc: selected.Mcc,
            HomeMnc: selected.Mnc);
    }

    private static async Task<IPAddress[]> ResolveAddressesAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses
                .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) &&
                                  !address.Equals(IPAddress.IPv6Any))
                .Distinct()
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static string NormalizeMcc(string mcc)
    {
        if (string.IsNullOrWhiteSpace(mcc) || mcc.Length is < 2 or > 3 || !mcc.All(char.IsAsciiDigit))
            throw new ArgumentException("MCC must contain two or three digits.", nameof(mcc));
        return mcc.PadLeft(3, '0');
    }

    private static string NormalizeMnc(string mnc)
    {
        if (string.IsNullOrWhiteSpace(mnc) || mnc.Length is < 2 or > 3 || !mnc.All(char.IsAsciiDigit))
            throw new ArgumentException("MNC must contain two or three digits.", nameof(mnc));
        return mnc.PadLeft(3, '0');
    }
}
