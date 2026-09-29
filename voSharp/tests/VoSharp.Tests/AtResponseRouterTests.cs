using VoSharp.Modem.At;
using Xunit;

namespace VoSharp.Tests;

public class AtResponseRouterTests
{
    private static TaskCompletionSource<AtResponse> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task TimedOutCommandDrainsOldTerminalBeforeNextCommand()
    {
        var router = new AtResponseRouter();
        var first = NewCompletion();
        router.BeginCommand("AT+CSQ", first);
        router.HandleLine("+CSQ: 12,99");
        router.Timeout(first);
        Assert.Equal("TIMEOUT", (await first.Task).ErrorCode);
        router.EndCommand(first);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var waiting = router.AwaitPreviousTerminalAsync(cts.Token);
        Assert.False(waiting.IsCompleted);
        router.HandleLine("+CSQ: 20,99");
        Assert.False(waiting.IsCompleted);
        router.HandleLine("OK");
        Assert.True(await waiting);

        var second = NewCompletion();
        router.BeginCommand("AT+CPIN?", second);
        router.HandleLine("+CPIN: READY");
        router.HandleLine("OK");
        var response = await second.Task;
        Assert.True(response.Success);
        Assert.Contains("+CPIN: READY", response.Lines);
        Assert.DoesNotContain("+CSQ: 20,99", response.Lines);
    }

    [Fact]
    public async Task CompletedCommandDoesNotBecomeDesynchronizedWhenTimeoutFiresLater()
    {
        var router = new AtResponseRouter();
        var command = NewCompletion();
        router.BeginCommand("AT", command);
        router.HandleLine("OK");
        router.Timeout(command);
        Assert.True((await command.Task).Success);
        router.EndCommand(command);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.True(await router.AwaitPreviousTerminalAsync(cts.Token));
    }

    [Fact]
    public async Task ConcurrentTimeoutAndTerminalNeverLeaveLateDrainStuck()
    {
        for (var i = 0; i < 200; i++)
        {
            var router = new AtResponseRouter();
            var command = NewCompletion();
            router.BeginCommand("AT", command);
            await Task.WhenAll(
                Task.Run(() => router.Timeout(command)),
                Task.Run(() => router.HandleLine("OK")));
            router.EndCommand(command);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            Assert.True(await router.AwaitPreviousTerminalAsync(cts.Token));
        }
    }

    [Fact]
    public async Task UrcSurvivesDrainAndCloseReleasesPendingWaiters()
    {
        var router = new AtResponseRouter();
        var urcs = new List<string>();
        router.UrcReceived += urcs.Add;
        var command = NewCompletion();
        router.BeginCommand("AT", command);
        router.Timeout(command);
        router.HandleLine("+CMTI: \"SM\",1");
        Assert.Contains("+CMTI: \"SM\",1", urcs);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var waiting = router.AwaitPreviousTerminalAsync(cts.Token);
        router.ResetOnClose();
        Assert.False(await waiting);
        Assert.True(await router.AwaitPreviousTerminalAsync(cts.Token));
    }
}
