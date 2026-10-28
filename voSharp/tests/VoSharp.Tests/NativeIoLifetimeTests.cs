using VoSharp.Telephony;

namespace VoSharp.Tests;

public sealed class NativeIoLifetimeTests
{
    [Fact]
    public async Task NativeBuffersAndLibraryAreNotReleasedUntilEveryOperationDrains()
    {
        var lifetime = new NativeIoLifetime(Guid.NewGuid().ToString());
        using var operation = lifetime.Enter();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = 0;
        var closing = Task.Run(() => lifetime.Close(() => cancelled.TrySetResult(),
            () => Interlocked.Increment(ref released), TimeSpan.FromSeconds(2)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, released);
        Assert.Throws<ObjectDisposedException>(() => lifetime.Enter());
        operation.Dispose();
        await closing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, released);
        Assert.True(await HostImsIoCleanup.WaitForCompletionAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task StuckNativeCancellationReturnsBoundedlyButDoesNotUnloadItsLibraryOrClaimExitSafe()
    {
        var lifetime = new NativeIoLifetime(Guid.NewGuid().ToString());
        using var unblock = new ManualResetEventSlim();
        var released = 0;
        try
        {
            Assert.Throws<TimeoutException>(() => lifetime.Close(() => unblock.Wait(),
                () => Interlocked.Increment(ref released), TimeSpan.FromMilliseconds(30)));
            Assert.Equal(0, released);
            Assert.False(await HostImsIoCleanup.WaitForCompletionAsync(TimeSpan.FromMilliseconds(20)));
        }
        finally { unblock.Set(); }
        Assert.True(await HostImsIoCleanup.WaitForCompletionAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task ConcurrentDisposalCancelsAndReleasesExactlyOnce()
    {
        var lifetime = new NativeIoLifetime(Guid.NewGuid().ToString());
        var cancellations = 0; var releases = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => lifetime.Close(
            () => Interlocked.Increment(ref cancellations), () => Interlocked.Increment(ref releases), TimeSpan.FromSeconds(2)))));
        Assert.Equal(1, cancellations); Assert.Equal(1, releases);
    }
}
