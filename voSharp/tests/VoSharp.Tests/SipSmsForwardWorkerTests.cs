using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Sms;

namespace VoSharp.Tests;

// Reuse only the independent loopback phone helpers and non-device controller;
// this fixture's journal is deliberately memory-only, never a durability claim.
public sealed partial class SipServerFeatureTests
{
    private static SipGatewayOptions ForwardOptions(ForwardJournal journal) => Options() with
    {
        SmsForwardStore = journal,
        LocalAccountEpochs = new(StringComparer.OrdinalIgnoreCase)
        { ["alice"] = new('a', 32), ["bob"] = new('b', 32), ["900"] = new('c', 32) }
    };
    private static SipSmsForwardPayload ForwardPayload(string text = "synthetic durable inbox SMS") =>
        new("modem-a", new('a', 64), "+15551234567", text);
    private static SipGatewayOptions ForwardPeerOptions(ForwardJournal journal, UdpClient peer) => ForwardOptions(journal) with
    {
        PeerCredentialEpoch = new('e', 32),
        Peer = new(End(peer), "600", new SipPeerInboundCredentials("peer-in", "synthetic-inbound"),
            new SipPeerOutboundCredentials("peer-out", "synthetic-outbound", "peer.test"))
    };
    private static async Task ForwardUntil(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Synthetic forwarding state did not settle.");
            await Task.Delay(10);
        }
    }
    private static async Task NoForwardDatagram(UdpClient phone)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await phone.ReceiveAsync(timeout.Token));
    }

    [Fact]
    public async Task DurableMtCapturesOfflineReceiversWithoutReusingTheirPaidSendPermission()
    {
        var journal = new ForwardJournal(); var options = ForwardOptions(journal) with
        { ExtensionPolicies = new() { ["alice"] = new() { AllowSms = false } } };
        await using var gateway = new SipGateway(options, new Controller()); await gateway.StartAsync();
        var recipients = gateway.CaptureSmsForwardRecipients("modem-a");
        Assert.Equal(new[] { "900", "alice", "bob" }, recipients.Select(value => value.User).Order().ToArray());
        Assert.All(recipients, value => Assert.Equal(SipSmsForwardRecipientKind.Local, value.Kind));
        Assert.Empty(gateway.CaptureSmsForwardRecipients("modem-b"));
        Assert.Equal(0, journal.Claims);
    }

    [Fact]
    public async Task DurableMtWaitsOfflineThenClaimsUnknownBeforeWireAndAcceptsOnce()
    {
        var journal = new ForwardJournal();
        await using var gateway = new SipGateway(ForwardOptions(journal), new Controller()); await gateway.StartAsync();
        var recipient = gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice");
        var id = journal.Add(recipient); gateway.NotifySmsInboxCommitted();
        await ForwardUntil(() => journal.Scans >= 2); Assert.Equal(0, journal.Claims);
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var incoming = await Receive(alice);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(1, journal.Claims);
        Assert.Contains("sip:sms2-", incoming.GetHeader("From")); Assert.Equal(ForwardPayload().Text, incoming.Body);
        await Send(alice, incoming.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(id) == SipSmsForwardState.Accepted);
        var scans = journal.Scans; gateway.NotifySmsInboxCommitted(); await ForwardUntil(() => journal.Scans > scans);
        Assert.Equal(1, journal.Claims); await NoForwardDatagram(alice);
    }

    [Fact]
    public async Task DurableMtDoesNotUseTheCarrierEventBeforeTheHostInboxCommit()
    {
        var journal = new ForwardJournal(); var controller = new Controller();
        await using var gateway = new SipGateway(ForwardOptions(journal), controller); await gateway.StartAsync();
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        controller.Receive(new(1, SmsStatus.Unread, "+15551234567", "not committed", DateTime.UtcNow), "modem-a");
        await NoForwardDatagram(alice); Assert.Equal(0, journal.Claims);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
        gateway.NotifySmsInboxCommitted(); var delivered = await Receive(alice);
        await Send(alice, delivered.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(id) == SipSmsForwardState.Accepted);
    }

    [Fact]
    public async Task DurableMtCancelsTheOldAccountEvenWhenItsNameAndPasswordAreRecreated()
    {
        var journal = new ForwardJournal(); var options = ForwardOptions(journal);
        SipSmsForwardRecipient recipient;
        await using (var original = new SipGateway(options, new Controller()))
        { await original.StartAsync(); recipient = original.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"); }
        var id = journal.Add(recipient);
        var replacement = options with { LocalAccountEpochs = new(options.LocalAccountEpochs) { ["alice"] = new('d', 32) } };
        await using var gateway = new SipGateway(replacement, new Controller()); await gateway.StartAsync();
        await ForwardUntil(() => journal.State(id) == SipSmsForwardState.Cancelled);
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        await NoForwardDatagram(alice); Assert.Equal(0, journal.Claims);
    }

    [Fact]
    public async Task DurableMtUnknownAfterShutdownCannotBeReplayedOnRestart()
    {
        var journal = new ForwardJournal(); var options = ForwardOptions(journal); string id;
        using var alice = Socket();
        await using (var gateway = new SipGateway(options, new Controller()))
        {
            gateway.MessageTransactionTimeout = TimeSpan.FromMilliseconds(200);
            await gateway.StartAsync(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
            id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
            gateway.NotifySmsInboxCommitted(); Assert.Equal("MESSAGE", (await Receive(alice)).Method);
            await ForwardUntil(() => journal.Completions == 1); Assert.Equal(SipSmsForwardState.Unknown, journal.State(id));
        }
        await using var restarted = new SipGateway(options, new Controller()); await restarted.StartAsync();
        await Register(alice, "alice", restarted.Status.LocalEndPoint!); restarted.NotifySmsInboxCommitted();
        await NoForwardDatagram(alice); Assert.Equal(1, journal.Claims); Assert.Equal(SipSmsForwardState.Unknown, journal.State(id));
    }

    [Fact]
    public async Task DurableMtPartialChunksRemainUnknownWithoutAutomaticRetry()
    {
        var journal = new ForwardJournal(); await using var gateway = new SipGateway(ForwardOptions(journal), new Controller());
        await gateway.StartAsync(); using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"), ForwardPayload(new('X', 600)));
        gateway.NotifySmsInboxCommitted(); var first = await Receive(alice); Assert.StartsWith("[1/3]", first.Body);
        await Send(alice, first.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        var second = await Receive(alice); Assert.StartsWith("[2/3]", second.Body);
        await Send(alice, second.CreateResponse(403, "Rejected"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.Completions == 1);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(id));
        gateway.NotifySmsInboxCommitted(); await NoForwardDatagram(alice); Assert.Equal(1, journal.Claims);
    }

    [Fact]
    public async Task DurableMtOneContactMustAcceptAllChunksNotOnlySomeOfThem()
    {
        var journal = new ForwardJournal(); await using var gateway = new SipGateway(ForwardOptions(journal), new Controller());
        await gateway.StartAsync(); using var good = Socket(); using var bad = Socket();
        await Register(good, "alice", gateway.Status.LocalEndPoint!); await Register(bad, "alice", gateway.Status.LocalEndPoint!);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"), ForwardPayload(new('X', 400)));
        gateway.NotifySmsInboxCommitted(); var rejected = await Receive(bad);
        await Send(bad, rejected.CreateResponse(403, "Rejected"), gateway.Status.LocalEndPoint!);
        for (var index = 0; index < 2; index++)
        { var part = await Receive(good); await Send(good, part.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!); }
        await ForwardUntil(() => journal.State(id) == SipSmsForwardState.Accepted); Assert.Equal(1, journal.Claims);
    }

    [Fact]
    public async Task DurableMtClaimBudgetExpiresWithoutAnyWireOrUnclaimRetry()
    {
        var journal = new ForwardJournal { HoldClaim = true };
        await using var gateway = new SipGateway(ForwardOptions(journal), new Controller()); gateway.MessageTransactionTimeout = TimeSpan.FromMilliseconds(100);
        await gateway.StartAsync(); using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
        gateway.NotifySmsInboxCommitted(); await ForwardUntil(() => journal.ClaimCancelled);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); await NoForwardDatagram(alice);
        gateway.NotifySmsInboxCommitted(); await NoForwardDatagram(alice); Assert.Equal(1, journal.Claims);
    }

    [Fact]
    public async Task DurableMtInvalidClaimDoesNotEscapeToTheNetwork()
    {
        var journal = new ForwardJournal { CorruptClaim = true };
        await using var gateway = new SipGateway(ForwardOptions(journal), new Controller()); await gateway.StartAsync();
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
        gateway.NotifySmsInboxCommitted(); await ForwardUntil(() => journal.Claims == 1);
        await NoForwardDatagram(alice); Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(0, journal.Completions);
    }

    [Fact]
    public async Task DurableMtLaterOnlineRecipientIsNotStarvedBehindTheEntireOfflineWindow()
    {
        var journal = new ForwardJournal(); await using var gateway = new SipGateway(ForwardOptions(journal), new Controller());
        await gateway.StartAsync(); var recipients = gateway.CaptureSmsForwardRecipients("modem-a");
        var offline = recipients.Single(value => value.User == "bob");
        for (var index = 0; index < 2047; index++) journal.Add(offline);
        var id = journal.Add(recipients.Single(value => value.User == "alice"));
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var delivered = await Receive(alice); Assert.Equal(1, journal.Claims); Assert.Equal(2048, journal.LastLimit);
        await Send(alice, delivered.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(id) == SipSmsForwardState.Accepted);
    }

    [Fact]
    public async Task DurableMtAnotherRecipientProgressesWhileTheFirstNeverResponds()
    {
        var journal = new ForwardJournal(); await using var gateway = new SipGateway(ForwardOptions(journal), new Controller());
        await gateway.StartAsync(); using var alice = Socket(); using var bob = Socket();
        await Register(alice, "alice", gateway.Status.LocalEndPoint!); await Register(bob, "bob", gateway.Status.LocalEndPoint!);
        var recipients = gateway.CaptureSmsForwardRecipients("modem-a");
        var blocked = journal.Add(recipients.Single(value => value.User == "alice"));
        var ready = journal.Add(recipients.Single(value => value.User == "bob"));
        gateway.NotifySmsInboxCommitted(); await Receive(alice); var message = await Receive(bob);
        await Send(bob, message.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(ready) == SipSmsForwardState.Accepted);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(blocked));
        await gateway.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(blocked));
    }

    [Fact]
    public async Task DurableMtStoppingBeforeLifetimeCancellationDoesNotRevokeQueuedRecipients()
    {
        var journal = new ForwardJournal { PendingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), PendingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var options = ForwardOptions(journal); await using var gateway = new SipGateway(options, new Controller());
        await gateway.StartAsync(); await journal.PendingEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
        var hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var fork = typeof(SipGateway).GetField("_forkGate", hidden)!.GetValue(gateway)!;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Task.Run(() => { lock (fork) { held.SetResult(); release.Task.GetAwaiter().GetResult(); } });
        await held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = Task.Run(async () => await gateway.DisposeAsync());
        try
        {
            await ForwardUntil(() => (int)typeof(SipGateway).GetField("_disposed", hidden)!.GetValue(gateway)! == 1);
            Assert.False(stopping.IsCompleted); journal.PendingRelease.SetResult();
            var worker = (Task)typeof(SipGateway).GetField("_smsForwardTask", hidden)!.GetValue(gateway)!;
            await worker.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(SipSmsForwardState.Queued, journal.State(id)); Assert.Equal(0, journal.Claims);
        }
        finally { release.TrySetResult(); journal.PendingRelease.TrySetResult(); await holding; await stopping; }
        await using var restarted = new SipGateway(options, new Controller()); await restarted.StartAsync();
        using var alice = Socket(); await Register(alice, "alice", restarted.Status.LocalEndPoint!);
        var message = await Receive(alice); await Send(alice, message.CreateResponse(200, "OK"), restarted.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(id) == SipSmsForwardState.Accepted);
    }

    [Fact]
    public async Task DurableMtGloballyDisabledFeatureKeepsTheExistingQueueUntouched()
    {
        var journal = new ForwardJournal(); var options = ForwardOptions(journal); SipSmsForwardRecipient recipient;
        await using (var original = new SipGateway(options, new Controller()))
        { await original.StartAsync(); recipient = original.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"); }
        var id = journal.Add(recipient); var scans = journal.Scans;
        await using var disabled = new SipGateway(options with { ForwardIncomingSms = false }, new Controller()); await disabled.StartAsync();
        disabled.NotifySmsInboxCommitted(); await Task.Delay(100);
        Assert.Equal(scans, journal.Scans); Assert.Equal(SipSmsForwardState.Queued, journal.State(id)); Assert.Equal(0, journal.Claims);
    }

    [Fact]
    public async Task DurableMtPeerReadinessHasNoPrivatePayloadThenTheClaimPrecedesMessageAuthentication()
    {
        var journal = new ForwardJournal(); using var peer = Socket();
        await using var gateway = new SipGateway(ForwardPeerOptions(journal, peer), new Controller()); await gateway.StartAsync();
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.Kind == SipSmsForwardRecipientKind.Peer));
        gateway.NotifySmsInboxCommitted(); var probe = await Receive(peer);
        Assert.Equal("OPTIONS", probe.Method); Assert.Equal(0, journal.Claims); Assert.Empty(probe.Body);
        var probeWire = Encoding.UTF8.GetString(probe.ToBytes());
        Assert.DoesNotContain("sms2-", probeWire); Assert.DoesNotContain("+15551234567", probeWire);
        Assert.Null(probe.GetHeader("Authorization")); Assert.Null(probe.GetHeader("Proxy-Authorization"));
        var readiness = probe.CreateResponse(407, "Proxy Authentication Required");
        readiness.SetHeader("Proxy-Authenticate", "Digest realm=\"peer.test\",nonce=\"readiness-probe-nonce\",algorithm=SHA-256,qop=\"auth\"");
        await Send(peer, readiness, gateway.Status.LocalEndPoint!);
        var message = await Receive(peer); Assert.Equal("MESSAGE", message.Method);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(1, journal.Claims);
        var challenge = message.CreateResponse(407, "Proxy Authentication Required");
        challenge.SetHeader("Proxy-Authenticate", "Digest realm=\"peer.test\",nonce=\"actual-message-nonce\",algorithm=SHA-256,qop=\"auth\"");
        await Send(peer, challenge, gateway.Status.LocalEndPoint!);
        var authenticated = await Receive(peer); Assert.Equal("MESSAGE", authenticated.Method); Assert.Equal("2 MESSAGE", authenticated.GetHeader("CSeq"));
        Assert.Contains("algorithm=SHA-256", authenticated.GetHeader("Proxy-Authorization"));
        await Send(peer, authenticated.CreateResponse(202, "Accepted"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(id) == SipSmsForwardState.Accepted);
    }

    [Theory]
    [InlineData(503, "peer.test", "SHA-256")]
    [InlineData(401, "unknown.test", "SHA-256")]
    [InlineData(407, "peer.test", "MD5")]
    public async Task DurableMtUnusablePeerReadinessKeepsQueuedAndNeverClaimsSms(int status, string realm, string algorithm)
    {
        var journal = new ForwardJournal(); using var peer = Socket();
        await using var gateway = new SipGateway(ForwardPeerOptions(journal, peer), new Controller()); await gateway.StartAsync();
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.Kind == SipSmsForwardRecipientKind.Peer));
        gateway.NotifySmsInboxCommitted(); var probe = await Receive(peer); Assert.Equal("OPTIONS", probe.Method);
        var response = probe.CreateResponse(status, "Not Ready");
        response.SetHeader(status == 401 ? "WWW-Authenticate" : "Proxy-Authenticate", $"Digest realm=\"{realm}\",nonce=\"synthetic-probe-nonce\",algorithm={algorithm},qop=\"auth\"");
        await Send(peer, response, gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => ((Task<bool>?)typeof(SipGateway).GetField("_smsPeerProbeTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway))?.IsCompleted == true);
        Assert.Equal(SipSmsForwardState.Queued, journal.State(id)); Assert.Equal(0, journal.Claims); await NoForwardDatagram(peer);
    }

    [Fact]
    public async Task DurableMtAnOfflinePeerDoesNotBlockAnOnlineLocalRecipient()
    {
        var journal = new ForwardJournal(); using var peer = Socket();
        await using var gateway = new SipGateway(ForwardPeerOptions(journal, peer), new Controller()); await gateway.StartAsync();
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var recipients = gateway.CaptureSmsForwardRecipients("modem-a");
        var remote = journal.Add(recipients.Single(value => value.Kind == SipSmsForwardRecipientKind.Peer));
        var local = journal.Add(recipients.Single(value => value.User == "alice")); gateway.NotifySmsInboxCommitted();
        Assert.Equal("OPTIONS", (await Receive(peer)).Method); var message = await Receive(alice);
        await Send(alice, message.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(local) == SipSmsForwardState.Accepted);
        Assert.Equal(SipSmsForwardState.Queued, journal.State(remote)); Assert.Equal(1, journal.Claims);
    }

    [Fact]
    public async Task DurableMtPeerSharesABoundedReadinessCacheWithoutReprobingEveryMessage()
    {
        var journal = new ForwardJournal(); using var peer = Socket();
        await using var gateway = new SipGateway(ForwardPeerOptions(journal, peer), new Controller()); await gateway.StartAsync();
        var recipient = gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.Kind == SipSmsForwardRecipientKind.Peer);
        var first = journal.Add(recipient); var second = journal.Add(recipient); gateway.NotifySmsInboxCommitted();
        var probe = await Receive(peer); Assert.Equal("OPTIONS", probe.Method);
        await Send(peer, probe.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        for (var index = 0; index < 2; index++)
        { var message = await Receive(peer); Assert.Equal("MESSAGE", message.Method); await Send(peer, message.CreateResponse(202, "Accepted"), gateway.Status.LocalEndPoint!); }
        await ForwardUntil(() => journal.State(first) == SipSmsForwardState.Accepted && journal.State(second) == SipSmsForwardState.Accepted);
        Assert.Equal(2, journal.Claims);
    }

    [Fact]
    public async Task DurableMtComplementaryPartialContactsCannotCompleteTheWholeMessage()
    {
        var journal = new ForwardJournal(); await using var gateway = new SipGateway(ForwardOptions(journal), new Controller());
        await gateway.StartAsync(); using var a = Socket(); using var b = Socket();
        await Register(a, "alice", gateway.Status.LocalEndPoint!); await Register(b, "alice", gateway.Status.LocalEndPoint!);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"), ForwardPayload(new('X', 600)));
        gateway.NotifySmsInboxCommitted();
        var a1 = await Receive(a); var b1 = await Receive(b);
        await Send(a, a1.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await Send(b, b1.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        var a2 = await Receive(a); var b2 = await Receive(b);
        await Send(a, a2.CreateResponse(403, "Rejected"), gateway.Status.LocalEndPoint!);
        await Send(b, b2.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        var b3 = await Receive(b); await Send(b, b3.CreateResponse(403, "Rejected"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.Completions == 1);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(1, journal.Claims);
    }

    [Fact]
    public async Task DurableMtCompletionStorageFailureKeepsUnknownAfterPayloadAcceptance()
    {
        var journal = new ForwardJournal { ThrowCompletion = true };
        await using var gateway = new SipGateway(ForwardOptions(journal), new Controller()); await gateway.StartAsync();
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
        gateway.NotifySmsInboxCommitted(); var message = await Receive(alice);
        await Send(alice, message.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.CompletionFailed);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(1, journal.Claims);
        gateway.NotifySmsInboxCommitted(); await NoForwardDatagram(alice);
    }

    private static async Task UnregisterForwardPhone(UdpClient phone, SipGateway gateway)
    {
        var request = Request("REGISTER", "alice", "alice");
        request.SetHeader("Contact", $"<sip:alice@127.0.0.1:{End(phone).Port}>"); request.SetHeader("Expires", "0");
        await Authenticate(phone, request, "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(200, (await Receive(phone)).StatusCode);
    }

    [Fact]
    public async Task DurableMtLastContactRemovedDuringClaimNeverReceivesThePayload()
    {
        var journal = new ForwardJournal { ClaimEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ClaimRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var gateway = new SipGateway(ForwardOptions(journal), new Controller()); await gateway.StartAsync();
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
        gateway.NotifySmsInboxCommitted(); await journal.ClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await UnregisterForwardPhone(alice, gateway);
            journal.ClaimRelease.SetResult(); await NoForwardDatagram(alice);
            Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(1, journal.Claims); Assert.Equal(0, journal.Completions);
        }
        finally { journal.ClaimRelease.TrySetResult(); }
    }

    [Fact]
    public async Task DurableMtContactIsCheckedAgainAfterTheActualDestinationQueueGate()
    {
        var journal = new ForwardJournal(); await using var gateway = new SipGateway(ForwardOptions(journal), new Controller());
        await gateway.StartAsync(); using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var destinations = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(SipGateway)
            .GetField("_messageDestinations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
        using var held = new SemaphoreSlim(0, 1); Assert.True(destinations.TryAdd("alice@" + End(alice), held));
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
        gateway.NotifySmsInboxCommitted(); await ForwardUntil(() => journal.Claims == 1);
        await UnregisterForwardPhone(alice, gateway); held.Release();
        await ForwardUntil(() => journal.Completions == 1); await NoForwardDatagram(alice);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(1, journal.Claims);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task DurableMtSourceGuardFailureNeverSendsOrRequeuesAndNewSourceStillWorks(string failure)
    {
        var journal = new ForwardJournal
        {
            RejectGuard = failure == "reject", ThrowGuard = failure == "throw",
            GuardRelease = failure == "cancel" ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null
        };
        await using var gateway = new SipGateway(ForwardOptions(journal), new Controller());
        if (failure == "cancel") gateway.MessageTransactionTimeout = TimeSpan.FromMilliseconds(100);
        await gateway.StartAsync(); using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var recipient = gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice");
        var id = journal.Add(recipient); gateway.NotifySmsInboxCommitted();
        try
        {
            await ForwardUntil(() => journal.GuardReturns > 0);
            if (failure == "cancel") Assert.True(journal.GuardCancelled);
            await NoForwardDatagram(alice);
            Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(1, journal.Claims);
            gateway.NotifySmsInboxCommitted(); await NoForwardDatagram(alice);
            Assert.Equal(1, journal.Claims); Assert.Equal(SipSmsForwardState.Unknown, journal.State(id));

            journal.RejectGuard = false; journal.ThrowGuard = false; journal.GuardRelease?.TrySetResult();
            gateway.MessageTransactionTimeout = TimeSpan.FromSeconds(2);
            var fresh = journal.Add(recipient, ForwardPayload("a new synthetic source remains deliverable"));
            gateway.NotifySmsInboxCommitted(); var message = await Receive(alice);
            await Send(alice, message.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
            await ForwardUntil(() => journal.State(fresh) == SipSmsForwardState.Accepted);
            Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(2, journal.Claims);
        }
        finally { journal.GuardRelease?.TrySetResult(); }
    }

    [Fact]
    public async Task DurableMtContactRemovedDuringAwaitedSourceGuardNeverReceivesPayload()
    {
        var journal = new ForwardJournal
        {
            GuardEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            GuardRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var gateway = new SipGateway(ForwardOptions(journal), new Controller()); await gateway.StartAsync();
        using var alice = Socket(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
        var id = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
        gateway.NotifySmsInboxCommitted(); await journal.GuardEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await UnregisterForwardPhone(alice, gateway);
            journal.GuardRelease.TrySetResult(); await ForwardUntil(() => journal.GuardReturns > 0);
            await NoForwardDatagram(alice);
            Assert.Equal(SipSmsForwardState.Unknown, journal.State(id)); Assert.Equal(1, journal.Claims);
            gateway.NotifySmsInboxCommitted(); await NoForwardDatagram(alice); Assert.Equal(1, journal.Claims);
        }
        finally { journal.GuardRelease.TrySetResult(); }
    }

    [Fact]
    public async Task DurableMtShutdownCancelsAwaitedSourceGuardWithoutWireOrReplayAndFreshSourceWorksAfterRestart()
    {
        var journal = new ForwardJournal
        {
            GuardEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            GuardRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var options = ForwardOptions(journal);
        using var alice = Socket();
        string original;
        await using (var gateway = new SipGateway(options, new Controller()))
        {
            await gateway.StartAsync(); await Register(alice, "alice", gateway.Status.LocalEndPoint!);
            original = journal.Add(gateway.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"));
            gateway.NotifySmsInboxCommitted();
            await journal.GuardEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SipSmsForwardState.Unknown, journal.State(original));
            var stopping = gateway.DisposeAsync().AsTask();
            try
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(journal.GuardCancelled); Assert.True(journal.GuardReturns > 0);
                await NoForwardDatagram(alice);
                Assert.Equal(SipSmsForwardState.Unknown, journal.State(original)); Assert.Equal(1, journal.Claims);
            }
            finally
            {
                // A failed cancellation assertion must not strand the test's
                // synthetic store waiter or leave a gateway owning its socket.
                journal.GuardRelease.TrySetResult();
                await stopping.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        await using var restarted = new SipGateway(options, new Controller()); await restarted.StartAsync();
        await Register(alice, "alice", restarted.Status.LocalEndPoint!);
        var scans = journal.Scans; restarted.NotifySmsInboxCommitted();
        await ForwardUntil(() => journal.Scans > scans); await NoForwardDatagram(alice);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(original)); Assert.Equal(1, journal.Claims);
        var fresh = journal.Add(restarted.CaptureSmsForwardRecipients("modem-a").Single(value => value.User == "alice"),
            ForwardPayload("synthetic fresh source after awaited-guard shutdown"));
        restarted.NotifySmsInboxCommitted(); var message = await Receive(alice);
        Assert.Equal("MESSAGE", message.Method); Assert.Equal("synthetic fresh source after awaited-guard shutdown", message.Body);
        await Send(alice, message.CreateResponse(200, "OK"), restarted.Status.LocalEndPoint!);
        await ForwardUntil(() => journal.State(fresh) == SipSmsForwardState.Accepted);
        Assert.Equal(SipSmsForwardState.Unknown, journal.State(original)); Assert.Equal(2, journal.Claims);
    }

    private sealed class ForwardJournal : ISipSmsForwardStore
    {
        private sealed record Entry(SipSmsForwardItem Item, SipSmsForwardState State, string? Token = null);
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new();
        public int Claims, Completions, Scans, LastLimit, GuardChecks, GuardReturns;
        public bool HoldClaim, CorruptClaim, ThrowCompletion, ThrowGuard, RejectGuard;
        public TaskCompletionSource? PendingEntered, PendingRelease;
        public TaskCompletionSource? ClaimEntered, ClaimRelease;
        public TaskCompletionSource? GuardEntered, GuardRelease;
        public volatile bool ClaimCancelled;
        public volatile bool CompletionFailed;
        public volatile bool GuardCancelled;
        public string Add(SipSmsForwardRecipient recipient, SipSmsForwardPayload? payload = null)
        {
            payload ??= ForwardPayload(); var id = Guid.NewGuid().ToString("N");
            lock (_gate) _entries.Add(id, new(new(id, recipient, payload, payload.Fingerprint(), DateTimeOffset.UtcNow.AddHours(24)), SipSmsForwardState.Queued));
            return id;
        }
        public SipSmsForwardState State(string id) { lock (_gate) return _entries[id].State; }
        public async Task<IReadOnlyList<SipSmsForwardItem>> GetPendingAsync(DateTimeOffset now, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Scans); LastLimit = limit;
            PendingEntered?.TrySetResult();
            if (PendingRelease != null) await PendingRelease.Task.WaitAsync(ct);
            lock (_gate) return _entries.Values.Where(value => value.State == SipSmsForwardState.Queued && value.Item.ExpiresAtUtc > now).Take(limit).Select(value => value.Item).ToArray();
        }
        public async Task<SipSmsForwardClaim?> TryClaimAsync(string id, string expectedFingerprint, DateTimeOffset now, CancellationToken ct)
        {
            SipSmsForwardClaim claim;
            lock (_gate)
            {
                if (!_entries.TryGetValue(id, out var entry) || entry.State != SipSmsForwardState.Queued || entry.Item.PayloadFingerprint != expectedFingerprint || entry.Item.ExpiresAtUtc <= now) return null;
                var token = Guid.NewGuid().ToString("N"); _entries[id] = entry with { State = SipSmsForwardState.Unknown, Token = token };
                Interlocked.Increment(ref Claims);
                claim = new(id, token, entry.Item.Recipient, entry.Item.Payload, entry.Item.PayloadFingerprint) { InboxId = entry.Item.Id };
            }
            ClaimEntered?.TrySetResult();
            if (ClaimRelease != null) await ClaimRelease.Task.WaitAsync(ct);
            if (HoldClaim)
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { ClaimCancelled = true; throw; }
            return CorruptClaim ? claim with { Payload = claim.Payload with { Text = "changed source" } } : claim;
        }
        public async Task<bool> IsClaimSourceCurrentAsync(SipSmsForwardClaim claim, CancellationToken ct)
        {
            Interlocked.Increment(ref GuardChecks);
            try
            {
                ct.ThrowIfCancellationRequested(); GuardEntered?.TrySetResult();
                if (GuardRelease != null) await GuardRelease.Task.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (ThrowGuard) throw new IOException("Synthetic source guard persistence failure.");
                if (RejectGuard) return false;
                lock (_gate)
                    return _entries.TryGetValue(claim.Id, out var entry) && entry.State == SipSmsForwardState.Unknown &&
                        entry.Token == claim.AttemptToken && claim.InboxId == entry.Item.Id &&
                        entry.Item.Recipient == claim.Recipient && entry.Item.ExpiresAtUtc > DateTimeOffset.UtcNow &&
                        entry.Item.PayloadFingerprint == claim.PayloadFingerprint && entry.Item.Payload == claim.Payload &&
                        entry.Item.Payload.Fingerprint() == entry.Item.PayloadFingerprint && claim.Payload.Fingerprint() == claim.PayloadFingerprint;
            }
            catch (OperationCanceledException) { GuardCancelled = true; throw; }
            finally { Interlocked.Increment(ref GuardReturns); }
        }
        public Task CompleteAsync(string id, string attemptToken, SipSmsForwardState state, CancellationToken ct)
        {
            if (ThrowCompletion) { CompletionFailed = true; throw new IOException("Synthetic completion persistence failure."); }
            ct.ThrowIfCancellationRequested(); lock (_gate)
            {
                var entry = _entries[id]; Assert.Equal(entry.Token, attemptToken); Assert.Equal(SipSmsForwardState.Unknown, entry.State);
                _entries[id] = entry with { State = state }; Interlocked.Increment(ref Completions);
            }
            return Task.CompletedTask;
        }
        public Task CancelQueuedAsync(string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); lock (_gate)
                if (_entries.TryGetValue(id, out var entry) && entry.State == SipSmsForwardState.Queued) _entries[id] = entry with { State = SipSmsForwardState.Cancelled };
            return Task.CompletedTask;
        }
    }
}
