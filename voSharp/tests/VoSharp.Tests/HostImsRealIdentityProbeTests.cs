using VoSharp.Modem;
using VoSharp.Kernel.Pool;
using Xunit.Abstractions;

namespace VoSharp.Tests;

/// <summary>Explicit read-only field diagnostic; never performs AKA or sends SIP.</summary>
[Collection("Windows modem lab")]
public sealed class HostImsRealIdentityProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OptionalReadOnlyServingCellSupportsCellularPaniWithoutPrintingLocation()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_CELL_PANI_PROBE") != "1") return;
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        await using var modem = new ModemDriver(port);
        modem.Open();
        Assert.True(await modem.PingAsync());
        var response = await modem.Session.ExecuteCommandAsync("AT+QENG=\"servingcell\"");
        Assert.True(response.Success);
        var pani = VoSharp.Telephony.HostImsRegistrationClient.ParseLteServingCellPani(response.Lines);
        Assert.NotNull(pani);
        var duplex = pani.StartsWith("3GPP-E-UTRAN-FDD", StringComparison.Ordinal) ? "FDD" : "TDD";
        output.WriteLine($"Cellular PANI available=True; LTE duplex={duplex}; MCC/MNC/TAC/ECI values omitted.");
    }

    [Fact]
    public async Task OptionalReadOnlyIsimReadinessUsesNoBearerOrCardAuthentication()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_IDENTITY_PROBE") != "1") return;
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        await using var modem = new ModemDriver(port);
        modem.Open();
        Assert.True(await modem.PingAsync());

        var qmi = await modem.TryProbeQmiIsimIdentityAsync();
        var at = await new IsimIdentityReader(modem.Session).ReadAsync();
        output.WriteLine($"QMI ISIM: {qmi?.Readiness.ToString() ?? "unavailable"}; {qmi?.Summary ?? "no QMI ISIM reader"}");
        output.WriteLine($"AT ISIM: {at.Readiness}; {at.Summary}");
        // Deliberately omit IMPI, IMPU, home domain, ICCID and IMSI from test output.
    }

    [Fact]
    public async Task OptionalReadOnlyQmiUsimPreflightVerifiesAtModemBinding()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_IDENTITY_PROBE") != "1") return;
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        await using var modem = new ModemDriver(port);
        modem.Open();
        Assert.True(await modem.PingAsync());
        Assert.True(modem.IsQmiActive, modem.LastQmiFallbackReason);
        var available = await modem.TryCanAuthenticateQmiUsimAsync();
        Assert.True(modem.IsQmiActive, modem.LastQmiFallbackReason);
        output.WriteLine($"QMI/AT modem binding verified=True; selected USIM AKA channel ready={available}; " +
            $"fallback={modem.LastQmiFallbackReason ?? "none"}; no AKA performed.");
    }

    [Fact]
    public async Task OptionalReadOnlyUsimFallbackReadinessOmitsSubscriberIdentifiers()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_IDENTITY_PROBE") != "1") return;
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        await using var slot = new ModemSlot("real-host-ims-identity", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        var sim = Assert.IsType<VoSharp.Sim.SimIdentity>(slot.Sim);
        output.WriteLine($"USIM fallback preflight: authoritative-home-PLMN={sim.IsHomePlmnAuthoritative}; " +
            $"home-PLMN={sim.Mcc}-{sim.Mnc}; " +
            $"PLMN-conflict={sim.HasPlmnConflict}; IMSI-source={sim.ImsiSource}; " +
            $"ICCID-present={!string.IsNullOrWhiteSpace(sim.Iccid)}");
    }

    [Fact]
    public async Task OptionalReadOnlyHostPdnReportsBearerWithoutSubscriberAddresses()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_IDENTITY_PROBE") != "1") return;
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        await using var modem = new ModemDriver(port);
        modem.Open();
        Assert.True(await modem.PingAsync());
        // Verify the QMI endpoint belongs to this AT modem before querying its PDN.
        // This preflight selects/reads the USIM channel, but performs no AKA.
        var usimReady = await modem.TryCanAuthenticateQmiUsimAsync();
        var probe = await modem.ProbeHostImsPdnAsync();
        output.WriteLine($"Host IMS PDN: readiness={probe.Readiness}; SIM present={probe.SimInserted}; " +
            $"modem IMS enabled={probe.ModemImsEnabled}; can register on Windows={probe.CanAttemptWindowsIms}; " +
            $"QMI active={modem.IsQmiActive}; USIM channel ready={usimReady}; source={probe.ProbeSource}");
        output.WriteLine($"IMS contexts={probe.Contexts.Count}; active contexts={probe.Contexts.Count(context => context.IsActive)}; " +
            $"Windows-owned addresses={probe.Contexts.Sum(context => context.HostOwnedAddresses.Count)}; " +
            $"verified routes={probe.RouteChecks?.Count(route => route.IsVerified) ?? 0}; " +
            $"cellular adapters connected={probe.WindowsCellularAdapters.Count(adapter => adapter.IsConnected)}");
        output.WriteLine("Subscriber identities, local/remote PDN addresses and cell location omitted; no PDN activation, IMS mode change, AKA, SIP, SMS or call performed.");
    }
}
