using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using qmiSharp.Services;

namespace qmiSharp.Network;

/// <summary>
/// Network configurator for Windows cellular adapters.
/// Handles adapter IP assignment, default route, DNS, and MTU configuration
/// corresponding to QMI WDS connection settings, mirroring libqmi/qmi-network and qmi-go netcfg on Windows.
/// </summary>
public sealed class WindowsNetworkConfigurator
{
    /// <summary>
    /// Finds network interfaces matching Quectel / Qualcomm or cellular WWAN adapters.
    /// </summary>
    public static IReadOnlyList<NetworkInterface> FindCellularInterfaces()
    {
        var list = new List<NetworkInterface>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            string desc = ni.Description.ToLowerInvariant();
            string name = ni.Name.ToLowerInvariant();
            if (desc.Contains("quectel") || desc.Contains("qualcomm") || desc.Contains("gobi") ||
                desc.Contains("cellular") || desc.Contains("wwan") || desc.Contains("mobile") ||
                name.Contains("cellular") || name.Contains("蜂窝"))
            {
                list.Add(ni);
            }
        }
        return list;
    }

    /// <summary>
    /// Configures a network interface with static IPv4, mask, and optional gateway using netsh.
    /// </summary>
    public async Task<bool> SetStaticIpAsync(string interfaceName, IPAddress ip, IPAddress mask, IPAddress? gateway = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(mask);

        string gwArg = gateway != null ? gateway.ToString() : "";
        string args = $"interface ip set address name=\"{interfaceName}\" static {ip} {mask} {gwArg}".Trim();
        var (exitCode, output) = await RunNetshAsync(args, cancellationToken).ConfigureAwait(false);
        return exitCode == 0;
    }

    /// <summary>
    /// Configures DNS servers for the specified interface.
    /// </summary>
    public async Task<bool> SetDnsAsync(string interfaceName, IPAddress primaryDns, IPAddress? secondaryDns = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        ArgumentNullException.ThrowIfNull(primaryDns);

        string args1 = $"interface ip set dns name=\"{interfaceName}\" static {primaryDns}";
        var (exit1, _) = await RunNetshAsync(args1, cancellationToken).ConfigureAwait(false);
        if (exit1 != 0) return false;

        if (secondaryDns != null)
        {
            string args2 = $"interface ip add dns name=\"{interfaceName}\" {secondaryDns} index=2";
            var (exit2, _) = await RunNetshAsync(args2, cancellationToken).ConfigureAwait(false);
            return exit2 == 0;
        }

        return true;
    }

    /// <summary>
    /// Resets the interface to DHCP (clears static IPs and routes).
    /// </summary>
    public async Task<bool> SetDhcpAsync(string interfaceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        var (exit1, _) = await RunNetshAsync($"interface ip set address name=\"{interfaceName}\" dhcp", cancellationToken).ConfigureAwait(false);
        var (exit2, _) = await RunNetshAsync($"interface ip set dns name=\"{interfaceName}\" dhcp", cancellationToken).ConfigureAwait(false);
        return exit1 == 0 && exit2 == 0;
    }

    /// <summary>
    /// Enables or disables the network interface.
    /// </summary>
    public async Task<bool> SetInterfaceStateAsync(string interfaceName, bool enable, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        string state = enable ? "enable" : "disable";
        var (exit, _) = await RunNetshAsync($"interface set interface name=\"{interfaceName}\" admin={state}", cancellationToken).ConfigureAwait(false);
        return exit == 0;
    }

    /// <summary>
    /// Sets the MTU for the specified interface.
    /// </summary>
    public async Task<bool> SetMtuAsync(string interfaceName, int mtu, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        var (exit, _) = await RunNetshAsync($"interface ipv4 set subinterface \"{interfaceName}\" mtu={mtu} store=persistent", cancellationToken).ConfigureAwait(false);
        return exit == 0;
    }

    /// <summary>
    /// Seamlessly applies the current QMI WDS connection settings (IP, subnet, gateway, DNS)
    /// to the specified Windows network interface.
    /// </summary>
    public async Task<bool> ApplyWdsSettingsAsync(string interfaceName, WdsCurrentSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        if (settings.Ipv4Address == null || settings.SubnetMask == null)
        {
            throw new InvalidOperationException("WdsCurrentSettings does not contain valid IPv4 address and subnet mask.");
        }

        bool ipOk = await SetStaticIpAsync(interfaceName, settings.Ipv4Address, settings.SubnetMask, settings.Gateway, cancellationToken).ConfigureAwait(false);
        if (!ipOk) return false;

        if (settings.PrimaryDns != null)
        {
            await SetDnsAsync(interfaceName, settings.PrimaryDns, settings.SecondaryDns, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<(int ExitCode, string Output)> RunNetshAsync(string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);

        return (process.ExitCode, string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}");
    }
}
