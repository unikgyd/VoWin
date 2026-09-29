using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using VoSharp.Sim;

namespace VoSharp.Telephony.VoWifi;

public enum EpdgAddressPreference
{
    System,
    Ipv4Preferred,
    Ipv6Preferred
}

public enum EpdgDiscoveryMethod
{
    Static,
    Plmn,
    Pco,
    CellularLocation
}

public enum EpdgPlmnSource
{
    Registered,
    Home,
    EquivalentHomeAll,
    EquivalentHomeFirst,
    CarrierConfigured
}

public sealed record EpdgDiscoveryOptions(
    string? CustomEpdg = null,
    IReadOnlyList<string>? StaticAddresses = null,
    string? RegisteredPlmn = null,
    IReadOnlyList<string>? CarrierPlmns = null,
    IReadOnlyList<string>? CellularLocationDomains = null,
    bool IsRoaming = false,
    bool IsEmergency = false,
    string? VisitedMcc = null,
    EpdgAddressPreference AddressPreference = EpdgAddressPreference.System,
    TimeSpan? DnsTimeout = null,
    IReadOnlyList<string>? PcoAddresses = null,
    IReadOnlyList<EpdgDiscoveryMethod>? MethodPriority = null,
    IReadOnlyList<EpdgPlmnSource>? PlmnPriority = null,
    IReadOnlyList<string>? VisitedCountryTargets = null);

public sealed record EpdgCandidate(string Host, string Method, string? Plmn = null);

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
    string HomeMnc = "",
    IReadOnlyList<EpdgCandidate>? Candidates = null,
    string SelectionMethod = "PLMN");

/// <summary>
/// AOSP-Iwlan-style ePDG selector. It builds a priority-ordered candidate set,
/// resolves candidates concurrently, preserves candidate priority, filters
/// recently failed addresses and applies an explicit IP-family preference.
/// </summary>
public static class EpdgResolver
{
    private static readonly TimeSpan DefaultDnsTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PlmnDnsTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExclusionLifetime = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<IPAddress, DateTimeOffset> ExcludedAddresses = new();

    public static string BuildEpdgDomain(string mcc, string mnc) =>
        $"epdg.epc.mnc{NormalizeMnc(mnc)}.mcc{NormalizeMcc(mcc)}.pub.3gppnetwork.org";

    public static string BuildEmergencyEpdgDomain(string mcc, string mnc) =>
        "sos." + BuildEpdgDomain(mcc, mnc);

    public static string BuildVisitedCountryDomain(string mcc, bool emergency = false) =>
        $"{(emergency ? "sos." : string.Empty)}epdg.epc.mcc{NormalizeMcc(mcc)}.visited-country.pub.3gppnetwork.org";

    public static string BuildLacDomain(string mcc, string mnc, int lac, bool emergency = false)
    {
        if (lac is < 0 or > 0xffff) throw new ArgumentOutOfRangeException(nameof(lac));
        return $"lac{lac:x4}.{(emergency ? "sos." : string.Empty)}{BuildEpdgDomain(mcc, mnc)}";
    }

    public static string BuildLteTaiDomain(string mcc, string mnc, int tac, bool emergency = false)
    {
        if (tac is < 0 or > 0xffff) throw new ArgumentOutOfRangeException(nameof(tac));
        var hex = tac.ToString("x4");
        return $"tac-lb{hex[2..]}.tac-hb{hex[..2]}.tac.{(emergency ? "sos." : string.Empty)}{BuildEpdgDomain(mcc, mnc)}";
    }

    public static string BuildNrTaiDomain(string mcc, string mnc, int tac, bool emergency = false)
    {
        if (tac is < 0 or > 0xffffff) throw new ArgumentOutOfRangeException(nameof(tac));
        var hex = tac.ToString("x6");
        return $"tac-lb{hex[4..]}.tac-mb{hex[2..4]}.tac-hb{hex[..2]}.5gstac.{(emergency ? "sos." : string.Empty)}{BuildEpdgDomain(mcc, mnc)}";
    }

    public static string BuildImsDomain(string mcc, string mnc) =>
        $"ims.mnc{NormalizeMnc(mnc)}.mcc{NormalizeMcc(mcc)}.3gppnetwork.org";

    /// <summary>Parses the 3-byte PLMN prefix plus IPv4/IPv6 address used by IWLAN PCO.</summary>
    public static IPAddress? ParsePcoAddress(ReadOnlySpan<byte> value)
    {
        const int plmnPrefixLength = 3;
        var addressLength = value.Length - plmnPrefixLength;
        return addressLength is 4 or 16 ? new IPAddress(value[plmnPrefixLength..]) : null;
    }

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
            if (TrySplitPlmn(plmn, out var candidateMcc, out var candidateMnc) &&
                imsi.StartsWith(candidateMcc + candidateMnc, StringComparison.Ordinal))
                AddCandidate(candidateMcc, candidateMnc);
        }

        if (sim.Mnc.Length is 2 or 3 && imsi.StartsWith(mcc + sim.Mnc, StringComparison.Ordinal))
            AddCandidate(mcc, sim.Mnc);

        if (sim.IsHomePlmnAuthoritative && candidates.Count > 0)
            return candidates;

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

    public static IReadOnlyList<EpdgCandidate> BuildCandidates(SimIdentity sim, EpdgDiscoveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sim);
        options ??= new EpdgDiscoveryOptions();
        var candidates = new List<EpdgCandidate>();

        void Add(string? host, string method, string? plmn = null)
        {
            host = host?.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(host)) return;
            if (!candidates.Any(candidate => candidate.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(new EpdgCandidate(host, method, plmn));
        }

        if (!string.IsNullOrWhiteSpace(options.CustomEpdg))
        {
            Add(options.CustomEpdg, "CUSTOM");
            return candidates;
        }

        // AOSP executes visited-country discovery before the carrier-configured
        // method list. NAPTR-derived targets can be supplied by the host; the
        // standardized visited-country name remains the DNS fallback.
        if (options.IsRoaming && !string.IsNullOrWhiteSpace(options.VisitedMcc))
        {
            var visitedTargets = options.VisitedCountryTargets ?? Array.Empty<string>();
            if (visitedTargets.Count > 0)
            {
                foreach (var target in visitedTargets) Add(target, "VISITED_COUNTRY");
            }
            else
            {
                if (options.IsEmergency) Add(BuildVisitedCountryDomain(options.VisitedMcc, true), "VISITED_COUNTRY");
                Add(BuildVisitedCountryDomain(options.VisitedMcc), "VISITED_COUNTRY");
            }
        }

        var plmns = new List<string>();
        var homePlmns = BuildHomePlmnCandidates(sim).Select(home => home.Mcc + home.Mnc).ToArray();
        var equivalentPlmns = (sim.HomePlmns ?? Array.Empty<string>())
            .Select(value => TryNormalizePlmn(value, out var normalized) ? normalized : null)
            .Where(value => value != null).Cast<string>().ToArray();
        var carrierPlmns = (options.CarrierPlmns ?? Array.Empty<string>())
            .Select(value => TryNormalizePlmn(value, out var normalized) ? normalized : null)
            .Where(value => value != null).Cast<string>().ToArray();
        var plmnPriority = options.PlmnPriority ??
            [EpdgPlmnSource.Registered, EpdgPlmnSource.Home, EpdgPlmnSource.EquivalentHomeAll, EpdgPlmnSource.CarrierConfigured];
        foreach (var source in plmnPriority)
        {
            switch (source)
            {
                case EpdgPlmnSource.Registered:
                    if (TryNormalizePlmn(options.RegisteredPlmn, out var registered) &&
                        (carrierPlmns.Length == 0 || carrierPlmns.Contains(registered, StringComparer.Ordinal)))
                        plmns.Add(registered);
                    break;
                case EpdgPlmnSource.Home:
                    plmns.AddRange(homePlmns);
                    break;
                case EpdgPlmnSource.EquivalentHomeAll:
                    plmns.AddRange(equivalentPlmns);
                    break;
                case EpdgPlmnSource.EquivalentHomeFirst:
                    if (equivalentPlmns.Length > 0) plmns.Add(equivalentPlmns[0]);
                    break;
                case EpdgPlmnSource.CarrierConfigured:
                    plmns.AddRange(carrierPlmns);
                    break;
            }
        }

        void AddPlmnCandidates()
        {
            foreach (var plmn in plmns.Distinct(StringComparer.Ordinal))
            {
                TrySplitPlmn(plmn, out var mcc, out var mnc);
                if (options.IsEmergency) Add(BuildEmergencyEpdgDomain(mcc, mnc), "PLMN", plmn);
                Add(BuildEpdgDomain(mcc, mnc), "PLMN", plmn);
            }
        }

        var methodPriority = options.MethodPriority ??
            [EpdgDiscoveryMethod.Static, EpdgDiscoveryMethod.Plmn, EpdgDiscoveryMethod.Pco, EpdgDiscoveryMethod.CellularLocation];
        foreach (var method in methodPriority.Distinct())
        {
            switch (method)
            {
                case EpdgDiscoveryMethod.Static:
                    foreach (var host in options.StaticAddresses ?? Array.Empty<string>()) Add(host, "STATIC");
                    break;
                case EpdgDiscoveryMethod.Plmn:
                    AddPlmnCandidates();
                    break;
                case EpdgDiscoveryMethod.Pco:
                    foreach (var address in options.PcoAddresses ?? Array.Empty<string>())
                        if (IPAddress.TryParse(address, out _)) Add(address, "PCO");
                    break;
                case EpdgDiscoveryMethod.CellularLocation:
                    foreach (var domain in options.CellularLocationDomains ?? Array.Empty<string>())
                        Add(domain, "CELLULAR_LOCATION");
                    break;
            }
        }
        return candidates;
    }

    public static Task<EpdgResolutionResult> ResolveAsync(
        SimIdentity sim,
        string? customEpdg = null,
        CancellationToken ct = default) =>
        ResolveAsync(sim, new EpdgDiscoveryOptions(CustomEpdg: customEpdg), ct);

    public static async Task<EpdgResolutionResult> ResolveAsync(
        SimIdentity sim,
        EpdgDiscoveryOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(options);
        PurgeExpiredExclusions();

        var cleanImsi = new string(sim.Imsi.Where(char.IsAsciiDigit).ToArray());
        var home = ResolveHomePlmn(sim);
        var candidates = BuildCandidates(sim, options);
        if (candidates.Count == 0)
            throw new InvalidOperationException("No valid ePDG discovery candidates could be generated.");

        var configuredTimeout = options.DnsTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : (TimeSpan?)null;
        var resolutionTasks = candidates.Select(candidate => ResolveCandidateAsync(
            candidate,
            configuredTimeout ?? (candidate.Method == "PLMN" ? PlmnDnsTimeout : DefaultDnsTimeout),
            ct)).ToArray();
        var resolved = await Task.WhenAll(resolutionTasks).ConfigureAwait(false);

        var allAddresses = resolved.SelectMany(result => result.Addresses).Distinct().ToArray();
        var usableAddresses = allAddresses.Where(address => !ExcludedAddresses.ContainsKey(address)).ToArray();
        if (usableAddresses.Length == 0 && allAddresses.Length > 0)
        {
            // Match AOSP: once every candidate has been excluded, clear the temporary
            // set and permit one complete retry rather than returning an empty list.
            ExcludedAddresses.Clear();
            usableAddresses = allAddresses;
        }
        usableAddresses = PrioritizeAddresses(usableAddresses, options.AddressPreference);

        var firstUsable = usableAddresses.FirstOrDefault();
        var selectedResult = resolved.FirstOrDefault(result =>
            firstUsable != null && result.Addresses.Contains(firstUsable));
        var selected = selectedResult.Candidate ?? candidates[0];
        var imsDomain = BuildImsDomain(home.Mcc, home.Mnc);
        var impi = $"{cleanImsi}@{imsDomain}";
        return new EpdgResolutionResult(
            Fqdn: selected.Host,
            ImsDomain: imsDomain,
            Impi: impi,
            Impu: $"sip:{impi}",
            IpAddresses: usableAddresses,
            MatchedCarrier: string.IsNullOrWhiteSpace(sim.OperatorName) ? null : sim.OperatorName.Trim(),
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: options.IsEmergency ? "sos" : "ims",
            SmsCenter: null,
            HomeMcc: home.Mcc,
            HomeMnc: home.Mnc,
            Candidates: candidates,
            SelectionMethod: selected.Method);
    }

    public static void ReportConnectionFailure(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ExcludedAddresses[address] = DateTimeOffset.UtcNow.Add(ExclusionLifetime);
    }

    public static void ReportConnectionSuccess() => ExcludedAddresses.Clear();

    internal static void ClearExcludedAddressesForTests() => ExcludedAddresses.Clear();

    private static async Task<(EpdgCandidate Candidate, IPAddress[] Addresses)> ResolveCandidateAsync(
        EpdgCandidate candidate,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (IPAddress.TryParse(candidate.Host, out var literal))
            return (candidate, IsUsableAddress(literal) ? [literal] : []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(candidate.Host, timeoutCts.Token).ConfigureAwait(false);
            return (candidate, addresses.Where(IsUsableAddress).Distinct().ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (candidate, []);
        }
        catch (SocketException)
        {
            return (candidate, []);
        }
        catch (ArgumentException)
        {
            return (candidate, []);
        }
    }

    private static IPAddress[] PrioritizeAddresses(IEnumerable<IPAddress> addresses, EpdgAddressPreference preference)
    {
        var indexed = addresses.Select((address, index) => (address, index));
        return preference switch
        {
            EpdgAddressPreference.Ipv4Preferred => indexed
                .OrderBy(item => item.address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ThenBy(item => item.index).Select(item => item.address).ToArray(),
            EpdgAddressPreference.Ipv6Preferred => indexed
                .OrderBy(item => item.address.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1)
                .ThenBy(item => item.index).Select(item => item.address).ToArray(),
            _ => indexed.OrderBy(item => item.index).Select(item => item.address).ToArray()
        };
    }

    private static bool IsUsableAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(IPAddress.IPv6Any) &&
        !address.IsIPv6Multicast;

    private static void PurgeExpiredExclusions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ExcludedAddresses)
            if (entry.Value <= now) ExcludedAddresses.TryRemove(entry.Key, out _);
    }

    private static bool TryNormalizePlmn(string? value, out string normalized)
    {
        normalized = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return normalized.Length is 5 or 6;
    }

    private static bool TrySplitPlmn(string? value, out string mcc, out string mnc)
    {
        mcc = mnc = string.Empty;
        if (!TryNormalizePlmn(value, out var normalized)) return false;
        mcc = normalized[..3];
        mnc = normalized[3..];
        return true;
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
