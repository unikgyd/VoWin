using System.Net;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public sealed class EpdgUserConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "epdg-user-tests-" + Guid.NewGuid().ToString("N"));
    private SimIdentityHistoryStore Store() => new(Path.Combine(_directory, "history.json"));
    private static SimIdentity Sim() => SimIdentity.FromImsiAndIccid("515661234567890", "test-profile", mncLength: 2);
    private sealed class Dns(Func<string, Task<IPAddress[]>> resolve) : IEpdgDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => resolve(host);
    }

    [Fact]
    public void UserConfigurationNormalizesDeduplicatesAndPreservesFallbackData()
    {
        var user = new EpdgUserConfiguration("EPDG.Example.TEST., 192.0.2.10\n2001:db8::10 epdg.example.test", "515-66, 20404 51566");
        var saved = user.Normalize();
        Assert.Equal("epdg.example.test\n192.0.2.10\n2001:db8::10", saved.PreferredAddresses);
        Assert.Equal("51566\n20404", saved.AdditionalPlmns);
        var options = user.ApplyTo(new(StaticAddresses: ["carrier.example.test"], CarrierPlmns: ["99912"],
            MethodPriority: [EpdgDiscoveryMethod.Plmn], PlmnPriority: [EpdgPlmnSource.Home]));
        Assert.Equal(EpdgDiscoveryMethod.Static, options.MethodPriority![0]);
        var candidates = EpdgResolver.BuildCandidates(Sim(), options);
        Assert.Equal("epdg.example.test", candidates[0].Host);
        Assert.Contains(candidates, candidate => candidate.Host == "carrier.example.test");
        Assert.Contains(candidates, candidate => candidate.Plmn == "20404");
        Assert.Contains(candidates, candidate => candidate.Plmn == "51566");
    }

    [Fact]
    public async Task FailedPreferredHostnameFallsBackWithoutChangingLiveHomeOrImsi()
    {
        var sim = Sim();
        var options = new EpdgUserConfiguration("missing.example.test", "20404").ApplyTo(new(DnsResolver: new Dns(host =>
            Task.FromResult(host.Contains("mcc515.") ? new[] { IPAddress.Parse("192.0.2.20") } : []))));
        var result = await EpdgResolver.ResolveAsync(sim, options);
        Assert.Equal("PLMN", result.SelectionMethod);
        Assert.Equal("515", result.HomeMcc);
        Assert.Equal("66", result.HomeMnc);
        Assert.StartsWith(sim.Imsi + "@", result.Impi);
        Assert.Equal("no-usable-address", result.DnsQueries![0].Status);
    }

    [Fact]
    public void UserPreferencePrecedesVisitedCountryWithoutChangingDefaultCarrierOrder()
    {
        var automatic = new EpdgDiscoveryOptions(IsRoaming: true, VisitedMcc: "204");
        Assert.Equal("VISITED_COUNTRY", EpdgResolver.BuildCandidates(Sim(), automatic)[0].Method);
        var options = new EpdgUserConfiguration("preferred.example.test").ApplyTo(automatic);
        var candidates = EpdgResolver.BuildCandidates(Sim(), options);
        Assert.Equal("preferred.example.test", candidates[0].Host);
        Assert.Equal("USER_PREFERRED", candidates[0].Method);
        Assert.Contains(candidates, candidate => candidate.Method == "VISITED_COUNTRY");
    }

    [Fact]
    public async Task ManualOnlyUsesExactlyUsersAddressesNotRoamingCarrierOrAutomaticDns()
    {
        var queried = new List<string>();
        var options = new EpdgUserConfiguration("manual.example.test,192.0.2.11", "20404", true).ApplyTo(new(
            StaticAddresses: ["carrier.example.test"], RoamingStaticAddresses: ["roaming.example.test"], IsRoaming: true,
            VisitedMcc: "204", DnsResolver: new Dns(host =>
            {
                queried.Add(host); return Task.FromResult(new[] { IPAddress.Parse("192.0.2.10") });
            })));
        var result = await EpdgResolver.ResolveAsync(Sim(), options);
        Assert.Equal(new[] { "manual.example.test", "192.0.2.11" }, result.Candidates!.Select(candidate => candidate.Host));
        Assert.Equal("MANUAL", result.SelectionMethod);
        Assert.Equal(new[] { "manual.example.test" }, queried);
        Assert.Equal("515", result.HomeMcc);
    }

    [Fact]
    public async Task AdditionalForeignDiscoveryPlmnCannotRewriteSubscriberAuthentication()
    {
        var result = await EpdgResolver.ResolveAsync(Sim(), new EpdgUserConfiguration(AdditionalPlmns: "20404").ApplyTo(new(
            DnsResolver: new Dns(host => Task.FromResult(host.Contains("mcc204.") ? new[] { IPAddress.Parse("192.0.2.50") } : [])))));
        Assert.Contains("mcc204.", result.Fqdn);
        Assert.Contains("mcc515.", result.ImsDomain);
        Assert.Equal("66", result.HomeMnc);
    }

    [Fact]
    public async Task StandardManualHostnameCanResolveUnknownMncLengthButNotChangeItsMcc()
    {
        var sim = SimIdentity.FromImsiAndIccid("999123123456789", "test-card");
        var host = EpdgResolver.BuildEpdgDomain("999", "123");
        var options = new EpdgUserConfiguration(host, ManualOnly: true).ApplyTo(new(
            DnsResolver: new Dns(_ => Task.FromResult(new[] { IPAddress.Parse("192.0.2.30") }))));
        var result = await EpdgResolver.ResolveAsync(sim, options);
        Assert.Equal("123", result.HomeMnc);
        Assert.Equal("999", result.HomeMcc);
        Assert.False(sim.IsHomePlmnAuthoritative);
    }

    [Fact]
    public void ExplicitCliEndpointStillOverridesUserAndAutomaticConfiguration()
    {
        using var manager = new VoWifiManager { UserEpdgConfiguration = new("192.0.2.11", "20404", true) };
        var candidates = EpdgResolver.BuildCandidates(Sim(), manager.BuildEpdgDiscoveryOptions("192.0.2.99", null, null));
        Assert.Equal("192.0.2.99", Assert.Single(candidates).Host);
        manager.UserEpdgConfiguration = null;
        Assert.DoesNotContain(EpdgResolver.BuildCandidates(Sim(), manager.BuildEpdgDiscoveryOptions(null, null, null)),
            candidate => candidate.Host == "192.0.2.11");
    }

    [Theory]
    [InlineData("https://user:secret@example.test/path", null, false)]
    [InlineData("epdg.example.test:4500", null, false)]
    [InlineData("127.0.0.1", null, false)]
    [InlineData("::ffff:127.0.0.1", null, false)]
    [InlineData("fe80::1%2", null, false)]
    [InlineData("999.999.1.1", null, false)]
    [InlineData(null, "515abc66", false)]
    [InlineData(null, "515/66", false)]
    [InlineData(null, null, true)]
    public void InvalidConfigurationIsRejectedWithoutEchoingSensitiveInput(string? hosts, string? plmns, bool manual)
    {
        var error = Assert.Throws<ArgumentException>(() => new EpdgUserConfiguration(hosts, plmns, manual).Normalize());
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void ConfigurationIsBoundedPerProfile()
    {
        Assert.Throws<ArgumentException>(() => new EpdgUserConfiguration(
            string.Join(',', Enumerable.Range(1, 17).Select(index => $"epdg{index}.example.test"))).Normalize());
        Assert.Throws<ArgumentException>(() => new EpdgUserConfiguration(new string('a', 8193)).Normalize());
    }

    [Fact]
    public async Task SameCardAlternateReadIsRetainedWithoutBorrowingCurrentMncLength()
    {
        var home = Sim() with { ReportedImsi = "204041234567890", PermanentImsi = Sim().Imsi };
        await using var slot = new ModemSlot("offline", "NO-PORT", identityHistory: Store());
        slot.SetVerifiedSimIdentity(home);
        var candidates = slot.BuildVoWifiIdentityCandidates();
        Assert.Equal(home.Imsi, candidates[0].Imsi);
        var alternate = Assert.Single(candidates, candidate => candidate.Imsi.StartsWith("204"));
        Assert.False(alternate.IsHomePlmnAuthoritative);
        Assert.Equal(2, EpdgResolver.BuildHomePlmnCandidates(alternate).Count);
        Assert.All(candidates, candidate => Assert.Equal(home.Iccid, candidate.Iccid));
    }

    [Fact]
    public async Task ProvenHomeIdentityOutranksRotatingIdentityAndNeverLeaksToAnotherSim()
    {
        var store = Store(); store.MarkSuccessful(Sim());
        var rotating = SimIdentity.FromImsiAndIccid("204041234567890", Sim().Iccid, mncLength: 2);
        await using var slot = new ModemSlot("offline", "NO-PORT", identityHistory: store);
        slot.SetVerifiedSimIdentity(rotating);
        Assert.Equal(Sim().Imsi, slot.BuildVoWifiIdentityCandidates()[0].Imsi);
        slot.VoWifi.UserEpdgConfiguration = new("192.0.2.10");
        slot.SetVerifiedSimIdentity(rotating with { Iccid = "different-profile" });
        Assert.Null(slot.VoWifi.UserEpdgConfiguration);
        Assert.DoesNotContain(slot.BuildVoWifiIdentityCandidates(), candidate => candidate.Imsi == Sim().Imsi);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
