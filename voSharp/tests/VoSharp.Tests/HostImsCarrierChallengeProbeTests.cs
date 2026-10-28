using VoSharp.Kernel.Pool;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.VoWifi;
using Xunit.Abstractions;

namespace VoSharp.Tests;

/// <summary>
/// Explicit field probe. It sends one initial REGISTER transaction, observes
/// the first final response and never invokes card AKA or sends an authenticated
/// REGISTER. The PPP owner and protected UDP ports are released in all paths.
/// </summary>
[Collection("Windows modem lab")]
public sealed class HostImsCarrierChallengeProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OptionalCarrierInitialChallengeStopsBeforeAka()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_CHALLENGE_PROBE") != "1") return;
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED") != "1")
            throw new InvalidOperationException(
                "Carrier challenge probing needs separate approval to transmit IMSI/IMEI-derived SIP identity to the P-CSCF.");
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.False(string.IsNullOrWhiteSpace(ras));

        await using var slot = new ModemSlot("carrier-challenge-only", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        var sim = Assert.IsType<VoSharp.Sim.SimIdentity>(slot.Sim);
        Assert.True(sim.IsHomePlmnAuthoritative && !sim.HasPlmnConflict);
        var imei = Assert.IsType<string>(slot.Imei);

        try
        {
            var bearerStatus = await slot.StartHostImsPppBearerAsync(script!, ras!, maxLeaseSeconds: 180);
            var bearer = Assert.IsType<VoSharp.Modem.WindowsHostImsPppBearerInfo>(bearerStatus.Bearer);
            var probe = await slot.Modem!.ProbeHostImsPdnAsync();
            var endpoint = Assert.Single(probe.EndpointCandidates, candidate =>
                candidate.LocalAddress.Equals(bearer.LocalAddress) &&
                candidate.PcscfAddress.Equals(bearer.PcscfAddresses[0]) &&
                probe.IsRouteVerified(candidate));

            using var transport = new SipTransport(timeoutMs: 10000);
            transport.ConnectOnInterface(endpoint.LocalAddress, endpoint.PcscfAddress,
                bearer.InterfaceIndex, remotePort: 5060, localPort: 0);
            string? cellularPani = null;
            try
            {
                var cell = await slot.Modem.Session.ExecuteCommandAsync("AT+QENG=\"servingcell\"", 3000);
                if (cell.Success)
                    cellularPani = HostImsRegistrationClient.ParseLteServingCellPani(cell.Lines);
            }
            catch { } // PANI is optional; never fabricate or log a serving-cell identity.
            var profile = HostImsRegistrationClient.BuildUsimFallbackProfile(
                sim, imei, endpoint.LocalAddress.ToString()) with
            {
                LocalPort = transport.LocalEndPoint!.Port,
                PAccessNetworkInfo = cellularPani
            };
            var protectedPorts = transport.ReserveProtectedPortsOnInterface();
            // A challenge-only probe never activates an SA. Generate the same
            // random C# ESP SPI offer without requiring the optional WFP DLL.
            using var esp = new ImsIpsecTransport(endpoint.LocalAddress, endpoint.PcscfAddress);
            var proposal = esp.Proposal with
            {
                PortClient = protectedPorts.ClientPort,
                PortServer = protectedPorts.ServerPort
            };
            using var session = new SipRegisterSession(transport, profile,
                proposal,
                (_, _, _) => throw new InvalidOperationException("Challenge-only probe must never activate an SA."));

            var request = ImsRegisterBuilder.BuildInitialRegister(profile,
                Guid.NewGuid().ToString("N") + "@" + profile.LocalIp, 1);
            session.AddSecurityHeaders(request);
            Assert.NotNull(request.GetHeader("Security-Client"));
            var response = await transport.SendAndReceiveAsync(request);
            Assert.NotNull(response);
            // Print this before parsing optional challenge/security headers so
            // even an unfamiliar carrier response yields an actionable status.
            output.WriteLine($"Initial carrier SIP status={response.StatusCode}; LTE PANI present={cellularPani is not null}");
            var challenges = response.Headers.TryGetValue("WWW-Authenticate", out var values)
                ? values : [];
            var supportedAka = challenges.Any(value => ImsRegisterBuilder.ParseWwwAuthenticate(value) is not null);
            var security = response.Headers.TryGetValue("Security-Server", out var offered)
                ? SecurityAgreementBuilder.EvaluateSecurityServer(string.Join(", ", offered), proposal)
                : null;
            var summary = $"Initial carrier SIP status={response.StatusCode}; " +
                $"AKA challenge present={supportedAka}; Security-Server present={security is not null}; " +
                $"compatible security={security?.Agreement is not null}; LTE PANI present={cellularPani is not null}";
            output.WriteLine(summary);
            if (security is not null)
                output.WriteLine(string.Join(" | ", security.CandidateDiagnostics));
            // Do not print the nonce, private/public identities, raw SIP or SPIs.
            // No second REGISTER, AKA, IPsec activation, SMS or voice occurs.
        }
        finally { await slot.StopHostImsPppBearerAsync(); }
    }
}

/// <summary>
/// Explicit field test for the complete Windows-owned IMS registration path.
/// It never initializes SMS or places a call. A successful binding is actively
/// de-registered before the guarded PPP owner restores modem-internal IMS.
/// </summary>
[Collection("Windows modem lab")]
public sealed class HostImsCarrierFullRegistrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OptionalCarrierFullRegistrationUsesCSharpEspAndRestoresBearer()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_FULL_REGISTER") != "1") return;
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED") != "1")
            throw new InvalidOperationException("Full carrier IMS registration requires separate subscriber-egress approval.");
        Assert.True(int.TryParse(Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_HOLD_SECONDS") ?? "0", out var holdSeconds)
            && holdSeconds is >= 0 and <= 86400, "Explicit IMS hold duration must be between 0 and 86400 seconds.");
        Assert.True(int.TryParse(Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_LEASE_SECONDS") ?? "300", out var leaseSeconds)
            && leaseSeconds is >= 15 and <= 3600, "PPP lease must be between 15 and 3600 seconds.");
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        var winDivert = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_WINDIVERT_DLL");
        Assert.True(!string.IsNullOrWhiteSpace(script) && File.Exists(script));
        Assert.True(!string.IsNullOrWhiteSpace(ras) && File.Exists(ras));
        Assert.True(!string.IsNullOrWhiteSpace(winDivert) &&
            Path.IsPathFullyQualified(winDivert) && File.Exists(winDivert) &&
            File.Exists(Path.Combine(Path.GetDirectoryName(winDivert)!, "WinDivert64.sys")));

        await using var slot = new ModemSlot("carrier-userspace-esp", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        Assert.NotNull(slot.Sim);
        output.WriteLine($"Explicit Host IMS hold seconds={holdSeconds}; renewable PPP lease seconds={leaseSeconds}.");
        try
        {
            var bearer = await slot.StartHostImsPppBearerAsync(script!, ras!, maxLeaseSeconds: leaseSeconds);
            Assert.True(bearer.IsActive, bearer.LastError);
            var active = Assert.IsType<VoSharp.Modem.WindowsHostImsPppBearerInfo>(bearer.Bearer);
            var probe = await slot.Modem!.ProbeHostImsPdnAsync();
            var endpoint = Assert.Single(probe.EndpointCandidates, candidate =>
                candidate.LocalAddress.Equals(active.LocalAddress) &&
                candidate.PcscfAddress.Equals(active.PcscfAddresses[0]) &&
                probe.IsRouteVerified(candidate));
            var status = await slot.StartHostImsRegistrationAsync(
                selectedEndpoint: endpoint,
                securityBackend: new WindowsHostImsUserspaceSecurityBackend(winDivert!),
                diagnosticLog: message => output.WriteLine(message));
            output.WriteLine($"Carrier Host IMS registration state={status.State}; " +
                $"registered={status.IsRegistered}; card-path={status.CardPath}");
            Assert.True(status.IsRegistered, status.LastError ?? status.State);
            var hold = System.Diagnostics.Stopwatch.StartNew();
            while (hold.Elapsed < TimeSpan.FromSeconds(holdSeconds))
            {
                var remaining = TimeSpan.FromSeconds(holdSeconds) - hold.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10));
                var current = slot.GetHostImsRegistrationStatus();
                var currentBearer = slot.GetHostImsPppBearerStatus();
                Assert.True(current.IsRegistered, current.LastError ?? current.State);
                Assert.True(current.ExpiresAtUtc > DateTime.UtcNow, "The cached registration grant has expired.");
                Assert.True(currentBearer.IsActive, currentBearer.LastError);
                output.WriteLine($"Host IMS hold seconds={(int)hold.Elapsed.TotalSeconds}; registered={current.IsRegistered}; " +
                    $"PPP renewals={currentBearer.RenewalCount}; default-route repairs={currentBearer.DefaultRouteRepairCount}; " +
                    $"registration expires in seconds={(int)(current.ExpiresAtUtc!.Value - DateTime.UtcNow).TotalSeconds}");
            }
            if (holdSeconds > leaseSeconds)
            {
                var renewed = slot.GetHostImsPppBearerStatus();
                Assert.True(renewed.RenewalCount > 0, "Surviving the initial PPP lease must include an acknowledged renewal.");
                Assert.NotNull(renewed.LastRenewedAtUtc);
            }
        }
        finally
        {
            try
            {
                await slot.StopHostImsRegistrationAsync();
                var stopped = slot.GetHostImsRegistrationStatus();
                output.WriteLine($"Carrier Host IMS remote de-registration confirmed={stopped.RemoteDeregistered}");
            }
            finally { await slot.StopHostImsPppBearerAsync(); }
        }
    }
}
