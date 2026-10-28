using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Reflection;
using VoSharp.Common.Aka;
using VoSharp.Sim;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class VoWifiRecoverySourceTests
{
    private const string Iccid = "890000000000000001";
    private static SimIdentity Sim => SimIdentity.FromImsiAndIccid("999123123456789", Iccid, mncLength: 3);
    private sealed class FakeAka(Func<string?, CancellationToken, Task<bool>> ready) : IAkaProvider
    {
        public ConcurrentQueue<string?> ExpectedCards { get; } = new();
        public int Authentications;
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default)
        {
            ExpectedCards.Enqueue(expectedIccid);
            return ready(expectedIccid, ct);
        }
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Authentications);
            throw new InvalidOperationException("Readiness/recovery tests must never consume an AKA challenge.");
        }
    }

    [Fact]
    public async Task IndependentProviderIsReadyWithoutAnyModemAndChecksExpectedCard()
    {
        var provider = new FakeAka((card, _) => Task.FromResult(card == Iccid));
        using var manager = new VoWifiManager { AkaProvider = provider };
        Assert.Null(manager.Modem);
        Assert.True(await manager.IsRecoverySourceReadyAsync(Iccid, default));
        Assert.False(await manager.IsRecoverySourceReadyAsync("890000000000000002", default));
        Assert.Equal(0, provider.Authentications);
    }

    [Fact]
    public async Task InFlightReadFromAReplacedProviderIsDiscarded()
    {
        var pendingRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new FakeAka((_, _) => pendingRead.Task);
        var replacement = new FakeAka((_, _) => Task.FromResult(false));
        using var manager = new VoWifiManager { AkaProvider = original };
        var check = manager.IsRecoverySourceReadyAsync(Iccid, default);
        manager.AkaProvider = replacement;
        pendingRead.SetResult(true);
        Assert.False(await check);
        Assert.False(await manager.IsRecoverySourceReadyAsync(Iccid, default));
        Assert.Equal(0, original.Authentications + replacement.Authentications);
    }

    [Fact]
    public async Task ProviderFailureWaitsForAvailabilityRatherThanConsumingAka()
    {
        var provider = new FakeAka((_, _) => throw new IOException("card reader removed"));
        using var manager = new VoWifiManager { AkaProvider = provider };
        Assert.False(await manager.IsRecoverySourceReadyAsync(Iccid, default));
        Assert.Equal(0, provider.Authentications);
    }

    [Fact]
    public async Task CardReadCancellationIsNotConvertedIntoAnAvailabilityFailure()
    {
        var pendingRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        using var manager = new VoWifiManager { AkaProvider = new FakeAka((_, _) => pendingRead.Task) };
        var check = manager.IsRecoverySourceReadyAsync(Iccid, cts.Token);
        cts.Cancel();
        pendingRead.SetResult(true); // Even a provider that returns late cannot restart after cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task ReaderReturnIsPolledWithoutAModemOrNetworkWakeEvent()
    {
        var checks = 0;
        var provider = new FakeAka((_, _) => Task.FromResult(Interlocked.Increment(ref checks) >= 3));
        using var manager = new VoWifiManager { AkaProvider = provider };
        Assert.True(await manager.WaitForRecoverySourceAsync(() => Iccid, default, TimeSpan.FromMilliseconds(10))
            .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, checks);
        Assert.All(provider.ExpectedCards, expected => Assert.Equal(Iccid, expected));
        Assert.Equal(0, provider.Authentications);
        Assert.Null(manager.EpdgInfo); // No DNS, IKE, SIP or hardware was started by the readiness wait.
    }

    [Fact]
    public async Task ReadinessWaitTracksCurrentCardIdentityAfterRebind()
    {
        var expected = Iccid;
        const string returnedCard = "890000000000000002";
        var provider = new FakeAka((card, _) =>
        {
            expected = returnedCard;
            return Task.FromResult(card == returnedCard);
        });
        using var manager = new VoWifiManager { AkaProvider = provider };
        Assert.True(await manager.WaitForRecoverySourceAsync(() => expected, default, TimeSpan.FromMilliseconds(10))
            .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { Iccid, returnedCard }, provider.ExpectedCards);
        Assert.Equal(0, provider.Authentications);
    }

    [Fact]
    public async Task MissingProviderCanReturnWithoutAnExternalWakeEvent()
    {
        using var manager = new VoWifiManager();
        var checkedIdentity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = manager.WaitForRecoverySourceAsync(() => { checkedIdentity.TrySetResult(); return Iccid; },
            default, TimeSpan.FromMilliseconds(10));
        await checkedIdentity.Task.WaitAsync(TimeSpan.FromSeconds(2));
        manager.AkaProvider = new FakeAka((_, _) => Task.FromResult(true));
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task NoSavedConnectionIntentTerminatesTheSourceWait()
    {
        using var manager = new VoWifiManager();
        Assert.False(await manager.WaitForRecoverySourceAsync(() => null, default));
    }

    [Theory]
    [InlineData("USER_UNKNOWN")]
    [InlineData("NON_3GPP_ACCESS_TO_EPC_NOT_ALLOWED")]
    [InlineData("NO_APN_SUBSCRIPTION")]
    [InlineData("AUTHORIZATION_REJECTED")]
    public void PermanentRejectionRemainsPausedAcrossNetworkAvailabilityEvents(string rejection)
    {
        var provider = new FakeAka((_, _) => throw new InvalidOperationException("Must not probe a paused recovery."));
        using var manager = new VoWifiManager { AkaProvider = provider };
        ArmSavedConnection(manager);
        QueueRecovery(manager, "EAP-AKA rejected: " + rejection);
        Assert.True(manager.AutomaticRecoveryPaused);
        Assert.Equal(VoWifiState.Failed, manager.State);
        var failure = manager.LastError;
        var networkEvent = (NetworkAvailabilityEventArgs)Activator.CreateInstance(typeof(NetworkAvailabilityEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [true], null)!;
        typeof(VoWifiManager).GetMethod("OnNetworkAvailabilityChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [null, networkEvent]);
        QueueRecovery(manager, "Network connectivity is available again.");
        Assert.True(manager.RecoveryCompletion.IsCompletedSuccessfully);
        Assert.Equal(failure, manager.LastError);
        Assert.Contains("paused", manager.LastError);
        Assert.Empty(provider.ExpectedCards);
    }

    [Theory]
    [InlineData("IKE_SA_INIT timed out")]
    [InlineData("IMS REGISTER failed: 500 Server Internal Error")]
    [InlineData(null)]
    public void TransientFailuresDoNotBecomePermanentAuthorizationRejections(string? error)
    {
        using var manager = new VoWifiManager();
        Assert.False(manager.PauseAutomaticRecoveryIfRejected(error));
        Assert.False(manager.AutomaticRecoveryPaused);
    }

    [Fact]
    public async Task StopWaitsForAnActiveRecoveryWorkerToFinishWithoutSendingNetworkTraffic()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeAka((_, _) => { entered.TrySetResult(); return Task.FromResult(false); });
        using var manager = new VoWifiManager { AkaProvider = provider };
        ArmSavedConnection(manager);
        QueueRecovery(manager, "test tunnel probe failed");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(manager.RecoveryCompletion.IsCompleted);
        await manager.StopVoWifiAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(manager.RecoveryCompletion.IsCompletedSuccessfully);
        Assert.False(manager.KeepOnlineRequested);
        Assert.Equal(VoWifiState.Disconnected, manager.State);
        Assert.Null(manager.EpdgInfo);
        Assert.Equal(0, provider.Authentications);
    }

    [Fact]
    public async Task PermanentRejectionCancelsAnAlreadyWaitingRecoveryWorker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeAka((_, _) => { entered.TrySetResult(); return Task.FromResult(false); });
        using var manager = new VoWifiManager { AkaProvider = provider };
        ArmSavedConnection(manager);
        QueueRecovery(manager, "test tunnel probe failed");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        QueueRecovery(manager, "test USER_UNKNOWN");
        await manager.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(manager.AutomaticRecoveryPaused);
        Assert.Equal(VoWifiState.Failed, manager.State);
        Assert.Contains("USER_UNKNOWN", manager.LastError);
        Assert.Null(manager.EpdgInfo);
        Assert.Equal(0, provider.Authentications);
    }

    [Fact]
    public async Task CancelingNewStartWhileOldRecoveryStopsCannotStartAnotherAttempt()
    {
        using var manualCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeAka((_, ct) =>
        {
            entered.TrySetResult();
            ct.Register(() => manualCancellation.Cancel());
            return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => false, ct);
        });
        using var manager = new VoWifiManager { AkaProvider = provider };
        ArmSavedConnection(manager);
        QueueRecovery(manager, "test tunnel probe failed");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartVoWifiAsync(Sim, ct: manualCancellation.Token));
        Assert.True(manager.RecoveryCompletion.IsCompletedSuccessfully);
        Assert.False(manager.KeepOnlineRequested);
        Assert.Null(manager.EpdgInfo);
        Assert.Equal(0, provider.Authentications);
    }

    [Fact]
    public async Task CancellationWhileWaitingForLifecycleGateDoesNotQueueReconnect()
    {
        using var manager = new VoWifiManager();
        var gate = (SemaphoreSlim)typeof(VoWifiManager).GetField("_lifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var starting = manager.StartVoWifiAsync(Sim, ct: cancellation.Token);
            Assert.True(manager.KeepOnlineRequested);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            Assert.False(manager.KeepOnlineRequested);
            Assert.True(manager.RecoveryCompletion.IsCompletedSuccessfully);
            Assert.Null(manager.EpdgInfo);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task AlreadyCanceledStartDoesNotReleaseARejectionPause()
    {
        using var manager = new VoWifiManager();
        manager.PauseAutomaticRecoveryIfRejected("USER_UNKNOWN");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartVoWifiAsync(Sim, ct: cancellation.Token));
        Assert.True(manager.AutomaticRecoveryPaused);
        Assert.False(manager.KeepOnlineRequested);
        Assert.Null(manager.EpdgInfo);
    }

    [Fact]
    public async Task DisposedManagerCannotStartOrProbeTheCardAgain()
    {
        var provider = new FakeAka((_, _) => throw new InvalidOperationException("Disposed manager must not probe."));
        var manager = new VoWifiManager { AkaProvider = provider };
        manager.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartVoWifiAsync(Sim));
        Assert.Empty(provider.ExpectedCards);
        Assert.False(manager.KeepOnlineRequested);
    }

    [Fact]
    public async Task ExplicitRetryReleasesPauseButCancellationDuringCardCheckDoesNotReconnect()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeAka((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<bool>(cancellation.Token);
        });
        using var manager = new VoWifiManager { AkaProvider = provider };
        manager.PauseAutomaticRecoveryIfRejected("NO_APN_SUBSCRIPTION");
        // A literal custom endpoint does not issue DNS; cancellation occurs
        // before constructing IKE, opening sockets or requesting an AKA vector.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartVoWifiAsync(Sim,
            customEpdg: "192.0.2.111", ct: cancellation.Token));
        Assert.False(manager.AutomaticRecoveryPaused);
        Assert.False(manager.KeepOnlineRequested);
        Assert.True(manager.RecoveryCompletion.IsCompletedSuccessfully);
        Assert.Null(manager.Transport);
        Assert.Equal(0ul, manager.IkeInitiatorSpi);
        Assert.Equal(0, provider.Authentications);
        Assert.Equal(VoWifiState.Disconnected, manager.State);
    }

    [Fact]
    public async Task PermanentRejectionReachedDuringRecoveryStopsTheWorker()
    {
        var checks = 0;
        var provider = new FakeAka((_, _) =>
        {
            if (Interlocked.Increment(ref checks) >= 3)
                throw new InvalidOperationException("test AUTHORIZATION_REJECTED");
            return Task.FromResult(true);
        });
        using var manager = new VoWifiManager { AkaProvider = provider };
        ArmSavedConnection(manager, "192.0.2.112");
        QueueRecovery(manager, "test tunnel probe failed");
        await manager.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, checks);
        Assert.True(manager.AutomaticRecoveryPaused);
        Assert.Contains("AUTHORIZATION_REJECTED", manager.LastError);
        Assert.Null(manager.Transport);
        Assert.Equal(0, provider.Authentications);
        QueueRecovery(manager, "test network returned");
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task CarrierBackoffCannotBeSkippedByNetworkWakeAndStopsWithoutAnySourceOrNetworkProbe()
    {
        var provider = new FakeAka((_, _) => throw new InvalidOperationException("No card probe is needed during carrier backoff."));
        using var manager = new VoWifiManager { AkaProvider = provider };
        ArmSavedConnection(manager);
        RetryGate(manager).Observe(Iccid, new(503, 300, false, true, DateTime.UtcNow));
        QueueRecovery(manager, "test registrar unavailable");
        await Task.Delay(100);
        Assert.False(manager.RecoveryCompletion.IsCompleted);
        var networkEvent = (NetworkAvailabilityEventArgs)Activator.CreateInstance(typeof(NetworkAvailabilityEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [true], null)!;
        typeof(VoWifiManager).GetMethod("OnNetworkAvailabilityChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [null, networkEvent]);
        await Task.Delay(100);
        Assert.Empty(provider.ExpectedCards);
        Assert.NotNull(manager.ImsRetryNotBeforeUtc);
        await manager.StopVoWifiAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(manager.RecoveryCompletion.IsCompletedSuccessfully);
        Assert.Null(manager.EpdgInfo);
        Assert.Null(manager.Transport);
        Assert.Empty(provider.ExpectedCards);
    }

    [Theory]
    [InlineData(403, false)]
    [InlineData(500, true)]
    public async Task SipRejectionOrMalformedBackoffPausesRecoveryAfterCleanup(int status, bool invalid)
    {
        var provider = new FakeAka((_, _) => throw new InvalidOperationException("Paused registration must not probe a card."));
        using var manager = new VoWifiManager { AkaProvider = provider };
        ArmSavedConnection(manager);
        RetryGate(manager).Observe(Iccid, new(status, null, invalid, true, DateTime.UtcNow));
        QueueRecovery(manager, "test registrar rejection");
        await manager.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(manager.AutomaticRecoveryPaused);
        Assert.Contains("paused", manager.LastError);
        Assert.Empty(provider.ExpectedCards);
        Assert.Null(manager.Transport);
        QueueRecovery(manager, "test network returned");
        Assert.Empty(provider.ExpectedCards);
    }

    [Fact]
    public async Task ExplicitStartPreservesValidServerBackoffAndCanBeCanceledBeforeDns()
    {
        var provider = new FakeAka((_, _) => throw new InvalidOperationException("No source probe during backoff."));
        using var manager = new VoWifiManager { AkaProvider = provider };
        RetryGate(manager).Observe(Iccid, new(503, 300, false, true, DateTime.UtcNow));
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.StateChanged += (_, _) => { if (manager.LastError?.Contains("Retry-After") == true) waiting.TrySetResult(); };
        var starting = manager.StartVoWifiAsync(Sim, ct: cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(starting.IsCompleted);
        Assert.Null(manager.EpdgInfo);
        Assert.Empty(provider.ExpectedCards);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.False(manager.KeepOnlineRequested);
        Assert.Throws<SipRegistrationRetryBlockedException>(() => RetryGate(manager).ThrowIfBlocked(Iccid));
    }

    private static SipRegistrationRetryGate RetryGate(VoWifiManager manager) =>
        (SipRegistrationRetryGate)typeof(VoWifiManager).GetField("_imsRetryGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;

    private static void ArmSavedConnection(VoWifiManager manager, string? customEpdg = null)
    {
        var optionsType = typeof(VoWifiManager).GetNestedType("VoWifiStartOptions", BindingFlags.NonPublic)!;
        var options = Activator.CreateInstance(optionsType, Sim, customEpdg, IkeProposalSuite.Auto, null, false, null);
        typeof(VoWifiManager).GetField("_lastStartOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, options);
        typeof(VoWifiManager).GetField("_keepOnlineRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, 1);
    }

    private static void QueueRecovery(VoWifiManager manager, string reason) =>
        typeof(VoWifiManager).GetMethod("QueueAutomaticRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [reason]);
}
