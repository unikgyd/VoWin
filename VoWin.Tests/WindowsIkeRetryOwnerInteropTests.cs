using System.IO;
using Microsoft.Data.Sqlite;
using VoSharp.Ike;
using VoSharp.Kernel.Persistence;
using VoSharp.Telephony.VoWifi;
using VoWin.Services;

namespace VoWin.Tests;

public class WindowsIkeRetryOwnerInteropTests
{
    private const string Card = "8944100000000000042";
    private static readonly IkeRetryTarget Target = new("epdg.example", "23415");
    private sealed class Clock : IIkeRetryClock
    {
        public long Ticks = TimeSpan.FromHours(1).Ticks;
        public IkeRetryClockStamp Read() => new(new string('a', 64), Ticks);
    }
    private static string Database() => Path.Combine(Path.GetTempPath(), "vowin-ike-interop-test-" + Guid.NewGuid().ToString("N"), "preferences.db");
    private static IIkeRetryJournalStore Shared(string path) => new SqliteIkeRetryJournalStore(new SqliteConnectionStringBuilder { DataSource = path }.ToString());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimerIsSharedInBothDirectionsWithoutChangingDesktopData(bool desktopFirst)
    {
        var path = Database(); var preferences = new PreferenceDatabaseService(path); await preferences.InitializeAsync();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            using var command = new SqliteCommand("INSERT INTO SimPreferences(Iccid,CardNickname) VALUES('synthetic-card','keep-me')", connection);
            await command.ExecuteNonQueryAsync();
        }
        var clock = new Clock(); var desktop = new IkeRetryGate(preferences, clock); var cli = IkeRetryStorage.CreateGate(path, clock);
        var writer = desktopFirst ? desktop : cli; var reader = desktopFirst ? cli : desktop;
        var card = await writer.PrepareVerifiedCardAsync("reader", Card); var token = await writer.BeginAsync(card, Target);
        await writer.ObserveAsync(card, Target, token, "192.0.2.9", new(10500, IkeRejectionScope.Epdg, IkeBackoffTimer.Decode([1, 0xAA])), true);
        await writer.CompleteAsync(card, token); clock.Ticks += TimeSpan.FromMinutes(3).Ticks;
        var blocked = await Assert.ThrowsAsync<IkeRetryGateException>(() => reader.InspectAsync(Card, Target));
        Assert.Equal(TimeSpan.FromMinutes(7), blocked.Status!.Remaining);
        Assert.Equal(await Shared(path).LoadAsync(default), await ((IIkeRetryJournalStore)preferences).LoadAsync(default));
        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()); await verify.OpenAsync();
        using var read = new SqliteCommand("SELECT CardNickname FROM SimPreferences WHERE Iccid='synthetic-card'", verify);
        Assert.Equal("keep-me", await read.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownReservationSurvivesSwitchingOwner(bool desktopFirst)
    {
        var path = Database(); var clock = new Clock();
        var desktop = new IkeRetryGate(new PreferenceDatabaseService(path), clock); var cli = IkeRetryStorage.CreateGate(path, clock);
        var writer = desktopFirst ? desktop : cli; var reader = desktopFirst ? cli : desktop;
        var card = await writer.PrepareVerifiedCardAsync("reader", Card); var token = await writer.BeginAsync(card, Target);
        var sameCard = await reader.PrepareVerifiedCardAsync("reader", Card);
        var blocked = await Assert.ThrowsAsync<IkeRetryGateException>(() => reader.BeginAsync(sameCard, Target));
        Assert.Equal("uncertain-attempt", blocked.Status!.Source); Assert.True(blocked.Status.UntilCardEvent);
        await reader.CompleteAsync(sameCard, token); await writer.InspectAsync(Card, Target);
    }

    [Fact]
    public async Task EightMixedOwnersShareSingleAdmission()
    {
        var path = Database(); var clock = new Clock();
        var gates = Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? new IkeRetryGate(new PreferenceDatabaseService(path), clock) : IkeRetryStorage.CreateGate(path, clock)).ToArray();
        var card = await gates[0].PrepareVerifiedCardAsync("reader", Card);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = gates.Select(gate => Task.Run(async () =>
        {
            await start.Task;
            try { await gate.BeginAsync(card, Target); return true; }
            catch (IkeRetryGateException e) when (e.Failure == IkeRetryFailure.Blocked) { return false; }
        })).ToArray();
        start.SetResult(); Assert.Single(await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20)), value => value);
    }

    [Fact]
    public async Task DesktopUsesTheSharedExactCasAndRetainsCommittedBytesOnCancellation()
    {
        var path = Database(); IIkeRetryJournalStore desktop = new PreferenceDatabaseService(path); var shared = Shared(path);
        Assert.True(await desktop.CompareExchangeAsync(null, "{ \"value\":1 }", default));
        Assert.False(await shared.CompareExchangeAsync("{\"value\":1}", "{}", default));
        Assert.True(await shared.CompareExchangeAsync("{ \"value\":1 }", "{\"value\":2}", default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => desktop.CompareExchangeAsync("{\"value\":2}", "{}", canceled.Token));
        Assert.Equal("{\"value\":2}", await desktop.LoadAsync(default));
    }
}
