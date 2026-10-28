using System.Net;
using System.Net.Sockets;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class EpdgDiscoveryCompatibilityTests
{
    private static SimIdentity Sim(string? card = null, int? mncLength = null) =>
        SimIdentity.FromImsiAndIccid("999123123456789", card ?? Guid.NewGuid().ToString("N"), mncLength: mncLength);

    private sealed class DnsStub(Func<string, CancellationToken, Task<IPAddress[]>> lookup) : IEpdgDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => lookup(host, ct);
    }

    private static EpdgDiscoveryOptions StaticOptions(string? context = null) => new(
        StaticAddresses: ["192.0.2.10", "192.0.2.20"], MethodPriority: [EpdgDiscoveryMethod.Static], NetworkContext: context);

    [Fact]
    public void EquivalentHomePrefixCannotOverrideAuthoritativeEfAd()
    {
        var sim = Sim() with { Mnc = "123", IsHomePlmnAuthoritative = true, HomePlmns = ["99912", "999123"] };
        Assert.Equal(new[] { ("999", "123") }, EpdgResolver.BuildHomePlmnCandidates(sim));
    }

    [Fact]
    public async Task ThreeDigitDnsFallbackKeepsIkeAndSipRealmsAligned()
    {
        var sim = Sim();
        var dns = new DnsStub((host, _) => Task.FromResult(
            host.Contains("mnc123.", StringComparison.Ordinal) ? new[] { IPAddress.Parse("192.0.2.30") } : []));
        var result = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(DnsResolver: dns));
        Assert.Equal("123", result.HomeMnc);
        Assert.Equal("ims.mnc123.mcc999.3gppnetwork.org", result.ImsDomain);
        Assert.EndsWith("@" + result.ImsDomain, result.Impi);
        var endpoint = Assert.Single(result.Endpoints!);
        using var backend = new CSharpIkeBackend { AuthenticationHomePlmn = (endpoint.HomeMcc, endpoint.HomeMnc) };
        Assert.Equal(("999", "123"), backend.ResolveAuthenticationHomePlmn(sim));
        Assert.Equal("12", sim.Mnc); // DNS inference does not rewrite the verified card
        Assert.False(sim.IsHomePlmnAuthoritative);
    }

    [Fact]
    public async Task SwitchingEndpointUpdatesFqdnDiagnosticIpAndSipIdentityTogether()
    {
        var sim = Sim();
        var dns = new DnsStub((host, _) => Task.FromResult(new[]
        {
            IPAddress.Parse(host.Contains("mnc123.", StringComparison.Ordinal) ? "192.0.2.30" : "192.0.2.20")
        }));
        var result = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(DnsResolver: dns));
        var fallback = result.Endpoints!.Single(endpoint => endpoint.HomeMnc == "123");
        var selected = EpdgResolver.SelectEndpoint(result, sim, fallback);
        Assert.Equal(fallback.Candidate.Host, selected.Fqdn);
        Assert.Equal(fallback.Address, selected.IpAddresses[0]);
        Assert.Contains("mnc123.", selected.ImsDomain);
        Assert.Equal("sip:" + selected.Impi, selected.Impu);
        using var manager = new VoWifiManager();
        typeof(VoWifiManager).GetProperty(nameof(VoWifiManager.EpdgInfo))!.SetValue(manager, selected);
        Assert.Equal(fallback.Address.ToString(), manager.GetDiagnosticInfo().EpdgIp);
        Assert.Equal(fallback.Candidate.Host, manager.GetDiagnosticInfo().EpdgFqdn);
    }

    [Fact]
    public async Task SharedIpDoesNotEraseAlternativeMncIdentity()
    {
        var dns = new DnsStub((_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.30") }));
        var result = await EpdgResolver.ResolveAsync(Sim(), new EpdgDiscoveryOptions(DnsResolver: dns));
        Assert.Single(result.IpAddresses);
        Assert.Equal(new[] { "12", "123" }, result.Endpoints!.Select(endpoint => endpoint.HomeMnc));
    }

    [Fact]
    public async Task FailedRealmOnSharedIpDoesNotExcludeAnotherLegalMncRealm()
    {
        var sim = Sim();
        var dns = new DnsStub((_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.30") }));
        var options = new EpdgDiscoveryOptions(DnsResolver: dns);
        var first = await EpdgResolver.ResolveAsync(sim, options);
        EpdgResolver.ReportConnectionFailure(first, first.IpAddresses[0]);
        var retry = await EpdgResolver.ResolveAsync(sim, options);
        Assert.Equal("123", Assert.Single(retry.Endpoints!).HomeMnc);
    }

    [Fact]
    public async Task ForeignAndStaticEndpointsDoNotRewriteHomeRealm()
    {
        var sim = Sim(mncLength: 3);
        var dns = new DnsStub((_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.50") }));
        var result = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(
            CarrierPlmns: ["310260"], PlmnPriority: [EpdgPlmnSource.CarrierConfigured], DnsResolver: dns));
        Assert.Contains("mcc310.", result.Fqdn);
        Assert.Contains("mcc999.", result.ImsDomain);
        Assert.Equal("123", result.HomeMnc);
        using var backend = new CSharpIkeBackend { AuthenticationHomePlmn = ("310", "260") };
        Assert.Throws<InvalidOperationException>(() => backend.ResolveAuthenticationHomePlmn(sim));
        var invalidEndpoint = Assert.Single(result.Endpoints!) with { HomeMcc = "310", HomeMnc = "260" };
        Assert.Throws<InvalidOperationException>(() => EpdgResolver.SelectEndpoint(result, sim, invalidEndpoint));
    }

    [Fact]
    public void RegisteredRoamingNetworkRequiresCarrierSelectionPermission()
    {
        var sim = Sim(mncLength: 3);
        var options = new EpdgDiscoveryOptions(RegisteredPlmn: "310260");
        Assert.DoesNotContain(EpdgResolver.BuildCandidates(sim, options), candidate => candidate.Plmn == "310260");
        Assert.Contains(EpdgResolver.BuildCandidates(sim, options with { CarrierPlmns = ["310-260"] }),
            candidate => candidate.Plmn == "310260");
    }

    [Fact]
    public void RoamingStaticAddressesAreDistinctFromHomeStaticAddresses()
    {
        var sim = Sim();
        var options = new EpdgDiscoveryOptions(StaticAddresses: ["home.example.test"],
            RoamingStaticAddresses: ["roam-a.example.test, roam-b.example.test"], IsRoaming: true,
            MethodPriority: [EpdgDiscoveryMethod.Static]);
        Assert.Equal(new[] { "roam-a.example.test", "roam-b.example.test" },
            EpdgResolver.BuildCandidates(sim, options).Select(candidate => candidate.Host));
        Assert.Equal("home.example.test", Assert.Single(EpdgResolver.BuildCandidates(sim,
            options with { RoamingStaticAddresses = [] })).Host);
    }

    [Fact]
    public async Task ManualEndpointRemainsAnExplicitOverride()
    {
        var result = await EpdgResolver.ResolveAsync(Sim(mncLength: 3), new EpdgDiscoveryOptions(
            CustomEpdg: "192.0.2.99", StaticAddresses: ["192.0.2.10"], CarrierPlmns: ["310260"]));
        Assert.Single(result.Candidates!);
        Assert.Equal("CUSTOM", result.SelectionMethod);
        Assert.Equal(IPAddress.Parse("192.0.2.99"), Assert.Single(result.IpAddresses));
        Assert.Equal("123", result.HomeMnc);
    }

    [Fact]
    public async Task FailureHistoryIsIsolatedPerSimAndNetworkExit()
    {
        var sim = Sim();
        var options = StaticOptions("direct");
        var first = await EpdgResolver.ResolveAsync(sim, options);
        EpdgResolver.ReportConnectionFailure(first, first.IpAddresses[0]);
        var retried = await EpdgResolver.ResolveAsync(sim, options);
        Assert.Equal(IPAddress.Parse("192.0.2.20"), Assert.Single(retried.IpAddresses));
        var anotherCard = await EpdgResolver.ResolveAsync(sim with { Iccid = "another-" + sim.Iccid }, options);
        var anotherExit = await EpdgResolver.ResolveAsync(sim, options with { NetworkContext = "socks-exit-b" });
        Assert.Equal(2, anotherCard.IpAddresses.Length);
        Assert.Equal(2, anotherExit.IpAddresses.Length);
    }

    [Fact]
    public async Task ExhaustionAndSuccessDoNotClearOtherCardsOrOtherFailedNodes()
    {
        var simA = Sim();
        var simB = Sim();
        var options = StaticOptions();
        var resultA = await EpdgResolver.ResolveAsync(simA, options);
        var resultB = await EpdgResolver.ResolveAsync(simB, options);
        foreach (var address in resultA.IpAddresses) EpdgResolver.ReportConnectionFailure(resultA, address);
        EpdgResolver.ReportConnectionFailure(resultB, resultB.IpAddresses[0]);
        Assert.Equal(2, (await EpdgResolver.ResolveAsync(simA, options)).IpAddresses.Length);
        Assert.Single((await EpdgResolver.ResolveAsync(simB, options)).IpAddresses);
        EpdgResolver.ReportConnectionSuccess(resultA, resultA.IpAddresses[1]);
        Assert.Single((await EpdgResolver.ResolveAsync(simB, options)).IpAddresses);
    }

    [Theory]
    [InlineData(EpdgAddressPreference.Ipv4Only, AddressFamily.InterNetwork)]
    [InlineData(EpdgAddressPreference.Ipv6Only, AddressFamily.InterNetworkV6)]
    public async Task CarrierFamilyOnlyPolicyFiltersRatherThanJustReorders(EpdgAddressPreference preference, AddressFamily family)
    {
        var result = await EpdgResolver.ResolveAsync(Sim(), new EpdgDiscoveryOptions(
            StaticAddresses: ["192.0.2.10", "2001:db8::10"], MethodPriority: [EpdgDiscoveryMethod.Static], AddressPreference: preference));
        Assert.Equal(family, Assert.Single(result.IpAddresses).AddressFamily);
    }

    [Fact]
    public async Task ExplicitLocalBindCannotFallThroughToIncompatibleRemoteFamily()
    {
        var result = await EpdgResolver.ResolveAsync(Sim(), new EpdgDiscoveryOptions(
            StaticAddresses: ["192.0.2.10", "2001:db8::10"], MethodPriority: [EpdgDiscoveryMethod.Static],
            AddressPreference: EpdgAddressPreference.Ipv6Preferred, RequiredAddressFamily: AddressFamily.InterNetwork));
        Assert.Equal(AddressFamily.InterNetwork, Assert.Single(result.IpAddresses).AddressFamily);
        using var manager = new VoWifiManager
        {
            EpdgDiscovery = new(StaticAddresses: ["192.0.2.10", "2001:db8::10"],
                MethodPriority: [EpdgDiscoveryMethod.Static], AddressPreference: EpdgAddressPreference.Ipv4Only)
        };
        var incompatible = manager.BuildEpdgDiscoveryOptions(null, null, IPAddress.Parse("2001:db8::20"));
        Assert.Equal(EpdgAddressPreference.Ipv4Only, incompatible.AddressPreference);
        Assert.Empty((await EpdgResolver.ResolveAsync(Sim(), incompatible)).IpAddresses);
    }

    [Fact]
    public async Task DnsFailureAndTimeoutAreVisibleWithoutDiscardingOtherCandidates()
    {
        var unresolved = new TaskCompletionSource<IPAddress[]>();
        var dns = new DnsStub((host, _) => host.StartsWith("timeout", StringComparison.Ordinal)
            ? unresolved.Task : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));
        var result = await EpdgResolver.ResolveAsync(Sim(), new EpdgDiscoveryOptions(
            StaticAddresses: ["timeout.example.test", "missing.example.test", "192.0.2.10"],
            MethodPriority: [EpdgDiscoveryMethod.Static], DnsTimeout: TimeSpan.FromMilliseconds(30), DnsResolver: dns))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(result.IpAddresses);
        Assert.Equal(new[] { "timeout", "dns-HostNotFound", "literal" }, result.DnsQueries!.Select(query => query.Status));
        unresolved.SetResult([]);
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoEmptyDnsResultEvenForLiteralEndpoints()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EpdgResolver.ResolveAsync(Sim(), StaticOptions(), cancellation.Token));
    }

    [Fact]
    public async Task DnsCancellationWorksWhenLookupIgnoresItsToken()
    {
        var unresolved = new TaskCompletionSource<IPAddress[]>();
        var dns = new DnsStub((_, _) => unresolved.Task);
        using var cancellation = new CancellationTokenSource();
        var pending = EpdgResolver.ResolveAsync(Sim(), new EpdgDiscoveryOptions(
            StaticAddresses: ["blocked.example.test"], MethodPriority: [EpdgDiscoveryMethod.Static], DnsResolver: dns), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        unresolved.SetResult([]);
    }

    [Fact]
    public async Task InvalidUnicastAnswersAreFilteredAndMappedIpv4IsNormalized()
    {
        var dns = new DnsStub((_, _) => Task.FromResult(new[]
        {
            IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Any, IPAddress.IPv6Any,
            IPAddress.Broadcast, IPAddress.Parse("224.0.0.1"), IPAddress.Parse("ff02::1"), IPAddress.Parse("fe80::1"),
            IPAddress.Parse("::ffff:192.0.2.40"), IPAddress.Parse("192.0.2.40")
        }));
        var result = await EpdgResolver.ResolveAsync(Sim(), new EpdgDiscoveryOptions(
            CustomEpdg: "carrier.example.test", DnsResolver: dns));
        Assert.Equal(IPAddress.Parse("192.0.2.40"), Assert.Single(result.IpAddresses));
        foreach (var invalid in new[] { "::ffff:127.0.0.1", "::ffff:224.0.0.1", "::ffff:255.255.255.255" })
            Assert.Empty((await EpdgResolver.ResolveAsync(Sim(), new EpdgDiscoveryOptions(CustomEpdg: invalid))).IpAddresses);
    }

    [Theory]
    [InlineData("310abc260")]
    [InlineData("310/260")]
    [InlineData("<310260>")]
    public void MalformedCarrierPlmnIsNotSilentlyConvertedIntoAnotherOperator(string plmn)
    {
        Assert.Empty(EpdgResolver.BuildCandidates(Sim(), new EpdgDiscoveryOptions(
            CarrierPlmns: [plmn], PlmnPriority: [EpdgPlmnSource.CarrierConfigured])));
    }

    [Fact]
    public void InvalidCustomEndpointFailsWithoutEchoingCredentials()
    {
        var error = Assert.Throws<ArgumentException>(() => EpdgResolver.BuildCandidates(Sim(),
            new EpdgDiscoveryOptions(CustomEpdg: "https://user:secret@carrier.example.test/path")));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("192.0.2.10", "192.0.2.20", true)]
    [InlineData("2001:db8::10", "2001:db8::20", true)]
    [InlineData("2001:db8::10", "192.0.2.20", false)]
    [InlineData("192.0.2.10", null, false)]
    public void SuccessfulIkeDoesNotImplyUsableImsPdn(string assigned, string? pcscf, bool usable)
    {
        var result = new VoSharp.Ike.IkeSessionResult(true, assigned, pcscf, [], 0, 0,
            null, null, null, null, null, null, null, false, null);
        Assert.Equal(usable, CSharpIkeBackend.HasUsablePdn(result));
    }
}
