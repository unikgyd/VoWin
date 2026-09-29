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
    string Summary);

/// <summary>
/// Read-only Windows route check for a P-CSCF. The source address, selected
/// interface and best route must all agree; address ownership alone is not a
/// usable IMS bearer.
/// </summary>
public static class HostImsRouteVerifier
{
    public static IReadOnlyList<HostImsRouteCheck> Check(
        IReadOnlyList<HostImsEndpointCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!OperatingSystem.IsWindows())
            return candidates.Select(candidate => new HostImsRouteCheck(candidate, false, null, null,
                "Windows IP route verification is unavailable on this platform.")).ToArray();

        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex)
        {
            return candidates.Select(candidate => new HostImsRouteCheck(candidate, false, null, null,
                $"Windows adapters could not be enumerated: {ex.GetType().Name}.")).ToArray();
        }

        return candidates.Select(candidate => Check(candidate, adapters)).ToArray();
    }

    private static HostImsRouteCheck Check(HostImsEndpointCandidate candidate, NetworkInterface[] adapters)
    {
        if (candidate.LocalAddress.AddressFamily != candidate.PcscfAddress.AddressFamily)
            return new(candidate, false, null, null, "IMS address and P-CSCF use different IP families.");

        HostImsRouteCheck? firstFailure = null;
        foreach (var nic in adapters)
        {
            if (!HostImsPdnParser.IsHostImsDataInterface(nic.Name, nic.Description,
                    nic.NetworkInterfaceType, nic.OperationalStatus)) continue;
            try
            {
                var properties = nic.GetIPProperties();
                if (!properties.UnicastAddresses.Any(item => item.Address.Equals(candidate.LocalAddress))) continue;
                var index = candidate.LocalAddress.AddressFamily == AddressFamily.InterNetwork
                    ? properties.GetIPv4Properties()?.Index
                    : properties.GetIPv6Properties()?.Index;
                if (index is not > 0)
                {
                    firstFailure ??= new(candidate, false, nic.Name, null,
                        "The IMS adapter has no usable IP interface index.");
                    continue;
                }

                var route = WindowsRoute.TryGetBestRoute(index.Value,
                    candidate.LocalAddress, candidate.PcscfAddress);
                if (!route.Success)
                {
                    firstFailure ??= new(candidate, false, nic.Name, index,
                        $"Windows has no matching P-CSCF route on this IMS interface (error {route.Error}).");
                    continue;
                }
                if (route.InterfaceIndex != index.Value || !route.SourceAddress!.Equals(candidate.LocalAddress))
                {
                    firstFailure ??= new(candidate, false, nic.Name, index,
                        "Windows would use a different interface or source address for this P-CSCF.");
                    continue;
                }
                return new(candidate, true, nic.Name, index,
                    "Windows selected the IMS interface and IMS source address for the P-CSCF route.");
            }
            catch (Exception ex)
            {
                firstFailure ??= new(candidate, false, nic.Name, null,
                    $"The IMS interface route could not be checked: {ex.GetType().Name}.");
            }
        }
        return firstFailure ?? new(candidate, false, null, null,
            "The IMS address is not assigned to a connected Windows cellular adapter.");
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

        internal static (bool Success, int InterfaceIndex, IPAddress? SourceAddress, uint Error)
            TryGetBestRoute(int interfaceIndex, IPAddress source, IPAddress destination)
        {
            if (interfaceIndex <= 0 || source.AddressFamily != destination.AddressFamily)
                return (false, 0, null, 87);
            var sourceSockaddr = SockaddrInet.FromAddress(source);
            var destinationSockaddr = SockaddrInet.FromAddress(destination);
            var error = GetBestRoute2(IntPtr.Zero, checked((uint)interfaceIndex),
                in sourceSockaddr, in destinationSockaddr, 0, out var row, out var bestSource);
            return error == 0
                ? (true, checked((int)row.InterfaceIndex), bestSource.ToAddress(), 0)
                : (false, 0, null, error);
        }
    }
}
