using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VoSharp.Modem;

namespace VoSharp.Telephony;

/// <summary>
/// Experimental Windows raw IPv6 ESP I/O for an already verified IMS PPP
/// bearer. WinDivert transports bytes; ImsIpsecTransport owns all crypto and
/// replay state. Construction is explicit and never used by the default path.
/// </summary>
internal sealed class WindowsHostImsWinDivertIo : IHostImsRawEspIo
{
    // No SNIFF: the C# SA owns this exact inbound ESP stream. Letting the
    // original packet continue into the Windows stack can produce an error
    // from a second IPsec consumer that has no matching SA.
    private const ulong ConsumeReceiveOnly = 0x0004;
    private const ulong SendOnly = 0x0008;
    private const int AddressBytes = 80; // WinDivert 2.x WINDIVERT_ADDRESS on x64.
    private static readonly nint InvalidHandle = new(-1);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private delegate nint OpenNative([MarshalAs(UnmanagedType.LPStr)] string filter,
        int layer, short priority, ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ReceiveNative(nint handle, [Out] byte[] packet,
        uint packetLength, out uint receivedLength, [Out] byte[] address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SendNative(nint handle, byte[] packet,
        uint packetLength, out uint sentLength, byte[] address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ShutdownNative(nint handle, int how);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool CloseNative(nint handle);

    private readonly object _gate = new();
    private readonly object _sendGate = new();
    private readonly NativeIoLifetime _ioLifetime;
    private readonly IPAddress _local;
    private readonly IPAddress _pcscf;
    private readonly int _interfaceIndex;
    private readonly Func<bool> _routeIsVerified;
    private readonly nint _library;
    private readonly ReceiveNative _receive;
    private readonly SendNative _send;
    private readonly ShutdownNative _shutdown;
    private readonly CloseNative _close;
    private nint _receiveHandle;
    private nint _sendHandle;
    private bool _receiving;
    private bool _disposed;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(nint handle, nint overlapped);

    internal WindowsHostImsWinDivertIo(string dllPath, IPAddress local,
        IPAddress pcscf, int interfaceIndex, ulong? interfaceLuid = null)
        : this(dllPath, local, pcscf, interfaceIndex,
            CreateRouteVerifier(local, pcscf, interfaceIndex, interfaceLuid)) { }

    // Only tests may replace the real route check; the production constructor
    // verifies source address, P-CSCF and interface before every send.
    internal WindowsHostImsWinDivertIo(string dllPath, IPAddress local,
        IPAddress pcscf, int interfaceIndex, Func<bool> routeIsVerified)
    {
        ArgumentNullException.ThrowIfNull(dllPath);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(pcscf);
        ArgumentNullException.ThrowIfNull(routeIsVerified);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WinDivert Host IMS packet I/O requires native Windows.");
        if (local.AddressFamily != AddressFamily.InterNetworkV6 ||
            pcscf.AddressFamily != AddressFamily.InterNetworkV6 ||
            local.Equals(pcscf) || local.IsIPv4MappedToIPv6 || pcscf.IsIPv4MappedToIPv6 ||
            interfaceIndex <= 0)
            throw new ArgumentException("A distinct IPv6 IMS address, P-CSCF and interface index are required.");
        if (!Path.IsPathFullyQualified(dllPath) ||
            !Path.GetFileName(dllPath).Equals("WinDivert.dll", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(dllPath) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(dllPath)!, "WinDivert64.sys")))
            throw new FileNotFoundException("An explicit WinDivert x64 DLL and driver pair is required.", dllPath);
        if (!routeIsVerified())
            throw new InvalidOperationException("The selected IMS P-CSCF route is not pinned to this Windows bearer.");

        _local = local;
        _pcscf = pcscf;
        _interfaceIndex = interfaceIndex;
        _routeIsVerified = routeIsVerified;
        var scope = "WinDivert/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{local}|{pcscf}|{interfaceIndex}")));
        if (HostImsIoCleanup.IsPending(scope))
            throw new InvalidOperationException("The previous Host IMS native I/O has not finished closing; do not open another channel on this bearer.");
        _ioLifetime = new(scope);
        _receiveHandle = InvalidHandle;
        _sendHandle = InvalidHandle;
        _library = NativeLibrary.Load(dllPath);
        try
        {
            var open = GetExport<OpenNative>("WinDivertOpen");
            _receive = GetExport<ReceiveNative>("WinDivertRecv");
            _send = GetExport<SendNative>("WinDivertSend");
            _shutdown = GetExport<ShutdownNative>("WinDivertShutdown");
            _close = GetExport<CloseNative>("WinDivertClose");
            var filter = $"inbound and ipv6 and protocol == 50 and ifIdx == {interfaceIndex} " +
                         $"and localAddr == {local} and remoteAddr == {pcscf}";
            _receiveHandle = open(filter, 0, 0, ConsumeReceiveOnly);
            if (_receiveHandle == InvalidHandle)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                    "Could not open scoped WinDivert IMS ESP receive consumer.");
            _sendHandle = open("false", 0, 0, SendOnly);
            if (_sendHandle == InvalidHandle)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                    "Could not open send-only WinDivert IMS ESP injector.");
        }
        catch
        {
            if (_receiveHandle != InvalidHandle && _close is not null) _close(_receiveHandle);
            if (_sendHandle != InvalidHandle && _close is not null) _close(_sendHandle);
            NativeLibrary.Free(_library);
            throw;
        }
    }

    private T GetExport<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    internal static Func<bool> CreateRouteVerifier(IPAddress local, IPAddress pcscf, int index, ulong? luid,
        Func<HostImsEndpointCandidate, int, ulong?, HostImsRouteCheck>? lookup = null)
    {
        var endpoint = new HostImsEndpointCandidate(5, local, pcscf);
        var ownedLuid = luid;
        lookup ??= HostImsRouteVerifier.CheckUnpinnedOutput;
        return () =>
        {
            var route = lookup(endpoint, index, ownedLuid);
            if (!route.IsVerified || route.InterfaceIndex != index || route.InterfaceLuid is not > 0 ||
                ownedLuid is { } expected && route.InterfaceLuid != expected) return false;
            ownedLuid ??= route.InterfaceLuid;
            return true;
        };
    }

    private static void ValidatePacket(byte[] packet, IPAddress source, IPAddress destination)
    {
        if (packet.Length < 48 || packet.Length > 65575 || packet[0] >> 4 != 6 ||
            packet[6] != 50 ||
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2)) != packet.Length - 40 ||
            !packet.AsSpan(8, 16).SequenceEqual(source.GetAddressBytes()) ||
            !packet.AsSpan(24, 16).SequenceEqual(destination.GetAddressBytes()))
            throw new FormatException("Raw Host IMS packet is not an exact IPv6 ESP bearer/P-CSCF packet.");
    }

    public void Send(byte[] ipv6EspPacket)
    {
        ArgumentNullException.ThrowIfNull(ipv6EspPacket);
        ValidatePacket(ipv6EspPacket, _local, _pcscf);
        lock (_sendGate)
        {
            using var operation = _ioLifetime.Enter();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_routeIsVerified())
                throw new InvalidOperationException("IMS P-CSCF route changed before raw ESP injection.");
            var address = new byte[AddressBytes];
            // WINDIVERT_ADDRESS: bit 17 = Outbound, bit 20 = IPv6; Network.IfIdx
            // begins at byte 16. WinDivert ignores this index for outbound
            // injection: _routeIsVerified must prove the unconstrained route.
            BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(8), (1u << 17) | (1u << 20));
            BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(16), checked((uint)_interfaceIndex));
            if (!_send(_sendHandle, ipv6EspPacket, checked((uint)ipv6EspPacket.Length),
                    out var sent, address) || sent != ipv6EspPacket.Length)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                    "WinDivert failed to inject the Host IMS ESP packet.");
        }
    }

    public byte[] Receive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = _ioLifetime.Enter();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_receiving) throw new InvalidOperationException("Only one IMS ESP receiver is supported.");
            _receiving = true;
        }
        try
        {
            using var registration = cancellationToken.Register(
                static state => ((WindowsHostImsWinDivertIo)state!).QueueShutdownReceive(), this);
            var buffer = new byte[65575];
            var address = new byte[AddressBytes];
            if (!_receive(_receiveHandle, buffer, (uint)buffer.Length, out var length, address))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                    "WinDivert failed to receive an IMS ESP packet.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var packet = buffer.AsSpan(0, checked((int)length)).ToArray();
            ValidatePacket(packet, _pcscf, _local);
            return packet;
        }
        finally
        {
            lock (_gate)
            {
                _receiving = false;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void QueueShutdownReceive()
    {
        IDisposable lease;
        try { lease = _ioLifetime.Enter(); }
        catch (ObjectDisposedException) { return; } // Dispose owns cancellation now.
        _ = Task.Run(() =>
        {
            using (lease)
            {
                // WinDivert's synchronous API internally waits on OVERLAPPED
                // I/O. CancelIoEx cancels that exact handle's pending requests.
                // Shutdown also handles cancellation just before Recv is posted.
                CancelIoEx(_receiveHandle, 0);
                _shutdown(_receiveHandle, 1);
                CancelIoEx(_receiveHandle, 0);
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _ioLifetime.Close(() =>
        {
            // Never hold _gate or _sendGate while cancelling native operations.
            CancelIoEx(_receiveHandle, 0);
            CancelIoEx(_sendHandle, 0);
            _shutdown(_receiveHandle, 3);
            _shutdown(_sendHandle, 3);
            CancelIoEx(_receiveHandle, 0);
            CancelIoEx(_sendHandle, 0);
        }, () =>
        {
            var receiveClosed = _close(_receiveHandle);
            var receiveError = receiveClosed ? 0 : Marshal.GetLastPInvokeError();
            var sendClosed = _close(_sendHandle);
            var sendError = sendClosed ? 0 : Marshal.GetLastPInvokeError();
            if (!receiveClosed || !sendClosed)
                throw new System.ComponentModel.Win32Exception(receiveError != 0 ? receiveError : sendError,
                    "Host IMS native handles did not both close; the native library remains loaded.");
            NativeLibrary.Free(_library);
        }, TimeSpan.FromSeconds(3));
    }
}
