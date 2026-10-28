using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VoSharp.HostIms;

[SupportedOSPlatform("windows")]
public static class ManagedRuntimeDiagnostics
{
    public static object VerifyReadOnly()
    {
        var routes = WindowsRoutes.ReadAll();
        if (Marshal.SizeOf<RouteRow>() != 104 || Marshal.SizeOf<IpInterfaceRow>() != 168 ||
            Marshal.OffsetOf<IpInterfaceRow>(nameof(IpInterfaceRow.DisableDefaultRoutes)).ToInt32() != 166 ||
            Marshal.SizeOf<RasDevice>() != 296 || Marshal.SizeOf<RasConnection>() != 1388 ||
            Marshal.SizeOf<RasStatus>() != 608 || Marshal.SizeOf<RasDialParameters>() != 2120)
            throw new InvalidDataException("Windows RAS / IP Helper ABI verification failed.");
        if (routes.Any(r => r.Destination.Family != 23 || r.NextHop.Family != 23 || r.PrefixLength > 128))
            throw new InvalidDataException("Windows route enumeration verification failed.");
        var interfaceCount = 0;
        foreach (var route in routes.DistinctBy(r => r.Luid))
        {
            _ = WindowsInterfacePolicy.Read(route.Index, route.Luid);
            interfaceCount++;
        }
        WindowsRecoveryTasks.VerifyReadOnly();
        return new { Success = true, ManagedOwner = true, ManagedRas = true, ManagedTaskScheduler = true,
            Framework = RuntimeInformation.FrameworkDescription, Ipv6RouteCount = routes.Length,
            Ipv6InterfacePolicyCount = interfaceCount,
            RasDeviceCount = WindowsRas.Devices().Length, RasConnectionCount = WindowsRas.Connections().Length,
            ModemAccessed = false, RoutesChanged = false, ChildProcessesStarted = false };
    }
}
