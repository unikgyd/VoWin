using System.Net;
using System.Net.NetworkInformation;
using VoSharp.Common.Utils;

namespace VoSharp.Modem;

/// <summary>
/// Read-only description of an IMS packet-data context reported by the modem.
/// This is the prerequisite for Host IMS: Windows must own a routable address on
/// the IMS PDN instead of seeing only the modem's Internet/NAT interface.
/// </summary>
public sealed record HostImsPdnContext(
    int ContextId,
    string PdpType,
    string Apn,
    bool IsActive,
    IReadOnlyList<IPAddress> LocalAddresses,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<IPAddress> DnsServers,
    IReadOnlyList<IPAddress> PcscfServers,
    IReadOnlyList<string> HostInterfaces)
{
    /// <summary>Addresses owned by a live Windows interface, including the controlled PPP /64 address.</summary>
    public IReadOnlyList<IPAddress> HostOwnedAddresses { get; init; } = [];
    public string Label => FormatContextId(ContextId);

    public static string FormatContextId(int contextId) => contextId switch
    {
        <= -1000 => $"QMI WDS profile {-1000 - contextId}",
        < 0 => "QMI WDS active IMS",
        _ => $"CID {contextId}"
    };
}

public sealed record HostImsEndpointCandidate(int ContextId, IPAddress LocalAddress, IPAddress PcscfAddress)
{
    public string Display => $"{HostImsPdnContext.FormatContextId(ContextId)}: {LocalAddress} → {PcscfAddress}";
}

public sealed record HostImsWindowsAdapter(string Name, string Description, bool IsConnected);

public enum HostImsReadiness
{
    NotConfigured,
    ConfiguredButInactive,
    ModemInternalOnly,
    HostRoutable,
    SimUnavailable,
    ProbeFailed
}

public sealed record HostImsProbeResult(
    HostImsReadiness Readiness,
    bool? ModemImsEnabled,
    string? UsbNetworkMode,
    IReadOnlyList<HostImsPdnContext> Contexts,
    string Summary)
{
    public bool CanAttemptWindowsIms => Readiness == HostImsReadiness.HostRoutable &&
                                        EndpointCandidates.Any(IsRouteVerified);
    public bool? SimInserted { get; init; }
    public string ProbeSource { get; init; } = "AT";
    /// <summary>Null when AT was not checked; false when AT preflight commands all failed.</summary>
    public bool? AtControlAvailable { get; init; }
    public string? ControlStackStatus { get; init; }
    public IReadOnlyList<HostImsWindowsAdapter> WindowsCellularAdapters { get; init; } = [];
    /// <summary>A missing result never authorizes registration.</summary>
    public IReadOnlyList<HostImsRouteCheck>? RouteChecks { get; init; }
    /// <summary>Trusted bearer-owner anchor, not inferred from a matching IP address.</summary>
    public int? OwnedInterfaceIndex { get; init; }
    public ulong? OwnedInterfaceLuid { get; init; }
    public IReadOnlyList<HostImsEndpointCandidate> EndpointCandidates => Readiness == HostImsReadiness.HostRoutable
        ? HostImsPdnParser.GetEndpointCandidates(Contexts)
        : [];

    public bool IsRouteVerified(HostImsEndpointCandidate endpoint) =>
        OwnedInterfaceIndex is not <= 0 && OwnedInterfaceLuid is not 0 &&
        (OwnedInterfaceLuid is null || OwnedInterfaceIndex is not null) &&
        RouteChecks?.Any(check => check.Endpoint == endpoint && check.IsVerified &&
            check.InterfaceIndex is > 0 &&
            (OwnedInterfaceIndex is null || check.InterfaceIndex == OwnedInterfaceIndex) &&
            (OwnedInterfaceLuid is null || check.InterfaceLuid == OwnedInterfaceLuid)) == true;
}

internal sealed record HostImsConfiguredContext(int ContextId, string PdpType, string Apn);

internal sealed record HostImsRuntimeContext(
    int ContextId,
    string Apn,
    IReadOnlyList<IPAddress> LocalAddresses,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<IPAddress> DnsServers,
    IReadOnlyList<IPAddress> PcscfServers);

internal sealed record HostImsPppAddress(IPAddress Address, int PrefixLength, string? InterfaceName = null);

internal static class HostImsPdnParser
{
    // This RAS entry is created only by the controlled CID 5 diagnostic. Do
    // not classify arbitrary VPN/PPP interfaces as cellular IMS bearers.
    internal const string ImsPppEntryName = "VoWin IMS PPP Probe";

    public static HostImsReadiness Classify(
        bool? simInserted,
        bool queryFailed,
        IReadOnlyList<HostImsPdnContext> contexts)
    {
        if (simInserted == false) return HostImsReadiness.SimUnavailable;
        if (queryFailed) return HostImsReadiness.ProbeFailed;
        if (contexts.Count == 0) return HostImsReadiness.NotConfigured;
        if (!contexts.Any(context => context.IsActive && context.LocalAddresses.Count > 0))
            return HostImsReadiness.ConfiguredButInactive;
        return GetEndpointCandidates(contexts).Count > 0
            ? HostImsReadiness.HostRoutable
            : HostImsReadiness.ModemInternalOnly;
    }

    public static IReadOnlyList<HostImsEndpointCandidate> GetEndpointCandidates(
        IReadOnlyList<HostImsPdnContext> contexts) =>
        contexts.Where(context => context.IsActive)
            .SelectMany(context => context.HostOwnedAddresses
                .Where(local => context.LocalAddresses.Contains(local))
                .SelectMany(local => context.PcscfServers
                    .Where(pcscf => pcscf.AddressFamily == local.AddressFamily)
                    .Select(pcscf => new HostImsEndpointCandidate(context.ContextId, local, pcscf))))
            .Distinct()
            .ToArray();

    internal static bool IsUsableEndpointAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.ScopeId == 0 && !address.IsIPv4MappedToIPv6 && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return !address.Equals(IPAddress.Broadcast) && bytes[0] is < 224 or > 239 &&
            !(bytes[0] == 169 && bytes[1] == 254);
    }

    public static IReadOnlyList<HostImsConfiguredContext> ParseConfigured(IEnumerable<string> lines) =>
        lines.Select(line => ParseFields(line, "+CGDCONT:"))
            .Where(fields => fields is { Count: >= 3 } && int.TryParse(fields[0], out _))
            .Select(fields => new HostImsConfiguredContext(int.Parse(fields![0]), fields[1], fields[2]))
            .ToArray();

    public static IReadOnlyDictionary<int, bool> ParseActivation(IEnumerable<string> lines)
    {
        var result = new Dictionary<int, bool>();
        foreach (var line in lines)
        {
            var fields = ParseFields(line, "+CGACT:");
            if (fields is { Count: >= 2 } && int.TryParse(fields[0], out var cid) && int.TryParse(fields[1], out var active))
                result[cid] = active == 1;
        }
        return result;
    }

    public static bool IsContextActive(int contextId, IReadOnlyDictionary<int, bool> activation, bool hasRuntime) =>
        activation.TryGetValue(contextId, out var active) ? active : hasRuntime;

    /// <summary>
    /// A modem may reject the dynamic-parameters query when the configured IMS
    /// context is explicitly inactive. In that case the failed query does not
    /// obscure readiness: there is no active IMS bearer to inspect yet.
    /// </summary>
    public static bool IsRuntimeQueryFailureCritical(
        IReadOnlyList<HostImsConfiguredContext> configured,
        IReadOnlyDictionary<int, bool> activation) =>
        configured.Count == 0 ||
        configured.Any(context => !activation.TryGetValue(context.ContextId, out var active) || active);

    public static IReadOnlyList<HostImsRuntimeContext> ParseRuntime(IEnumerable<string> lines)
    {
        var result = new List<HostImsRuntimeContext>();
        foreach (var line in lines)
        {
            var fields = ParseFields(line, "+CGCONTRDP:");
            if (fields is not { Count: >= 4 } ||
                !int.TryParse(fields[0], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var cid) || cid is < 1 or > 255) continue;
            result.Add(new HostImsRuntimeContext(
                cid,
                fields[2],
                ParseAddresses(fields[3], stripIpv4Mask: true),
                fields.Count > 4 ? ParseAddresses(fields[4]) : [],
                ParseAddressRange(fields, 5, 2),
                ParseAddressRange(fields, 7, 2)));
        }
        return result;
    }

    public static bool IsImsApn(string? apn)
    {
        if (string.IsNullOrWhiteSpace(apn)) return false;
        return apn.Equals("ims", StringComparison.OrdinalIgnoreCase) ||
               apn.StartsWith("ims.", StringComparison.OrdinalIgnoreCase) ||
               apn.EndsWith(".ims", StringComparison.OrdinalIgnoreCase);
    }

    public static bool? ParseQuectelImsEnabled(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var fields = ParseFields(line, "+QCFG:");
            if (fields is not { Count: >= 2 } || !fields[0].Equals("ims", StringComparison.OrdinalIgnoreCase)) continue;
            return fields[1] switch { "1" => true, "2" => false, "0" => null, _ => null };
        }
        return null;
    }

    public static string? ParseQuectelUsbNetworkMode(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var fields = ParseFields(line, "+QCFG:");
            if (fields is not { Count: >= 2 } || !fields[0].Equals("usbnet", StringComparison.OrdinalIgnoreCase)) continue;
            return fields[1] switch
            {
                "0" => "RmNet/QMI",
                "1" => "ECM",
                "2" => "MBIM",
                "3" => "RNDIS",
                _ => $"unknown ({fields[1]})"
            };
        }
        return null;
    }

    public static bool? ParseQuectelSimInserted(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var fields = ParseFields(line, "+QSIMSTAT:");
            if (fields is not { Count: >= 2 }) continue;
            return fields[1] switch { "1" => true, "0" => false, _ => null };
        }
        return null;
    }

    public static IReadOnlyDictionary<IPAddress, string[]> GetHostAddresses(string? expectedPppEntry = null)
    {
        var result = new Dictionary<IPAddress, List<string>>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsHostImsDataInterface(nic.Name, nic.Description, nic.NetworkInterfaceType, nic.OperationalStatus))
                    continue;
                if (expectedPppEntry != null && nic.NetworkInterfaceType == NetworkInterfaceType.Ppp &&
                    nic.Name != expectedPppEntry) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(item => item.Address))
                {
                    if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) continue;
                    if (!result.TryGetValue(address, out var names)) result[address] = names = [];
                    if (!names.Contains(nic.Name, StringComparer.OrdinalIgnoreCase)) names.Add(nic.Name);
                }
            }
        }
        catch { }
        return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    public static IReadOnlyList<HostImsPppAddress> GetActiveImsPppAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => IsKnownImsPppInterface(nic.Name, nic.NetworkInterfaceType) &&
                              nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses
                    .Where(item => item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                                   !item.Address.IsIPv6LinkLocal && item.PrefixLength == 64)
                    .Select(item => new HostImsPppAddress(item.Address, item.PrefixLength, nic.Name)))
                .Distinct()
                .ToArray();
        }
        catch { return []; }
    }

    internal static IReadOnlyList<IPAddress> ResolveHostOwnedAddresses(
        IReadOnlyList<IPAddress> modemAddresses,
        IReadOnlyDictionary<IPAddress, string[]> hostAddresses,
        IReadOnlyList<HostImsPppAddress> pppAddresses) =>
        modemAddresses.Where(hostAddresses.ContainsKey)
            .Concat(pppAddresses.Where(ppp => hostAddresses.ContainsKey(ppp.Address) &&
                                              modemAddresses.Any(modem => IsSameImsPppPrefix(modem, ppp)))
                .Select(ppp => ppp.Address))
            .Distinct()
            .ToArray();

    internal static bool IsSameImsPppPrefix(IPAddress modemAddress, HostImsPppAddress ppp)
    {
        if (modemAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            ppp.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            ppp.PrefixLength != 64 || modemAddress.IsIPv6LinkLocal || ppp.Address.IsIPv6LinkLocal)
            return false;
        return modemAddress.GetAddressBytes().AsSpan(0, 8)
            .SequenceEqual(ppp.Address.GetAddressBytes().AsSpan(0, 8));
    }

    public static IReadOnlyList<HostImsWindowsAdapter> GetWindowsCellularAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => LooksLikeCellularAdapter(nic.Name, nic.Description, nic.NetworkInterfaceType))
                .Select(nic => new HostImsWindowsAdapter(nic.Name, nic.Description,
                    nic.OperationalStatus == OperationalStatus.Up))
                .ToArray();
        }
        catch { return []; }
    }

    internal static bool LooksLikeCellularAdapter(string name, string description, NetworkInterfaceType type) =>
        IsKnownImsPppInterface(name, type) ||
        type is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 ||
        name.Contains("cellular", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("cellular", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("mobile broadband", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("quectel", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHostImsDataInterface(
        string name, string description, NetworkInterfaceType type, OperationalStatus status) =>
        status == OperationalStatus.Up &&
        type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
        LooksLikeCellularAdapter(name, description, type);

    private static bool IsKnownImsPppInterface(string name, NetworkInterfaceType type) =>
        type == NetworkInterfaceType.Ppp &&
        HostImsBearerIdentity.IsControlledEntry(name);

    private static IReadOnlyList<IPAddress> ParseAddressRange(IReadOnlyList<string> fields, int start, int count) =>
        Enumerable.Range(start, count)
            .Where(index => index < fields.Count)
            .SelectMany(index => ParseAddresses(fields[index]))
            .Distinct()
            .ToArray();

    internal static IReadOnlyList<IPAddress> ParseAddresses(string? value, bool stripIpv4Mask = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var candidates = value.Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<IPAddress>();
        foreach (var candidate in candidates)
        {
            IPAddress? address = null;
            // Only colon-form IPv6 may use the framework parser. Its IPv4
            // compatibility parser otherwise accepts shorthand, hex and octal,
            // and even turns a /64 prefix into the address 0.0.0.64.
            if (candidate.Contains(':') && !candidate.Contains('%') && IPAddress.TryParse(candidate, out address))
            {
                if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)) result.Add(address);
                continue;
            }

            var octets = candidate.Split('.');
            if (!octets.All(part => part.Length is >= 1 and <= 3 && part.All(char.IsAsciiDigit) &&
                byte.TryParse(part, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out _))) continue;
            var bytes = octets.Select(part => byte.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (stripIpv4Mask && bytes.Length is 8 or 32)
            {
                var half = bytes.Length / 2;
                if (!IsContiguousMask(bytes.AsSpan(half))) continue;
                bytes = bytes[..half];
            }
            if (bytes.Length is 4 or 16)
            {
                address = new IPAddress(bytes);
                if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)) result.Add(address);
            }
        }
        return result.Distinct().ToArray();
    }

    private static bool IsContiguousMask(ReadOnlySpan<byte> mask)
    {
        var zeroSeen = false;
        foreach (var octet in mask)
            for (var bit = 7; bit >= 0; bit--)
                if ((octet & (1 << bit)) == 0) zeroSeen = true;
                else if (zeroSeen) return false;
        return true;
    }

    private static IReadOnlyList<string>? ParseFields(string line, string prefix)
    {
        line = line.Trim();
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var input = line[prefix.Length..].Trim();
        if (input.Length > 4096 || input.Any(char.IsControl)) return null;
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var closed = false;
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '"' && !closed && (quoted || current.ToString().Trim().Length == 0))
            {
                quoted = !quoted;
                if (!quoted) closed = true;
                continue;
            }
            if (ch == '"') return null;
            if (ch == ',' && !quoted)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                closed = false;
                if (fields.Count >= 32) return null;
            }
            else if (closed && !char.IsWhiteSpace(ch)) return null;
            else current.Append(ch);
        }
        if (quoted) return null;
        fields.Add(current.ToString().Trim());
        return fields;
    }
}
