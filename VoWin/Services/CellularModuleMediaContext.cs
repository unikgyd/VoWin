namespace VoWin.Services;

/// <summary>One modem's UAC process, route and cancellation lifetime. Never shared between slots.</summary>
internal sealed class CellularModuleMediaContext(string slotId, string atPort, Guid containerId, string? imei = null) : IAsyncDisposable
{
    private readonly object _lifetimeGate = new();
    private string? _owner;
    private CancellationTokenSource? _cancellation;
    private int _disposed;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public string SlotId { get; } = slotId;
    public string AtPort { get; } = atPort;
    public Guid ContainerId { get; } = containerId;
    public string? Imei { get; } = imei;
    public CellularAudioBridge Bridge { get; } = new();
    public Qdc507VoiceRuntime Runtime { get; } = new(atPort, containerId, imei);
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool RouteActive;

    internal CancellationToken Begin(string owner, CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_owner != null) throw new InvalidOperationException("所选模组的 USB 音频仍在使用或回收中。");
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _owner = owner;
            return _cancellation.Token;
        }
    }
    internal bool IsOwner(string owner) { lock (_lifetimeGate) return _owner == owner; }
    internal string? Cancel(string? expectedOwner = null)
    {
        lock (_lifetimeGate)
        {
            if (_owner == null || expectedOwner != null && _owner != expectedOwner) return null;
            _cancellation?.Cancel();
            return _owner;
        }
    }
    internal void End(string owner)
    {
        lock (_lifetimeGate)
        {
            if (_owner != owner) return;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _owner = null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var owner = Cancel();
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Bridge.DisposeAsync().ConfigureAwait(false);
            if (RouteActive)
            {
                RouteActive = false;
                await Runtime.StopRouteAsync().ConfigureAwait(false);
            }
        }
        finally { if (owner != null) End(owner); Gate.Release(); }
        // Do not dispose Gate while a canceled startup/monitor is unwinding.
    }
}

internal sealed class CellularSipReservation(Guid containerId)
{
    public Guid ContainerId { get; } = containerId;
    public string OwnerId { get; } = "sip:" + Guid.NewGuid().ToString("N");
    public Task? ReleaseTask;
}
