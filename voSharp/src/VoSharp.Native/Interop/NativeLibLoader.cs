using System.Runtime.InteropServices;

namespace VoSharp.Native.Interop;

/// <summary>
/// Resolves and loads architecture-specific native DLLs at startup.
/// Supports amd64 and arm64 layouts in the native/ subdirectory.
/// </summary>
public static class NativeLibLoader
{
    private static bool _initialized;
    private static readonly object _lock = new();

    public static string NativeDir { get; private set; } = string.Empty;

    /// <summary>
    /// Must be called once before any P/Invoke to wintun.dll.
    /// Adds the architecture-appropriate subdirectory to the DLL search path.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64   => "amd64",
                Architecture.Arm64 => "arm64",
                _                  => throw new PlatformNotSupportedException(
                    $"VoSharp.Native: unsupported architecture {RuntimeInformation.OSArchitecture}. " +
                    "Only x64 (amd64) and ARM64 are supported.")
            };

            // Look for native/ relative to this assembly's directory
            var baseDir = Path.GetDirectoryName(typeof(NativeLibLoader).Assembly.Location)
                          ?? AppContext.BaseDirectory;

            string[] candidates =
            [
                Path.Combine(baseDir, "native", arch),
                Path.Combine(AppContext.BaseDirectory, "native", arch),
                Path.Combine(baseDir, "native"),
                Path.Combine(AppContext.BaseDirectory, "native"),
                Path.Combine(baseDir, "..", "..", "native", arch),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "native", arch),
                baseDir,
                AppContext.BaseDirectory
            ];

            var nativeDir = candidates.FirstOrDefault(d => Directory.Exists(d) && File.Exists(Path.Combine(d, "wintun.dll")))
                            ?? candidates.FirstOrDefault(Directory.Exists);

            if (nativeDir == null || !Directory.Exists(nativeDir))
            {
                throw new DllNotFoundException(
                    $"VoSharp.Native: native DLL directory not found.\n" +
                    "Run 'download-native.ps1' to download Wintun binaries.");
            }

            NativeDir = nativeDir;

            // Add to DLL search path so P/Invoke can find wintun.dll
            NativeLibrary.SetDllImportResolver(
                typeof(NativeLibLoader).Assembly,
                (libraryName, _, _) =>
                {
                    var candidate = Path.Combine(nativeDir, libraryName);
                    if (NativeLibrary.TryLoad(candidate, out var handle))
                        return handle;
                    // Also try with .dll extension
                    candidate = Path.Combine(nativeDir, libraryName + ".dll");
                    if (NativeLibrary.TryLoad(candidate, out handle))
                        return handle;
                    return IntPtr.Zero;
                });

            _initialized = true;
        }
    }

    /// <summary>
    /// Returns the full path of a native binary (e.g., charon-svc.exe).
    /// </summary>
    public static string GetNativePath(string filename) =>
        Path.Combine(NativeDir, filename);

    /// <summary>
    /// Returns the path to strongswan.conf in the native directory.
    /// </summary>
    public static string StrongSwanConfPath =>
        Path.Combine(NativeDir, "strongswan.conf");
}
