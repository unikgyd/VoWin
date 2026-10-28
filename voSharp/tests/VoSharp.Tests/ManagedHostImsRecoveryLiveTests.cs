using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using VoSharp.HostIms;
using VoSharp.Modem;

namespace VoSharp.Tests;

[SupportedOSPlatform("windows")]
[Collection("Windows modem lab")]
public sealed class ManagedHostImsRecoveryLiveTests
{
    // Explicit lab gate: changes IMS mode and kills only the owner started by this test.
    // No SIP, AKA, RTP, SMS or application payload is sent by the test.
    [Fact]
    [Trait("Lab", "ExplicitManagedOwnerCrash")]
    public async Task OptionalManagedOwnerCrashRestoresImsAndRemovesSystemTask()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_MANAGED_OWNER_CRASH") != "1") return;
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        Assert.False(WindowsRas.HasImsConnection());
        var executable = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        Assert.True(File.Exists(executable), "An extracted, protected managed owner is required.");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        var start = RecoveryGuard.ChildStart(executable!, "--own", port, "180");
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var owner = Process.Start(start)!;
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ConcurrentQueue<string>();
        async Task Pump(StreamReader reader, bool stdout)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                output.Enqueue(line);
                if (stdout && line.StartsWith("VOWIN_IMS_BEARER_READY ", StringComparison.Ordinal))
                    ready.TrySetResult(line);
            }
            if (stdout) ready.TrySetException(new IOException("Owner exited before READY: " + string.Join('\n', output)));
        }
        var stdout = Pump(owner.StandardOutput, true);
        var stderr = Pump(owner.StandardError, false);
        try
        {
            var bearer = WindowsHostImsPppBearerLease.ParseReady(await ready.Task.WaitAsync(TimeSpan.FromMinutes(5)));
            var states = Directory.GetFiles(RecoveryGuard.Root, "*.json")
                .Select(path => (Path: path, State: JsonSerializer.Deserialize<RecoveryState>(File.ReadAllText(path))!))
                .Where(item => item.State.OwnerId == owner.Id && item.State.OwnerTicks == owner.StartTime.ToUniversalTime().Ticks);
            var state = Assert.Single(states);
            Assert.True(File.Exists(state.Path + ".watching"));
            Assert.False(File.Exists(state.Path + ".done"));
            var taskXml = ReadTaskXml(state.State.TaskName);
            Assert.NotNull(taskXml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var document = XDocument.Parse(taskXml);
            Assert.Equal("S-1-5-18", document.Descendants(ns + "UserId").Single().Value);
            Assert.Equal(executable, document.Descendants(ns + "Command").Single().Value, ignoreCase: true);

            // Not an entire-tree kill: the independent watchdog must survive, and
            // the RAS worker must hang up on its parent's broken stdin pipe.
            owner.Kill();
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!File.Exists(state.Path + ".done") && DateTime.UtcNow < deadline) await Task.Delay(1000);
            Assert.True(File.Exists(state.Path + ".done"), "Independent recovery did not confirm IMS restoration.");
            Assert.Equal("ims=1,1", await File.ReadAllTextAsync(state.Path + ".done"));
            Assert.Null(ReadTaskXml(state.State.TaskName));
            Assert.False(WindowsRas.HasImsConnection());
            Assert.DoesNotContain(NetworkInterface.GetAllNetworkInterfaces(),
                adapter => adapter.Name == state.State.EntryName && adapter.OperationalStatus == OperationalStatus.Up);
            Assert.DoesNotContain(WindowsRoutes.ReadAll(), route => route.Index == bearer.InterfaceIndex);
            Assert.Equal("1,1", await new ModemControl(port).WaitState(null, 15));
        }
        finally
        {
            if (!owner.HasExited)
            {
                await owner.StandardInput.WriteLineAsync("STOP");
                owner.StandardInput.Close();
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            }
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    [Trait("Lab", "ExplicitManagedSystemRecovery")]
    public async Task OptionalSystemTaskRunsProtectedRecoveryWithoutModemMutation()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_MANAGED_SYSTEM_RECOVERY") != "1") return;
        var executable = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        Assert.True(File.Exists(executable));
        RecoveryGuard.EnsureSecureRoot();
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(RecoveryGuard.Root, id + ".json");
        using var owner = Process.GetCurrentProcess();
        // A deliberately non-matching hash is also fail-closed if the test dies.
        // The live owner branch must return before opening the serial port.
        var state = new RecoveryState("COM6", new string('0', 64), owner.Id,
            owner.StartTime.ToUniversalTime().Ticks, "VoWin IMS Restore 0000000000000000 " + id[..8]);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(file, state);
        var registered = false;
        try
        {
            WindowsRecoveryTasks.Register(state.TaskName, RecoveryGuard.BuildTaskXml(executable!, path));
            registered = true;
            WithTask(state.TaskName, task =>
            {
                object running = task.Run(null);
                try { return true; }
                finally { if (Marshal.IsComObject(running)) Marshal.FinalReleaseComObject(running); }
            });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path + ".log") && WithTask(state.TaskName, task => (int)task.State) != 4) break;
                await Task.Delay(500);
            }
            Assert.Contains("Owner still live; no recovery mutation.", await File.ReadAllTextAsync(path + ".log"));
            Assert.Equal(0, WithTask(state.TaskName, task => (int)task.LastTaskResult));
            Assert.False(File.Exists(path + ".done"));
        }
        finally
        {
            if (registered) WindowsRecoveryTasks.Delete(state.TaskName);
            // Retain the small state/log as lab evidence, just like normal recovery.
        }
    }

    private static string? ReadTaskXml(string name)
    {
        try { return WithTask(name, task => (string)task.Xml); }
        // The COM binder maps ERROR_FILE_NOT_FOUND to FileNotFoundException,
        // not necessarily COMException.
        catch (Exception ex) when (ex is COMException or FileNotFoundException &&
            ex.HResult == unchecked((int)0x80070002)) { return null; }
    }

    private static T WithTask<T>(string name, Func<dynamic, T> action)
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            task = ((dynamic)folder).GetTask(name);
            return action(task);
        }
        finally
        {
            foreach (var item in new[] { task, folder, service })
                if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }

    [Fact]
    public void MissingRecoveryTaskIsReadOnlyAbsenceRatherThanFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(ReadTaskXml("VoWin Test Missing " + Guid.NewGuid().ToString("N")));
    }
}
