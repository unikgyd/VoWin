using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Principal;
using VoSharp.HostIms;

namespace VoWin.Services;

/// <summary>Offline package verification; does not start the kernel, ADB server or a modem.</summary>
internal static class RuntimeSmokeCheck
{
    public static async Task RunAsync(string reportPath)
    {
        await BundledRuntime.PrepareAsync().ConfigureAwait(false);
        if (!BundledRuntime.IsAvailable) throw new InvalidOperationException("This executable has no embedded runtime bundle.");
        var owner = BundledRuntime.FindHostIms("VoSharp.HostIms.exe") ?? throw new FileNotFoundException("Managed IMS owner missing.");
        await VoSharp.Modem.HostImsOwnerCompatibility.VerifyAsync(owner).ConfigureAwait(false);
        var managedResult = ManagedRuntimeDiagnostics.VerifyReadOnly();
        using var identityHelper = Qdc507AdbIdentity.OpenHelper(); // Embedded bytes only; never run the modem helper here.
        if (BundledRuntime.Find("host-ims/RasImsPppProbe.exe") != null)
            throw new InvalidDataException("The removed native RAS helper must not be packaged.");
        var native = NativeLibrary.Load(BundledRuntime.FindHostIms("WinDivert.dll")!);
        try
        {
            foreach (var export in new[] { "WinDivertOpen", "WinDivertRecv", "WinDivertSend" })
                _ = NativeLibrary.GetExport(native, export);
        }
        finally { NativeLibrary.Free(native); } // No WinDivertOpen: do not install/start a driver.
        var adbMetadata = BundledRuntime.Find("adb/source.properties") ?? throw new FileNotFoundException("ADB component metadata missing.");
        var adbVersion = (await File.ReadAllLinesAsync(adbMetadata).ConfigureAwait(false)).FirstOrDefault(line => line.StartsWith("Pkg.Revision", StringComparison.Ordinal)) ?? "unknown";
        await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(report, new
        {
            Success = true, Framework = RuntimeInformation.FrameworkDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeRoot = BundledRuntime.Root, ManagedImsOwner = managedResult,
            UsesSystemDotNet = true, IncludesPowerShell = false,
            AdministratorToken = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            PortableAdb = adbVersion, WinDivertLibraryLoaded = true,
            HasModemDrivers = BundledRuntime.HasModemDrivers,
            HasWinDivert = BundledRuntime.FindHostIms("WinDivert64.sys") != null,
            HostImsOwnerProtocolVerified = true,
            HasAdb = BundledRuntime.Find("adb/adb.exe") != null,
            HasVoiceRuntime = BundledRuntime.Find("voice/qdc507_voice.ko") != null,
            QdcIdentityHelperVerified = true, QdcIdentityHelperBytes = identityHelper.Length,
            ModemAccessed = false, ChildProcessesStarted = true,
            ReadOnlyChildProcessesStarted = 1, BearerProcessesStarted = false
        }, new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
    }
}
