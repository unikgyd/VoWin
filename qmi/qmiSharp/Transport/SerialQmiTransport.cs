using System.IO.Ports;

namespace qmiSharp.Transport;

/// <summary>
/// QMI transport over Windows serial COM port.
/// </summary>
public sealed class SerialQmiTransport : IQmiTransport
{
    private readonly SerialPort _port;
    private readonly Stream _stream;
    private bool _disposed;

    public string Name { get; }
    public bool IsConnected => !_disposed && _port.IsOpen;

    public SerialQmiTransport(string portName, int baudRate = 115200)
    {
        Name = $"Serial({portName})";
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = Timeout.Infinite,
            DtrEnable = true,
            RtsEnable = true
        };
        _port.Open();
        _stream = _port.BaseStream;
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
        _port.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _port.Dispose();
    }
}
