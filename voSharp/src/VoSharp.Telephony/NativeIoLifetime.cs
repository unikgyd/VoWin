using System.Collections.Concurrent;

namespace VoSharp.Telephony;

/// <summary>Unfinished native closes must not be mistaken for a completed application shutdown.</summary>
public static class HostImsIoCleanup
{
    private static readonly ConcurrentDictionary<Task, string> Pending = new();
    internal static bool IsPending(string scope) => Pending.Values.Contains(scope);
    internal static void Track(string scope, Task work)
    {
        Pending.TryAdd(work, scope);
        _ = work.ContinueWith(completed =>
        {
            _ = completed.Exception; // Observe errors; failed cleanup remains visible and blocks an unsafe exit/reopen.
            if (completed.IsCompletedSuccessfully) Pending.TryRemove(completed, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public static async Task<bool> WaitForCompletionAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        try { await Task.WhenAll(Pending.Keys.ToArray()).WaitAsync(timeout, ct).ConfigureAwait(false); return Pending.IsEmpty; }
        catch (TimeoutException) { return false; }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }
}

/// <summary>Never hold a monitor through native I/O, or unload its DLL while an operation still runs.</summary>
internal sealed class NativeIoLifetime(string scope)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closing;
    private Task? _closeTask;

    public IDisposable Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _active++;
            return new Lease(this);
        }
    }
    private sealed class Lease(NativeIoLifetime owner) : IDisposable
    {
        private NativeIoLifetime? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
    private void Exit()
    {
        lock (_gate)
        {
            if (--_active == 0 && _closing) _drained.TrySetResult();
        }
    }
    public void Close(Action cancel, Action release, TimeSpan timeout)
    {
        Task closing;
        lock (_gate)
        {
            if (_closeTask == null)
            {
                _closing = true;
                if (_active == 0) _drained.TrySetResult();
                _closeTask = Task.Run(async () =>
                {
                    cancel();
                    await _drained.Task.ConfigureAwait(false);
                    release();
                });
                HostImsIoCleanup.Track(scope, _closeTask);
            }
            closing = _closeTask;
        }
        // The work continues if a faulty driver does not cancel. Its handles,
        // delegates and native library remain pinned until safe completion.
        closing.WaitAsync(timeout).GetAwaiter().GetResult();
    }
}
