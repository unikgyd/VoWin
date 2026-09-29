using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using VoSharp.Ike.Transport;

namespace VoSharp.Ike;

/// <summary>
/// Datagram transport for IKEv2 supporting UDP 500, port floating to UDP 4500 with non-ESP marker,
/// retransmissions with exponential backoff, NAT-T keepalive (0xFF), and optional SOCKS5 UDP relay.
/// </summary>
public sealed class IkeTransport : IDisposable
{
    private readonly IPAddress _remoteIp;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _espChannel = Channel.CreateUnbounded<byte[]>();
    private readonly ConcurrentDictionary<(uint MessageId, IkeExchangeType Exchange), TaskCompletionSource<byte[]>> _pendingIkeResponses = new();
    private readonly Socks5Client? _socks5Client;
    private readonly Action<string>? _diagnosticLog;
    private readonly IPAddress? _localAddress;

    private Socket? _socket;
    private IPEndPoint _remoteEndpoint;
    private IPEndPoint? _socks5RelayEndpoint;
    private bool _isFloated;
    private Task? _keepaliveTask;
    private Task? _packetPumpTask;
    private Func<byte[], Task>? _incomingIkeRequestHandler;
    private bool _disposed;

    public bool IsFloated => _isFloated;
    public IPEndPoint RemoteEndpoint => _remoteEndpoint;
    public IPEndPoint? LocalEndpoint => _socket?.LocalEndPoint as IPEndPoint;
    public ChannelReader<byte[]> EspPackets => _espChannel.Reader;
    public Socks5Client? Socks5Proxy => _socks5Client;

    /// <summary>
    /// Installs the active IKE SA request handler. After the ESP packet pump owns
    /// the socket, peer-initiated IKE requests are delivered here instead of
    /// being silently discarded.
    /// </summary>
    internal void SetIncomingIkeRequestHandler(Func<byte[], Task>? handler)
        => Volatile.Write(ref _incomingIkeRequestHandler, handler);

    public IkeTransport(
        IPAddress remoteIp,
        int initialPort = IkeDefaults.UdpPort,
        TimeSpan? timeout = null,
        Socks5Client? socks5Client = null,
        Action<string>? diagnosticLog = null,
        IPAddress? localAddress = null)
    {
        _remoteIp = remoteIp ?? throw new ArgumentNullException(nameof(remoteIp));
        _remoteEndpoint = new IPEndPoint(remoteIp, initialPort);
        _timeout = timeout ?? TimeSpan.FromSeconds(12);
        _socks5Client = socks5Client;
        _diagnosticLog = diagnosticLog;
        if (localAddress is not null && localAddress.AddressFamily != remoteIp.AddressFamily)
            throw new ArgumentException("The requested local address must use the same IP family as the ePDG endpoint.", nameof(localAddress));
        _localAddress = localAddress;

        InitSocket(bindPort: 0);
    }

    private void InitSocket(int bindPort)
    {
        _socket?.Dispose();
        _socket = null;

        if (_socks5Client != null)
        {
            try
            {
                _socks5RelayEndpoint = _socks5Client.UdpAssociateAsync().GetAwaiter().GetResult();
                // The UDP socket talks to the SOCKS relay, not directly to the ePDG. Its address
                // family therefore follows the relay endpoint; an IPv4 proxy can carry an IPv6
                // ePDG target inside the RFC 1928 datagram header and vice versa.
                _socket = new Socket(_socks5RelayEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                var any = _socks5RelayEndpoint.AddressFamily == AddressFamily.InterNetwork
                    ? IPAddress.Any : IPAddress.IPv6Any;
                var bindAddress = _localAddress?.AddressFamily == _socks5RelayEndpoint.AddressFamily
                    ? _localAddress
                    : any;
                _socket.Bind(new IPEndPoint(bindAddress, 0));
                _socket.Connect(_socks5RelayEndpoint);
                Log($"IKE transport SOCKS5 UDP ready; relay={_socks5RelayEndpoint}; target={_remoteEndpoint}; local={LocalEndpoint}.");
                return;
            }
            catch (Exception ex)
            {
                _socket?.Dispose();
                _socket = null;
                throw new InvalidOperationException(
                    $"SOCKS5 UDP ASSOCIATE failed for {_socks5Client.ProxyHost}:{_socks5Client.ProxyPort}. " +
                    "VoWiFi was configured to use this proxy, so direct UDP is disabled.", ex);
            }
        }

        _socket = new Socket(_remoteIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (bindPort > 0)
        {
            try
            {
                var bindAddr = GetBindAddress();
                _socket.Bind(new IPEndPoint(bindAddr, bindPort));
            }
            catch
            {
                // Fall back to dynamic port if specific bind failed
                _socket.Bind(new IPEndPoint(GetBindAddress(), 0));
            }
        }
        else
        {
            _socket.Bind(new IPEndPoint(GetBindAddress(), 0));
        }

        _socket.Connect(_remoteEndpoint);
        Log($"IKE transport direct UDP ready; target={_remoteEndpoint}; local={LocalEndpoint}; local-bind={_localAddress?.ToString() ?? "system-route"}.");
    }

    private IPAddress GetBindAddress() => _localAddress
        ?? (_remoteIp.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any);

    /// <summary>
    /// Floats the transport to UDP port 4500 for NAT-Traversal (RFC 7296 §2.23 / RFC 3948).
    /// Once floated, outbound IKE packets prepend a 4-byte Non-ESP Marker (0x00000000)
    /// and a background NAT keepalive task is started.
    /// </summary>
    public void FloatTo4500()
    {
        if (_isFloated || _disposed) return;

        _isFloated = true;
        _remoteEndpoint = new IPEndPoint(_remoteIp, IkeDefaults.NattPort);
        if (_socks5Client == null)
        {
            InitSocket(bindPort: IkeDefaults.NattPort);
        }
        // A SOCKS5 UDP association is bound to its TCP control connection.
        // Recreating the UDP socket here used to send a second UDP ASSOCIATE
        // on that connection.  Many relays reject it and the old code then
        // sent IKE/ESP directly.  Keep the association and only change the
        // target encoded in RFC 1928 UDP datagrams.
        Console.WriteLine($"[IKE NAT-T] UDP {LocalEndpoint} -> {_remoteEndpoint}");

        // Start background NAT-T keepalive (sending 0xFF every 20s)
        _keepaliveTask = Task.Run(RunKeepaliveLoopAsync);
    }

    /// <summary>
    /// Sends an IKE packet and waits for a matching response, with retransmissions.
    /// </summary>
    public async Task<byte[]> RoundTripAsync(byte[] packet, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Length < IkeDefaults.HeaderLength)
            throw new ArgumentException("Packet is too short for IKE header.", nameof(packet));

        var expectedMessageId = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(20, 4));
        var expectedExchange = (IkeExchangeType)packet[18];

        // Once ESP dispatch owns the socket it also owns all receives.  Route IKE
        // INFORMATIONAL responses through that pump instead of racing two readers.
        if (_packetPumpTask != null)
            return await RoundTripViaPacketPumpAsync(packet, expectedMessageId, expectedExchange, ct).ConfigureAwait(false);

        byte[] wirePacket;
        if (_isFloated)
        {
            // Prepend 4-byte non-ESP marker: 00 00 00 00
            wirePacket = new byte[4 + packet.Length];
            Buffer.BlockCopy(packet, 0, wirePacket, 4, packet.Length);
        }
        else
        {
            wirePacket = packet;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linkedCts.CancelAfter(_timeout);
        var token = linkedCts.Token;

        var backoffs = new[]
        {
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4)
        };

        var recvBuffer = new byte[65535];

        for (var attempt = 0; attempt < backoffs.Length; attempt++)
        {
            var interval = backoffs[attempt];
            token.ThrowIfCancellationRequested();

            if (_socket == null)
                throw new InvalidOperationException("Socket is not initialized.");

            var sendBuf = _socks5Client != null
                ? Socks5Client.EncapsulateUdpDatagram(wirePacket, _remoteEndpoint)
                : wirePacket;
            Log($"IKE UDP TX; exchange={expectedExchange}; message-id={expectedMessageId}; attempt={attempt + 1}/{backoffs.Length}; " +
                $"target={_remoteEndpoint}; relay={_socks5RelayEndpoint?.ToString() ?? "direct"}; ike-bytes={wirePacket.Length}; udp-bytes={sendBuf.Length}.");
            await _socket.SendAsync(sendBuf, SocketFlags.None, token).ConfigureAwait(false);

            var attemptDeadline = DateTime.UtcNow + interval;
            while (DateTime.UtcNow < attemptDeadline)
            {
                token.ThrowIfCancellationRequested();

                var remaining = attemptDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                using var readCts = new CancellationTokenSource(remaining);
                using var readLinked = CancellationTokenSource.CreateLinkedTokenSource(token, readCts.Token);

                try
                {
                    var received = await _socket.ReceiveAsync(recvBuffer, SocketFlags.None, readLinked.Token).ConfigureAwait(false);
                    ReadOnlySpan<byte> respSpan;
                    if (_socks5Client != null)
                    {
                        var dec = Socks5Client.DecapsulateUdpDatagram(recvBuffer.AsMemory(0, received));
                        if (dec == null || dec.Value.Length < IkeDefaults.HeaderLength)
                        {
                            Log($"IKE UDP RX ignored; relay={_socks5RelayEndpoint}; udp-bytes={received}; reason=invalid SOCKS5 UDP datagram or short IKE payload.");
                            continue;
                        }
                        respSpan = dec.Value.Span;
                    }
                    else
                    {
                        if (received < IkeDefaults.HeaderLength)
                            continue;
                        respSpan = recvBuffer.AsSpan(0, received);
                    }

                    Log($"IKE UDP RX; exchange={expectedExchange}; expected-message-id={expectedMessageId}; udp-bytes={received}; ike-bytes={respSpan.Length}; relay={_socks5RelayEndpoint?.ToString() ?? "direct"}.");

                    if (_isFloated)
                    {
                        // Check for Non-ESP Marker (4 bytes 00 00 00 00)
                        if (respSpan.Length >= 4 + IkeDefaults.HeaderLength &&
                            BinaryPrimitives.ReadUInt32BigEndian(respSpan[..4]) == 0)
                        {
                            respSpan = respSpan[4..];
                        }
                        else
                        {
                            // Could be ESP packet or keepalive; ignore for IKE state machine
                            continue;
                        }
                    }

                    var msgId = BinaryPrimitives.ReadUInt32BigEndian(respSpan.Slice(20, 4));
                    var exchange = (IkeExchangeType)respSpan[18];
                    var flags = (IkeFlags)respSpan[19];

                    // Check that it is a Response matching our MessageId
                    if (flags.HasFlag(IkeFlags.Response) && msgId == expectedMessageId && exchange == expectedExchange)
                    {
                        return respSpan.ToArray();
                    }
                    Log($"IKE UDP RX ignored; expected={expectedExchange}/{expectedMessageId}; actual={exchange}/{msgId}; flags={flags}.");
                }
                catch (OperationCanceledException) when (readCts.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    // Timeout on this read attempt, retry or retransmit
                    Log($"IKE UDP RX timeout; exchange={expectedExchange}; message-id={expectedMessageId}; attempt={attempt + 1}/{backoffs.Length}; waited-ms={(int)interval.TotalMilliseconds}; target={_remoteEndpoint}.");
                    break;
                }
            }
        }

        throw new TimeoutException($"IKE exchange timed out waiting for ePDG response to Message ID {expectedMessageId}.");
    }

    private async Task<byte[]> RoundTripViaPacketPumpAsync(
        byte[] packet, uint expectedMessageId, IkeExchangeType expectedExchange, CancellationToken ct)
    {
        var key = (expectedMessageId, expectedExchange);
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingIkeResponses.TryAdd(key, response))
            throw new InvalidOperationException($"IKE exchange {expectedExchange}/{expectedMessageId} is already pending.");

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linkedCts.CancelAfter(_timeout);
            var token = linkedCts.Token;
            var wirePacket = _isFloated ? new byte[4 + packet.Length] : packet;
            if (_isFloated)
                Buffer.BlockCopy(packet, 0, wirePacket, 4, packet.Length);
            var sendBuf = _socks5Client != null
                ? Socks5Client.EncapsulateUdpDatagram(wirePacket, _remoteEndpoint)
                : wirePacket;
            var backoffs = new[]
            {
                TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)
            };

            foreach (var interval in backoffs)
            {
                token.ThrowIfCancellationRequested();
                if (_socket == null) throw new InvalidOperationException("Socket is not initialized.");
                await _socket.SendAsync(sendBuf, SocketFlags.None, token).ConfigureAwait(false);

                var completed = await Task.WhenAny(response.Task, Task.Delay(interval, token)).ConfigureAwait(false);
                if (completed == response.Task)
                    return await response.Task.ConfigureAwait(false);
            }

            throw new TimeoutException($"IKE exchange timed out waiting for ePDG response to Message ID {expectedMessageId}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            throw new TimeoutException($"IKE exchange timed out waiting for ePDG response to Message ID {expectedMessageId}.");
        }
        finally
        {
            _pendingIkeResponses.TryRemove(key, out _);
        }
    }

    private async Task RunKeepaliveLoopAsync()
    {
        var keepalivePacket = new byte[] { 0xFF };

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IkeDefaults.NattKeepaliveSeconds), _cts.Token).ConfigureAwait(false);
                if (_socket != null && _isFloated)
                {
                    var sendBuf = _socks5Client != null
                        ? Socks5Client.EncapsulateUdpDatagram(keepalivePacket, _remoteEndpoint)
                        : keepalivePacket;
                    await _socket.SendAsync(sendBuf, SocketFlags.None, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Silently ignore keepalive send failures
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void Log(string message)
    {
        try { _diagnosticLog?.Invoke(message); } catch { }
    }

    /// <summary>
    /// Starts background pump to read incoming ESP packets on UDP 4500 and write to EspPackets.
    /// </summary>
    public void StartPacketPump(uint inboundSpi)
    {
        if (_packetPumpTask != null || _disposed || _socket == null) return;

        _packetPumpTask = Task.Run(async () =>
        {
            var buffer = new byte[65535];
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (_socket == null) break;
                    var rec = await _socket.ReceiveAsync(buffer, SocketFlags.None, _cts.Token).ConfigureAwait(false);
                    if (rec < 4) continue;

                    ReadOnlyMemory<byte> rawMemory;
                    if (_socks5Client != null)
                    {
                        var dec = Socks5Client.DecapsulateUdpDatagram(buffer.AsMemory(0, rec));
                        if (dec == null || dec.Value.Length < 4) continue;
                        rawMemory = dec.Value;
                    }
                    else
                    {
                        rawMemory = buffer.AsMemory(0, rec);
                    }

                    // Demultiplex (RFC 3948):
                    // First 4 bytes == 0: Non-ESP marker (IKE packet or keepalive)
                    // First 4 bytes != 0: ESP packet with SPI
                    uint spi = BinaryPrimitives.ReadUInt32BigEndian(rawMemory.Span[..4]);
                    if (spi == 0)
                    {
                        TryDeliverIkeResponse(rawMemory.Span[4..]);
                    }
                    else if (spi == inboundSpi || inboundSpi == 0)
                    {
                        Console.WriteLine($"[IKE ESP RX] {rawMemory.Length} bytes, SPI=0x{spi:x8}");
                        _espChannel.Writer.TryWrite(rawMemory.ToArray());
                    }
                    else if (!TryDeliverIkeResponse(rawMemory.Span))
                    {
                        Console.WriteLine($"[IkeTransport UDP 4500] Dropped packet with mismatched SPI=0x{spi:x8} (expected 0x{inboundSpi:x8})");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });
    }

    private bool TryDeliverIkeResponse(ReadOnlySpan<byte> ikePacket)
    {
        if (ikePacket.Length < IkeDefaults.HeaderLength) return false;
        if ((ikePacket[17] >> 4) != 2 ||
            ikePacket[18] is < (byte)IkeExchangeType.IkeSaInit or > (byte)IkeExchangeType.Informational ||
            BinaryPrimitives.ReadUInt32BigEndian(ikePacket.Slice(24, 4)) != ikePacket.Length)
            return false;
        var flags = (IkeFlags)ikePacket[19];
        if (!flags.HasFlag(IkeFlags.Response))
        {
            var handler = Volatile.Read(ref _incomingIkeRequestHandler);
            if (handler == null) return false;

            var packet = ikePacket.ToArray();
            try
            {
                var handling = handler(packet);
                if (!handling.IsCompletedSuccessfully)
                {
                    _ = handling.ContinueWith(
                        task => Log($"IKE peer request failed: {task.Exception?.GetBaseException().Message}"),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            catch (Exception ex) { Log($"IKE peer request dispatch failed: {ex.Message}"); }
            return true;
        }

        var key = (
            BinaryPrimitives.ReadUInt32BigEndian(ikePacket.Slice(20, 4)),
            (IkeExchangeType)ikePacket[18]);
        if (_pendingIkeResponses.TryGetValue(key, out var pending))
        {
            pending.TrySetResult(ikePacket.ToArray());
            return true;
        }
        return false;
    }

    /// <summary>Sends an IKE packet without registering a local request/response transaction.</summary>
    internal async Task SendIkeAsync(byte[] ikePacket, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(ikePacket);
        if (ikePacket.Length < IkeDefaults.HeaderLength)
            throw new ArgumentException("Packet is too short for an IKE header.", nameof(ikePacket));
        if (_socket == null)
            throw new InvalidOperationException("Socket is not initialized.");

        var wirePacket = _isFloated ? new byte[4 + ikePacket.Length] : ikePacket;
        if (_isFloated)
            Buffer.BlockCopy(ikePacket, 0, wirePacket, 4, ikePacket.Length);
        var sendBuf = _socks5Client != null
            ? Socks5Client.EncapsulateUdpDatagram(wirePacket, _remoteEndpoint)
            : wirePacket;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _socket.SendAsync(sendBuf, SocketFlags.None, linkedCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an RFC 4303 / RFC 3948 ESP packet directly over the UDP 4500 socket to the ePDG.
    /// </summary>
    public async Task SendEspAsync(byte[] espPacket, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(espPacket);
        if (_socket == null) throw new InvalidOperationException("Socket is not initialized.");

        var sendBuf = _socks5Client != null
            ? Socks5Client.EncapsulateUdpDatagram(espPacket, _remoteEndpoint)
            : espPacket;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var spi = espPacket.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(espPacket) : 0;
        Console.WriteLine($"[IKE ESP TX] {espPacket.Length} bytes, SPI=0x{spi:x8}, {LocalEndpoint} -> {_remoteEndpoint}");
        await _socket.SendAsync(sendBuf, SocketFlags.None, linkedCts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        Volatile.Write(ref _incomingIkeRequestHandler, null);
        foreach (var pending in _pendingIkeResponses.Values)
            pending.TrySetCanceled();
        _pendingIkeResponses.Clear();
        _espChannel.Writer.TryComplete();
        _socket?.Dispose();
        _socket = null;
        _socks5Client?.Dispose();
        _cts.Dispose();
    }
}
