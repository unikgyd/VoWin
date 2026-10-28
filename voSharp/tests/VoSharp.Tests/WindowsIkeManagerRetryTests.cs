using System.Net;
using VoSharp.Common.Aka;
using VoSharp.Ike;
using VoSharp.Kernel;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class WindowsIkeManagerRetryTests
{
    private static readonly SimIdentity Sim=SimIdentity.FromImsiAndIccid("234150123456789","8944100000000000001",mncLength:2);
    private sealed class Clock:IIkeRetryClock
    {
        public long Ticks=TimeSpan.FromHours(1).Ticks;
        public IkeRetryClockStamp Read()=>new(new string('a',64),Interlocked.Read(ref Ticks));
    }
    private sealed class Aka:IAkaProvider
    {
        public int Checks;
        public Task<bool> CheckReadyAsync(string? expectedIccid=null,CancellationToken ct=default){ct.ThrowIfCancellationRequested();Interlocked.Increment(ref Checks);return Task.FromResult(true);}
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge,CancellationToken ct=default)=>throw new InvalidOperationException("A rejected synthetic IKE must not consume AKA.");
    }
    private sealed class Dns:IEpdgDnsResolver
    {
        public int Calls;
        public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct){ct.ThrowIfCancellationRequested();Interlocked.Increment(ref Calls);return Task.FromResult(new[]{IPAddress.Parse("192.0.2.9")});}
    }
    private sealed class Backend(IkeBackendContext context,byte timer,Action? afterObserve=null,Func<Task>? beforePermit=null,Action? beforeWire=null):IIkeBackend
    {
        public EspTunnel? EspTunnel=>null;public IkeTransport? Transport=>null;
        public Task ProbeLivenessAsync(CancellationToken ct=default)=>Task.CompletedTask;
        public Task StopTunnelAsync(CancellationToken ct=default)=>Task.CompletedTask;
        public void Dispose(){}
        public async Task<IkeBackendResult> StartTunnelAsync(SimIdentity sim,IAkaProvider akaProvider,string epdgIp,string apn="ims",string? fallbackPcscf=null,IkeProposalSuite suite=IkeProposalSuite.Auto,string? proxyUrl=null,string? imei=null,Action<string>? diagnosticLog=null,CancellationToken ct=default,bool forceNatt=false,IPAddress? localAddress=null)
        {
            if(beforePermit!=null)await beforePermit();
            await context.Permit(IPAddress.Parse(epdgIp),ct);
            beforeWire?.Invoke();
            var proof=new IkeNetworkRejection(10500,IkeRejectionScope.Epdg,IkeBackoffTimer.Decode([1,timer]));
            await context.Observe(proof,IPAddress.Parse(epdgIp),CancellationToken.None);
            afterObserve?.Invoke();ct.ThrowIfCancellationRequested();
            return new(false,null,null,[],0,0,"synthetic network rejection",InitialResponderAuthVerified:true,NetworkRejection:proof);
        }
    }
    private static VoWifiManager Manager(IkeRetryGate gate,Aka aka,Dns dns,string device="reader")
    {
        var history=new EpdgConnectionHistoryStore(Path.Combine(Path.GetTempPath(),"vowin-ike-manager-"+Guid.NewGuid().ToString("N"),"history.json"));
        return new(epdgHistory:history,ikeRetry:gate,retryDeviceId:device)
        {AkaProvider=aka,DeviceImei="356938035643809",EpdgDiscovery=new(ManualOnly:true,DnsResolver:dns)};
    }
    private static Task<bool> Start(VoWifiManager manager,SimIdentity? sim=null,CancellationToken ct=default)=>manager.StartVoWifiAsync(sim??Sim,customEpdg:"epdg.example",proxyUrl:"socks5://127.0.0.1:9",ct:ct);

    [Fact]
    public async Task ActualManagerManualRetryStopEnableAndNewInstanceCannotBypassTimedRestriction()
    {
        var store=new MemoryIkeRetryJournalStore();var gate=new IkeRetryGate(store,new Clock());var aka=new Aka();var dns=new Dns();var starts=0;
        using(var manager=Manager(gate,aka,dns))
        {
            manager.IkeBackendFactory=c=>{starts++;return new Backend(c,0xAA);};
            Assert.False(await Start(manager));Assert.NotNull(manager.IkeRetryStatus?.Remaining);Assert.False(manager.AutomaticRecoveryPaused);
            Assert.False(await Start(manager));await manager.StopVoWifiAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await Start(manager));await manager.StopVoWifiAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(manager.RecoveryCompletion.IsCompleted);Assert.Equal(1,starts);Assert.Equal(1,aka.Checks);Assert.Equal(1,dns.Calls);
        }
        using var reopened=Manager(new(store,new Clock()),aka,dns);
        reopened.IkeBackendFactory=c=>{starts++;return new Backend(c,0xAA);};
        Assert.False(await Start(reopened));await reopened.StopVoWifiAsync();
        Assert.Equal(1,starts);Assert.Equal(1,aka.Checks);Assert.Equal(1,dns.Calls);
    }

    [Fact]
    public async Task DeactivatedRestrictionRemainsPausedAcrossManualRetryButOtherCardStillWorks()
    {
        var gate=new IkeRetryGate(new MemoryIkeRetryJournalStore(),new Clock());var aka=new Aka();var dns=new Dns();var starts=0;
        using var first=Manager(gate,aka,dns);first.IkeBackendFactory=c=>{starts++;return new Backend(c,0xE0);};
        Assert.False(await Start(first));Assert.True(first.AutomaticRecoveryPaused);Assert.True(first.RecoveryCompletion.IsCompleted);
        Assert.False(await Start(first));Assert.Equal(1,starts);Assert.Equal(1,aka.Checks);
        using var other=Manager(gate,aka,dns,"other-reader");other.IkeBackendFactory=c=>{starts++;return new Backend(c,0xE0);};
        Assert.False(await Start(other,Sim with{Iccid="8944100000000000002"}));Assert.Equal(2,starts);Assert.Equal(2,aka.Checks);
        await first.StopVoWifiAsync();await other.StopVoWifiAsync();
    }

    [Fact]
    public async Task NetworkWakeCannotSkipTimerAndExpiryCanRecoverWithoutManualRelease()
    {
        var clock=new Clock();var gate=new IkeRetryGate(new MemoryIkeRetryJournalStore(),clock);var aka=new Aka();var dns=new Dns();var starts=0;
        using var manager=Manager(gate,aka,dns);manager.IkeBackendFactory=c=>{Interlocked.Increment(ref starts);return new Backend(c,0xAA);};
        Assert.False(await Start(manager));manager.NotifyNetworkPathChanged();await Task.Delay(100);
        Assert.Equal(1,starts);Assert.Equal(1,aka.Checks);Assert.Equal(1,dns.Calls);
        Interlocked.Add(ref clock.Ticks,TimeSpan.FromMinutes(10).Ticks);manager.NotifyNetworkPathChanged();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(Volatile.Read(ref starts)<2)await Task.Delay(10,timeout.Token);
        await manager.StopVoWifiAsync().WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(2,starts);
    }

    [Fact]
    public async Task CancellationAfterAuthenticatedCommitStillBlocksNextStart()
    {
        var gate=new IkeRetryGate(new MemoryIkeRetryJournalStore(),new Clock());var aka=new Aka();var dns=new Dns();using var canceled=new CancellationTokenSource();var starts=0;
        using var manager=Manager(gate,aka,dns);manager.IkeBackendFactory=c=>{starts++;return new Backend(c,0xAA,()=>canceled.Cancel());};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Start(manager,ct:canceled.Token));
        Assert.False(await Start(manager));Assert.Equal(1,starts);Assert.Equal(1,aka.Checks);Assert.Equal(1,dns.Calls);
        await manager.StopVoWifiAsync();
    }

    private sealed class FailingObservationStore:IIkeRetryJournalStore
    {
        private readonly MemoryIkeRetryJournalStore _inner=new();public bool Failed;
        public Task<string?> LoadAsync(CancellationToken ct)=>_inner.LoadAsync(ct);
        public Task<bool> CompareExchangeAsync(string? expected,string replacement,CancellationToken ct)
        {if(replacement.Contains("\"Cause\":10500",StringComparison.Ordinal)){Failed=true;throw new IOException("synthetic unconfirmed save");}return _inner.CompareExchangeAsync(expected,replacement,ct);}
    }
    [Fact]
    public async Task ObservationStoreFailureLeavesAttemptUnknownBeforeAnyFurtherDnsOrHardwareCheck()
    {
        var store=new FailingObservationStore();var gate=new IkeRetryGate(store,new Clock());var aka=new Aka();var dns=new Dns();var starts=0;
        using var manager=Manager(gate,aka,dns);manager.IkeBackendFactory=c=>{starts++;return new Backend(c,0xAA);};
        Assert.False(await Start(manager));Assert.True(store.Failed);Assert.True(manager.AutomaticRecoveryPaused);Assert.Equal("uncertain-attempt",manager.IkeRetryStatus!.Source);
        Assert.False(await Start(manager));Assert.Equal(1,starts);Assert.Equal(1,aka.Checks);Assert.Equal(1,dns.Calls);
        Assert.Contains("\"Active\":{\"",await store.LoadAsync(default));await manager.StopVoWifiAsync();
    }

    [Fact]
    public async Task MissingStoreStopsBeforeDnsReadinessAndBackendCreation()
    {
        var aka=new Aka();var dns=new Dns();using var manager=Manager(new(new UnavailableIkeRetryJournalStore(),new Clock()),aka,dns);
        manager.IkeBackendFactory=_=>throw new InvalidOperationException("Backend must not be constructed.");
        Assert.False(await Start(manager));Assert.True(manager.AutomaticRecoveryPaused);Assert.Equal(0,aka.Checks);Assert.Equal(0,dns.Calls);await manager.StopVoWifiAsync();
    }

    [Fact]
    public async Task StopIntentFencesAQueuedActualSendAndRemovesOnlyItsOwnAdmission()
    {
        var gate=new IkeRetryGate(new MemoryIkeRetryJournalStore(),new Clock());var aka=new Aka();var dns=new Dns();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var sent=0;
        using var manager=Manager(gate,aka,dns);
        manager.IkeBackendFactory=c=>new Backend(c,0xAA,beforePermit:()=>{entered.TrySetResult();return release.Task;},beforeWire:()=>sent++);
        var starting=Start(manager);await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping=manager.StopVoWifiAsync();release.TrySetResult();
        Assert.False(await starting.WaitAsync(TimeSpan.FromSeconds(2)));await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0,sent);Assert.False(manager.KeepOnlineRequested);Assert.Equal(VoWifiState.Disconnected,manager.State);
        await gate.InspectAsync(Sim.Iccid,new("epdg.example",null));
    }

    [Fact]
    public async Task KernelFallbackAndBothSlotFactoriesShareTheInjectedGateWithIndependentDeviceIds()
    {
        var gate=new IkeRetryGate(new MemoryIkeRetryJournalStore(),new Clock());await using var kernel=new VoKernel(ikeRetry:gate);
        Assert.Same(gate,kernel.VoWifi.RetryGate);
        await using var modem=kernel.Pool.CreateSlot("module-a","synthetic-port",115200);
        await using var reader=kernel.Pool.CreateSlot("reader-b","PC/SC · synthetic",0);
        Assert.Same(gate,modem.VoWifi.RetryGate);Assert.Same(gate,reader.VoWifi.RetryGate);
        Assert.Equal("module-a",modem.VoWifi.RetryDeviceId);Assert.Equal("reader-b",reader.VoWifi.RetryDeviceId);
    }

    [Fact]
    public async Task BackendForwardsBothHooksAcrossAkaPrimeAndAddressFamilyRebuilds()
    {
        var calls=0;Func<IPAddress,CancellationToken,Task> permit=(_,_)=>Task.CompletedTask;
        Func<IkeNetworkRejection,IPAddress,CancellationToken,Task> observe=(_,_,_)=>Task.CompletedTask;
        using var backend=new CSharpIkeBackend{BeforeInitialSend=permit,ObserveNetworkRejection=observe,EstablishSession=(r,_)=>
        {
            Assert.Same(permit,r.BeforeInitialSend);Assert.Same(observe,r.ObserveNetworkRejection);calls++;
            var result=new IkeSessionResult(false,null,null,[],0,0,null,null,null,null,null,null,null,false,"synthetic");
            return Task.FromResult(calls==1?result with{RequestedEapType=EapType.AkaPrime}:calls==2?result with{RetryableAddressFamilyFailure=true,PreEapFamilyFailure=true}:result);
        }};
        await backend.StartTunnelAsync(Sim,new Aka(),"192.0.2.9");Assert.Equal(3,calls);
    }
}
