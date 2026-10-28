using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VoSharp.Telephony.VoWifi;

internal sealed record EpdgRouteResult(bool? Available, IPAddress? Source = null, int? InterfaceIndex = null,
    string Detail = "unknown");
internal sealed record EpdgNetworkPaths(EpdgResolvedEndpoint[] Endpoints, string Summary);

/// <summary>Outer IP reachability only. The IMS PDN inside ESP is independently negotiated.</summary>
internal static class EpdgNetworkPathSelector
{
    internal static EpdgNetworkPaths Select(IReadOnlyList<EpdgResolvedEndpoint> endpoints,
        string? proxyUrl, IPAddress? localAddress = null,
        Func<IPAddress, IPAddress?, EpdgRouteResult>? probe = null, Action<string>? log = null)
    {
        if (!string.IsNullOrWhiteSpace(proxyUrl) && !proxyUrl.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return new(endpoints.ToArray(), "SOCKS5 UDP 中继：ePDG 可用 IPv4 / IPv6，外层按中继路由选择");
        probe ??= QueryWindowsRoute;
        var results = new Dictionary<IPAddress, EpdgRouteResult>();
        foreach (var address in endpoints.Select(endpoint => endpoint.Address).Distinct())
        {
            var route = localAddress != null && localAddress.AddressFamily != address.AddressFamily
                ? new(false, Detail: "incompatible-explicit-local-bind") : probe(address, localAddress);
            results.Add(address, route);
            log?.Invoke($"ePDG outer route: family={address.AddressFamily}; endpoint={address}; available={route.Available?.ToString() ?? "unknown"}; source={route.Source}; interface={route.InterfaceIndex}; detail={route.Detail}.");
        }
        var usable = endpoints.Where(endpoint => results[endpoint.Address].Available != false).ToArray();
        var v4 = usable.Select(endpoint => endpoint.Address).Distinct().Count(address => address.AddressFamily == AddressFamily.InterNetwork);
        var v6 = usable.Select(endpoint => endpoint.Address).Distinct().Count(address => address.AddressFamily == AddressFamily.InterNetworkV6);
        var unknown = results.Values.Any(route => route.Available == null);
        return new(usable, $"自动选路：IPv4 候选 {v4} · IPv6 候选 {v6}" +
            (unknown ? "（部分路径待 IKE 验证）" : "（已检查本机路由）"));
    }

    internal static EpdgRouteResult QueryWindowsRoute(IPAddress destination, IPAddress? source)
    {
        if (!OperatingSystem.IsWindows()) return new(null, Detail: "route-query-unavailable-on-this-platform");
        if (source != null && source.AddressFamily != destination.AddressFamily)
            return new(false, Detail: "incompatible-explicit-local-bind");
        var remote = SockaddrInet.From(destination);
        uint error;
        RouteBuffer route;
        SockaddrInet selected;
        try
        {
            if (source == null)
                error = GetBestRoute2(0, 0, 0, in remote, 0, out route, out selected);
            else
            {
                var local = SockaddrInet.From(source);
                error = GetBestRoute2WithSource(0, 0, in local, in remote, 0, out route, out selected);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { return new(null, Detail: "route-query-unavailable"); }
        if (error != 0) return new(error == 5 ? null : false, Detail: $"GetBestRoute2-error-{error}");
        var address = selected.ToAddress();
        var usable = address != null && address.AddressFamily == destination.AddressFamily && route.InterfaceIndex > 0 &&
            !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) &&
            (!address.IsIPv6LinkLocal || destination.IsIPv6LinkLocal) && (source == null || source.Equals(address));
        return new(usable, address, checked((int)route.InterfaceIndex), usable ? "system-best-route" : "no-usable-source");
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint V4;
        [FieldOffset(8)] public ulong V6Low;
        [FieldOffset(16)] public ulong V6High;
        [FieldOffset(24)] public uint ScopeId;
        public static SockaddrInet From(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return address.AddressFamily == AddressFamily.InterNetwork
                ? new() { Family = 2, V4 = BitConverter.ToUInt32(bytes) }
                : new() { Family = 23, V6Low = BitConverter.ToUInt64(bytes, 0), V6High = BitConverter.ToUInt64(bytes, 8), ScopeId = checked((uint)address.ScopeId) };
        }
        public readonly IPAddress? ToAddress() => Family switch
        {
            2 => new(BitConverter.GetBytes(V4)),
            23 => new(BitConverter.GetBytes(V6Low).Concat(BitConverter.GetBytes(V6High)).ToArray(), ScopeId),
            _ => null
        };
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct RouteBuffer { [FieldOffset(8)] public uint InterfaceIndex; }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestRoute2(nint luid, uint index, nint source, in SockaddrInet destination,
        uint options, out RouteBuffer route, out SockaddrInet selectedSource);
    [DllImport("iphlpapi.dll", EntryPoint = "GetBestRoute2", ExactSpelling = true)]
    private static extern uint GetBestRoute2WithSource(nint luid, uint index, in SockaddrInet source, in SockaddrInet destination,
        uint options, out RouteBuffer route, out SockaddrInet selectedSource);
}
