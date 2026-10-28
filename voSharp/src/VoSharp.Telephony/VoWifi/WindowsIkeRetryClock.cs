using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VoSharp.Telephony.VoWifi;

/// <summary>Windows boot-relative clock, independent of wall-clock corrections.</summary>
public sealed class WindowsIkeRetryClock : IIkeRetryClock
{
    public IkeRetryClockStamp Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows IKE clock requires Windows.");
        // This native information class is not a stable public SDK contract.
        // Refuse a sample on changed/unsupported layouts instead of inventing
        // a boot epoch from wall time or silently forgetting saved restrictions.
        var status = NtQuerySystemInformation(90, out var boot, 32, out var length);
        if (status != 0 || length != 32 || boot.BootIdentifier == Guid.Empty)
            throw new InvalidOperationException("The Windows IKE boot epoch cannot be confirmed.");
        QueryInterruptTime(out var ticks);
        if (ticks > long.MaxValue) throw new InvalidOperationException("Windows IKE clock is out of range.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("VoWin.IKE.boot.v1:"+boot.BootIdentifier.ToString("N")))).ToLowerInvariant();
        return new(fingerprint, (long)ticks);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BootEnvironment { public Guid BootIdentifier; public uint FirmwareType; public ulong BootFlags; }
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, out BootEnvironment information, uint length, out uint returnedLength);
    // On the tested Windows host Kernel32 has no direct export for this API;
    // the documented realtime API-set contract resolves correctly.
    [DllImport("api-ms-win-core-realtime-l1-1-1.dll")]
    private static extern void QueryInterruptTime(out ulong interruptTime);
}
