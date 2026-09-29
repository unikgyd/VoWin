namespace VoSharp.Ike;

/// <summary>
/// Compatibility facade for callers that used the original one-shot probe.
/// Message IDs and key ownership now live in <see cref="IkeSessionController"/>.
/// </summary>
public sealed class IkeLivenessProbe : IDisposable
{
    private IkeSessionController? _controller;

    internal IkeLivenessProbe(IkeSessionController controller)
        => _controller = controller ?? throw new ArgumentNullException(nameof(controller));

    public Task ProbeAsync(CancellationToken ct = default)
        => (_controller ?? throw new ObjectDisposedException(nameof(IkeLivenessProbe))).ProbeAsync(ct);

    public void Dispose() => _controller = null;
}
