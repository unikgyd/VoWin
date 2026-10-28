using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VoSharp.Sim;

namespace VoSharp.Telephony.VoWifi;

public enum EpdgAddressPreference
{
    System,
    Ipv4Preferred,
    Ipv6Preferred,
    Ipv4Only,
    Ipv6Only
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
    IReadOnlyList<string>? VisitedCountryTargets = null,
    IReadOnlyList<string>? RoamingStaticAddresses = null,
    string? NetworkContext = null,
    AddressFamily? RequiredAddressFamily = null,
    IEpdgDnsResolver? DnsResolver = null,
    bool ManualOnly = false,
    IReadOnlyList<string>? UserPreferredAddresses = null,
    EpdgSuccessfulSelection? LastSuccessfulSelection = null);

/// <summary>Injectable address lookup; production defaults to Windows/system DNS, never public DNS bypass.</summary>
public interface IEpdgDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed record EpdgCandidate(string Host, string Method, string? Plmn = null);

public sealed record EpdgResolvedEndpoint(IPAddress Address, EpdgCandidate Candidate, string HomeMcc, string HomeMnc);
public sealed record EpdgDnsQueryResult(EpdgCandidate Candidate, IPAddress[] Addresses, string Status, long ElapsedMs,
    string Resolver = "configured/custom", string? Detail = null);

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
    string SelectionMethod = "PLMN",
    IReadOnlyList<EpdgResolvedEndpoint>? Endpoints = null,
    IReadOnlyList<EpdgDnsQueryResult>? DnsQueries = null,
    string FailureScope = "");

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
    private static readonly ConcurrentDictionary<(string Scope, IPAddress Address, string HomePlmn), DateTimeOffset> ExcludedAddresses = new();

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

        // Equivalent-home entries may share an IMSI prefix of another legal
        // length. They must never override the actual EF_AD MNC length.
        if (sim.IsHomePlmnAuthoritative && sim.Mnc.Length is 2 or 3 &&
            imsi.StartsWith(mcc + sim.Mnc, StringComparison.Ordinal))
            return [(mcc, sim.Mnc)];

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
            if (IPAddress.TryParse(host, out var literal)) host = literal.ToString();
            else if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                if (method == "CUSTOM")
                    throw new ArgumentException("Custom ePDG must be a hostname or IP address, without a URL scheme, port or credentials.");
                return;
            }
            if (!candidates.Any(candidate => candidate.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(new EpdgCandidate(host, method, plmn));
        }

        if (!string.IsNullOrWhiteSpace(options.CustomEpdg))
        {
            Add(options.CustomEpdg, "CUSTOM");
            return candidates;
        }

        if (options.ManualOnly)
        {
            foreach (var entry in options.StaticAddresses ?? [])
                foreach (var host in entry.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    Add(host, "MANUAL");
            if (candidates.Count == 0)
                throw new ArgumentException("Manual-only discovery requires at least one valid ePDG address.");
            return candidates;
        }

        if (options.LastSuccessfulSelection is { } successful &&
            !IPAddress.TryParse(successful.Host, out _) &&
            BuildHomePlmnCandidates(sim).Contains((successful.HomeMcc, successful.HomeMnc)))
            Add(successful.Host, "LAST_SUCCESS", successful.DiscoveryPlmn);
        foreach (var host in options.UserPreferredAddresses ?? []) Add(host, "USER_PREFERRED");

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
                        carrierPlmns.Contains(registered, StringComparer.Ordinal))
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
                    var staticAddresses = options.IsRoaming && options.RoamingStaticAddresses is { Count: > 0 }
                        ? options.RoamingStaticAddresses : options.StaticAddresses;
                    foreach (var entry in staticAddresses ?? Array.Empty<string>())
                        foreach (var host in entry.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                            Add(host, "STATIC");
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

    public static Task<EpdgResolutionResult> ResolveAsync(
        SimIdentity sim,
        EpdgDiscoveryOptions options,
        CancellationToken ct = default) => ResolveCandidatesAsync(sim, options, BuildCandidates(sim, options), ct);

    // The owner can admit a plan before any concurrent DNS queries start. This
    // is an internal plan, not a user-configurable bypass of candidate policy.
    internal static async Task<EpdgResolutionResult> ResolveCandidatesAsync(
        SimIdentity sim, EpdgDiscoveryOptions options, IReadOnlyList<EpdgCandidate> candidates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        PurgeExpiredExclusions();

        var cleanImsi = new string(sim.Imsi.Where(char.IsAsciiDigit).ToArray());
        if (candidates.Count == 0)
            throw new InvalidOperationException("No valid ePDG discovery candidates could be generated.");

        var configuredTimeout = options.DnsTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : (TimeSpan?)null;
        var resolutionTasks = candidates.Select(candidate => ResolveCandidateAsync(
            candidate,
            configuredTimeout ?? (candidate.Method == "PLMN" ? PlmnDnsTimeout : DefaultDnsTimeout),
            options.DnsResolver,
            ct)).ToArray();
        var resolved = await Task.WhenAll(resolutionTasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{sim.Iccid}\n{sim.Imsi}\n{sim.Mnc}/{sim.IsHomePlmnAuthoritative}\n{options.NetworkContext}\n{options.RequiredAddressFamily}\n{options.AddressPreference}")));
        var allEndpoints = resolved.SelectMany(result => result.Addresses.Select(address =>
        {
            var home = ResolveEndpointHomePlmn(sim, result.Candidate);
            if (options.LastSuccessfulSelection is { } last && result.Candidate.Host.Equals(last.Host, StringComparison.OrdinalIgnoreCase) &&
                BuildHomePlmnCandidates(sim).Contains((last.HomeMcc, last.HomeMnc)))
                home = (last.HomeMcc, last.HomeMnc);
            return new EpdgResolvedEndpoint(address, result.Candidate, home.Mcc, home.Mnc);
        })).Where(endpoint => options.RequiredAddressFamily == null || endpoint.Address.AddressFamily == options.RequiredAddressFamily)
            .Where(endpoint => options.AddressPreference != EpdgAddressPreference.Ipv4Only || endpoint.Address.AddressFamily == AddressFamily.InterNetwork)
            .Where(endpoint => options.AddressPreference != EpdgAddressPreference.Ipv6Only || endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .OrderBy(endpoint => MatchesLastSuccess(endpoint, options.LastSuccessfulSelection) ? 0 : 1)
            .DistinctBy(endpoint => (endpoint.Address, endpoint.HomeMcc, endpoint.HomeMnc)).ToArray();
        var usableEndpoints = allEndpoints.Where(endpoint => !ExcludedAddresses.ContainsKey((scope, endpoint.Address, endpoint.HomeMcc + endpoint.HomeMnc))).ToArray();
        if (usableEndpoints.Length == 0 && allEndpoints.Length > 0)
        {
            // Permit one complete retry, without clearing another SIM or route's history.
            foreach (var endpoint in allEndpoints) ExcludedAddresses.TryRemove((scope, endpoint.Address, endpoint.HomeMcc + endpoint.HomeMnc), out _);
            usableEndpoints = allEndpoints;
        }
        usableEndpoints = PrioritizeEndpoints(usableEndpoints, options.AddressPreference);
        if (options.LastSuccessfulSelection is { } previous)
            usableEndpoints = usableEndpoints.OrderBy(endpoint => MatchesLastSuccess(endpoint, previous) ? 0 : 1).ToArray();
        var selectedEndpoint = usableEndpoints.FirstOrDefault();
        var selected = selectedEndpoint?.Candidate ?? candidates[0];
        var home = selectedEndpoint is null ? ResolveHomePlmn(sim) : (Mcc: selectedEndpoint.HomeMcc, Mnc: selectedEndpoint.HomeMnc);
        var imsDomain = BuildImsDomain(home.Mcc, home.Mnc);
        var impi = $"{cleanImsi}@{imsDomain}";
        return new EpdgResolutionResult(
            Fqdn: selected.Host,
            ImsDomain: imsDomain,
            Impi: impi,
            Impu: $"sip:{impi}",
            IpAddresses: usableEndpoints.Select(endpoint => endpoint.Address).Distinct().ToArray(),
            MatchedCarrier: string.IsNullOrWhiteSpace(sim.OperatorName) ? null : sim.OperatorName.Trim(),
            PreferredSuite: IkeProposalSuite.Standard,
            Apn: options.IsEmergency ? "sos" : "ims",
            SmsCenter: null,
            HomeMcc: home.Mcc,
            HomeMnc: home.Mnc,
            Candidates: candidates,
            SelectionMethod: selected.Method,
            Endpoints: usableEndpoints,
            DnsQueries: resolved,
            FailureScope: scope);
    }

    public static void ReportConnectionFailure(EpdgResolutionResult discovery, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ExcludedAddresses[(discovery.FailureScope, address, discovery.HomeMcc + discovery.HomeMnc)] = DateTimeOffset.UtcNow.Add(ExclusionLifetime);
    }

    public static void ReportConnectionSuccess(EpdgResolutionResult discovery, IPAddress address) =>
        ExcludedAddresses.TryRemove((discovery.FailureScope, address, discovery.HomeMcc + discovery.HomeMnc), out _);

    internal static (string Mcc, string Mnc) ResolveEndpointHomePlmn(SimIdentity sim, EpdgCandidate candidate)
    {
        var homes = BuildHomePlmnCandidates(sim);
        if (!sim.IsHomePlmnAuthoritative && TrySplitPlmn(candidate.Plmn, out var mcc, out var mnc) && homes.Contains((mcc, mnc)))
            return (mcc, mnc);
        if (!sim.IsHomePlmnAuthoritative)
            foreach (var home in homes)
                if (candidate.Host.Equals(BuildEpdgDomain(home.Mcc, home.Mnc), StringComparison.OrdinalIgnoreCase))
                    return home; // A standard manual hostname may disambiguate MNC length, never invent a foreign IMSI realm.
        return homes[0]; // Visited/foreign ePDG selection must not change the subscriber's realm.
    }

    internal static EpdgResolutionResult SelectEndpoint(EpdgResolutionResult discovery, SimIdentity sim, EpdgResolvedEndpoint endpoint)
    {
        if (!BuildHomePlmnCandidates(sim).Contains((endpoint.HomeMcc, endpoint.HomeMnc)))
            throw new InvalidOperationException("The ePDG endpoint's home PLMN does not match the live SIM.");
        var domain = BuildImsDomain(endpoint.HomeMcc, endpoint.HomeMnc);
        var impi = $"{sim.Imsi}@{domain}";
        return discovery with
        {
            Fqdn = endpoint.Candidate.Host, SelectionMethod = endpoint.Candidate.Method,
            HomeMcc = endpoint.HomeMcc, HomeMnc = endpoint.HomeMnc, ImsDomain = domain, Impi = impi, Impu = $"sip:{impi}",
            IpAddresses = new[] { endpoint.Address }.Concat(discovery.IpAddresses).Distinct().ToArray()
        };
    }

    private static async Task<EpdgDnsQueryResult> ResolveCandidateAsync(
        EpdgCandidate candidate,
        TimeSpan timeout,
        IEpdgDnsResolver? resolver,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (IPAddress.TryParse(candidate.Host, out var literal))
        {
            if (literal.IsIPv4MappedToIPv6) literal = literal.MapToIPv4();
            return new(candidate, IsUsableAddress(literal) ? [literal] : [], "literal", timer.ElapsedMilliseconds);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        resolver ??= new SystemEpdgDnsResolver();
        EpdgDnsQueryResult Result(IPAddress[] addresses, string status) => new(candidate, addresses, status,
            timer.ElapsedMilliseconds, (resolver as SystemEpdgDnsResolver)?.Name ?? "configured/custom",
            (resolver as SystemEpdgDnsResolver)?.Detail);
        try
        {
            var addresses = await resolver.ResolveAsync(candidate.Host, timeoutCts.Token).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            var usable = addresses.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
                .Where(IsUsableAddress).Distinct().ToArray();
            return Result(usable, usable.Length > 0 ? "resolved" : "no-usable-address");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result([], "timeout");
        }
        catch (SocketException ex)
        {
            return Result([], $"dns-{ex.SocketErrorCode}");
        }
        catch (ArgumentException)
        {
            return Result([], "invalid-host");
        }
    }

    private static bool MatchesLastSuccess(EpdgResolvedEndpoint endpoint, EpdgSuccessfulSelection? previous) =>
        previous != null && endpoint.Candidate.Host.Equals(previous.Host, StringComparison.OrdinalIgnoreCase) &&
        endpoint.HomeMcc == previous.HomeMcc && endpoint.HomeMnc == previous.HomeMnc;

    private static EpdgResolvedEndpoint[] PrioritizeEndpoints(IEnumerable<EpdgResolvedEndpoint> endpoints, EpdgAddressPreference preference)
    {
        var indexed = endpoints.Select((endpoint, index) => (endpoint, index));
        return preference switch
        {
            EpdgAddressPreference.Ipv4Preferred => indexed
                .OrderBy(item => item.endpoint.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ThenBy(item => item.index).Select(item => item.endpoint).ToArray(),
            EpdgAddressPreference.Ipv6Preferred => indexed
                .OrderBy(item => item.endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1)
                .ThenBy(item => item.index).Select(item => item.endpoint).ToArray(),
            _ => indexed.OrderBy(item => item.index).Select(item => item.endpoint).ToArray()
        };
    }

    private static bool IsUsableAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(IPAddress.IPv6Any) &&
        !address.IsIPv6Multicast && !address.IsIPv6LinkLocal &&
        !address.Equals(IPAddress.Broadcast) &&
        (address.AddressFamily != AddressFamily.InterNetwork || address.GetAddressBytes()[0] is < 224 or > 239);

    private static void PurgeExpiredExclusions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ExcludedAddresses)
            if (entry.Value <= now) ExcludedAddresses.TryRemove(entry.Key, out _);
    }

    private static bool TryNormalizePlmn(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length is 5 or 6 && normalized.All(char.IsAsciiDigit);
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
