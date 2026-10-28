using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoSharp.HostIms;

// MIB_IPINTERFACE_ROW, Windows SDK netioapi.h, x64. Keep every field so the
// before/after snapshots can verify that a targeted policy write preserves other settings.
[StructLayout(LayoutKind.Sequential)]
internal struct IpInterfaceRow
{
    public ushort Family;
    public ulong Luid;
    public uint Index, MaxReassemblySize;
    public ulong InterfaceIdentifier;
    public uint MinRouterAdvertisementInterval, MaxRouterAdvertisementInterval;
    public byte AdvertisingEnabled, ForwardingEnabled, WeakHostSend, WeakHostReceive,
        UseAutomaticMetric, UseNeighborUnreachabilityDetection, ManagedAddressConfigurationSupported,
        OtherStatefulConfigurationSupported, AdvertiseDefaultRoute;
    public uint RouterDiscoveryBehavior, DadTransmits, BaseReachableTime, RetransmitTime,
        PathMtuDiscoveryTimeout, LinkLocalAddressBehavior, LinkLocalAddressTimeout;
    public uint Zone0, Zone1, Zone2, Zone3, Zone4, Zone5, Zone6, Zone7,
        Zone8, Zone9, Zone10, Zone11, Zone12, Zone13, Zone14, Zone15;
    public uint SitePrefixLength, Metric, NlMtu;
    public byte Connected, SupportsWakeUpPatterns, SupportsNeighborDiscovery, SupportsRouterDiscovery;
    public uint ReachableTime;
    public byte TransmitOffload, ReceiveOffload, DisableDefaultRoutes;
}

internal static class WindowsInterfacePolicy
{
    internal static ulong GetLuid(uint index)
    {
        Check(ConvertInterfaceIndexToLuid(index, out var luid));
        if (luid == 0) throw new InvalidDataException("Windows returned an empty interface LUID.");
        return luid;
    }

    internal static IpInterfaceRow Read(uint index, ulong luid)
    {
        if (index == 0 || luid == 0) throw new ArgumentException("An exact interface index and LUID are required.");
        var row = new IpInterfaceRow { Family = 23, Index = index, Luid = luid };
        Check(GetIpInterfaceEntry(ref row));
        // Windows prefers a supplied LUID to the index. Check both: never write
        // a replacement adapter that happens to reuse the old numeric index.
        if (row.Family != 23 || row.Index != index || row.Luid != luid)
            throw new InvalidOperationException("IMS PPP interface identity changed before policy access.");
        return row;
    }

    internal static void SetDefaultRoutesDisabled(PppEndpoint endpoint, bool disabled)
    {
        var before = Read(endpoint.Index, endpoint.Luid);
        var row = CreateDefaultRoutePolicyUpdate(before, disabled);
        var result = SetIpInterfaceEntry(ref row);
        if (result != 0)
            throw new Win32Exception(checked((int)result),
                $"SetIpInterfaceEntry failed ({result}); family={row.Family}; index={row.Index}; " +
                $"site-prefix={row.SitePrefixLength}; MTU={row.NlMtu}; router-discovery={row.RouterDiscoveryBehavior}; " +
                $"link-local={row.LinkLocalAddressBehavior}; metric={row.Metric}; default-routes-disabled={row.DisableDefaultRoutes}; " +
                $"observed-site-prefix={before.SitePrefixLength}; observed-MTU={before.NlMtu}; observed-metric={before.Metric}. " +
                "Interface addresses and subscriber identities omitted.");
        var after = Read(endpoint.Index, endpoint.Luid);
        if ((after.DisableDefaultRoutes != 0) != disabled)
            throw new InvalidOperationException("Windows did not apply the IMS PPP default-route policy.");
        if (!OtherWritablePolicyMatches(before, after))
            throw new InvalidOperationException("IMS PPP interface settings changed during the targeted default-route policy write; refusing to continue.");
    }

    internal static IpInterfaceRow CreateDefaultRoutePolicyUpdate(IpInterfaceRow observed, bool disabled)
    {
        if (observed.Family != 23 || observed.Index == 0 || observed.Luid == 0)
            throw new ArgumentException("An exact IPv6 interface identity is required for a policy write.");
        // The SDK requires InitializeIpInterfaceEntry before a SET. It supplies
        // unchanged sentinels (BOOLEAN 0xff, enum/metric 0xffffffff, MTU 0, etc.).
        // A GET snapshot is NOT a safe SET payload on RAS: replaying it produced
        // ERROR_INVALID_PARAMETER on the live PPP interface. Do not reconfigure ND,
        // zone identifiers, MTU, metric or other settings merely to change this bit.
        var update = new IpInterfaceRow();
        InitializeIpInterfaceEntry(ref update); // Only initializes local memory; no interface is changed.
        update.Family = observed.Family;
        update.Index = observed.Index;
        update.Luid = observed.Luid;
        update.DisableDefaultRoutes = disabled ? (byte)1 : (byte)0;
        return update;
    }

    internal static bool OtherWritablePolicyMatches(IpInterfaceRow a, IpInterfaceRow b) =>
        a.Family == b.Family && a.Luid == b.Luid && a.Index == b.Index &&
        a.InterfaceIdentifier == b.InterfaceIdentifier && a.AdvertisingEnabled == b.AdvertisingEnabled &&
        a.ForwardingEnabled == b.ForwardingEnabled && a.WeakHostSend == b.WeakHostSend &&
        a.WeakHostReceive == b.WeakHostReceive && a.UseAutomaticMetric == b.UseAutomaticMetric &&
        a.UseNeighborUnreachabilityDetection == b.UseNeighborUnreachabilityDetection &&
        a.ManagedAddressConfigurationSupported == b.ManagedAddressConfigurationSupported &&
        a.OtherStatefulConfigurationSupported == b.OtherStatefulConfigurationSupported &&
        a.AdvertiseDefaultRoute == b.AdvertiseDefaultRoute && a.RouterDiscoveryBehavior == b.RouterDiscoveryBehavior &&
        a.DadTransmits == b.DadTransmits && a.BaseReachableTime == b.BaseReachableTime &&
        a.RetransmitTime == b.RetransmitTime && a.PathMtuDiscoveryTimeout == b.PathMtuDiscoveryTimeout &&
        a.LinkLocalAddressBehavior == b.LinkLocalAddressBehavior && a.LinkLocalAddressTimeout == b.LinkLocalAddressTimeout &&
        a.Zone0 == b.Zone0 && a.Zone1 == b.Zone1 && a.Zone2 == b.Zone2 && a.Zone3 == b.Zone3 &&
        a.Zone4 == b.Zone4 && a.Zone5 == b.Zone5 && a.Zone6 == b.Zone6 && a.Zone7 == b.Zone7 &&
        a.Zone8 == b.Zone8 && a.Zone9 == b.Zone9 && a.Zone10 == b.Zone10 && a.Zone11 == b.Zone11 &&
        a.Zone12 == b.Zone12 && a.Zone13 == b.Zone13 && a.Zone14 == b.Zone14 && a.Zone15 == b.Zone15 &&
        a.SitePrefixLength == b.SitePrefixLength && a.Metric == b.Metric && a.NlMtu == b.NlMtu;

    private static void Check(uint result) { if (result != 0) throw new Win32Exception(checked((int)result)); }
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpInterfaceEntry(ref IpInterfaceRow row);
    [DllImport("iphlpapi.dll")] internal static extern void InitializeIpInterfaceEntry(ref IpInterfaceRow row);
    [DllImport("iphlpapi.dll")] private static extern uint SetIpInterfaceEntry(ref IpInterfaceRow row);
}
