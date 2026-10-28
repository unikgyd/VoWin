using System.Net.NetworkInformation;
using VoSharp.Common.Utils;
using qmiSharp.Transport;

namespace VoSharp.HostIms;

/// <summary>Selected AT, RAS and WWAN functions must belong to the same physical USB device.</summary>
internal sealed record WindowsModemBinding(Guid ContainerId, string RasDeviceName, Guid NetworkInterfaceId)
{
    internal static WindowsModemBinding Resolve(string atPort) => Select(WindowsUsbTopology.EnumeratePresent(), atPort);

    internal static WindowsModemBinding Select(IEnumerable<WindowsUsbFunction> functions, string atPort)
    {
        var inventory = functions.ToArray();
        var at = WindowsUsbTopology.SelectPort(inventory, atPort);
        if (!at.InstanceId.StartsWith(@"USB\VID_2C7C&", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selected AT port is not a supported Quectel USB device.");
        var paired = inventory.Where(f => f.ContainerId == at.ContainerId).ToArray();
        var modems = paired.Where(f => f.ClassId == WindowsUsbTopology.ModemClass &&
            f.Name.Contains("Quectel USB Modem Port", StringComparison.OrdinalIgnoreCase)).ToArray();
        var networks = paired.Where(f => f.ClassId == WindowsUsbTopology.NetworkClass &&
            f.Name.Contains("Quectel", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (modems.Length != 1 || networks.Length != 1 ||
            !Guid.TryParse(networks[0].NetworkInterfaceId, out var networkId))
            throw new InvalidOperationException("Selected USB modem's RAS/WWAN pairing is missing or ambiguous; no handoff attempted.");
        return new(at.ContainerId, modems[0].Name, networkId);
    }

    internal RasDevice SelectRasDevice(IEnumerable<RasDevice> devices)
    {
        var matches = devices.Where(d => d.Type == "modem" &&
            string.Equals(d.Name, RasDeviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new InvalidOperationException("Selected USB modem does not have one matching RAS device.");
    }

    internal void RequireWwanDisconnected()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n =>
            Guid.TryParse(n.Id, out var id) && id == NetworkInterfaceId).ToArray();
        if (adapters.Length != 1 || adapters[0].OperationalStatus != OperationalStatus.Down)
            throw new InvalidOperationException("Selected modem's WWAN must be disconnected before IMS handoff.");
    }

    internal static string EntryForImeiHash(string hash) => HostImsBearerIdentity.EntryForImeiHash(hash);
}
