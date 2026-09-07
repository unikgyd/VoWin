using System.Runtime.InteropServices;
using VoSharp.Native.Interop;

namespace VoSharp.Native.Wintun;

/// <summary>
/// Wraps a Wintun ring-buffer session for reading/writing raw IP packets.
/// Provides async-friendly packet I/O on the TUN adapter.
/// </summary>
public class WintunSession : IDisposable
{
    private IntPtr _session;
    private readonly IntPtr _readEvent;
    private bool _disposed;

    private WintunSession(IntPtr session)
    {
        _session = session;
        _readEvent = WintunInterop.WintunGetReadWaitEvent(session);
    }

    internal static WintunSession Start(IntPtr adapter, uint capacity)
    {
        var session = WintunInterop.WintunStartSession(adapter, capacity);
        if (session == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"WintunStartSession failed (error {err}). Capacity must be power-of-2 in [{WintunInterop.MinRingCapacity}..{WintunInterop.MaxRingCapacity}].");
        }
        return new WintunSession(session);
    }

    /// <summary>
    /// The native event handle that is signaled when packets are available for reading.
    /// Can be used with WaitForSingleObject or converted to a WaitHandle.
    /// </summary>
    public IntPtr ReadWaitEvent => _readEvent;

    /// <summary>
    /// Tries to receive one IP packet from the TUN adapter.
    /// Returns null if no packet is available (non-blocking).
    /// The returned array is a copy of the packet data.
    /// </summary>
    public byte[]? TryReceivePacket()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WintunSession));

        var ptr = WintunInterop.WintunReceivePacket(_session, out uint size);
        if (ptr == IntPtr.Zero)
            return null; // ERROR_NO_MORE_ITEMS

        try
        {
            var packet = new byte[size];
            Marshal.Copy(ptr, packet, 0, (int)size);
            return packet;
        }
        finally
        {
            WintunInterop.WintunReleaseReceivePacket(_session, ptr);
        }
    }

    /// <summary>
    /// Waits for and receives one IP packet. Blocks until data is available or cancelled.
    /// </summary>
    public async Task<byte[]?> ReceivePacketAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            var packet = TryReceivePacket();
            if (packet != null) return packet;

            // Wait for the read event to be signaled (packet available)
            // Use a short poll interval since we can't easily wrap HANDLE into async .NET
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Sends an IP packet into the TUN adapter (inbound to the OS network stack).
    /// The packet is injected as if it arrived from the tunnel.
    /// </summary>
    /// <param name="ipPacket">Raw IPv4/IPv6 packet bytes.</param>
    /// <returns>True if sent successfully, false if ring buffer is full.</returns>
    public bool SendPacket(ReadOnlySpan<byte> ipPacket)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WintunSession));

        var ptr = WintunInterop.WintunAllocateSendPacket(_session, (uint)ipPacket.Length);
        if (ptr == IntPtr.Zero)
            return false; // Ring full (ERROR_BUFFER_OVERFLOW)

        unsafe
        {
            fixed (byte* src = ipPacket)
            {
                Buffer.MemoryCopy(src, (void*)ptr, ipPacket.Length, ipPacket.Length);
            }
        }

        WintunInterop.WintunSendPacket(_session, ptr);
        return true;
    }

    /// <summary>Sends a raw IP packet (byte array overload).</summary>
    public bool SendPacket(byte[] ipPacket) => SendPacket(ipPacket.AsSpan());

    /// <summary>
    /// Runs a continuous receive loop, invoking the callback for each received IP packet.
    /// The loop runs until cancellation or disposal.
    /// </summary>
    public async Task RunReceiveLoopAsync(
        Action<byte[]> onPacketReceived,
        CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var packet = TryReceivePacket();
                if (packet != null)
                {
                    onPacketReceived(packet);
                }
                else
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* log and continue */ }
        }
    }

    public void Dispose()
    {
        if (!_disposed && _session != IntPtr.Zero)
        {
            _disposed = true;
            WintunInterop.WintunEndSession(_session);
            _session = IntPtr.Zero;
        }
    }
}
