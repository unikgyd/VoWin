using System.Net;
using System.Net.Sockets;

namespace VoSharp.Sip;

/// <summary>Pin one Windows UDP socket to an already verified outgoing interface.</summary>
public static class WindowsUnicastInterface
{
    public static void Pin(Socket socket, int interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (interfaceIndex <= 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows interface pinning is required for Host IMS.");

        // ws2ipdef.h: IP_UNICAST_IF and IPV6_UNICAST_IF are both 31.
        // IPv4 sets in network byte order; IPv6 sets in host byte order.
        const SocketOptionName unicastInterface = (SocketOptionName)31;
        var level = socket.AddressFamily switch
        {
            AddressFamily.InterNetwork => SocketOptionLevel.IP,
            AddressFamily.InterNetworkV6 => SocketOptionLevel.IPv6,
            _ => throw new ArgumentException("Only IPv4 and IPv6 sockets can be pinned.", nameof(socket))
        };
        var setting = level == SocketOptionLevel.IPv6
            ? BitConverter.GetBytes(interfaceIndex)
            : BitConverter.GetBytes(IPAddress.HostToNetworkOrder(interfaceIndex));
        socket.SetSocketOption(level, unicastInterface, setting);
        var actual = socket.GetSocketOption(level, unicastInterface, sizeof(int));
        if (actual is not { Length: sizeof(int) } || BitConverter.ToInt32(actual) != interfaceIndex)
            throw new InvalidOperationException("Windows did not retain the selected IMS outgoing interface.");
    }
}
