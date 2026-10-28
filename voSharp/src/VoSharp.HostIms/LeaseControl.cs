using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;

namespace VoSharp.HostIms;

internal static class LeaseControl
{
    internal static async Task Run(RouteIsolation routes, PppEndpoint endpoint, IReadOnlyList<IPAddress> pcscf,
        ChannelReader<string> input, TextWriter output, Func<bool> rasExited, Func<Task> renewRas,
        TimeSpan leaseTime, CancellationToken ct, TimeSpan? checkInterval = null)
    {
        await output.WriteLineAsync("VOWIN_IMS_BEARER_READY " + JsonSerializer.Serialize(new { InterfaceIndex = endpoint.Index, InterfaceLuid = endpoint.Luid,
            LocalAddress = endpoint.LocalAddress.ToString(), PcscfAddresses = pcscf.Select(a => a.ToString()).ToArray() }));
        var clock = Stopwatch.StartNew();
        var deadline = clock.Elapsed + leaseTime;
        var reportedRepairs = 0;
        while (!ct.IsCancellationRequested)
        {
            if (rasExited()) throw new IOException("IMS RAS owner exited.");
            if (clock.Elapsed >= deadline) throw new TimeoutException("IMS bearer lease expired.");
            routes.Check();
            if (routes.DefaultRouteRepairCount != reportedRepairs)
            {
                reportedRepairs = routes.DefaultRouteRepairCount;
                await output.WriteLineAsync($"VOWIN_IMS_DEFAULT_ROUTE_REISOLATED count={reportedRepairs}");
            }
            if (input.TryRead(out var line))
            {
                if (line == "STOP") return;
                if (line == "RENEW")
                {
                    await renewRas();
                    deadline = clock.Elapsed + leaseTime;
                    await output.WriteLineAsync("VOWIN_IMS_LEASE_RENEWED");
                }
                else if (line.StartsWith("ROUTE ADD ") && IPAddress.TryParse(line[10..], out var add))
                    await output.WriteLineAsync(routes.AddMedia(add) ? "VOWIN_IMS_MEDIA_ROUTE_ADDED " + add : "VOWIN_IMS_MEDIA_ROUTE_REJECTED");
                else if (line.StartsWith("ROUTE REMOVE ") && IPAddress.TryParse(line[13..], out var remove))
                { routes.RemoveMedia(remove); await output.WriteLineAsync("VOWIN_IMS_MEDIA_ROUTE_REMOVED " + remove); }
                else throw new InvalidDataException("Unexpected IMS control command.");
                continue;
            }
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = deadline - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("IMS bearer lease expired.");
            wake.CancelAfter(remaining < (checkInterval ?? TimeSpan.FromSeconds(2)) ? remaining : checkInterval ?? TimeSpan.FromSeconds(2));
            try { if (!await input.WaitToReadAsync(wake.Token)) return; } // EOF is a normal stop, not a spin loop.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }
}
