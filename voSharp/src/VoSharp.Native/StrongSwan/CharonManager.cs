using System.Diagnostics;
using System.Text.RegularExpressions;
using VoSharp.Native.Interop;

namespace VoSharp.Native.StrongSwan;

/// <summary>
/// Manages the lifecycle of the strongSwan charon-svc.exe Windows service/process.
/// Ensures the daemon is running before VICI commands are sent.
/// </summary>
public class CharonManager : IDisposable
{
    private Process? _charonProcess;
    private bool _disposed;
    private readonly string _charonPath;
    private readonly string _confDir;

    /// <summary>Name strongSwan registers itself under (src/charon-svc/charon-svc.c: SERVICE_NAME).</summary>
    public const string ServiceName = "charon-svc";

    /// <summary>True if charon-svc is running (either as a Windows service or a spawned process).</summary>
    public bool IsRunning => _charonProcess is { HasExited: false } || IsServiceRunning();

    public CharonManager(string? charonPath = null, string? confDir = null)
    {
        _charonPath = charonPath ?? NativeLibLoader.GetNativePath("charon-svc.exe");
        _confDir    = confDir    ?? Path.GetDirectoryName(NativeLibLoader.StrongSwanConfPath)
                                    ?? NativeLibLoader.NativeDir;
    }

    /// <summary>
    /// Ensures charon-svc is running. If it's already a Windows service, does nothing.
    /// Otherwise, spawns it as a child process.
    /// </summary>
    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        // Try to start via Windows service first
        if (TryStartService())
        {
            await Task.Delay(1000, ct).ConfigureAwait(false); // give service time to start
            if (IsServiceRunning()) return;
        }

        // Fallback: spawn as child process
        if (!File.Exists(_charonPath))
        {
            throw new FileNotFoundException(
                $"charon-svc.exe not found at '{_charonPath}'.\n" +
                "Build strongSwan for Windows using MSYS2/MinGW-W64 and place charon-svc.exe in native/amd64/.",
                _charonPath);
        }

        // Set STRONGSWAN_CONF environment variable to point to our config
        var confFile = Path.Combine(_confDir, "strongswan.conf");

        // No arguments: Windows has no syslog, so --use-syslog only suppressed the output we
        // need. File logging is already configured in the strongswan.conf we ship.
        _charonProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _charonPath,
                WorkingDirectory = _confDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["STRONGSWAN_CONF"] = confFile
                }
            },
            EnableRaisingEvents = true
        };

        _charonProcess.Start();

        // Wait briefly for the VICI socket to become available
        for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            if (await IsViciAvailableAsync(ct).ConfigureAwait(false))
                return;
        }

        throw new TimeoutException(
            "charon-svc started but VICI TCP socket (127.0.0.1:4502) is not responding. " +
            "Check strongswan.conf and charon-svc logs.");
    }

    /// <summary>
    /// Stops the charon-svc process (if spawned by us).
    /// Does NOT stop the Windows service.
    /// </summary>
    public void Stop()
    {
        if (_charonProcess is { HasExited: false })
        {
            try { _charonProcess.Kill(entireProcessTree: true); }
            catch { }
        }
    }

    /// <summary>
    /// Queries the service control manager for charon-svc.
    /// </summary>
    /// <remarks>
    /// This deliberately asks the SCM rather than scanning the process list: a charon-svc.exe
    /// process started by hand is <em>not</em> running as a service, and conflating the two made
    /// <see cref="TryStartService"/> believe a service was already up when it was not.
    /// </remarks>
    public static bool IsServiceRunning() => QueryServiceState() == "RUNNING";

    /// <summary>Returns the SCM state string (RUNNING / STOPPED / …) or null when not installed.</summary>
    public static string? QueryServiceState()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"query \"{ServiceName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0) return null;

            var match = Regex.Match(output, @"STATE\s*:\s*\d+\s+(\w+)");
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to start the strongSwan Windows service.
    /// </summary>
    private static bool TryStartService()
    {
        // Already running, or not installed — either way there is nothing to start.
        var state = QueryServiceState();
        if (state is null || state == "RUNNING")
            return state == "RUNNING";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"start \"{ServiceName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Probes the VICI TCP socket to see if charon is accepting connections.
    /// </summary>
    private static async Task<bool> IsViciAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 4502, ct).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns a diagnostic summary of the charon process state.
    /// </summary>
    public object GetDiagnosticInfo() => new
    {
        IsRunning,
        CharonPath = _charonPath,
        ConfDir = _confDir,
        Pid = _charonProcess?.Id,
        IsWindowsService = IsServiceRunning() && _charonProcess == null
    };

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop();
            _charonProcess?.Dispose();
        }
    }
}
