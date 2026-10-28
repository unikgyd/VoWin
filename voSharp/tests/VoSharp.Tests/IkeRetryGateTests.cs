using VoSharp.Ike;

namespace VoSharp.Telephony.VoWifi;

public class IkeRetryGateTests
{
    private const string Card="8944100000000000001",OtherCard="8944100000000000002";
    private static readonly IkeRetryTarget Target=new("epdg.example","234-15");
    private sealed class Clock : IIkeRetryClock
    {
        public string Boot=new('a',64);public long Ticks=TimeSpan.FromHours(1).Ticks;
        public IkeRetryClockStamp Read()=>new(Boot,Ticks);
        public void Advance(TimeSpan duration)=>Ticks+=duration.Ticks;
    }
    private static IkeNetworkRejection Proof(ushort code=10500,byte timer=0xAA)=>new(code,code==10500?IkeRejectionScope.Epdg:code==9002?IkeRejectionScope.PlmnApn:IkeRejectionScope.Plmn,IkeBackoffTimer.Decode([1,timer]));
    private static async Task<IkeRetryGateException> Blocked(Func<Task> action)
    {var e=await Assert.ThrowsAsync<IkeRetryGateException>(action);Assert.Equal(IkeRetryFailure.Blocked,e.Failure);return e;}
    private static async Task<(IkeRetryCard Card,string Token)> Reserve(IkeRetryGate gate,string device="reader",string card=Card)
    {var c=await gate.PrepareVerifiedCardAsync(device,card);return(c,await gate.BeginAsync(c,Target));}

    [Fact]
    public async Task NewGateRetainsOriginalDeadlineAndAliasAddress()
    {
        var store=new MemoryIkeRetryJournalStore();var clock=new Clock();var gate=new IkeRetryGate(store,clock);
        var(c,token)=await Reserve(gate);await gate.ObserveAsync(c,Target,token,"192.0.2.9",Proof(),true);await gate.CompleteAsync(c,token);
        clock.Advance(TimeSpan.FromMinutes(3));gate=new(store,clock);
        var blocked=await Blocked(()=>gate.InspectAsync(Card,Target with{Host="alias.example"},"::ffff:192.0.2.9"));
        Assert.Equal(TimeSpan.FromMinutes(7),blocked.Status!.Remaining);
        await gate.InspectAsync(Card,Target with{Host="alias.example"},"192.0.2.10");
        await gate.InspectAsync(OtherCard,Target,"192.0.2.9");
        clock.Advance(TimeSpan.FromMinutes(7));await gate.InspectAsync(Card,Target,"192.0.2.9");
    }

    [Theory]
    [InlineData(10500,true)]
    [InlineData(9002,true)]
    [InlineData(9003,false)]
    public async Task KnownScopeDoesNotPauseOtherPlmnOrCard(ushort code,bool expires)
    {
        var store=new MemoryIkeRetryJournalStore();var clock=new Clock();var gate=new IkeRetryGate(store,clock);
        var(c,token)=await Reserve(gate);await gate.ObserveAsync(c,Target,token,"192.0.2.9",Proof(code),true);await gate.CompleteAsync(c,token);
        await Blocked(()=>gate.InspectAsync(Card,Target,"192.0.2.9"));
        await gate.InspectAsync(Card,Target with{Plmn="51566"},"192.0.2.9");
        await gate.InspectAsync(OtherCard,Target,"192.0.2.9");
        if(code==9003)await Blocked(()=>gate.InspectAsync(Card,Target with{Apn="other"},"192.0.2.10"));
        else await gate.InspectAsync(Card,Target with{Apn="other"},"192.0.2.9");
        clock.Advance(TimeSpan.FromDays(1));
        if(expires)await gate.InspectAsync(Card,Target);else await Blocked(()=>gate.InspectAsync(Card,Target));
    }

    [Fact]
    public async Task NewBootRebasesFullDelayOnlyOnceNotOnEveryGateCreation()
    {
        var store=new MemoryIkeRetryJournalStore();var clock=new Clock();var gate=new IkeRetryGate(store,clock);
        var(c,token)=await Reserve(gate);await gate.ObserveAsync(c,Target,token,"192.0.2.9",Proof(),true);await gate.CompleteAsync(c,token);
        clock.Advance(TimeSpan.FromMinutes(9));clock.Boot=new('b',64);clock.Ticks=TimeSpan.FromSeconds(5).Ticks;
        var first=await Blocked(()=>new IkeRetryGate(store,clock).InspectAsync(Card,Target));Assert.Equal(TimeSpan.FromMinutes(10),first.Status!.Remaining);
        clock.Advance(TimeSpan.FromMinutes(4));
        var second=await Blocked(()=>new IkeRetryGate(store,clock).InspectAsync(Card,Target));Assert.Equal(TimeSpan.FromMinutes(6),second.Status!.Remaining);
        clock.Advance(TimeSpan.FromMinutes(6));await new IkeRetryGate(store,clock).InspectAsync(Card,Target);
    }

    [Fact]
    public async Task ZeroDoesNotEraseEarlierRestrictionAndDeactivatedNeedsVerifiedCardChange()
    {
        var store=new MemoryIkeRetryJournalStore();var clock=new Clock();var gate=new IkeRetryGate(store,clock);
        var(c,token)=await Reserve(gate);await gate.ObserveAsync(c,Target,token,"192.0.2.9",Proof(),true);
        await gate.ObserveAsync(c,Target,token,"192.0.2.9",Proof(timer:0),true);await gate.CompleteAsync(c,token);
        await Blocked(()=>gate.InspectAsync(Card,Target));clock.Advance(TimeSpan.FromMinutes(10));
        var next=await gate.BeginAsync(c,Target);await gate.ObserveAsync(c,Target,next,"192.0.2.9",Proof(timer:0xE0),true);await gate.CompleteAsync(c,next);
        clock.Advance(TimeSpan.FromDays(2));await Blocked(()=>gate.InspectAsync(Card,Target));
        await gate.InspectAsync(OtherCard,Target);await Blocked(()=>gate.InspectAsync(Card,Target)); // Cached unknown card is not a lifecycle event.
        await gate.PrepareVerifiedCardAsync("reader",OtherCard);await gate.InspectAsync(Card,Target);
        var stale=await Assert.ThrowsAsync<IkeRetryGateException>(()=>gate.ObserveAsync(c,Target,next,"192.0.2.9",Proof(),true));Assert.Equal(IkeRetryFailure.Binding,stale.Failure);
    }

    [Fact]
    public async Task UnknownAttemptSurvivesNewGateAndLateCompletionCannotEraseReplacement()
    {
        var store=new MemoryIkeRetryJournalStore();var clock=new Clock();var gate=new IkeRetryGate(store,clock);
        var(c,token)=await Reserve(gate);gate=new(store,clock);
        Assert.True((await Blocked(()=>gate.InspectAsync(Card,Target))).Status!.UntilCardEvent);
        await gate.PermitAsync(c,Target,token,"192.0.2.9");await gate.CompleteAsync(c,token);
        var newer=await gate.BeginAsync(c,Target);
        Assert.Equal(IkeRetryFailure.Binding,(await Assert.ThrowsAsync<IkeRetryGateException>(()=>gate.CompleteAsync(c,token))).Failure);
        await Blocked(()=>gate.InspectAsync(Card,Target));await gate.CompleteAsync(c,newer);
        var json=await store.LoadAsync(default);Assert.DoesNotContain(Card,json);Assert.DoesNotContain("reader",json);
    }

    [Fact]
    public async Task ConcurrentStoresAdmitOnlyOneAttemptForSameCard()
    {
        var store=new MemoryIkeRetryJournalStore();var clock=new Clock();var gate=new IkeRetryGate(store,clock);
        var card=await gate.PrepareVerifiedCardAsync("reader",Card);
        var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(async()=>
        {try{await new IkeRetryGate(store,clock).BeginAsync(card,Target);return true;}catch(IkeRetryGateException e)when(e.Failure==IkeRetryFailure.Blocked){return false;}})));
        Assert.Equal(1,results.Count(success=>success));
    }

    [Theory]
    [InlineData(false,10500,IkeRejectionScope.Epdg)]
    [InlineData(true,9002,IkeRejectionScope.Epdg)]
    [InlineData(true,9999,IkeRejectionScope.Plmn)]
    public async Task UnauthenticatedOrIncoherentEvidenceCannotInstallPolicy(bool authenticated,ushort cause,IkeRejectionScope scope)
    {
        var store=new MemoryIkeRetryJournalStore();var gate=new IkeRetryGate(store,new Clock());var(c,t)=await Reserve(gate);
        var proof=new IkeNetworkRejection(cause,scope,IkeBackoffTimer.Decode([1,0xAA]));
        Assert.Equal(IkeRetryFailure.Binding,(await Assert.ThrowsAsync<IkeRetryGateException>(()=>gate.ObserveAsync(c,Target,t,"192.0.2.9",proof,authenticated))).Failure);
        await gate.CompleteAsync(c,t);await gate.InspectAsync(Card,Target);
    }

    private sealed class Journal : IIkeRetryJournalStore
    {
        public string? Value;public bool Fail;
        public Task<string?> LoadAsync(CancellationToken ct)=>Task.FromResult(Value);
        public Task<bool> CompareExchangeAsync(string? expected,string replacement,CancellationToken ct)
        {if(Fail)throw new IOException("Synthetic unconfirmed commit.");if(Value!=expected)return Task.FromResult(false);Value=replacement;return Task.FromResult(true);}
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"Version\":1,\"version\":1}")]
    [InlineData("{\"Version\":2,\"Cards\":{},\"Devices\":{},\"Entries\":{},\"Active\":{}}")]
    [InlineData("{\"Version\":1,\"Cards\":{},\"Devices\":{},\"Entries\":{},\"Active\":{},\"Ignored\":true}")]
    public async Task DamagedJournalNeverBecomesEmptyOrWritable(string raw)
    {
        var store=new Journal{Value=raw};var gate=new IkeRetryGate(store,new Clock());
        Assert.Equal(IkeRetryFailure.Store,(await Assert.ThrowsAsync<IkeRetryGateException>(()=>gate.InspectAsync(Card,Target))).Failure);
        Assert.Equal(raw,store.Value);
    }

    [Fact]
    public async Task UnconfirmedObservationLeavesDurableAdmissionUnknown()
    {
        var store=new Journal();var clock=new Clock();var gate=new IkeRetryGate(store,clock);var(c,t)=await Reserve(gate);
        store.Fail=true;
        Assert.Equal(IkeRetryFailure.Store,(await Assert.ThrowsAsync<IkeRetryGateException>(()=>gate.ObserveAsync(c,Target,t,"192.0.2.9",Proof(),true))).Failure);
        store.Fail=false;
        Assert.Equal("uncertain-attempt",(await Blocked(()=>new IkeRetryGate(store,clock).InspectAsync(Card,Target))).Status!.Source);
    }

    [Fact]
    public void ActualWindowsClockKeepsBootEpochAcrossInstances()
    {
        var a=new WindowsIkeRetryClock().Read();var b=new WindowsIkeRetryClock().Read();
        Assert.Equal(a.Boot,b.Boot);Assert.True(b.Ticks>=a.Ticks);Assert.Equal(64,a.Boot.Length);
    }

    [Theory]
    [InlineData("2001:db8::1%2","23415")]
    [InlineData("192.0.2.9","23-4-15")]
    public async Task AmbiguousPeerOrPlmnNeverReachesJournalMutation(string peer,string plmn)
    {
        var store=new MemoryIkeRetryJournalStore();var gate=new IkeRetryGate(store,new Clock());var(c,t)=await Reserve(gate);
        var before=await store.LoadAsync(default);
        Assert.Equal(IkeRetryFailure.Binding,(await Assert.ThrowsAsync<IkeRetryGateException>(()=>gate.PermitAsync(c,Target with{Plmn=plmn},t,peer))).Failure);
        Assert.Equal(before,await store.LoadAsync(default));
    }

    [Fact]
    public async Task CorruptCanonicalReceiptIsStoreFailureNotCallerBindingFailure()
    {
        var store=new Journal();var gate=new IkeRetryGate(store,new Clock());var(c,t)=await Reserve(gate);
        await gate.ObserveAsync(c,Target,t,"192.0.2.9",Proof(),true);await gate.CompleteAsync(c,t);
        store.Value=store.Value!.Replace("epdg.example","not a host");var damaged=store.Value;
        Assert.Equal(IkeRetryFailure.Store,(await Assert.ThrowsAsync<IkeRetryGateException>(()=>gate.InspectAsync(Card,Target))).Failure);
        Assert.Equal(damaged,store.Value);
    }

    private sealed class DelayedRead(MemoryIkeRetryJournalStore inner):IIkeRetryJournalStore
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> LoadAsync(CancellationToken ct){var value=await inner.LoadAsync(ct);Entered.TrySetResult();await Release.Task.WaitAsync(ct);return value;}
        public Task<bool> CompareExchangeAsync(string? expected,string replacement,CancellationToken ct)=>inner.CompareExchangeAsync(expected,replacement,ct);
    }
    [Fact]
    public async Task DelayedOldCardReadCannotClearNewVerifiedOwnersRestriction()
    {
        var store=new MemoryIkeRetryJournalStore();var clock=new Clock();var gate=new IkeRetryGate(store,clock);await gate.PrepareVerifiedCardAsync("reader",Card);
        var delayed=new DelayedRead(store);var current=true;
        var stale=new IkeRetryGate(delayed,clock).PrepareVerifiedCardAsync("reader",OtherCard,stillCurrent:()=>Volatile.Read(ref current));
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));Volatile.Write(ref current,false);
        var newer=await gate.PrepareVerifiedCardAsync("reader","8944100000000000003");var token=await gate.BeginAsync(newer,Target);
        await gate.ObserveAsync(newer,Target,token,"192.0.2.9",Proof(),true);await gate.CompleteAsync(newer,token);
        delayed.Release.TrySetResult();Assert.Equal(IkeRetryFailure.Binding,(await Assert.ThrowsAsync<IkeRetryGateException>(()=>stale)).Failure);
        await Blocked(()=>gate.InspectAsync("8944100000000000003",Target));
    }
}
