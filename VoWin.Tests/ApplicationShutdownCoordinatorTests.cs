using System.Collections.Concurrent;
using System.IO;
using VoWin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoWin.Services.RemoteControl;

namespace VoWin.Tests;

public sealed class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public async Task StuckStageWarnsWithoutBlockingCallerOrReleasingLaterResources()
    {
        var coordinator = new ApplicationShutdownCoordinator();
        using var unblock = new ManualResetEventSlim();
        var messages = new ConcurrentQueue<string>(); var nextStage = 0;
        var stages = new[] { new ShutdownStage("native cleanup", () => { unblock.Wait(); return Task.CompletedTask; }),
            new ShutdownStage("release ownership", () => { Interlocked.Increment(ref nextStage); return Task.CompletedTask; }) };
        var task = coordinator.StopAsync(stages, messages.Enqueue, TimeSpan.FromMilliseconds(20));
        try
        {
            Assert.Same(task, coordinator.StopAsync(stages, messages.Enqueue, TimeSpan.FromMilliseconds(20)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!messages.Any(message => message.Contains("清理超过预期"))) await Task.Delay(5, deadline.Token);
            Assert.False(task.IsCompleted); Assert.Equal(0, nextStage);
        }
        finally { unblock.Set(); }
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, nextStage);
    }

    [Fact]
    public async Task CleanupExceptionDoesNotRunFinalOwnershipRelease()
    {
        var coordinator = new ApplicationShutdownCoordinator(); var released = false;
        await Assert.ThrowsAsync<IOException>(() => coordinator.StopAsync([
            new("driver cleanup", () => throw new IOException("synthetic native failure")),
            new("release ownership", () => { released = true; return Task.CompletedTask; })], _ => { }, TimeSpan.FromSeconds(1)));
        Assert.False(released);
    }

    [Fact]
    public async Task RemoteControlSingletonCanBeDisposedThroughAllDiAliasesAndAgainAfterwards()
    {
        var kernel = System.Reflection.DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)kernel).Handler = (_, _) => null;
        var services = new ServiceCollection();
        services.AddSingleton(kernel);
        services.AddSingleton<RemoteControlService>();
        services.AddSingleton<IRemoteControlService>(provider => provider.GetRequiredService<RemoteControlService>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<RemoteControlService>());
        var provider = services.BuildServiceProvider();
        var remote = provider.GetRequiredService<RemoteControlService>();
        Assert.Same(remote, provider.GetRequiredService<IRemoteControlService>());
        Assert.Same(remote, provider.GetRequiredService<IHostedService>());
        await remote.StopAsync(CancellationToken.None); // No StartAsync: no settings, account or network access.
        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => remote.DisposeAsync().AsTask()));
        await remote.StopAsync(CancellationToken.None);
    }
}
