using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace qmiSharp.Transport;

/// <summary>
/// QMI transport over Win32 device symbolic link (e.g. \\.\{GUID}_1).
/// </summary>
public sealed class Win32DeviceTransport : IQmiTransport
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private bool _disposed;

    public string Name { get; }
    public bool IsConnected => !_disposed && !_handle.IsInvalid && !_handle.IsClosed;

    public Win32DeviceTransport(string devicePath)
    {
        Name = $"Win32Device({devicePath})";
        _handle = CreateFile(
            devicePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to open device '{devicePath}': Win32 error {err}");
        }

        _stream = new FileStream(_handle, FileAccess.ReadWrite, 4096, isAsync: true);
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
        _handle.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _handle.Dispose();
    }
}
