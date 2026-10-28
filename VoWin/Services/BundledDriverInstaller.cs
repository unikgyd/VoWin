using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VoWin.Services;

public static class BundledDriverInstaller
{
    public static async Task<string> InstallAsync(CancellationToken ct = default)
    {
        if (!VoKernelService.HasAdministratorToken())
            throw new InvalidOperationException("安装模组驱动需要管理员权限，请先以管理员身份重启。");
        // Revalidate the embedded payload before launching any elevated installer.
        await BundledRuntime.PrepareAsync(ct: ct).ConfigureAwait(false);
        if (!BundledRuntime.HasModemDrivers)
            throw new InvalidOperationException("此发布包未内置厂商驱动，请安装模组厂商提供的签名驱动。");
        var reboot = false;
        var files = Directory.GetFiles(Path.Combine(BundledRuntime.Root!, "drivers"), "*.inf", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            // Flags=0 retains a better installed driver; NeedReboot prevents automatic restart prompts.
            // Once started, do not interrupt a PnP operation because navigation cancels the caller.
            reboot |= await Task.Run(() =>
            {
                if (!DiInstallDriverW(IntPtr.Zero, file, 0, out var needsReboot))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"驱动安装失败：{Path.GetFileName(file)}");
                return needsReboot;
            }).ConfigureAwait(false);
        }
        return reboot ? "驱动已安装，需要重启 Windows；请先保存工作，软件不会自动重启电脑。"
            : "内置模组驱动已交给 Windows 安装；已有更合适驱动时会保留它。请重新扫描设备。";
    }
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DiInstallDriverW(IntPtr parent, string infPath, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool needsReboot);
}
