using Microsoft.Data.Sqlite;
using VoSharp.Ike;
using VoSharp.Kernel;
using VoSharp.Kernel.Persistence;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class IkeRetryStorageTests
{
    private const string Card = "8944100000000000041";
    private static readonly IkeRetryTarget Target = new("epdg.example", "23415");
    private sealed class Clock : IIkeRetryClock
    {
        public long Ticks = TimeSpan.FromHours(1).Ticks;
        public IkeRetryClockStamp Read() => new(new string('a', 64), Ticks);
    }

    private static string Database() => Path.Combine(Path.GetTempPath(), "vowin-ike-owner-test-" + Guid.NewGuid().ToString("N"), "preferences.db");
    private static SqliteIkeRetryJournalStore Store(string path) => new(new SqliteConnectionStringBuilder { DataSource = path }.ToString());

    [Fact]
    public void DefaultLocationIsTheExistingPerUserDesktopDatabase()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "preferences.db"),
            IkeRetryStorage.DefaultPreferencesDatabasePath);
        Assert.NotNull(IkeRetryStorage.CreateDefaultGate()); // Construction only; never open the real database.
    }

    [Fact]
    public async Task ConstructionAndPreCanceledReadDoNotCreateAFileOrDirectory()
    {
        var path = Database();
        var store = Store(path);
        Assert.NotNull(IkeRetryStorage.CreateGate(path, new Clock()));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompareExchangeAsync(null, "{}", canceled.Token));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=test;Mode=Memory")]
    [InlineData("Data Source=file:test?mode=memory")]
    [InlineData("Data Source=test;Mode=ReadOnly")]
    [InlineData("Data Source= ")]
    public void NonPersistentOrReadOnlySourcesAreRejected(string connectionString) =>
        Assert.Throws<ArgumentException>(() => new SqliteIkeRetryJournalStore(connectionString));

    [Fact]
    public async Task ExactCasSurvivesReopenAndDoesNotInterpretJsonOrUnicode()
    {
        var path = Database(); var first = Store(path);
        const string raw = "{ \"value\": \"验证码 α\" }";
        Assert.Null(await first.LoadAsync(default));
        Assert.True(await first.CompareExchangeAsync(null, raw, default));
        Assert.False(await Store(path).CompareExchangeAsync("{\"value\":\"验证码 α\"}", "{}", default));
        Assert.Equal(raw, await Store(path).LoadAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => first.CompareExchangeAsync(raw, new string('中', IkeRetryGate.MaximumBytes / 3 + 1), default));
        Assert.Equal(raw, await Store(path).LoadAsync(default));
        Assert.True(await Store(path).CompareExchangeAsync(raw, "{\"new\":true}", default));
    }

    [Fact]
    public async Task RecreatedStandaloneOwnerRetainsTimerAndUsesOriginalDeadline()
    {
        var path = Database(); var clock = new Clock(); var gate = IkeRetryStorage.CreateGate(path, clock);
        var card = await gate.PrepareVerifiedCardAsync("reader", Card); var token = await gate.BeginAsync(card, Target);
        await gate.ObserveAsync(card, Target, token, "192.0.2.9", new(10500, IkeRejectionScope.Epdg, IkeBackoffTimer.Decode([1, 0xAA])), true);
        await gate.CompleteAsync(card, token);
        clock.Ticks += TimeSpan.FromMinutes(3).Ticks;
        var blocked = await Assert.ThrowsAsync<IkeRetryGateException>(() => IkeRetryStorage.CreateGate(path, clock).InspectAsync(Card, Target));
        Assert.Equal(TimeSpan.FromMinutes(7), blocked.Status!.Remaining);
        clock.Ticks += TimeSpan.FromMinutes(7).Ticks;
        await IkeRetryStorage.CreateGate(path, clock).InspectAsync(Card, Target);
    }

    [Fact]
    public async Task RealStandaloneKernelReceivesPersistentFactoryGate()
    {
        var path = Database(); var gate = IkeRetryStorage.CreateGate(path, new Clock());
        var card = await gate.PrepareVerifiedCardAsync("kernel-fallback", Card); await gate.BeginAsync(card, Target);
        await using var kernel = new VoKernel(ikeRetry: IkeRetryStorage.CreateGate(path, new Clock()));
        kernel.VoWifi.EpdgDiscovery = new(ManualOnly: true);
        var sim = SimIdentity.FromImsiAndIccid("234150123456789", Card, mncLength: 2);
        var blocked = await Assert.ThrowsAsync<IkeRetryGateException>(() => kernel.VoWifi.PreflightIkeRetryAsync(sim, Target.Host));
        Assert.Equal("uncertain-attempt", blocked.Status!.Source);
    }

    [Theory]
    [InlineData("INSERT INTO IkeRetryPrivateJournal VALUES(1,zeroblob(1048577))")]
    [InlineData("INSERT INTO IkeRetryPrivateJournal VALUES(1,zeroblob(4))")]
    [InlineData("INSERT INTO IkeRetryPrivateJournal VALUES(2,'{}')")]
    [InlineData("INSERT INTO IkeRetryPrivateJournal VALUES(1,'{}'),(2,'{}')")]
    [InlineData("INSERT INTO IkeRetryPrivateJournal VALUES(1,'')")]
    public async Task DamagedRowsNeverBecomeAnEmptyAdmissionStore(string insert)
    {
        var path = Database(); await Store(path).LoadAsync(default);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            using var command = new SqliteCommand("DROP TABLE IkeRetryPrivateJournal;CREATE TABLE IkeRetryPrivateJournal(Id,Body);" + insert, connection);
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Store(path).LoadAsync(default));
        var failure = await Assert.ThrowsAsync<IkeRetryGateException>(() => IkeRetryStorage.CreateGate(path, new Clock()).InspectAsync(Card, Target));
        Assert.Equal(IkeRetryFailure.Store, failure.Failure);
    }

    [Fact]
    public async Task MalformedJournalDoesNotFallBackToMemory()
    {
        var path = Database(); Assert.True(await Store(path).CompareExchangeAsync(null, "{}", default));
        var failure = await Assert.ThrowsAsync<IkeRetryGateException>(() => IkeRetryStorage.CreateGate(path, new Clock()).PrepareVerifiedCardAsync("reader", Card));
        Assert.Equal(IkeRetryFailure.Store, failure.Failure);
        Assert.Equal("{}", await Store(path).LoadAsync(default));
    }

    [Fact]
    public async Task UnwritableLocationDoesNotCreateAFreshGate()
    {
        var path = Database(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); Directory.CreateDirectory(path);
        var failure = await Assert.ThrowsAsync<IkeRetryGateException>(() => IkeRetryStorage.CreateGate(path, new Clock()).InspectAsync(Card, Target));
        Assert.Equal(IkeRetryFailure.Store, failure.Failure);
        Assert.True(Directory.Exists(path));
    }
}
