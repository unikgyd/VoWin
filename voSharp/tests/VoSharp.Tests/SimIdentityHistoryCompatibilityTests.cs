using System.Security.Cryptography;
using System.Text;
using VoSharp.Sim;
using Xunit;

namespace VoSharp.Tests;

public sealed class SimIdentityHistoryCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vowifi-history-" + Guid.NewGuid().ToString("N"));
    private string HistoryPath => Path.Combine(_directory, "identities.json");
    private static SimIdentity Identity(int? length = null) =>
        SimIdentity.FromImsiAndIccid("999123123456789", "test-history-card", mncLength: length);

    [Fact]
    public void ObservedGuessIsNotPromotedToUsimAuthority()
    {
        var store = new SimIdentityHistoryStore(HistoryPath);
        var sim = Identity();
        store.Observe(sim);
        var learned = Assert.Single(new SimIdentityHistoryStore(HistoryPath).GetCandidates(sim));
        Assert.False(learned.IsHomePlmnAuthoritative);
        Assert.Equal("12", learned.Mnc);
    }

    [Fact]
    public void FreshEfAdOverridesStaleHintForSameImsi()
    {
        var store = new SimIdentityHistoryStore(HistoryPath);
        store.Observe(Identity());
        var live = Identity(3);
        var learned = Assert.Single(store.GetCandidates(live));
        Assert.Same(live, learned);
        Assert.Equal("123", learned.Mnc);
        Assert.True(learned.IsHomePlmnAuthoritative);
    }

    [Fact]
    public void ObservationDoesNotEraseSuccessfulDnsInferredMncLength()
    {
        var store = new SimIdentityHistoryStore(HistoryPath);
        store.MarkSuccessful(Identity() with { Mnc = "123" });
        store.Observe(Identity());
        var learned = Assert.Single(new SimIdentityHistoryStore(HistoryPath).GetCandidates(Identity()));
        Assert.Equal("123", learned.Mnc);
        Assert.False(learned.IsHomePlmnAuthoritative);
    }

    [Fact]
    public void EfAdProvenanceSurvivesRoundTripForRotatingIdentity()
    {
        var store = new SimIdentityHistoryStore(HistoryPath);
        store.MarkSuccessful(Identity(3));
        var roaming = Identity() with { Imsi = "001011234567890", Mcc = "001", Mnc = "01" };
        var learned = Assert.Single(new SimIdentityHistoryStore(HistoryPath).GetCandidates(roaming));
        Assert.Equal(Identity().Imsi, learned.Imsi);
        Assert.True(learned.IsHomePlmnAuthoritative);
        Assert.Equal("123", learned.Mnc);
    }

    [Fact]
    public void VersionOneMigrationPreservesSuccessfulMultiImsiHistoryWithoutFalseAuthority()
    {
        Directory.CreateDirectory(_directory);
        var sim = Identity();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(sim.Iccid))).ToLowerInvariant();
        File.WriteAllText(HistoryPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1,
            Cards = new Dictionary<string, object>
            {
                [key] = new[] { new { sim.Imsi, MncLength = 3, HomePlmns = Array.Empty<string>(),
                    LastSeenUtc = "2026-09-20T00:00:00Z", LastSucceededUtc = "2026-09-20T00:00:00Z" } }
            }
        }));
        var store = new SimIdentityHistoryStore(HistoryPath);
        var learned = Assert.Single(store.GetCandidates(sim));
        Assert.Equal("123", learned.Mnc);
        Assert.False(learned.IsHomePlmnAuthoritative);
        store.Observe(sim);
        Assert.Contains("\"Version\": 2", File.ReadAllText(HistoryPath));
        Assert.Equal("123", Assert.Single(store.GetCandidates(sim)).Mnc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
