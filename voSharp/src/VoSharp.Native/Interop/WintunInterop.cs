using System.Runtime.InteropServices;

namespace VoSharp.Native.Interop;

/// <summary>
/// P/Invoke declarations for Wintun Layer 3 TUN driver (wintun.dll).
/// API reference: https://git.zx2c4.com/wintun/about/#reference
/// Version: 0.14.1
/// </summary>
public static class WintunInterop
{
    private const string DllName = "wintun.dll";

    // ── Adapter Management ──────────────────────────────────────────────────

    /// <summary>Creates a new Wintun adapter.</summary>
    /// <param name="name">Adapter name visible in Network Connections (max 127 chars).</param>
    /// <param name="tunnelType">Tunnel type name (max 127 chars), e.g. "VoSharp-VoWiFi".</param>
    /// <param name="requestedGuid">Optional GUID for stable adapter identity. Pass IntPtr.Zero for auto.</param>
    /// <returns>Adapter handle, or IntPtr.Zero on failure. Call Marshal.GetLastPInvokeError().</returns>
    [DllImport(DllName, CharSet = CharSet.Unicode, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunCreateAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string tunnelType,
        IntPtr requestedGuid);

    /// <summary>Creates adapter with a specific GUID.</summary>
    [DllImport(DllName, CharSet = CharSet.Unicode, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunCreateAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string tunnelType,
        ref Guid requestedGuid);

    /// <summary>Opens an existing Wintun adapter by name.</summary>
    [DllImport(DllName, CharSet = CharSet.Unicode, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunOpenAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name);

    /// <summary>Releases resources associated with an adapter handle.</summary>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunCloseAdapter(IntPtr adapter);

    /// <summary>Deletes driver and adapter from the system (permanent removal).</summary>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WintunDeleteDriver();

    /// <summary>Gets the LUID of the adapter's network interface.</summary>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunGetAdapterLUID(IntPtr adapter, out long luid);

    // ── Session Management ──────────────────────────────────────────────────

    /// <summary>
    /// Starts a session on the adapter with a given ring buffer capacity.
    /// Capacity must be between 0x20000 (128 KiB) and 0x4000000 (64 MiB), must be power of 2.
    /// </summary>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    /// <summary>Ends a session, releasing the ring buffers.</summary>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunEndSession(IntPtr session);

    /// <summary>
    /// Gets an event handle (HANDLE) that is signaled when data is available for reading.
    /// Use WaitForSingleObject or ManualResetEvent to wait on it.
    /// </summary>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

    // ── Packet I/O ──────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves a pointer to the next received IP packet from the receive ring buffer.
    /// The pointer is valid until WintunReleaseReceivePacket is called.
    /// Returns IntPtr.Zero if no packet is available (ERROR_NO_MORE_ITEMS = 259).
    /// </summary>
    /// <param name="session">Session handle from WintunStartSession.</param>
    /// <param name="packetSize">Receives the size of the IP packet in bytes.</param>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    /// <summary>
    /// Releases a received packet buffer back to the ring. Must be called after processing.
    /// </summary>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    /// <summary>
    /// Allocates space in the send ring buffer for an outbound IP packet.
    /// The caller must write the IP packet into the returned buffer, then call WintunSendPacket.
    /// Returns IntPtr.Zero on failure (ring full = ERROR_BUFFER_OVERFLOW = 111).
    /// </summary>
    /// <param name="session">Session handle.</param>
    /// <param name="packetSize">Size of the IP packet to send.</param>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    /// <summary>
    /// Sends a previously allocated packet. The packet buffer becomes invalid after this call.
    /// </summary>
    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunSendPacket(IntPtr session, IntPtr packet);

    // ── Constants ────────────────────────────────────────────────────────────

    public const uint MinRingCapacity  = 0x20000;    // 128 KiB
    public const uint MaxRingCapacity  = 0x4000000;  // 64 MiB
    public const uint DefaultCapacity  = 0x400000;   // 4 MiB
    public const int  ErrorNoMoreItems = 259;
    public const int  ErrorBufferOverflow = 111;
}
