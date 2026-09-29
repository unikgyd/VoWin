using System.Net.Sockets;

namespace qmiSharp.Transport;

/// <summary>
/// QMI transport over TCP socket (e.g. forwarder, daemon or local proxy).
/// </summary>
public sealed class SocketQmiTransport : IQmiTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _disposed;

    public string Name { get; }
    public bool IsConnected => !_disposed && _client.Connected;

    public SocketQmiTransport(string host, int port)
    {
        Name = $"Socket({host}:{port})";
        _client = new TcpClient();
        _client.Connect(host, port);
        _stream = _client.GetStream();
    }

    public SocketQmiTransport(TcpClient connectedClient)
    {
        _client = connectedClient ?? throw new ArgumentNullException(nameof(connectedClient));
        Name = $"Socket({_client.Client.RemoteEndPoint})";
        _stream = _client.GetStream();
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        _client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
