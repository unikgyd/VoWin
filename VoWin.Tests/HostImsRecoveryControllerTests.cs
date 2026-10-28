using VoSharp.Kernel.Pool;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class HostImsRecoveryControllerTests
{
    private static HostImsRecoveryObservation Lost => new(false, true, true, true, false, false, true,
        HostImsLossKind.RegistrationRefreshFailed);
    private static Task FastDelay(TimeSpan _, CancellationToken ct) => Task.Delay(5, ct);
    private static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task TransientRefreshLossRebuildsOnceThenKeepsMonitoring()
    {
        var view = Lost;
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Volatile.Read(ref view),
            _ => Task.CompletedTask, _ =>
            {
                Interlocked.Increment(ref rebuilds);
                Volatile.Write(ref view, Lost with { Registered = true, LossKind = HostImsLossKind.None });
                return Task.FromResult(new HostImsRecoveryAttempt(true, false));
            }, delay: FastDelay);
        await UntilAsync(() => recovery.Status.LastRecoveredAtUtc is not null);
        Assert.Equal(1, rebuilds);
        Assert.Equal(1, recovery.Status.Attempts);
        Assert.True(recovery.Status.KeepOnlineRequested);
        Assert.False(recovery.Status.IsPaused);
        Assert.False(recovery.Completion.IsCompleted);
    }

    [Fact]
    public async Task CleanupAndCallsMustFinishBeforeAnyRebuild()
    {
        var view = Lost with { CleanupPending = true, HasCall = true };
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Volatile.Read(ref view),
            _ => Task.CompletedTask, _ =>
            {
                rebuilds++;
                Volatile.Write(ref view, Lost with { Registered = true });
                return Task.FromResult(new HostImsRecoveryAttempt(true, false));
            }, delay: FastDelay);
        await UntilAsync(() => recovery.Status.State == "waiting-cleanup");
        Assert.Equal(0, rebuilds);
        Volatile.Write(ref view, Lost);
        await UntilAsync(() => recovery.Status.LastRecoveredAtUtc is not null);
        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public async Task CancelDuringLocalBackoffCannotStartARebuild()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Lost, _ => Task.CompletedTask,
            _ => { rebuilds++; return Task.FromResult(new HostImsRecoveryAttempt(true, false)); },
            delay: (_, ct) => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        recovery.Cancel();
        await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, rebuilds);
        Assert.False(recovery.Status.KeepOnlineRequested);
    }

    [Fact]
    public async Task IdentityChangeDuringBackoffIsRecheckedBeforeCarrierWaitOrRebuild()
    {
        var view = Lost;
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var carrierChecks = 0;
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Volatile.Read(ref view),
            _ => { carrierChecks++; return Task.CompletedTask; },
            _ => { rebuilds++; return Task.FromResult(new HostImsRecoveryAttempt(true, false)); },
            delay: (_, ct) => { delayed.TrySetResult(); return release.Task.WaitAsync(ct); });
        await delayed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref view, Lost with { SameIdentity = false });
        release.SetResult();
        await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(recovery.Status.IsPaused);
        Assert.Equal(0, carrierChecks);
        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public async Task CancelWhileCarrierBackoffWaitsDoesNotBypassTheWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Lost,
            ct => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); },
            _ => { rebuilds++; return Task.FromResult(new HostImsRecoveryAttempt(true, false)); }, delay: FastDelay);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        recovery.Cancel();
        await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, rebuilds);
    }

    [Theory]
    [InlineData(HostImsLossKind.BearerLost)]
    [InlineData(HostImsLossKind.RestorationFailed)]
    public async Task UnsafeBearerLossNeverBecomesARepeatedModemReboot(HostImsLossKind kind)
    {
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Lost with { LossKind = kind },
            _ => Task.CompletedTask, _ => { rebuilds++; return Task.FromResult(new HostImsRecoveryAttempt(true, false)); }, delay: FastDelay);
        await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(recovery.Status.IsPaused);
        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public async Task RepeatedTransientFailuresPauseAfterThreeAttempts()
    {
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Lost, _ => Task.CompletedTask,
            _ => { rebuilds++; return Task.FromResult(new HostImsRecoveryAttempt(false, true, "temporary failure")); }, delay: FastDelay);
        await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(recovery.Status.IsPaused);
        Assert.Equal(3, rebuilds);
        Assert.Equal(3, recovery.Status.Attempts);
    }

    [Fact]
    public async Task StableIntervalsResetConsecutiveFailuresButNotTheDailyBudget()
    {
        var clock = new RecoveryClock();
        var healthyReads = 2;
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(
            () => healthyReads++ < 2 ? Lost with { Registered = true } : Lost,
            _ => Task.CompletedTask, _ =>
            {
                rebuilds++;
                healthyReads = 0;
                return Task.FromResult(new HostImsRecoveryAttempt(true, false));
            }, timeProvider: clock, delay: (duration, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(duration == TimeSpan.FromSeconds(2) ? TimeSpan.FromMinutes(3) : duration);
                return Task.CompletedTask;
            });
        await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(recovery.Status.IsPaused);
        Assert.Equal(6, rebuilds);
        Assert.Equal(6, recovery.Status.Attempts);
    }

    [Fact]
    public async Task CancelingOneModuleDoesNotCancelAnotherModulesRecovery()
    {
        var waitingA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewB = Lost;
        var rebuildsA = 0;
        var rebuildsB = 0;
        await using var first = new HostImsRecoveryController(() => Lost,
            ct => { waitingA.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); },
            _ => { rebuildsA++; return Task.FromResult(new HostImsRecoveryAttempt(true, false)); }, delay: FastDelay);
        await using var second = new HostImsRecoveryController(() => Volatile.Read(ref viewB),
            ct => { waitingB.TrySetResult(); return releaseB.Task.WaitAsync(ct); }, _ =>
            {
                rebuildsB++;
                Volatile.Write(ref viewB, Lost with { Registered = true });
                return Task.FromResult(new HostImsRecoveryAttempt(true, false));
            }, delay: FastDelay);
        await Task.WhenAll(waitingA.Task, waitingB.Task).WaitAsync(TimeSpan.FromSeconds(2));
        first.Cancel();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        releaseB.SetResult();
        await UntilAsync(() => second.Status.LastRecoveredAtUtc is not null);
        Assert.Equal(0, rebuildsA);
        Assert.Equal(1, rebuildsB);
        Assert.True(second.Status.KeepOnlineRequested);
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    [Fact]
    public async Task MissingOriginalSourceWaitsWithoutRebuildAndCanBeStopped()
    {
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Lost with { IdentityAvailable = false, DeviceReady = false },
            _ => Task.CompletedTask, _ => { rebuilds++; return Task.FromResult(new HostImsRecoveryAttempt(true, false)); }, delay: FastDelay);
        await UntilAsync(() => recovery.Status.State == "waiting-source");
        Assert.True(recovery.Status.KeepOnlineRequested);
        recovery.Cancel();
        await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public async Task ManualStopIntentIsNotMistakenForNetworkLoss()
    {
        var rebuilds = 0;
        await using var recovery = new HostImsRecoveryController(() => Lost with { LossKind = HostImsLossKind.ManuallyStopped },
            _ => Task.CompletedTask, _ => { rebuilds++; return Task.FromResult(new HostImsRecoveryAttempt(true, false)); }, delay: FastDelay);
        await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(recovery.Status.IsPaused);
        Assert.False(recovery.Status.KeepOnlineRequested);
        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public async Task CancelDuringRebuildWaitsForTheDelegateToUnwind()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwound = false;
        var recovery = new HostImsRecoveryController(() => Lost, _ => Task.CompletedTask, async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { unwound = true; }
            return new(true, false);
        }, delay: FastDelay);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await recovery.DisposeAsync();
        Assert.True(unwound);
        Assert.True(recovery.Completion.IsCompletedSuccessfully);
    }
}
