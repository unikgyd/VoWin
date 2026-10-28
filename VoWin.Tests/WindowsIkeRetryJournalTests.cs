using System.IO;
using Microsoft.Data.Sqlite;
using VoSharp.Ike;
using VoSharp.Telephony.VoWifi;
using VoWin.Services;
using VoSharp.Sim;

namespace VoWin.Tests;

public class WindowsIkeRetryJournalTests
{
    private const string Card="8944100000000000001";
    private static readonly IkeRetryTarget Target=new("epdg.example","23415");
    private sealed class Clock:IIkeRetryClock
    {
        public long Ticks=TimeSpan.FromHours(1).Ticks;
        public IkeRetryClockStamp Read()=>new(new string('a',64),Ticks);
    }
    private static string Database()
    {
        var directory=Path.Combine(Path.GetTempPath(),"vowin-ike-retry-journal-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);return Path.Combine(directory,"preferences.db");
    }
    private static IIkeRetryJournalStore Store(string path)=>new PreferenceDatabaseService(path);

    [Fact]
    public async Task ActualSqliteCASAndReopenRetainExactPrivateBytes()
    {
        var path=Database();var first=Store(path);
        Assert.Null(await first.LoadAsync(default));
        Assert.True(await first.CompareExchangeAsync(null,"{\"value\":1}",default));
        Assert.False(await Store(path).CompareExchangeAsync(null,"{\"value\":2}",default));
        Assert.Equal("{\"value\":1}",await Store(path).LoadAsync(default));
        Assert.True(await Store(path).CompareExchangeAsync("{\"value\":1}","{\"value\":3}",default));
        Assert.Equal("{\"value\":3}",await first.LoadAsync(default));
    }
    [Fact]
    public async Task ActualGateOverReopenedPreferencesRetainsOriginalRemainingTime()
    {
        var path=Database();var clock=new Clock();var gate=new IkeRetryGate(Store(path),clock);
        var card=await gate.PrepareVerifiedCardAsync("reader",Card);var token=await gate.BeginAsync(card,Target);
        await gate.ObserveAsync(card,Target,token,"192.0.2.9",new(10500,IkeRejectionScope.Epdg,IkeBackoffTimer.Decode([1,0xAA])),true);
        await gate.CompleteAsync(card,token);clock.Ticks+=TimeSpan.FromMinutes(3).Ticks;
        var blocked=await Assert.ThrowsAsync<IkeRetryGateException>(()=>new IkeRetryGate(Store(path),clock).InspectAsync(Card,Target));
        Assert.Equal(IkeRetryFailure.Blocked,blocked.Failure);Assert.Equal(TimeSpan.FromMinutes(7),blocked.Status!.Remaining);
        var raw=await Store(path).LoadAsync(default);Assert.DoesNotContain(Card,raw);Assert.DoesNotContain("reader",raw);
        clock.Ticks+=TimeSpan.FromMinutes(7).Ticks;await new IkeRetryGate(Store(path),clock).InspectAsync(Card,Target);
    }
    [Fact]
    public async Task EightIndependentPreferencesObjectsAdmitOnlyOneOwner()
    {
        var path=Database();var clock=new Clock();var gate=new IkeRetryGate(Store(path),clock);
        var card=await gate.PrepareVerifiedCardAsync("reader",Card);
        var stores=Enumerable.Range(0,8).Select(_=>new PreferenceDatabaseService(path)).ToArray();
        foreach(var store in stores)await store.InitializeAsync();
        var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks=stores.Select(store=>Task.Run(async()=>
        {
            await start.Task;
            try{await new IkeRetryGate(store,clock).BeginAsync(card,Target);return true;}
            catch(IkeRetryGateException error)when(error.Failure==IkeRetryFailure.Blocked){return false;}
        })).ToArray();
        start.SetResult();var owners=await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));Assert.Single(owners,value=>value);
    }
    [Fact]
    public async Task CanceledCallerCannotReplaceAlreadyCommittedAdmission()
    {
        var path=Database();var store=Store(path);var gate=new IkeRetryGate(store,new Clock());
        var card=await gate.PrepareVerifiedCardAsync("reader",Card);await gate.BeginAsync(card,Target);
        var raw=await store.LoadAsync(default);using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store(path).CompareExchangeAsync(raw,"{}",canceled.Token));
        Assert.Equal(raw,await Store(path).LoadAsync(default));
        Assert.Equal("uncertain-attempt",(await Assert.ThrowsAsync<IkeRetryGateException>(()=>new IkeRetryGate(Store(path),new Clock()).InspectAsync(Card,Target))).Status!.Source);
    }
    [Fact]
    public async Task DamagedDatabaseRowIsNeverReportedAsEmpty()
    {
        var path=Database();var store=Store(path);await store.LoadAsync(default);
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
        {
            await connection.OpenAsync();using var command=new SqliteCommand("DROP TABLE IkeRetryPrivateJournal;CREATE TABLE IkeRetryPrivateJournal(Id,Body);INSERT INTO IkeRetryPrivateJournal VALUES(1,zeroblob(1048577))",connection);await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(()=>Store(path).LoadAsync(default));
        var error=await Assert.ThrowsAsync<IkeRetryGateException>(()=>new IkeRetryGate(Store(path),new Clock()).InspectAsync(Card,Target));Assert.Equal(IkeRetryFailure.Store,error.Failure);
    }

    [Fact]
    public async Task RealDesktopOwnerInjectsSqliteIntoKernelFallbackManager()
    {
        var path=Database();var preferences=new PreferenceDatabaseService(path);
        var gate=new IkeRetryGate(preferences,new WindowsIkeRetryClock());var target=new IkeRetryTarget("epdg.example",null);
        var card=await gate.PrepareVerifiedCardAsync("kernel-fallback",Card);var token=await gate.BeginAsync(card,target);
        await gate.ObserveAsync(card,target,token,"192.0.2.9",new(10500,IkeRejectionScope.Epdg,IkeBackoffTimer.Decode([1,0xAA])),true);await gate.CompleteAsync(card,token);
        await using var owner=new VoKernelService(preferences,diagnosticLogPath:Path.Combine(Path.GetDirectoryName(path)!,"diagnostic.log"));
        owner.Kernel.VoWifi.EpdgDiscovery=new(ManualOnly:true);
        var sim=SimIdentity.FromImsiAndIccid("234150123456789",Card,mncLength:2);
        var blocked=await Assert.ThrowsAsync<IkeRetryGateException>(()=>owner.Kernel.VoWifi.PreflightIkeRetryAsync(sim,"epdg.example"));
        Assert.Equal(IkeRetryFailure.Blocked,blocked.Failure);Assert.NotNull(blocked.Status?.Remaining);
    }
}
