using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoWin.Services;

internal sealed record WindowsWaveEndpoint(int DeviceNumber, bool Capture, string EndpointId,
    Guid ContainerId, string Name);

/// <summary>
/// Resolves WinMM indices through their Core Audio endpoint IDs, not truncated
/// product names or enumeration order. Indices are always resolved afresh in
/// the audio helper after the modem has republished its USB audio function.
/// </summary>
internal static class WindowsUsbAudioEndpoints
{
    private const uint QueryFunctionInstanceId = 0x800 + 17; // mmddk.h: DRV_RESERVED + 17
    private const uint QueryFunctionInstanceIdSize = 0x800 + 18;
    private static readonly PropertyKey ContainerKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    internal static IReadOnlyList<WindowsWaveEndpoint> Enumerate()
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<WindowsWaveEndpoint>();
        foreach (var capture in new[] { true, false })
        {
            var count = capture ? WaveIn.DeviceCount : WaveOut.DeviceCount;
            for (var index = 0; index < count; index++)
            {
                var name = capture ? WaveIn.GetCapabilities(index).ProductName : WaveOut.GetCapabilities(index).ProductName;
                var id = QueryEndpointId(index, capture);
                var container = Guid.Empty;
                if (id != null)
                {
                    try
                    {
                        using var device = enumerator.GetDevice(id);
                        if (device.Properties.Contains(ContainerKey) && device.Properties[ContainerKey].Value is Guid value)
                            container = value;
                    }
                    catch (Exception ex) when (ex is COMException or ArgumentException) { }
                }
                endpoints.Add(new(index, capture, id ?? string.Empty, container, name));
            }
        }
        return endpoints;
    }

    internal static (WindowsWaveEndpoint Capture, WindowsWaveEndpoint Playback) Select(
        IEnumerable<WindowsWaveEndpoint> endpoints, Guid containerId)
    {
        if (containerId == Guid.Empty) throw new ArgumentException("USB audio requires a physical device identity.", nameof(containerId));
        var matches = endpoints.Where(e => e.ContainerId == containerId && !string.IsNullOrEmpty(e.EndpointId) && e.DeviceNumber >= 0).ToArray();
        var capture = matches.Where(e => e.Capture).ToArray();
        var playback = matches.Where(e => !e.Capture).ToArray();
        if (capture.Length != 1 || playback.Length != 1)
            throw new InvalidOperationException($"所选模组的 USB 音频端点尚未唯一就绪（输入 {capture.Length}，输出 {playback.Length}），不会使用其他模组的端点。");
        return (capture[0], playback[0]);
    }

    internal static int SelectHost(IEnumerable<WindowsWaveEndpoint> endpoints, IReadOnlySet<Guid> modemContainers, bool capture) =>
        endpoints.Where(e => e.Capture == capture && e.ContainerId != Guid.Empty &&
            !modemContainers.Contains(e.ContainerId) && !string.IsNullOrEmpty(e.EndpointId) &&
            !e.Name.Contains("AC Interface", StringComparison.OrdinalIgnoreCase) && !e.Name.Contains("Quectel", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.DeviceNumber).DefaultIfEmpty(-1).First();

    private static string? QueryEndpointId(int deviceNumber, bool capture)
    {
        uint Message(uint message, nint p1, nuint p2) => capture
            ? waveInMessage((nint)deviceNumber, message, p1, p2)
            : waveOutMessage((nint)deviceNumber, message, p1, p2);
        var sizePointer = Marshal.AllocHGlobal(nint.Size);
        try
        {
            Marshal.WriteIntPtr(sizePointer, nint.Zero);
            if (Message(QueryFunctionInstanceIdSize, sizePointer, 0) != 0) return null;
            var bytes = (long)Marshal.ReadIntPtr(sizePointer);
            if (bytes is < 4 or > 65536 || bytes % 2 != 0) return null;
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                // An invalid response must not let PtrToStringUni scan beyond the buffer.
                Marshal.Copy(new byte[(int)bytes], 0, buffer, (int)bytes);
                if (Message(QueryFunctionInstanceId, buffer, (nuint)bytes) != 0) return null;
                var value = Marshal.PtrToStringUni(buffer, (int)bytes / 2)!;
                var end = value.IndexOf('\0');
                return end > 0 ? value[..end] : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Marshal.FreeHGlobal(sizePointer); }
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInMessage(nint device, uint message, nint parameter1, nuint parameter2);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutMessage(nint device, uint message, nint parameter1, nuint parameter2);
}
