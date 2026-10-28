using System.Runtime.InteropServices;

namespace VoSharp.HostIms;

/// <summary>Task Scheduler COM API. No shell, PowerShell or schtasks child process.</summary>
internal static class WindowsRecoveryTasks
{
    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        object? service = null, folder = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            service = Activator.CreateInstance(type)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            return action(folder);
        }
        finally
        {
            if (folder != null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
            if (service != null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
    }

    internal static void VerifyReadOnly() => WithFolder(folder => { _ = folder.Path; return true; });
    internal static void Register(string name, string xml) => WithFolder(folder =>
    {
        // TASK_CREATE only: never replace a pre-existing task. SYSTEM has an empty password variant.
        object task = folder.RegisterTask(name, xml, 2, "SYSTEM", null, 5, null);
        try { return true; }
        finally { if (Marshal.IsComObject(task)) Marshal.FinalReleaseComObject(task); }
    });
    internal static void Delete(string name) => WithFolder(folder => { folder.DeleteTask(name, 0); return true; });
}
