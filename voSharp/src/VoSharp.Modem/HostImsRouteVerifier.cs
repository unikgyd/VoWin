using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VoSharp.Modem;

public sealed record HostImsRouteCheck(
    HostImsEndpointCandidate Endpoint,
    bool IsVerified,
    string? InterfaceName,
    int? InterfaceIndex,
    string Summary)
{
    public ulong? InterfaceLuid { get; init; }
}

internal sealed record HostImsRouteAdapter(string Name, int InterfaceIndex, IReadOnlyList<IPAddress> Addresses);
internal sealed record HostImsRouteObservation(bool Success, int InterfaceIndex,
    IPAddress? SourceAddress, uint Error, ulong InterfaceLuid = 0);

/// <summary>
/// Read-only Windows route check for a P-CSCF. The source address, selected
/// interface and best route must all agree; address ownership alone is not a
/// usable IMS bearer.
/// </summary>
public static class HostImsRouteVerifier
{
    public static IReadOnlyList<HostImsRouteCheck> Check(
        IReadOnlyList<HostImsEndpointCandidate> candidates,
        int? expectedInterfaceIndex = null, ulong? expectedInterfaceLuid = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (expectedInterfaceIndex is <= 0 || expectedInterfaceLuid is 0 ||
            expectedInterfaceLuid is not null && expectedInterfaceIndex is null)
            throw new ArgumentException("An IMS interface owner must have a positive index and optional nonzero LUID.");
        if (!OperatingSystem.IsWindows())
            return candidates.Select(candidate => new HostImsRouteCheck(candidate, false, null, null,
                "Windows IP route verification is unavailable on this platform.")).ToArray();

        IReadOnlyList<HostImsRouteAdapter> adapters;
        try { adapters = ReadAdapters(candidates); }
        catch (Exception ex)
        {
            return candidates.Select(candidate => new HostImsRouteCheck(candidate, false, null, null,
                $"Windows adapters could not be enumerated: {ex.GetType().Name}.")).ToArray();
        }

        return candidates.Select(candidate => CheckWithRoutes(candidate, adapters,
            WindowsRoute.TryGetBestRoute, expectedInterfaceIndex, expectedInterfaceLuid)).ToArray();
    }

    /// <summary>Raw injection is not socket-pinned: prove the unconstrained Windows route too.</summary>
    public static HostImsRouteCheck CheckUnpinnedOutput(HostImsEndpointCandidate candidate,
        int expectedInterfaceIndex, ulong? expectedInterfaceLuid = null)
    {
        if (expectedInterfaceIndex <= 0 || expectedInterfaceLuid is 0)
            throw new ArgumentException("Raw IMS output requires an explicit interface owner.");
        if (!OperatingSystem.IsWindows())
            return new(candidate, false, null, null, "Windows IP route verification is unavailable on this platform.");
        try
        {
            return CheckWithRoutes(candidate, ReadAdapters([candidate]), WindowsRoute.TryGetBestRoute,
                expectedInterfaceIndex, expectedInterfaceLuid, requireGlobalBest: true);
        }
        catch (Exception ex)
        {
            return new(candidate, false, null, null, $"Windows raw-output route could not be checked: {ex.GetType().Name}.");
        }
    }

    private static IReadOnlyList<HostImsRouteAdapter> ReadAdapters(IReadOnlyList<HostImsEndpointCandidate> candidates)
    {
        var adapters = new List<HostImsRouteAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!HostImsPdnParser.IsHostImsDataInterface(nic.Name, nic.Description,
                    nic.NetworkInterfaceType, nic.OperationalStatus)) continue;
            var properties = nic.GetIPProperties();
            var addresses = properties.UnicastAddresses.Select(item => item.Address).ToArray();
            foreach (var family in candidates.Select(candidate => candidate.LocalAddress.AddressFamily).Distinct())
            {
                var index = family == AddressFamily.InterNetwork
                    ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
                if (index is > 0)
                    adapters.Add(new(nic.Name, index.Value, addresses.Where(address => address.AddressFamily == family).ToArray()));
            }
        }
        return adapters;
    }

    // Pure route oracle for deterministic overlap/owner/reuse tests. Production
    // callers always use the native inventory and GetBestRoute2 above.
    internal static HostImsRouteCheck CheckWithRoutes(HostImsEndpointCandidate candidate,
        IReadOnlyList<HostImsRouteAdapter> adapters,
        Func<int, IPAddress, IPAddress, HostImsRouteObservation> lookup,
        int? expectedInterfaceIndex = null, ulong? expectedInterfaceLuid = null,
        bool requireGlobalBest = false)
    {
        if (!HostImsPdnParser.IsUsableEndpointAddress(candidate.LocalAddress) ||
            !HostImsPdnParser.IsUsableEndpointAddress(candidate.PcscfAddress) ||
            candidate.LocalAddress.Equals(candidate.PcscfAddress))
            return new(candidate, false, null, null, "IMS endpoints must be distinct usable unicast addresses.");
        if (candidate.LocalAddress.AddressFamily != candidate.PcscfAddress.AddressFamily)
            return new(candidate, false, null, null, "IMS address and P-CSCF use different IP families.");

        var owners = adapters.Where(adapter => adapter.Addresses.Contains(candidate.LocalAddress)).ToArray();
        if (expectedInterfaceIndex is null && owners.Length > 1)
            return new(candidate, false, null, null, "The IMS source address belongs to multiple interfaces; an explicit bearer owner is required.");
        if (expectedInterfaceIndex is { } expected)
            owners = owners.Where(adapter => adapter.InterfaceIndex == expected).ToArray();
        if (owners.Length != 1)
            return new(candidate, false, null, expectedInterfaceIndex, "The IMS address is not uniquely assigned to the selected bearer interface.");
        var owner = owners[0];
        try
        {
            var route = lookup(owner.InterfaceIndex, candidate.LocalAddress, candidate.PcscfAddress);
            if (!route.Success || route.InterfaceIndex != owner.InterfaceIndex ||
                !candidate.LocalAddress.Equals(route.SourceAddress) ||
                expectedInterfaceLuid is { } luid && route.InterfaceLuid != luid)
                return new(candidate, false, owner.Name, owner.InterfaceIndex,
                    "Windows did not select the owned IMS interface, LUID and source for this endpoint.");
            if (requireGlobalBest)
            {
                var actual = lookup(0, candidate.LocalAddress, candidate.PcscfAddress);
                if (!actual.Success || actual.InterfaceIndex != owner.InterfaceIndex ||
                    actual.InterfaceLuid != route.InterfaceLuid || !candidate.LocalAddress.Equals(actual.SourceAddress))
                    return new(candidate, false, owner.Name, owner.InterfaceIndex,
                        "The unpinned Windows output route does not select the owned IMS bearer.");
            }
            return new(candidate, true, owner.Name, owner.InterfaceIndex,
                "Windows selected the owned IMS interface and source address for this endpoint.")
                { InterfaceLuid = route.InterfaceLuid };
        }
        catch (Exception ex)
        {
            return new(candidate, false, owner.Name, owner.InterfaceIndex,
                $"The IMS interface route could not be checked: {ex.GetType().Name}.");
        }
    }

    internal static class WindowsRoute
    {
        // SOCKADDR_INET is the 28-byte union of SOCKADDR_IN and SOCKADDR_IN6.
        [StructLayout(LayoutKind.Explicit, Size = 28)]
        private struct SockaddrInet
        {
            [FieldOffset(0)] public ushort Family;
            [FieldOffset(4)] public uint Ipv4;
            [FieldOffset(8)] public ulong Ipv6Low;
            [FieldOffset(16)] public ulong Ipv6High;
            [FieldOffset(24)] public uint ScopeId;

            public static SockaddrInet FromAddress(IPAddress address)
            {
                var bytes = address.GetAddressBytes();
                return address.AddressFamily switch
                {
                    AddressFamily.InterNetwork => new SockaddrInet
                    {
                        Family = 2, Ipv4 = BitConverter.ToUInt32(bytes)
                    },
                    AddressFamily.InterNetworkV6 => new SockaddrInet
                    {
                        Family = 23,
                        Ipv6Low = BitConverter.ToUInt64(bytes, 0),
                        Ipv6High = BitConverter.ToUInt64(bytes, 8),
                        ScopeId = checked((uint)address.ScopeId)
                    },
                    _ => throw new ArgumentException("Only IPv4 and IPv6 routes are supported.", nameof(address))
                };
            }

            public IPAddress? ToAddress() => Family switch
            {
                2 => new IPAddress(BitConverter.GetBytes(Ipv4)),
                23 => new IPAddress(BitConverter.GetBytes(Ipv6Low)
                    .Concat(BitConverter.GetBytes(Ipv6High)).ToArray(), ScopeId),
                _ => null
            };
        }

        // The InterfaceIndex follows the 8-byte NET_LUID in MIB_IPFORWARD_ROW2.
        // Oversize the output buffer so the OS can safely write the complete row
        // without depending on managed padding of its remaining members.
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct RouteRowBuffer
        {
            [FieldOffset(0)] public ulong InterfaceLuid;
            [FieldOffset(8)] public uint InterfaceIndex;
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetBestRoute2(
            IntPtr interfaceLuid,
            uint interfaceIndex,
            in SockaddrInet sourceAddress,
            in SockaddrInet destinationAddress,
            uint addressSortOptions,
            out RouteRowBuffer bestRoute,
            out SockaddrInet bestSourceAddress);

        internal static HostImsRouteObservation
            TryGetBestRoute(int interfaceIndex, IPAddress source, IPAddress destination)
        {
            if (interfaceIndex < 0 || source.AddressFamily != destination.AddressFamily)
                return new(false, 0, null, 87);
            var sourceSockaddr = SockaddrInet.FromAddress(source);
            var destinationSockaddr = SockaddrInet.FromAddress(destination);
            var error = GetBestRoute2(IntPtr.Zero, checked((uint)interfaceIndex),
                in sourceSockaddr, in destinationSockaddr, 0, out var row, out var bestSource);
            return error == 0
                ? new(true, checked((int)row.InterfaceIndex), bestSource.ToAddress(), 0, row.InterfaceLuid)
                : new(false, 0, null, error);
        }
    }
}
