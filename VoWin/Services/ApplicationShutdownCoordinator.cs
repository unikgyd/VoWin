namespace VoWin.Services;

internal sealed record ShutdownStage(string Name, Func<Task> Stop);

/// <summary>A timeout is a visible warning, never permission to abandon native I/O and force exit.</summary>
internal sealed class ApplicationShutdownCoordinator
{
    private readonly object _gate = new();
    private Task? _completion;
    public Task StopAsync(IEnumerable<ShutdownStage> stages, Action<string> progress, TimeSpan warningAfter)
    {
        lock (_gate) return _completion ??= RunAsync(stages.ToArray(), progress, warningAfter);
    }
    private static async Task RunAsync(ShutdownStage[] stages, Action<string> progress, TimeSpan warningAfter)
    {
        foreach (var stage in stages)
        {
            progress(stage.Name);
            // A synchronous driver call in Dispose must never occupy WPF's dispatcher.
            var stopping = Task.Run(stage.Stop);
            try { await stopping.WaitAsync(warningAfter).ConfigureAwait(false); }
            catch (TimeoutException) when (!stopping.IsCompleted)
            {
                progress($"{stage.Name}：清理超过预期，仍在等待。请勿反复结束任务；若驱动一直无响应，请保存其他工作后重启 Windows。");
                await stopping.ConfigureAwait(false);
            }
        }
        progress("通信资源已安全释放，正在退出…");
    }
}
