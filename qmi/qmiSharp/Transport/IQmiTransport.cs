namespace qmiSharp.Transport;

/// <summary>
/// Abstraction for QMI physical/virtual transport channel.
/// </summary>
public interface IQmiTransport : IAsyncDisposable, IDisposable
{
    string Name { get; }
    bool IsConnected { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
