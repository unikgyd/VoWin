using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Ike;
using VoSharp.Sim;
using VoSharp.Sip;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class SmsSimBindingSubmissionTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static SimIdentity Card(string suffix = "0") => SimIdentity.FromImsiAndIccid("00101123456789" + suffix, "890000000000000000" + suffix, mncLength: 2);
    private static SmsSubmitResult Submitted() => new("+15550000001", "offline", SmsEncoding.Gsm7Bit, null, 1, 1, 1, true, "submitted", [], DateTime.UtcNow);
    private static ModemSlot Slot(string id) => new(id, "COM999", identityHistory: new SimIdentityHistoryStore(Path.Combine(Path.GetTempPath(), "vowin-binding-" + Guid.NewGuid().ToString("N") + ".json")));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("unverified")] [InlineData("8900000000000000000F")]
    public void UnverifiedCardCannotAcquireBinding(string? identity) => Assert.Null(SmsSubscriptionIdentity.ForIccid(identity));

    [Fact]
    public async Task ReplacedSimIsRejectedBeforeAnySenderAndCurrentSimCanStillSend()
    {
        await using var slot = Slot("original"); slot.SetVerifiedSimIdentity(Card());
        var original = slot.GetSmsSimBinding()!; slot.SetVerifiedSimIdentity(Card("1")); var sent = 0;
        Task<SmsSubmitResult> Send(SimIdentity _, Action verify, CancellationToken ct) { verify(); sent++; return Task.FromResult(Submitted()); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => slot.SendSmsForSimWithAsync(original, Send));
        Assert.Equal(0, sent);
        await slot.SendSmsForSimWithAsync(slot.GetSmsSimBinding()!, Send);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task IdentityChangeDuringFirstPartFencesEveryLaterPart()
    {
        await using var slot = Slot("parts"); slot.SetVerifiedSimIdentity(Card()); var first = Signal(); var response = Signal(); var parts = 0;
        var sending = slot.SendSmsForSimWithAsync(slot.GetSmsSimBinding()!, async (_, verify, ct) =>
        {
            verify(); Interlocked.Increment(ref parts); first.SetResult();
            await response.Task; // Deliberately ignore cancellation: emulate a late already-sent response.
            verify(); Interlocked.Increment(ref parts); return Submitted();
        });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        slot.SetVerifiedSimIdentity(Card("1")); response.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(1, parts);
    }

    [Fact]
    public async Task ProfileMutationWaitsForTheOwnedSendButIndependentModemDoesNot()
    {
        await using var a = Slot("a"); await using var b = Slot("b"); a.SetVerifiedSimIdentity(Card()); b.SetVerifiedSimIdentity(Card("1"));
        var entered = Signal(); var release = Signal();
        var sending = a.SendSmsForSimWithAsync(a.GetSmsSimBinding()!, async (_, verify, ct) => { entered.SetResult(); await release.Task; verify(); return Submitted(); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var refresh = a.RefreshSimAsync(); Assert.False(refresh.IsCompleted);
        await b.SendSmsForSimWithAsync(b.GetSmsSimBinding()!, (_, verify, _) => { verify(); return Task.FromResult(Submitted()); }).WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.False(await refresh.WaitAsync(TimeSpan.FromSeconds(2))); // No real modem was opened.
    }

    [Fact]
    public async Task ManagedMutationFencesBeforeWaitingAndNeverOverlapsTheOldSender()
    {
        await using var slot = Slot("mutating"); slot.SetVerifiedSimIdentity(Card());
        var binding = slot.GetSmsSimBinding()!; var entered = Signal(); var response = Signal(); var active = false; var secondPart = false;
        var sending = slot.SendSmsForSimWithAsync(binding, async (_, verify, ct) =>
        {
            active = true; entered.SetResult();
            try { await response.Task; verify(); secondPart = true; return Submitted(); }
            finally { active = false; }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var mutation = slot.RunSmsSourceMutationAsync(_ => { Assert.False(active); slot.SetVerifiedSimIdentity(Card("1")); return Task.FromResult(true); });
        Assert.False(mutation.IsCompleted); Assert.Null(slot.GetSmsSimBinding());
        response.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.True(await mutation.WaitAsync(TimeSpan.FromSeconds(2))); Assert.False(secondPart);
        Assert.NotEqual(binding, slot.GetSmsSimBinding());
    }

    [Theory]
    [InlineData("AT+CSQ", true)] [InlineData(" AT+CPIN? ", true)] [InlineData("AT+QDSIM?", true)]
    [InlineData("AT+QDSIM=2", false)] [InlineData("AT+CFUN=1,1", false)] [InlineData("AT+CSIM=10,00A4000000", false)]
    [InlineData("AT+CRSM=214", false)] [InlineData("AT+UNKNOWN=1", false)] [InlineData("AT+CSQ;AT+CFUN=1", false)]
    public void RawDebugOnlyPreservesIdentityForAnExplicitReadOnlyWhitelist(string command, bool readOnly) =>
        Assert.Equal(readOnly, ModemSlot.IsReadOnlySmsSafeAt(command));

    [Fact]
    public async Task KernelCannotRedirectAnAlreadyQueuedSendToAReplacementSlotObject()
    {
        await using var kernel = new VoKernel(); var a = Slot("same-id"); var b = Slot("same-id");
        a.SetVerifiedSimIdentity(Card()); b.SetVerifiedSimIdentity(Card());
        var slots = (ConcurrentDictionary<string, ModemSlot>)typeof(ModemPool).GetField("_slots", Hidden)!.GetValue(kernel.Pool)!;
        slots[a.Id] = a;
        var held = await kernel.Pool.AcquireSlotLeaseAsync(a.Id);
        Assert.NotNull(held);
        var sending = kernel.SendSmsForSimAsync("+15550000001", "offline", a.Id, a.GetSmsSimBinding()!);
        Assert.False(sending.IsCompleted);
        slots[a.Id] = b;
        await held!.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sending);
        Assert.Empty(kernel.GetOutbox());
        await a.DisposeAsync(); // b remains owned by the pool.
    }

    [Fact]
    public async Task CanceledWaiterCannotStartANewPaidOperation()
    {
        await using var slot = Slot("waiting"); slot.SetVerifiedSimIdentity(Card()); var entered = Signal(); var release = Signal();
        var first = slot.SendSmsForSimWithAsync(slot.GetSmsSimBinding()!, async (_, _, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return Submitted(); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); using var stop = new CancellationTokenSource(); var called = false;
        var queued = slot.SendSmsForSimWithAsync(slot.GetSmsSimBinding()!, (_, _, _) => { called = true; return Task.FromResult(Submitted()); }, stop.Token);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); Assert.False(called);
        release.SetResult(); await first;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AQueuedSlotSendCannotRebindAfterACanceledOrNoOpMutationOnTheSameCard(bool cancelMutation)
    {
        await using var slot = Slot("old-queue"); slot.SetVerifiedSimIdentity(Card()); var binding = slot.GetSmsSimBinding()!;
        var gate = (SemaphoreSlim)typeof(ModemSlot).GetField("_profileSwitchGate", Hidden)!.GetValue(slot)!;
        await gate.WaitAsync(); var called = 0; using var cancel = new CancellationTokenSource();
        try
        {
            var sending = slot.SendSmsForSimWithAsync(binding, (_, _, _) => { called++; return Task.FromResult(Submitted()); });
            var mutation = slot.RunSmsSourceMutationAsync(_ => Task.FromResult(true), cancel.Token);
            if (cancelMutation) { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, called);
            gate.Release(); if (!cancelMutation) Assert.True(await mutation);
            await slot.SendSmsForSimWithAsync(binding, (_, verify, _) => { verify(); called++; return Task.FromResult(Submitted()); });
            Assert.Equal(1, called); // A genuinely new request on the same SIM remains usable.
        }
        finally { if (gate.CurrentCount == 0) gate.Release(); }
    }

    [Fact]
    public async Task KernelQueuePinsTheOwnerBeforeWaitingForThePoolLease()
    {
        await using var kernel = new VoKernel(); var slot = Slot("pool-queue"); slot.SetVerifiedSimIdentity(Card());
        var slots = (ConcurrentDictionary<string, ModemSlot>)typeof(ModemPool).GetField("_slots", Hidden)!.GetValue(kernel.Pool)!;
        slots[slot.Id] = slot;
        var held = await kernel.Pool.AcquireSlotLeaseAsync(slot.Id); Assert.NotNull(held);
        try
        {
            var sending = kernel.SendSmsForSimAsync("+15550000001", "offline", slot.Id, slot.GetSmsSimBinding()!);
            Assert.False(sending.IsCompleted);
            Assert.True(await slot.RunSmsSourceMutationAsync(_ => Task.FromResult(true)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(kernel.GetOutbox());
        }
        finally { await held!.DisposeAsync(); }
    }

    [Fact]
    public async Task CachedIncomingEventRetainsOriginalCardWhenFlushedAfterReplacement()
    {
        await using var slot = Slot("queue"); slot.SetVerifiedSimIdentity(Card()); var binding = slot.GetSmsSimBinding();
        var source = new SmsReceivedEventArgs(new(1, SmsStatus.Unread, "+15550000001", "offline", DateTime.UtcNow), slot.Id, binding);
        typeof(ModemSlot).GetMethod("RaiseReceivedSms", Hidden)!.Invoke(slot, [source]);
        slot.SetVerifiedSimIdentity(Card("1")); SmsReceivedEventArgs? delivered = null;
        slot.SmsReceived += (_, e) => delivered = e; slot.FlushPendingEvents();
        Assert.Same(source, delivered); Assert.Equal(binding, delivered!.SimBinding); Assert.NotEqual(slot.GetSmsSimBinding(), delivered.SimBinding);
    }

    [Fact]
    public async Task KernelInboxDoesNotMergeEqualMessageIndexesAcrossSlotsOrCards()
    {
        await using var kernel = new VoKernel(); await using var a = Slot("a"); await using var b = Slot("b");
        typeof(VoKernel).GetMethod("HookSlotEvents", Hidden)!.Invoke(kernel, [a]); typeof(VoKernel).GetMethod("HookSlotEvents", Hidden)!.Invoke(kernel, [b]);
        var message = new SmsMessage(1, SmsStatus.Unread, "+15550000001", "same", DateTime.UtcNow, Id: "source-id"); var received = 0;
        kernel.SmsReceived += (_, _) => received++;
        void Emit(ModemSlot slot, string binding) => typeof(ModemSlot).GetMethod("RaiseReceivedSms", Hidden)!.Invoke(slot, [new SmsReceivedEventArgs(message, slot.Id, binding)]);
        Emit(a, new('a', 64)); Emit(a, new('a', 64)); Emit(b, new('a', 64)); Emit(a, new('b', 64));
        Assert.Equal(3, received); Assert.Equal(3, kernel.GetInbox().Count);
    }

    private sealed class MemoryIMS : IDisposable
    {
        public readonly SipTransport Transport = new();
        public readonly Channel<SipDatagram> Sent = Channel.CreateUnbounded<SipDatagram>();
        public readonly Channel<SipDatagram> Received = Channel.CreateUnbounded<SipDatagram>();
        public readonly IPEndPoint Local = new(IPAddress.Loopback, 5060), Remote = new(IPAddress.Loopback, 5090);
        public MemoryIMS() => Transport.ConnectDatagrams(Local, Remote,
            (packet, ct) => Sent.Writer.WriteAsync(packet, ct).AsTask(), ct => Received.Reader.ReadAsync(ct).AsTask());
        public async Task Accept(SipDatagram request) => await Received.Writer.WriteAsync(new(SipMessage.Parse(request.Payload).CreateResponse(202, "Accepted").ToBytes(), Local, Remote));
        public void Dispose() => Transport.Dispose();
    }
    private static VoWifiImsSessionInfo Info() => new("registered", "ims.test", "001011234567890@ims.test", "sip:user@ims.test", "sip:127.0.0.1:5090", "sip:user@127.0.0.1", 1, 600, "synthetic", DateTime.UtcNow);
    private static void Register(VoWifiManager manager, MemoryIMS ims, SimIdentity card)
    {
        manager.PublishSmsSource(card, ims.Transport, Info(), "+15551230000");
        typeof(VoWifiManager).GetProperty("State")!.SetValue(manager, VoWifiState.ImsRegistered);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CapturedVoWifiSessionCannotMoveMultipartToAReplacement(bool replaceCard)
    {
        using var manager = new VoWifiManager(); using var a = new MemoryIMS(); using var b = new MemoryIMS(); Register(manager, a, Card());
        var sending = manager.SendSmsForSubscriptionAsync("+15550000001", new string('A', 200), Card().Imsi, Card().Iccid);
        var first = await a.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Register(manager, b, replaceCard ? Card("1") : Card());
        await a.Accept(first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.False(b.Sent.Reader.TryRead(out _)); Assert.False(a.Sent.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StopFencesAnAwaitingSmsBeforeWaitingForLifecycleGate()
    {
        using var manager = new VoWifiManager(); using var ims = new MemoryIMS(); Register(manager, ims, Card());
        var lifecycle = (SemaphoreSlim)typeof(VoWifiManager).GetField("_lifecycleGate", Hidden)!.GetValue(manager)!;
        await lifecycle.WaitAsync();
        try
        {
            var sending = manager.SendSmsForSubscriptionAsync("+15550000001", "offline", Card().Imsi, Card().Iccid);
            await ims.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var stopping = manager.StopVoWifiAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(stopping.IsCompleted);
            lifecycle.Release(); await stopping.WaitAsync(TimeSpan.FromSeconds(2)); return;
        }
        finally { if (lifecycle.CurrentCount == 0) lifecycle.Release(); }
    }

    [Fact]
    public async Task StopFencesAnAwaitingSmsBeforeABlockedRecoveryWorkerCompletes()
    {
        using var manager = new VoWifiManager(); using var ims = new MemoryIMS(); Register(manager, ims, Card());
        var recovery = Signal();
        typeof(VoWifiManager).GetField("_recoveryTask", Hidden)!.SetValue(manager, recovery.Task);
        var sending = manager.SendSmsForSubscriptionAsync("+15550000001", "offline", Card().Imsi, Card().Iccid);
        await ims.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = manager.StopVoWifiAsync();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(stopping.IsCompleted);
        }
        finally { recovery.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public void LateRegistrationCannotRepublishAnSmsSourceAfterAStopFence()
    {
        using var manager = new VoWifiManager(); using var ims = new MemoryIMS();
        var generation = (long)typeof(VoWifiManager).GetField("_smsSourceGeneration", Hidden)!.GetValue(manager)!;
        manager.InvalidateSmsSubscription();
        Assert.Throws<OperationCanceledException>(() => manager.PublishSmsSource(Card(), ims.Transport, Info(), "+15551230000", expectedGeneration: generation));
        Assert.False(ims.Sent.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AStoppedQueuedStartOrOldCleanupCannotCaptureANewSmsStopGeneration(bool awaitingOldCleanup)
    {
        using var manager = new VoWifiManager(); using var ims = new MemoryIMS(); using var cancel = new CancellationTokenSource();
        var lifecycle = (SemaphoreSlim)typeof(VoWifiManager).GetField("_lifecycleGate", Hidden)!.GetValue(manager)!;
        var recovery = Signal(); var health = Signal();
        var optionsType = typeof(VoWifiManager).GetNestedType("VoWifiStartOptions", BindingFlags.NonPublic)!;
        var options = Activator.CreateInstance(optionsType, [Card(), null, IkeProposalSuite.Auto, null, false, null])!;
        typeof(VoWifiManager).GetField("_lastStartOptions", Hidden)!.SetValue(manager, options);
        typeof(VoWifiManager).GetField("_keepOnlineRequested", Hidden)!.SetValue(manager, 1);
        typeof(VoWifiManager).GetField("_recoveryTask", Hidden)!.SetValue(manager, recovery.Task);
        // If the guard regresses, hold it before DNS/AKA or any external I/O.
        var retry = (SipRegistrationRetryGate)typeof(VoWifiManager).GetField("_imsRetryGate", Hidden)!.GetValue(manager)!;
        retry.Observe(Card().Iccid, new(503, 300, false, true, DateTime.UtcNow));
        if (awaitingOldCleanup)
        {
            typeof(VoWifiManager).GetField("_sipTransport", Hidden)!.SetValue(manager, ims.Transport);
            typeof(VoWifiManager).GetField("_healthTask", Hidden)!.SetValue(manager, health.Task);
        }
        else await lifecycle.WaitAsync();
        var starting = (Task<bool>)typeof(VoWifiManager).GetMethod("StartVoWifiAttemptAsync", Hidden)!.Invoke(manager, [options, cancel.Token, null])!;
        Assert.False(starting.IsCompleted);
        var stopping = manager.StopVoWifiAsync();
        try
        {
            if (awaitingOldCleanup) health.TrySetResult(); else lifecycle.Release();
            Assert.False(await starting.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(stopping.IsCompleted);
            Assert.False(ims.Sent.Reader.TryRead(out _));
            Assert.Null(typeof(VoWifiManager).GetField("_smsSource", Hidden)!.GetValue(manager));
        }
        finally
        {
            cancel.Cancel(); health.TrySetResult(); recovery.TrySetResult();
            if (lifecycle.CurrentCount == 0 && !awaitingOldCleanup && !starting.IsCompleted) lifecycle.Release();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NewVoWifiSourceDoesNotReuseAnOldSourcesIncomingTransaction(bool sameCard)
    {
        using var manager = new VoWifiManager(); using var a = new MemoryIMS(); using var b = new MemoryIMS();
        var deliveries = new List<string?>(); var reports = 0;
        manager.SmsReceived += (_, e) => deliveries.Add(e.SimBinding);
        Task<SipMessage> Report(SipMessage request) { reports++; return Task.FromResult(request.CreateResponse(200, "OK")); }
        Register(manager, a, Card());
        await manager.HandleIncomingSipMessageAsync(VoWifiSmsReceptionTests.Incoming(), _ => Task.CompletedTask, Report);
        Register(manager, b, sameCard ? Card() : Card("1"));
        await manager.HandleIncomingSipMessageAsync(VoWifiSmsReceptionTests.Incoming(), _ => Task.CompletedTask, Report);
        Assert.Equal(2, reports); Assert.Equal(2, deliveries.Count);
        Assert.Equal(SmsSubscriptionIdentity.ForIccid(Card().Iccid), deliveries[0]);
        Assert.Equal(SmsSubscriptionIdentity.ForIccid((sameCard ? Card() : Card("1")).Iccid), deliveries[1]);
    }
}
