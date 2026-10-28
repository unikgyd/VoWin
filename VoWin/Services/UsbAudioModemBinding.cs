using qmiSharp.Transport;

namespace VoWin.Services;

internal static class UsbAudioModemBinding
{
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    internal static Guid RequireSelected(IEnumerable<WindowsUsbFunction> functions, string port, bool audioExposed)
    {
        var inventory = functions.ToArray();
        var selected = WindowsUsbTopology.SelectPort(inventory, port);
        var modems = inventory.Where(f => f.ClassId == WindowsUsbTopology.ModemClass &&
            f.InstanceId.StartsWith(@"USB\VID_2C7C&", StringComparison.OrdinalIgnoreCase)).Select(f => f.ContainerId).Distinct().ToArray();
        if (!modems.Contains(selected.ContainerId))
            throw new InvalidOperationException("所选 AT 口没有同一物理设备的 Quectel 模组接口。");
        if (!audioExposed) return selected.ContainerId; // QDC507 setup may expose its UAC function later.
        var audio = inventory.Where(f => f.ClassId == MediaClass &&
            f.ContainerId == selected.ContainerId && f.InstanceId.StartsWith(@"USB\VID_2C7C&", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (audio.Length != 1)
            throw new InvalidOperationException("所选模组的 USB 音频接口未唯一就绪，已拒绝猜测配对。");
        return selected.ContainerId;
    }

    internal static void RequireQdc507Runtime(IEnumerable<WindowsUsbFunction> functions, string port, Guid expectedContainer,
        string? expectedImei = null)
    {
        var inventory = functions.ToArray();
        if (RequireSelected(inventory, port, false) != expectedContainer)
            throw new InvalidOperationException("模组 USB 身份已变化，拒绝修改先前语音路由。");
        // Windows' legacy ADB backend has no USB path and serials may all be '?'.
        // Multiple containers are allowed only when the runtime will also prove
        // the selected modem's cached IMEI via a specifically addressed transport.
        var adb = inventory.Where(f => f.InstanceId.StartsWith(@"USB\VID_2C7C&", StringComparison.OrdinalIgnoreCase) &&
            f.Name.Contains("ADB", StringComparison.OrdinalIgnoreCase)).Select(f => f.ContainerId).Distinct().ToArray();
        if (!adb.Contains(expectedContainer) || adb.Length != 1 && !Qdc507AdbIdentity.IsImei(expectedImei))
            throw new InvalidOperationException("QDC507 ADB 语音路由缺少物理接口或 IMEI 身份依据，已拒绝猜测配对。标准 USB Audio、Host IMS 与 VoWiFi 不受此限制。");
    }
}
