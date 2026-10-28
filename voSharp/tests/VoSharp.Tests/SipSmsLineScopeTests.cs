using VoSharp.Kernel.SipGateway;

namespace VoSharp.Tests;

public sealed partial class SipServerFeatureTests
{
    [Fact]
    public async Task CaseAliasesOfTheSameSmsLineCannotBypassItsSubmissionGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new Controller { BeforeSend = _ => { entered.TrySetResult(); return release.Task; } };
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync();
        using var alice = Socket(); using var bob = Socket(); var endpoint = gateway.Status.LocalEndPoint!;
        var first = Text("+15551234567"); first.SetHeader("X-VoWin-Line", "modem-a");
        await Authenticate(alice, first, "alice", endpoint); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var overlap = -1;
        try
        {
            var second = Text("+15551234568"); second.SetHeader("X-VoWin-Line", "MODEM-A");
            await Authenticate(bob, second, "bob", endpoint);
            await Task.Delay(150);
            overlap = controller.SmsCount;
        }
        finally { release.TrySetResult(); }
        Assert.Equal(202, (await Receive(alice)).StatusCode);
        Assert.Equal(202, (await Receive(bob)).StatusCode);
        Assert.Equal(1, overlap);
        Assert.Equal(2, controller.SmsCount);
    }
}
