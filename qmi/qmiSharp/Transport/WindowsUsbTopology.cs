using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace qmiSharp.Transport;

public sealed record WindowsUsbFunction(string InstanceId, Guid ContainerId, Guid ClassId,
    string Name, string? PortName, string? NetworkInterfaceId, string? QmiControlFile);

/// <summary>Read-only USB function pairing. Never infer a modem from discovery order or a shared friendly name.</summary>
public static class WindowsUsbTopology
{
    public static readonly Guid ModemClass = new("4d36e96d-e325-11ce-bfc1-08002be10318");
    public static readonly Guid NetworkClass = new("4d36e972-e325-11ce-bfc1-08002be10318");
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassId; public uint DevInst; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    public static WindowsUsbFunction SelectPort(IEnumerable<WindowsUsbFunction> functions, string port)
    {
        var found = functions.Where(f => string.Equals(f.PortName, port, StringComparison.OrdinalIgnoreCase)).ToArray();
        return found.Length == 1 && found[0].ContainerId != Guid.Empty ? found[0]
            : throw new InvalidOperationException($"USB identity for {port} is missing or ambiguous.");
    }

    public static IReadOnlyList<WindowsUsbFunction> EnumeratePresent()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var set = SetupDiGetClassDevsW(nint.Zero, "USB", nint.Zero, 6); // PRESENT | ALLCLASSES
        if (set == new nint(-1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var result = new List<WindowsUsbFunction>();
            for (uint index = 0; index < 4096; index++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    if (Marshal.GetLastPInvokeError() == 259) return result;
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                var instance = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceIdW(set, ref info, instance, instance.Capacity, out _)) continue;
                var key = new PropertyKey { Format = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
                var container = new byte[16];
                if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, container, 16, out var bytes, 0) ||
                    type != 13 || bytes != 16) continue; // DEVPROP_TYPE_GUID
                string? Text(uint property)
                {
                    var buffer = new byte[2048];
                    return SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out var valueType, buffer, buffer.Length, out var length) &&
                        valueType == 1 && length is > 0 and <= 2048 ? Encoding.Unicode.GetString(buffer, 0, (int)length).TrimEnd('\0') : null;
                }
                string? port = null, network = null, control = null;
                try
                {
                    using var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instance + @"\Device Parameters");
                    port = parameters?.GetValue("PortName") as string;
                    if (Text(9) is { } driver)
                    {
                        using var settings = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver);
                        network = settings?.GetValue("NetCfgInstanceId") as string;
                        control = settings?.GetValue("QCDeviceControlFile") as string;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                result.Add(new(instance.ToString(), new(container), info.ClassId, Text(12) ?? Text(0) ?? instance.ToString(), port, network, control));
            }
            throw new InvalidDataException("USB inventory exceeds the bounded device count.");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(nint classId, string enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref DeviceInfo info, StringBuilder id, int size, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDevicePropertyW(nint set, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] data, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfo info, uint property, out uint type, byte[] data, int size, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
