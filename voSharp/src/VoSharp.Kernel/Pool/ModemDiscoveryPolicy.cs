using qmiSharp.Transport;

namespace VoSharp.Kernel.Pool;

/// <summary>Protect only the owned physical USB device, never discovery order or a friendly name.</summary>
internal static class ModemDiscoveryPolicy
{
    internal static string[] SelectPorts(IEnumerable<string> ports, IReadOnlyList<WindowsUsbFunction> inventory,
        IEnumerable<(string Port, Guid? Container)> owners)
    {
        var owned = owners.ToArray();
        var protectedContainers = new HashSet<Guid>();
        foreach (var owner in owned)
        {
            var container = owner.Container ?? FindContainer(inventory, owner.Port);
            if (container is null || container == Guid.Empty) return []; // Unknown ownership: do not guess.
            protectedContainers.Add(container.Value);
        }
        var result = new List<string>();
        foreach (var port in ports.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (owned.Any(owner => owner.Port.Equals(port, StringComparison.OrdinalIgnoreCase))) continue;
            var container = FindContainer(inventory, port);
            if (protectedContainers.Count != 0 && (container is null || protectedContainers.Contains(container.Value))) continue;
            // Quectel exposes NMEA/DM/data ports beside AT. Opening all siblings
            // concurrently can interrupt the very SIM initialization being probed.
            if (container is not null)
            {
                var atPorts = inventory.Where(function => function.ContainerId == container &&
                    function.PortName is not null && function.Name.Contains(" AT ", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (atPorts.Length != 0 && !atPorts.Any(function => function.PortName!.Equals(port, StringComparison.OrdinalIgnoreCase))) continue;
            }
            result.Add(port);
        }
        return result.ToArray();
    }

    internal static Guid? FindContainer(IReadOnlyList<WindowsUsbFunction> inventory, string port)
    {
        var containers = inventory.Where(function => function.PortName?.Equals(port, StringComparison.OrdinalIgnoreCase) == true)
            .Select(function => function.ContainerId).Distinct().ToArray();
        return containers.Length == 1 && containers[0] != Guid.Empty ? containers[0] : null;
    }
}
