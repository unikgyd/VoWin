using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VoSharp.HostIms;

internal sealed record RecoveryState(string AtPort, string ImeiHash, int OwnerId, long OwnerTicks, string TaskName, string? EntryName = null);

internal sealed class RecoveryGuard : IAsyncDisposable
{
    private readonly string _statePath;
    private readonly RecoveryState _state;
    private Process? _watchdog;
    private bool _armed;
    private bool _restored;
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoWin.ManagedImsRecovery");
    private RecoveryGuard(string statePath, RecoveryState state) { _statePath = statePath; _state = state; }
    internal static bool OwnerAlive(RecoveryState state)
    {
        try { using var p = Process.GetProcessById(state.OwnerId); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == state.OwnerTicks; }
        catch (ArgumentException) { return false; }
    }
    private static void SecureDirectory(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recovery directory must not be a reparse point.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (!Directory.Exists(path)) FileSystemAclExtensions.CreateDirectory(acl, path);
        else new DirectoryInfo(path).SetAccessControl(acl);
    }
    internal static void EnsureSecureRoot() => SecureDirectory(Root);
    internal static FileStream AcquireModemLock(string hash)
    {
        if (!Regex.IsMatch(hash, "^[0-9a-f]{64}$")) throw new ArgumentException("Invalid modem hash.");
        SecureDirectory(Root);
        var path = Path.Combine(Root, hash + ".lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted lock path.");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal static async Task<RecoveryGuard> Arm(string atPort, string imeiHash)
    {
        // Only a protected extracted binary may be started by SYSTEM at boot.
        var executable = Path.GetFullPath(Environment.ProcessPath!);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed IMS owner must run from the administrator-protected Program Files runtime cache.");
        for (var item = executable; item != null; item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery executable ancestry must not contain reparse points.");
        SecureDirectory(Root);
        var id = Guid.NewGuid().ToString("N");
        using var owner = Process.GetCurrentProcess();
        var state = new RecoveryState(atPort, imeiHash, owner.Id, owner.StartTime.ToUniversalTime().Ticks,
            "VoWin IMS Restore " + imeiHash[..16] + " " + id[..8], WindowsModemBinding.EntryForImeiHash(imeiHash));
        var path = Path.Combine(Root, id + ".json");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(file, state);
        var guard = new RecoveryGuard(path, state);
        try
        {
            var xmlPath = path + ".xml";
            await File.WriteAllTextAsync(xmlPath, BuildTaskXml(executable, path));
            WindowsRecoveryTasks.Register(state.TaskName, BuildTaskXml(executable, path)); // Never overwrite another task.
            guard._armed = true;
            File.Delete(xmlPath);
            var start = ChildStart(executable, "--watch", path);
            guard._watchdog = Process.Start(start) ?? throw new InvalidOperationException("Could not start IMS recovery watchdog.");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(path + ".watching"))
            {
                if (guard._watchdog.HasExited) throw new InvalidOperationException("IMS watchdog exited before arming.");
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("IMS watchdog did not arm.");
                await Task.Delay(100);
            }
            return guard;
        }
        catch { guard._restored = true; await guard.DisposeAsync(); throw; } // No modem change has been made yet.
    }
    internal static string BuildTaskXml(string executable, string statePath)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Triggers", new XElement(ns + "BootTrigger", new XElement(ns + "Enabled", true))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "System"),
                new XElement(ns + "UserId", "S-1-5-18"), new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false), new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "StartWhenAvailable", true), new XElement(ns + "ExecutionTimeLimit", "PT10M"),
                new XElement(ns + "RestartOnFailure", new XElement(ns + "Interval", "PT2M"), new XElement(ns + "Count", 3))),
            new XElement(ns + "Actions", new XAttribute("Context", "System"), new XElement(ns + "Exec",
                new XElement(ns + "Command", executable), new XElement(ns + "Arguments", "--recover-boot \"" + statePath + "\"")))))
            .ToString();
    }
    internal static ProcessStartInfo ChildStart(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return start;
    }
    internal void ConfirmRestored() => _restored = true;
    public async ValueTask DisposeAsync()
    {
        // Failed restoration retains both independent recovery guards and the state.
        if (!_restored) { _watchdog?.Dispose(); return; }
        if (_armed) WindowsRecoveryTasks.Delete(_state.TaskName);
        await File.WriteAllTextAsync(_statePath + ".done", "ims=1,1");
        if (_watchdog != null)
        {
            await _watchdog.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _watchdog.Dispose();
        }
        // Keep small state/log files as a diagnostic record; no broad cleanup.
    }
    internal static async Task<int> Recover(string path, bool watch)
    {
        path = Path.GetFullPath(path);
        if (Path.GetDirectoryName(path) != Root || !Regex.IsMatch(Path.GetFileName(path), @"^[0-9a-f]{32}\.json$") ||
            (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Untrusted IMS recovery state path.");
        var state = JsonSerializer.Deserialize<RecoveryState>(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException("Missing recovery state.");
        ModemControl.ValidatePort(state.AtPort);
        if (!Regex.IsMatch(state.ImeiHash, "^[0-9a-f]{64}$") || !Regex.IsMatch(state.TaskName, "^VoWin IMS Restore [0-9a-f]{16} [0-9a-f]{8}$") ||
            state.OwnerId <= 0 || state.OwnerTicks <= 0) throw new InvalidDataException("Invalid recovery identity.");
        var entryName = state.EntryName ?? WindowsRas.EntryName; // Old protected state remains recoverable.
        if (state.EntryName != null && state.EntryName != WindowsModemBinding.EntryForImeiHash(state.ImeiHash))
            throw new InvalidDataException("Recovery RAS entry does not match this modem.");
        void Log(string message) => File.AppendAllText(path + ".log", DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        try
        {
            if (watch)
            {
                await File.WriteAllTextAsync(path + ".watching", "WATCHING");
                while (OwnerAlive(state))
                {
                    if (File.Exists(path + ".done")) return 0;
                    await Task.Delay(1000);
                }
            }
            else if (OwnerAlive(state)) { Log("Owner still live; no recovery mutation."); return 0; }
            if (File.Exists(path + ".done")) return 0;
            using var modemLock = AcquireModemLock(state.ImeiHash);
            // On owner death its RAS stdin pipe closes. Wait for hangup; never kill an unrelated process.
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == entryName && n.OperationalStatus == OperationalStatus.Up) ||
                WindowsRas.HasImsConnection(entryName))
            {
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("RAS lease still live; refusing modem recovery mutation.");
                await Task.Delay(1000);
            }
            await new ModemControl(state.AtPort).Restore(state.ImeiHash, Log);
            WindowsRecoveryTasks.Delete(state.TaskName);
            await File.WriteAllTextAsync(path + ".done", "ims=1,1");
            Log("DISARMED after verified ims=1,1."); return 0;
        }
        catch (Exception ex) { Log("RECOVERY FAILED: " + ex.Message); return 1; }
    }
}
