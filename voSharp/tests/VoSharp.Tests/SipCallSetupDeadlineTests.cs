using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class SipCallSetupDeadlineTests
{
    private static ImsProfile Profile() => new("001010123456789@ims.test", "sip:+15550001111@ims.test",
        "ims.test", "123456789012345", "127.0.0.1") { PAccessNetworkInfo = "previous-cell" };

    [Fact]
    public async Task SlowOptionalCellReadIsBoundedAndLateResultCannotRestoreStaleMetadata()
    {
        using var transport = new SipTransport();
        var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var registration = new SipRegisterSession(transport, Profile(),
            readAccessNetworkInfo: _ => Interlocked.Increment(ref reads) == 1
                ? late.Task : Task.FromResult<string?>("current-cell"));

        Assert.Null(await registration.RefreshAccessNetworkInfoAsync(budget: TimeSpan.FromMilliseconds(40))
            .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(registration.CurrentProfile.PAccessNetworkInfo);
        late.TrySetResult("late-obsolete-cell");
        Assert.Equal("current-cell", await registration.RefreshAccessNetworkInfoAsync(budget: TimeSpan.FromSeconds(1)));
        await Task.Yield();
        Assert.Equal("current-cell", registration.CurrentProfile.PAccessNetworkInfo);
    }

    [Fact]
    public async Task CallerCancellationDuringCellReadIsNotConvertedToOptionalMetadataTimeout()
    {
        using var transport = new SipTransport();
        using var cancellation = new CancellationTokenSource();
        using var registration = new SipRegisterSession(transport, Profile(),
            readAccessNetworkInfo: token => Task.Delay(Timeout.Infinite, token).ContinueWith<string?>(
                task => { task.GetAwaiter().GetResult(); return null; }, TaskScheduler.Default));
        var read = registration.RefreshAccessNetworkInfoAsync(cancellation.Token, TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal("previous-cell", registration.CurrentProfile.PAccessNetworkInfo);
    }
}
