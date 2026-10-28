using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;

namespace VoSharp.HostIms;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows IMS owner requires Windows.");
            if (args is ["--describe-protocol"])
            {
                // No elevation, native API, modem, RAS or recovery task is needed for this handshake.
                Console.WriteLine(JsonSerializer.Serialize(VoSharp.Common.Utils.HostImsOwnerProtocol.Current));
                return 0;
            }
            if (args is ["--verify-runtime"])
            {
                // Read-only ABI/network check. Never access AT, dial RAS, schedule tasks or mutate routes.
                Console.WriteLine(JsonSerializer.Serialize(ManagedRuntimeDiagnostics.VerifyReadOnly()));
                return 0;
            }
            if (args is ["--check-ras-modem"])
            {
                _ = WindowsRas.SelectModem(WindowsRas.Devices());
                Console.WriteLine("Managed RAS modem ready; no dial attempted.");
                return 0;
            }
            if (args is ["--verify-binding", var selectedPort])
            {
                ModemControl.ValidatePort(selectedPort);
                var binding = WindowsModemBinding.Resolve(selectedPort);
                _ = binding.SelectRasDevice(WindowsRas.Devices());
                Console.WriteLine(JsonSerializer.Serialize(new { Success = true, Binding = binding,
                    QmiEndpoint = qmiSharp.Transport.QualcommWwanTransport.FindControlPathForAtPort(selectedPort),
                    ModemAccessed = false, RoutesChanged = false }));
                return 0;
            }
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Administrator token required.");
            if (args is ["--ras", var leaseSeconds] && int.TryParse(leaseSeconds, out var rasSeconds))
                return await WindowsRas.RunWorker(rasSeconds);
            if (args is ["--ras", var lease, var rasPort, var entry, var container] &&
                int.TryParse(lease, out var duration) && Guid.TryParse(container, out var containerId))
                return await WindowsRas.RunWorker(duration, rasPort, entry, containerId);
            if (args is ["--watch", var path]) return await RecoveryGuard.Recover(path, watch: true);
            if (args is ["--recover-boot", var bootPath]) return await RecoveryGuard.Recover(bootPath, watch: false);
            // Accept the previous library protocol, but never run its native helper argument.
            if (args is ["--own", _, _, _]) args = [args[0], args[1], args[3]];
            if (args is not ["--own", var atPort, var seconds] || !int.TryParse(seconds, out var maxSeconds) || maxSeconds is < 15 or > 3600)
                throw new ArgumentException("Usage: VoSharp.HostIms --own COMn lease-seconds");
            ModemControl.ValidatePort(atPort);
            return await Own(atPort, maxSeconds);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task<int> Own(string atPort, int maxSeconds)
    {
        if (!Console.IsInputRedirected) throw new InvalidOperationException("Managed IMS requires private parent stdin.");
        Console.SetOut(new ParentPipeWriter(Console.Out));
        Console.SetError(new ParentPipeWriter(Console.Error));
        using var parentEnded = new CancellationTokenSource();
        var input = Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                // Console.In can synchronously block its caller: dedicate a thread, never block route checks.
                while (await Console.In.ReadLineAsync() is { } line)
                {
                    if (line == "STOP") { parentEnded.Cancel(); break; }
                    await input.Writer.WriteAsync(line);
                }
            }
            finally { input.Writer.TryComplete(); parentEnded.Cancel(); }
        });
        var ct = parentEnded.Token;
        var binding = WindowsModemBinding.Resolve(atPort);
        _ = binding.SelectRasDevice(WindowsRas.Devices());
        binding.RequireWwanDisconnected();
        var modem = new ModemControl(atPort);
        var baseline = await modem.WaitState(null, 10, ct);
        if (baseline != "1,1") throw new InvalidOperationException("Expected baseline IMS mode 1,1; refusing handoff.");
        if (!(await modem.At("AT+QCFG=\"usbnet\"", ct)).Contains("\"usbnet\",0")) throw new InvalidOperationException("Expected RmNet usbnet=0.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(await modem.At("AT+CGDCONT?", ct), "(?m)^\\+CGDCONT:\\s*5,\"IPV4V6\",\"ims\""))
            throw new InvalidOperationException("Expected known CID 5 IPV4V6 ims profile.");
        var imeiHash = ModemControl.HashImei(await modem.At("AT+GSN", ct));
        var entryName = WindowsModemBinding.EntryForImeiHash(imeiHash);
        // Share.None is cross-session and releases after a crash, without thread-affine mutex ownership.
        using var modemLock = RecoveryGuard.AcquireModemLock(imeiHash);
        if (await modem.WaitState(null, 10, ct) != "1,1") throw new InvalidOperationException("IMS baseline changed before handoff.");
        try
        {
            await using var recovery = await RecoveryGuard.Arm(atPort, imeiHash);
            Process? ras = null;
            Task? rasOutput = null;
            var disableIssued = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                disableIssued = true;
                ModemControl.RequireOk(await modem.At("AT+QCFG=\"ims\",2", ct));
                await modem.Restart(ct); await modem.WaitState("2,0", ct: ct);
                await WaitRasModem(atPort, binding.ContainerId, ct);
                if (ModemControl.HashImei(await modem.At("AT+GSN", ct)) != imeiHash)
                    throw new InvalidOperationException("Modem identity changed during USB restart.");
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = RecoveryGuard.ChildStart(Environment.ProcessPath!, "--ras", maxSeconds.ToString(), atPort,
                    entryName, binding.ContainerId.ToString("D"));
                start.RedirectStandardInput = true; start.RedirectStandardOutput = true;
                // Inherit stderr so no second bounded pipe can fill during RAS startup.
                ras = Process.Start(start) ?? throw new InvalidOperationException("Could not launch controlled RAS.");
                rasOutput = Task.Run(async () =>
                {
                    try
                    {
                        while (await ras.StandardOutput.ReadLineAsync() is { } line)
                        {
                            Console.WriteLine(line);
                            if (line.StartsWith("RAS connected; holding")) connected.TrySetResult();
                        }
                        connected.TrySetException(new IOException("RAS stdout closed before connection."));
                    }
                    catch (Exception ex) { connected.TrySetException(ex); throw; }
                });
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
                await Task.Delay(2000, ct);
                var platform = new WindowsRoutes(entryName);
                var endpoint = platform.GetPpp();
                var pcscf = ModemControl.ParsePcscf(await modem.At("AT+CGCONTRDP=5", ct), endpoint.LocalAddress);
                using var routes = new RouteIsolation(platform, endpoint, pcscf);
                routes.Begin();
                await LeaseControl.Run(routes, endpoint, pcscf, input.Reader, Console.Out, () => ras.HasExited,
                    async () => { await ras.StandardInput.WriteLineAsync("RENEW"); await ras.StandardInput.FlushAsync(); },
                    TimeSpan.FromSeconds(maxSeconds), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { Console.WriteLine("VOWIN_IMS_BEARER_LOST"); throw; }
            finally
            {
                Console.WriteLine("VOWIN_IMS_BEARER_LOST");
                Exception? hangupError = null;
                if (ras != null)
                {
                    try
                    {
                        if (!ras.HasExited)
                        {
                            try { await ras.StandardInput.WriteLineAsync("STOP"); ras.StandardInput.Close(); } catch (IOException) { }
                            try { await ras.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
                            catch (TimeoutException) { ras.Kill(); await ras.WaitForExitAsync(); }
                        }
                        if (rasOutput != null) await rasOutput;
                        // A failed/killed worker may leave asynchronous RAS teardown in progress.
                        // Never restart the modem solely because its worker process has exited.
                        if (WindowsRas.HasImsConnection(entryName))
                            throw new IOException("IMS RAS is still connected after worker exit.");
                    }
                    catch (Exception ex) { hangupError = ex; }
                    finally { ras.Dispose(); }
                }
                if (hangupError != null)
                {
                    // Do not restore the modem against a possibly live PPP session.
                    // Retained guards independently wait for RAS to end before restoring.
                    throw new IOException("Controlled RAS cleanup failed; independent recovery remains armed.", hangupError);
                }
                if (disableIssued) await modem.Restore(imeiHash, Console.WriteLine);
                recovery.ConfirmRestored();
            }
            return 0;
        }
        finally { /* The exclusive modem lock releases on every exit path. */ }
    }
    private static async Task WaitRasModem(string atPort, Guid expectedContainer, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var binding = WindowsModemBinding.Resolve(atPort);
                if (binding.ContainerId != expectedContainer) throw new IOException("Selected USB modem identity changed.");
                _ = binding.SelectRasDevice(WindowsRas.Devices());
                return;
            }
            catch (InvalidOperationException) { /* USB RAS modem is still enumerating. */ }
            await Task.Delay(5000, ct);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Quectel RAS modem did not return.");
    }
}
