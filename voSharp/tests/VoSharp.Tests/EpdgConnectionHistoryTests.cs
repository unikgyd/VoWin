using System.Net;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class EpdgConnectionHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "epdg-history-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "history.json");
    private static SimIdentity Sim() => SimIdentity.FromImsiAndIccid("999123123456789", "synthetic-card-" + Guid.NewGuid().ToString("N"));
    private sealed class Dns(string address) : IEpdgDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse(address) });
    }

    [Fact]
    public async Task RememberedGatewayAndLegalMncWinNextTimeButDnsIsResolvedFresh()
    {
        var sim = Sim(); var store = new EpdgConnectionHistoryStore(FilePath);
        Assert.Null(store.Get(sim));
        var discovery = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(DnsResolver: new Dns("192.0.2.10")));
        var alternate = discovery.Endpoints!.Single(endpoint => endpoint.HomeMnc == "123");
        discovery = EpdgResolver.SelectEndpoint(discovery, sim, alternate);
        store.MarkRegistered(sim, discovery);
        var previous = new EpdgConnectionHistoryStore(FilePath).Get(sim);
        Assert.Equal(alternate.Candidate.Host, previous!.Host);
        var retry = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(
            LastSuccessfulSelection: previous, DnsResolver: new Dns("192.0.2.99")));
        Assert.Equal(previous.Host, retry.Fqdn); Assert.Equal("123", retry.HomeMnc);
        Assert.Equal(IPAddress.Parse("192.0.2.99"), retry.IpAddresses[0]);
        Assert.DoesNotContain("192.0.2.10", File.ReadAllText(FilePath));
        Assert.DoesNotContain(sim.Imsi, File.ReadAllText(FilePath));
        Assert.DoesNotContain(sim.Iccid, File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task SuccessMemoryIsIsolatedBySimAndIdentityAndCannotOverrideManualOnly()
    {
        var sim = Sim(); var store = new EpdgConnectionHistoryStore(FilePath);
        var discovered = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(DnsResolver: new Dns("192.0.2.10")));
        store.MarkRegistered(sim, discovered);
        Assert.Null(store.Get(sim with { Iccid = "another-card" }));
        Assert.Null(store.Get(sim with { Imsi = "999124123456789" }));
        var manual = await EpdgResolver.ResolveAsync(sim, new EpdgUserConfiguration("192.0.2.42", ManualOnly: true)
            .ApplyTo(new(LastSuccessfulSelection: store.Get(sim))));
        Assert.Equal("192.0.2.42", Assert.Single(manual.Candidates!).Host);
    }

    [Fact]
    public async Task NoEndpointOrInvalidAuthenticationRealmCannotBecomeASuccessHint()
    {
        var sim = Sim(); var store = new EpdgConnectionHistoryStore(FilePath);
        var discovered = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(DnsResolver: new Dns("192.0.2.10")));
        store.MarkRegistered(sim, discovered with { IpAddresses = [] });
        Assert.Null(store.Get(sim));
        store.MarkRegistered(sim, discovered with { HomeMcc = "204" });
        Assert.Null(store.Get(sim));
    }

    [Fact]
    public async Task RememberedAddressWinsWithinManualListButNeverReintroducesARemovedManualHost()
    {
        var sim = Sim(); var store = new EpdgConnectionHistoryStore(FilePath);
        var discovered = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(
            CustomEpdg: "success.example.test", DnsResolver: new Dns("192.0.2.10")));
        store.MarkRegistered(sim, discovered);
        var options = new EpdgDiscoveryOptions(LastSuccessfulSelection: store.Get(sim), DnsResolver: new Dns("192.0.2.10"));
        var manual = await EpdgResolver.ResolveAsync(sim, new EpdgUserConfiguration(
            "first.example.test,success.example.test", ManualOnly: true).ApplyTo(options));
        Assert.Equal("success.example.test", manual.Fqdn); // Even when the two names share one IP.
        var changed = await EpdgResolver.ResolveAsync(sim, new EpdgUserConfiguration(
            "first.example.test", ManualOnly: true).ApplyTo(options));
        Assert.Equal("first.example.test", changed.Fqdn);
        Assert.Single(changed.Candidates!);
        var preferred = await EpdgResolver.ResolveAsync(sim, new EpdgUserConfiguration("first.example.test").ApplyTo(options));
        Assert.Equal("success.example.test", preferred.Fqdn);
    }

    [Fact]
    public async Task FailedRememberedEndpointFallsBackWithoutDestroyingSuccessHistory()
    {
        var sim = Sim(); var store = new EpdgConnectionHistoryStore(FilePath);
        var discovered = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(
            CustomEpdg: "success.example.test", DnsResolver: new Dns("192.0.2.10")));
        store.MarkRegistered(sim, discovered);
        var previous = store.Get(sim);
        var options = new EpdgDiscoveryOptions(LastSuccessfulSelection: previous, DnsResolver: new Dns("192.0.2.10"));
        var first = await EpdgResolver.ResolveAsync(sim, options);
        EpdgResolver.ReportConnectionFailure(first, first.IpAddresses[0]);
        try
        {
            var retry = await EpdgResolver.ResolveAsync(sim, options);
            Assert.NotEqual(first.HomeMnc, retry.HomeMnc);
            Assert.Equal(previous, new EpdgConnectionHistoryStore(FilePath).Get(sim));
        }
        finally { EpdgResolver.ReportConnectionSuccess(first, first.IpAddresses[0]); }
    }

    [Theory]
    [InlineData("{\"bad\":null}")]
    [InlineData("{\"bad\":{\"Host\":null}}")]
    [InlineData("not json")]
    public async Task CorruptHistoryCannotBreakRegistrationOrPreventASuccessWrite(string json)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, json.Replace("bad", new string('A', 64), StringComparison.Ordinal));
        var sim = Sim(); var store = new EpdgConnectionHistoryStore(FilePath);
        Assert.Null(store.Get(sim));
        var discovered = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(DnsResolver: new Dns("192.0.2.10")));
        store.MarkRegistered(sim, discovered);
        Assert.NotNull(store.Get(sim));
    }

    [Theory]
    [InlineData(VoWifiState.ImsRegistering, true)]
    [InlineData(VoWifiState.AuthenticatingEapAka, false)]
    [InlineData(VoWifiState.ConnectingIkev2, false)]
    public void OnlyImsTransportFailuresAllowAnotherRegisteredGatewayAttempt(VoWifiState state, bool allowed)
    {
        Assert.False(VoWifiManager.CanFailOverImsTransport(state, new TimeoutException()));
        Assert.Equal(allowed, VoWifiManager.CanFailOverImsTransport(state,
            new VoSharp.Sip.SipRegistrationTransportException(new TimeoutException())));
        Assert.False(VoWifiManager.CanFailOverImsTransport(state, new InvalidOperationException("403 Forbidden")));
        Assert.False(VoWifiManager.CanFailOverImsTransport(state, new System.Security.Cryptography.CryptographicException()));
        Assert.False(VoWifiManager.CanFailOverImsTransport(state, new OperationCanceledException()));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
