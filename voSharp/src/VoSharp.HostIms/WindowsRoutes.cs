using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VoSharp.HostIms;

// Layouts from the Windows SDK netioapi.h / ws2ipdef.h (x64).
// IP Helper changes only ActiveStore routes; no PowerShell, CIM or netsh.
[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct SockaddrInet
{
    [FieldOffset(0)] public ushort Family;
    [FieldOffset(8)] public uint A;
    [FieldOffset(12)] public uint B;
    [FieldOffset(16)] public uint C;
    [FieldOffset(20)] public uint D;
    [FieldOffset(24)] public uint Scope;

    public static SockaddrInet From(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) throw new ArgumentException("IPv6 required.");
        var b = ip.GetAddressBytes();
        return new() { Family = 23, A = BinaryPrimitives.ReadUInt32LittleEndian(b),
            B = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)), C = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)),
            D = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)), Scope = checked((uint)ip.ScopeId) };
    }

    public readonly IPAddress Address
    {
        get
        {
            if (Family != 23) throw new InvalidOperationException("Not an IPv6 route.");
            Span<byte> b = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(b, A); BinaryPrimitives.WriteUInt32LittleEndian(b[4..], B);
            BinaryPrimitives.WriteUInt32LittleEndian(b[8..], C); BinaryPrimitives.WriteUInt32LittleEndian(b[12..], D);
            return new IPAddress(b, Scope);
        }
    }
}

[StructLayout(LayoutKind.Explicit, Size = 104)]
internal struct RouteRow
{
    [FieldOffset(0)] public ulong Luid;
    [FieldOffset(8)] public uint Index;
    [FieldOffset(12)] public SockaddrInet Destination;
    [FieldOffset(40)] public byte PrefixLength;
    [FieldOffset(44)] public SockaddrInet NextHop;
    [FieldOffset(72)] public byte SitePrefixLength;
    [FieldOffset(76)] public uint ValidLifetime;
    [FieldOffset(80)] public uint PreferredLifetime;
    [FieldOffset(84)] public uint Metric;
    [FieldOffset(88)] public uint Protocol;
    [FieldOffset(92)] public byte Loopback;
    [FieldOffset(93)] public byte Autoconfigure;
    [FieldOffset(94)] public byte Publish;
    [FieldOffset(95)] public byte Immortal;
    [FieldOffset(96)] public uint Age;
    [FieldOffset(100)] public uint Origin;
}

internal sealed record PppEndpoint(uint Index, IPAddress LocalAddress, ulong Luid);

internal interface IRoutePlatform
{
    PppEndpoint GetPpp();
    IReadOnlyList<RouteRow> Read(uint index);
    void Add(RouteRow row);
    void Delete(RouteRow row);
    RouteRow HostRoute(RouteRow original, IPAddress address);
    void Verify(PppEndpoint endpoint, IPAddress address);
    bool GetDefaultRoutesDisabled(PppEndpoint endpoint);
    void SetDefaultRoutesDisabled(PppEndpoint endpoint, bool disabled);
}

internal sealed class WindowsRoutes(string pppName = WindowsRoutes.PppName) : IRoutePlatform
{
    internal const string PppName = "VoWin IMS PPP Probe";
    public PppEndpoint GetPpp()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.Name == pppName && n.OperationalStatus == OperationalStatus.Up).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("Controlled IMS PPP is not uniquely connected.");
        var properties = candidates[0].GetIPProperties();
        var locals = properties.UnicastAddresses.Where(a => a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred &&
            RouteIsolation.IsUsable(a.Address)).Select(a => a.Address).ToArray();
        if (locals.Length != 1) throw new InvalidOperationException("Expected one preferred IMS PPP IPv6 source.");
        var index = checked((uint)properties.GetIPv6Properties().Index);
        return new(index, locals[0], WindowsInterfacePolicy.GetLuid(index));
    }

    public bool GetDefaultRoutesDisabled(PppEndpoint endpoint) =>
        WindowsInterfacePolicy.Read(endpoint.Index, endpoint.Luid).DisableDefaultRoutes != 0;
    public void SetDefaultRoutesDisabled(PppEndpoint endpoint, bool disabled) =>
        WindowsInterfacePolicy.SetDefaultRoutesDisabled(endpoint, disabled);

    public IReadOnlyList<RouteRow> Read(uint index) => ReadAll().Where(r => r.Index == index).ToArray();
    internal static RouteRow[] ReadAll()
    {
        Check(GetIpForwardTable2(23, out var table));
        try
        {
            var count = Marshal.ReadInt32(table);
            if (count is < 0 or > 65536) throw new InvalidDataException("Invalid Windows route count.");
            var rows = new RouteRow[count];
            for (var i = 0; i < count; i++) rows[i] = Marshal.PtrToStructure<RouteRow>(table + 8 + i * 104);
            return rows;
        }
        finally { FreeMibTable(table); }
    }
    public void Add(RouteRow row) => Check(CreateIpForwardEntry2(ref row));
    public void Delete(RouteRow row) => Check(DeleteIpForwardEntry2(ref row));
    public RouteRow HostRoute(RouteRow original, IPAddress address)
    {
        InitializeIpForwardEntry(out var row);
        row.Luid = original.Luid; row.Index = original.Index;
        row.Destination = SockaddrInet.From(address); row.PrefixLength = 128;
        row.NextHop = original.NextHop; row.Metric = 1; row.Protocol = 3; // MIB_IPPROTO_NETMGMT
        return row;
    }
    public void Verify(PppEndpoint endpoint, IPAddress address)
    {
        var destination = SockaddrInet.From(address);
        var requestedSource = SockaddrInet.From(endpoint.LocalAddress);
        // SIP, ESP and RTP sockets are pinned to this interface. Verify that
        // exact source/interface pair; a second modem may use the same P-CSCF.
        Check(GetBestRoute2(IntPtr.Zero, endpoint.Index, ref requestedSource, ref destination, 0, out var row, out var source));
        if (row.Index != endpoint.Index || row.Luid != endpoint.Luid || row.PrefixLength != 128 || !row.Destination.Address.Equals(address) ||
            !source.Address.Equals(endpoint.LocalAddress))
            throw new InvalidOperationException("Windows did not select the exact IMS PPP source and /128 route.");
    }
    private static void Check(uint code) { if (code != 0) throw new Win32Exception(checked((int)code)); }
    [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void InitializeIpForwardEntry(out RouteRow row);
    [DllImport("iphlpapi.dll")] private static extern uint CreateIpForwardEntry2(ref RouteRow row);
    [DllImport("iphlpapi.dll")] private static extern uint DeleteIpForwardEntry2(ref RouteRow row);
    [DllImport("iphlpapi.dll")] private static extern uint GetBestRoute2(IntPtr luid, uint index, ref SockaddrInet source,
        ref SockaddrInet destination, uint options, out RouteRow route, out SockaddrInet bestSource);
}

internal sealed class RouteIsolation(IRoutePlatform platform, PppEndpoint endpoint, IReadOnlyList<IPAddress> pcscf) : IDisposable
{
    private RouteRow? _original;
    private bool _defaultRemoved;
    private bool _defaultPolicyOwned;
    private readonly Dictionary<IPAddress, RouteRow> _owned = [];
    internal int MediaCount => _owned.Keys.Count(a => !pcscf.Contains(a));
    internal int DefaultRouteRepairCount { get; private set; }
    internal static bool IsUsable(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId == 0 &&
        !ip.IsIPv4MappedToIPv6 && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.IPv6Any);

    internal void Begin()
    {
        if (pcscf.Count is < 1 or > 4 || pcscf.Distinct().Count() != pcscf.Count ||
            pcscf.Any(a => !IsUsable(a) || a.Equals(endpoint.LocalAddress))) throw new InvalidDataException("Invalid P-CSCF endpoints.");
        var defaults = platform.Read(endpoint.Index).Where(r => r.PrefixLength == 0).ToArray();
        if (defaults.Length != 1 || !defaults[0].NextHop.Address.IsIPv6LinkLocal)
            throw new InvalidOperationException("Expected one link-local PPP default; no route was changed.");
        if (defaults[0].Luid != endpoint.Luid || platform.GetPpp() != endpoint)
            throw new InvalidOperationException("IMS PPP interface identity changed before route isolation.");
        _original = defaults[0];
        foreach (var address in pcscf) AddOwned(address);
        // The native policy write itself can remove the automatic default,
        // even if its read-back later fails. Include that in rollback too.
        _defaultRemoved = true;
        // Deleting ::/0 once does not stop a later RA/RAS route update from
        // installing it again. Disable default-route use on this owned PPP
        // interface only; never change Wi-Fi, Ethernet or another modem.
        if (!platform.GetDefaultRoutesDisabled(endpoint))
        {
            _defaultPolicyOwned = true; // Include a partially successful native call in rollback.
            platform.SetDefaultRoutesDisabled(endpoint, true);
        }
        if (!platform.GetDefaultRoutesDisabled(endpoint))
            throw new InvalidOperationException("IMS PPP default-route suppression was not confirmed.");
        defaults = platform.Read(endpoint.Index).Where(r => r.PrefixLength == 0).ToArray();
        // Applying suppression may already remove the automatic default.
        if (defaults.Length > 1 || defaults.Length == 1 && !Same(defaults[0], _original.Value))
            throw new InvalidOperationException("PPP default changed while enabling isolation; refusing deletion.");
        if (defaults.Length == 1) platform.Delete(defaults[0]);
        Check();
    }
    private static bool Same(RouteRow a, RouteRow b) => a.Index == b.Index && a.Luid == b.Luid && a.PrefixLength == b.PrefixLength &&
        a.Destination.Address.Equals(b.Destination.Address) && a.NextHop.Address.Equals(b.NextHop.Address) && a.Metric == b.Metric &&
        a.Protocol == b.Protocol && a.Origin == b.Origin;
    private void AddOwned(IPAddress address)
    {
        if (platform.Read(endpoint.Index).Any(r => r.PrefixLength == 128 && r.Destination.Address.Equals(address)))
            throw new InvalidOperationException("An exact route already exists; refusing to take ownership.");
        var row = platform.HostRoute(_original!.Value, address);
        platform.Add(row); _owned.Add(address, row);
    }
    internal void Check()
    {
        if (platform.GetPpp() != endpoint) throw new InvalidOperationException("IMS PPP interface/source/LUID changed.");
        if (!platform.GetDefaultRoutesDisabled(endpoint))
            throw new InvalidOperationException("IMS PPP default-route suppression was lost.");
        var rows = platform.Read(endpoint.Index);
        var defaults = rows.Where(r => r.PrefixLength == 0).ToArray();
        if (defaults.Length == 1 && _original is { } original && Same(defaults[0], original))
        {
            // RAS/RA may re-publish the exact default we removed. The native
            // policy above already prevents using it. Remove only that known
            // fingerprint, then verify isolation again; never adopt a changed
            // gateway, metric, origin, protocol or interface after reconnect.
            platform.Delete(defaults[0]);
            rows = platform.Read(endpoint.Index);
            defaults = rows.Where(r => r.PrefixLength == 0).ToArray();
            if (platform.GetPpp() != endpoint || !platform.GetDefaultRoutesDisabled(endpoint))
                throw new InvalidOperationException("IMS PPP identity/policy changed during default-route repair.");
            if (defaults.Length == 0) DefaultRouteRepairCount++;
        }
        if (defaults.Length != 0) throw new InvalidOperationException("PPP default route isolation was lost: " +
            $"interface={endpoint.Index}; luid={endpoint.Luid}; " + string.Join(" | ", defaults.Select(r =>
                $"next-hop={r.NextHop.Address}; metric={r.Metric}; protocol={r.Protocol}; origin={r.Origin}; age={r.Age}; lifetime={r.ValidLifetime}")));
        foreach (var (address, row) in _owned)
        {
            if (rows.Count(r => Same(r, row)) != 1) throw new InvalidOperationException("An owned IMS route disappeared or changed.");
            platform.Verify(endpoint, address);
        }
    }
    internal bool AddMedia(IPAddress address)
    {
        Check(); // Isolation errors must terminate the bearer, not become an SDP rejection.
        if (!IsUsable(address) || address.Equals(endpoint.LocalAddress) || (MediaCount >= 8 && !_owned.ContainsKey(address))) return false;
        if (_owned.ContainsKey(address)) return true;
        try { AddOwned(address); platform.Verify(endpoint, address); return true; }
        catch
        {
            if (_owned.ContainsKey(address)) RemoveOwned(address);
            return false;
        }
    }
    private void RemoveOwned(IPAddress address)
    {
        var row = _owned[address];
        var matches = platform.Read(endpoint.Index).Where(r => r.PrefixLength == 128 && r.Destination.Address.Equals(address)).ToArray();
        if (matches.Length != 1 || !Same(matches[0], row)) throw new InvalidOperationException("Owned media route changed; refusing deletion.");
        platform.Delete(row);
        if (platform.Read(endpoint.Index).Any(r => r.PrefixLength == 128 && r.Destination.Address.Equals(address)))
            throw new InvalidOperationException("Owned route remained after deletion.");
        _owned.Remove(address);
    }
    internal void RemoveMedia(IPAddress address)
    {
        Check();
        if (pcscf.Contains(address) || !_owned.ContainsKey(address)) throw new InvalidOperationException("Not an owned media route.");
        RemoveOwned(address);
    }
    public void Dispose()
    {
        // RAS removes routes with the disconnected interface. Never touch a reused index.
        try { if (platform.GetPpp() != endpoint) return; }
        catch (InvalidOperationException) { return; }
        List<Exception> errors = [];
        if (_defaultPolicyOwned)
        {
            try
            {
                // Restore only our one changed policy bit, using a fresh native
                // read so unrelated interface settings are not rolled back.
                if (platform.GetDefaultRoutesDisabled(endpoint)) platform.SetDefaultRoutesDisabled(endpoint, false);
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (_defaultRemoved && _original is { } original)
        {
            try
            {
                var defaults = platform.Read(endpoint.Index).Where(r => r.PrefixLength == 0).ToArray();
                if (defaults.Length == 0) platform.Add(original);
                else if (defaults.Length != 1 || !Same(defaults[0], original)) throw new InvalidOperationException("Unexpected default route during restore.");
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        foreach (var address in _owned.Keys.ToArray())
        {
            try { RemoveOwned(address); } catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0) throw new AggregateException("PPP route restoration failed.", errors);
    }
}
