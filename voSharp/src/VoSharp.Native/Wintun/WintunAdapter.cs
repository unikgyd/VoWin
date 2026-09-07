using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using VoSharp.Native.Interop;

namespace VoSharp.Native.Wintun;

/// <summary>
/// Manages a Wintun virtual network adapter lifecycle and IP address assignment.
/// Creates a Layer 3 TUN interface that the OS routes packets through.
/// </summary>
public class WintunAdapter : IDisposable
{
    private IntPtr _adapter;
    private bool _disposed;

    public string Name { get; }
    public Guid AdapterGuid { get; }
    public long Luid { get; private set; }
    public string? AssignedIp { get; private set; }
    public int PrefixLength { get; private set; }

    private WintunAdapter(string name, Guid guid, IntPtr adapter)
    {
        Name = name;
        AdapterGuid = guid;
        _adapter = adapter;

        WintunInterop.WintunGetAdapterLUID(adapter, out var luid);
        Luid = luid;
    }

    /// <summary>
    /// Creates a new Wintun TUN adapter with the given name.
    /// Requires Administrator privileges.
    /// </summary>
    /// <param name="name">Adapter name (appears in Network Connections).</param>
    /// <param name="tunnelType">Tunnel type label.</param>
    /// <param name="stableGuid">Optional fixed GUID for consistent adapter identity across reboots.</param>
    public static WintunAdapter Create(
        string name = "VoSharp-VoWiFi",
        string tunnelType = "VoSharp VoWiFi Tunnel",
        Guid? stableGuid = null)
    {
        NativeLibLoader.EnsureInitialized();

        IntPtr adapter;
        var guid = stableGuid ?? Guid.NewGuid();

        adapter = WintunInterop.WintunCreateAdapter(name, tunnelType, ref guid);
        if (adapter == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"WintunCreateAdapter failed (error {err}). " +
                "Ensure the application is running as Administrator and wintun.dll is present.");
        }

        return new WintunAdapter(name, guid, adapter);
    }

    /// <summary>
    /// Opens an existing Wintun adapter by name.
    /// </summary>
    public static WintunAdapter? Open(string name = "VoSharp-VoWiFi")
    {
        NativeLibLoader.EnsureInitialized();

        var adapter = WintunInterop.WintunOpenAdapter(name);
        if (adapter == IntPtr.Zero)
            return null;

        var guid = Guid.Empty; // can't retrieve GUID from open
        return new WintunAdapter(name, guid, adapter);
    }

    /// <summary>
    /// Assigns an IPv4 address to this adapter using netsh.
    /// This is the tunnel IP assigned by the IKEv2 Configuration Payload.
    /// </summary>
    public async Task AssignIpAddressAsync(string ipAddress, int prefixLen = 32, CancellationToken ct = default)
    {
        AssignedIp = ipAddress;
        PrefixLength = prefixLen;

        // Use netsh to assign IP to the adapter
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface ip set address name=\"{Name}\" static {ipAddress} 255.255.255.255",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (process != null)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds a route so that traffic destined for a given IP goes through this Wintun adapter.
    /// Used to route P-CSCF traffic through the IPsec tunnel.
    /// </summary>
    public async Task AddRouteAsync(string destination, int prefixLen = 32, CancellationToken ct = default)
    {
        var mask = prefixLen == 32 ? "255.255.255.255" : PrefixToMask(prefixLen);

        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "route",
            Arguments = $"add {destination} mask {mask} {AssignedIp} IF {GetInterfaceIndex()}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        });

        if (process != null)
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Starts a packet I/O session on this adapter.</summary>
    public WintunSession StartSession(uint capacity = WintunInterop.DefaultCapacity)
    {
        if (_adapter == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WintunAdapter));

        return WintunSession.Start(_adapter, capacity);
    }

    /// <summary>
    /// Returns the Windows interface index for this adapter (used for route commands).
    /// </summary>
    public int GetInterfaceIndex()
    {
        try
        {
            var iface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name == Name || n.Description.Contains("Wintun"));
            if (iface != null)
            {
                var ipProps = iface.GetIPProperties();
                return ipProps.GetIPv4Properties()?.Index ?? 0;
            }
        }
        catch { }
        return 0;
    }

    private static string PrefixToMask(int prefixLen)
    {
        uint mask = prefixLen == 0 ? 0 : uint.MaxValue << (32 - prefixLen);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }

    public void Dispose()
    {
        if (!_disposed && _adapter != IntPtr.Zero)
        {
            _disposed = true;
            WintunInterop.WintunCloseAdapter(_adapter);
            _adapter = IntPtr.Zero;
        }
    }
}
