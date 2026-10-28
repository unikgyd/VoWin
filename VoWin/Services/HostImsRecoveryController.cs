using VoSharp.Kernel.Pool;
using VoSharp.Sip;
using VoWin.Models;

namespace VoWin.Services;

internal sealed record HostImsRecoveryObservation(bool Registered, bool SameIdentity, bool IdentityAvailable,
    bool DeviceReady, bool CleanupPending, bool HasCall, bool StillSelected, HostImsLossKind LossKind,
    bool CarrierPaused = false, DateTime? CarrierRetryAtUtc = null, string? Error = null);

internal sealed record HostImsRecoveryAttempt(bool Succeeded, bool Retryable, string? Error = null);

/// <summary>One explicit Host IMS intent. Observations are cached; only the rebuild delegate touches hardware.</summary>
internal sealed class HostImsRecoveryController : IAsyncDisposable
{
    private readonly Func<HostImsRecoveryObservation> _observe;
    private readonly Func<CancellationToken, Task> _waitForCarrier;
    private readonly Func<CancellationToken, Task<HostImsRecoveryAttempt>> _rebuild;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly TimeSpan _poll;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<long> _dailyAttempts = new();
    private readonly Task _worker;
    private HostImsRecoveryStatus _status = new("monitoring", true, false, false, 0, null, "Host IMS · 已注册，后台保持在线");
    public HostImsRecoveryStatus Status => Volatile.Read(ref _status);
    public event Action? Changed;
    internal Task Completion => _worker;

    internal HostImsRecoveryController(Func<HostImsRecoveryObservation> observe,
        Func<CancellationToken, Task> waitForCarrier, Func<CancellationToken, Task<HostImsRecoveryAttempt>> rebuild,
        TimeProvider? timeProvider = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? pollInterval = null)
    {
        _observe = observe; _waitForCarrier = waitForCarrier; _rebuild = rebuild;
        _time = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _time, token));
        _poll = pollInterval ?? TimeSpan.FromSeconds(2);
        _worker = Task.Run(RunAsync);
    }

    public void Cancel()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void Publish(string state, string message, bool waiting = false, bool paused = false)
    {
        var old = Status;
        var next = old with { State = state, Message = message, IsWaiting = waiting, IsPaused = paused,
            KeepOnlineRequested = !paused && state != "stopped" };
        if (next == old) return;
        Volatile.Write(ref _status, next);
        try { Changed?.Invoke(); } catch { }
    }

    private void Pause(string reason) => Publish("paused", reason + " · 自动恢复已暂停，请检查后手动重试", paused: true);

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        var consecutive = 0;
        long? healthySince = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var view = _observe();
                if (!view.StillSelected || view.LossKind == HostImsLossKind.ManuallyStopped) return;
                if (view.IdentityAvailable && !view.SameIdentity) { Pause("设备或 SIM 身份已改变"); return; }
                if (view.Registered)
                {
                    healthySince ??= _time.GetTimestamp();
                    if (_time.GetElapsedTime(healthySince.Value) >= TimeSpan.FromMinutes(2)) consecutive = 0;
                    Publish("monitoring", "Host IMS · 已注册，后台保持在线");
                    await _delay(_poll, ct).ConfigureAwait(false);
                    continue;
                }
                healthySince = null;
                if (view.LossKind is HostImsLossKind.BearerLost or HostImsLossKind.RestorationFailed)
                { Pause(view.Error ?? "承载隔离丢失或模组恢复未确认"); return; }
                if (view.CarrierPaused) { Pause(view.Error ?? "运营商拒绝注册或退避信息无效"); return; }
                if (!view.IdentityAvailable || !view.DeviceReady)
                {
                    Publish("waiting-source", "等待原模组与原 SIM 恢复 · 可停止自动恢复", waiting: true);
                    await _delay(_poll, ct).ConfigureAwait(false);
                    continue;
                }
                if (view.CleanupPending || view.HasCall || view.LossKind == HostImsLossKind.None)
                {
                    Publish("waiting-cleanup", "等待旧 Host IMS 会话与模组恢复完成", waiting: true);
                    await _delay(_poll, ct).ConfigureAwait(false);
                    continue;
                }
                while (_dailyAttempts.TryPeek(out var stamp) && _time.GetElapsedTime(stamp) >= TimeSpan.FromDays(1)) _dailyAttempts.Dequeue();
                if (consecutive >= 3 || _dailyAttempts.Count >= 6)
                { Pause("自动恢复次数已达保护上限，避免反复重启模组"); return; }
                Publish("backoff", $"Host IMS 已掉线 · 等待自动恢复（第 {consecutive + 1} 次）", waiting: true);
                await _delay(TimeSpan.FromSeconds(consecutive switch { 0 => 5, 1 => 15, _ => 30 }), ct).ConfigureAwait(false);
                view = _observe();
                if (!view.StillSelected || view.LossKind == HostImsLossKind.ManuallyStopped) return;
                if (view.IdentityAvailable && !view.SameIdentity) { Pause("设备或 SIM 身份已改变"); return; }
                if (view.Registered || !view.DeviceReady || !view.IdentityAvailable || view.CleanupPending || view.HasCall) continue;
                Publish("carrier-backoff", "等待运营商退避完成后恢复 Host IMS", waiting: true);
                await _waitForCarrier(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                view = _observe(); // Never act on the pre-delay identity or a superseded stop/mode intent.
                if (!view.StillSelected || view.LossKind == HostImsLossKind.ManuallyStopped) return;
                if (view.Registered) continue;
                if (view.IdentityAvailable && !view.SameIdentity) { Pause("设备或 SIM 身份已改变"); return; }
                if (!view.DeviceReady || !view.IdentityAvailable || view.CleanupPending || view.HasCall) continue;
                if (view.CarrierPaused || view.LossKind is not HostImsLossKind.RegistrationRefreshFailed)
                { Pause(view.Error ?? "恢复条件已改变"); return; }
                consecutive++;
                _dailyAttempts.Enqueue(_time.GetTimestamp());
                Volatile.Write(ref _status, Status with { Attempts = Status.Attempts + 1 });
                Publish("reconnecting", "正在自动恢复 Windows Host IMS · 可取消", waiting: true);
                var result = await _rebuild(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (result.Succeeded)
                {
                    Volatile.Write(ref _status, Status with { LastRecoveredAtUtc = _time.GetUtcNow().UtcDateTime });
                    Publish("monitoring", "Host IMS · 自动恢复成功");
                }
                else if (!result.Retryable) { Pause(result.Error ?? "Host IMS 自动恢复失败"); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SipRegistrationRetryBlockedException ex) when (ex.AutomaticRecoveryPaused) { Pause(ex.Message); }
        catch (Exception ex) { Pause(ex.Message); }
        finally
        {
            if (!Status.IsPaused) Publish("stopped", "Host IMS 自动恢复已停止");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
