using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
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
    // UDP loss is preferable to retaining an unbounded encrypted media backlog.
    private readonly Channel<byte[]> _espChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
        SingleReader = false
    });
    private sealed record PendingResponse(Action<byte[]> Deliver, Action Cancel);
    private readonly ConcurrentDictionary<(uint MessageId, IkeExchangeType Exchange), PendingResponse> _pendingIkeResponses = new();
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private readonly Socks5Client? _socks5Client;
    private readonly Action<string>? _diagnosticLog;
    private readonly IPAddress? _localAddress;
    private readonly Func<IPAddress, CancellationToken, Task>? _beforeInitialSend;

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
    internal IPAddress GatewayAddress => _socks5Client == null && _socket?.RemoteEndPoint is IPEndPoint peer
        ? peer.Address : _remoteEndpoint.Address;

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
        IPAddress? localAddress = null,
        Func<IPAddress, CancellationToken, Task>? beforeInitialSend = null)
    {
        _remoteIp = remoteIp ?? throw new ArgumentNullException(nameof(remoteIp));
        _remoteEndpoint = new IPEndPoint(remoteIp, initialPort);
        _timeout = timeout ?? TimeSpan.FromSeconds(12);
        _socks5Client = socks5Client;
        _diagnosticLog = diagnosticLog;
        if (socks5Client == null && localAddress is not null && localAddress.AddressFamily != remoteIp.AddressFamily)
            throw new ArgumentException("The requested local address must use the same IP family as the ePDG endpoint.", nameof(localAddress));
        _localAddress = localAddress;
        _beforeInitialSend = beforeInitialSend;

        try { InitSocket(bindPort: 0); }
        catch { Dispose(); throw; }
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
                if (_localAddress != null && _localAddress.AddressFamily != _socks5RelayEndpoint.AddressFamily)
                    throw new ArgumentException("The requested local address must use the SOCKS relay's IP family, not the ePDG target's family.");
                var bindAddress = _localAddress ?? any;
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
    public Task<byte[]> RoundTripAsync(byte[] packet, CancellationToken ct = default)
        => RoundTripCoreAsync(new[] { packet }, bytes => bytes, ct);

    /// <summary>Authenticated SK/SKF exchange; filters forged datagrams without ending the receive window.</summary>
    public async Task<IkeDecryptedMessage> RoundTripEncryptedAsync(
        IkeWire.IkeMessage header, IReadOnlyList<IkePayload> payloads, IkeSuite suite,
        byte[] outgoingEncryption, byte[] outgoingIntegrity, byte[] incomingEncryption, byte[] incomingIntegrity,
        bool fragmentationNegotiated, CancellationToken ct = default)
    {
        var maximum = _remoteIp.AddressFamily == AddressFamily.InterNetwork ? 540 : 1228;
        if (_socks5Client != null) maximum -= 40; // relay address/header and outer-family allowance
        var packets = IkeProtectedMessage.Encrypt(header, payloads, suite, outgoingEncryption, outgoingIntegrity, fragmentationNegotiated, maximum);
        var expected = new IkeWire.IkeMessage
        {
            InitiatorSpi = header.InitiatorSpi,
            ResponderSpi = header.ResponderSpi,
            MessageId = header.MessageId,
            Exchange = header.Exchange,
            Flags = (header.Flags ^ IkeFlags.Initiator) | IkeFlags.Response
        };
        using var receiver = new IkeEncryptedMessageReceiver(expected, suite, incomingEncryption, incomingIntegrity, fragmentationNegotiated,
            _timeout > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : _timeout);
        return await RoundTripCoreAsync(packets, bytes => receiver.Accept(bytes), ct).ConfigureAwait(false);
    }

    private async Task<T> RoundTripCoreAsync<T>(IReadOnlyList<byte[]> packets, Func<byte[], T?> accept, CancellationToken ct) where T : class
    {
        ThrowIfDisposed();
        if (packets.Count is < 1 or > IkeProtectedMessage.MaximumFragments) throw new ArgumentException("Invalid IKE packet batch.");
        var request = IkeWire.ParseMessage(packets[0]);
        if (request.Flags.HasFlag(IkeFlags.Response)) throw new ArgumentException("IKE request must not have response flag.");
        await _exchangeGate.WaitAsync(ct).ConfigureAwait(false);
        var key = (request.MessageId, request.Exchange);
        var response = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryGate = new object();
        var invalid = 0;
        void Deliver(byte[] bytes)
        {
            lock (deliveryGate)
            {
                if (response.Task.IsCompleted) return;
                try
                {
                    if (bytes.Length < 28 || bytes[17] >> 4 != 2 ||
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(24, 4)) != bytes.Length ||
                        BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(0, 8)) != request.InitiatorSpi ||
                        request.ResponderSpi != 0 && BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8, 8)) != request.ResponderSpi ||
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) != request.MessageId ||
                        bytes[18] != (byte)request.Exchange || (bytes[19] & 0x28) != ((((byte)request.Flags ^ 8) & 8) | 32))
                    {
                        if (++invalid >= 256) response.TrySetException(new IkeFormatException("Too many unrelated IKE datagrams."));
                        return;
                    }
                    var value = accept(bytes);
                    if (value != null) response.TrySetResult(value);
                }
                catch (Exception ex) when (ex is IkeFormatException or CryptographicException)
                {
                    if (++invalid >= 256) response.TrySetException(new IkeFormatException("Too many invalid protected IKE datagrams."));
                }
                catch (Exception ex) { response.TrySetException(ex); }
            }
        }
        try
        {
            ThrowIfDisposed();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linkedCts.CancelAfter(_timeout);
            var token = linkedCts.Token;
            var viaPump = _packetPumpTask != null;
            if (viaPump && !_pendingIkeResponses.TryAdd(key, new(Deliver, () => response.TrySetCanceled())))
                throw new InvalidOperationException("IKE transaction is already pending.");
            var gateway = new IPEndPoint(GatewayAddress, _remoteEndpoint.Port);
            var wirePackets = packets.Select(packet =>
            {
                var wire = _isFloated ? new byte[4 + packet.Length] : packet;
                if (_isFloated) Buffer.BlockCopy(packet, 0, wire, 4, packet.Length);
                return _socks5Client != null ? Socks5Client.EncapsulateUdpDatagram(wire, gateway) : wire;
            }).ToArray();
            var buffer = new byte[65535];
            foreach (var interval in new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) })
            {
                token.ThrowIfCancellationRequested();
                if (_socket == null) throw new InvalidOperationException("Socket is not initialized.");
                foreach (var wire in wirePackets)
                {
                    if (request.Exchange == IkeExchangeType.IkeSaInit && request.MessageId == 0 && _beforeInitialSend != null)
                    {
                        try
                        {
                            await _beforeInitialSend(gateway.Address, token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested) { throw; }
                        catch (IkeAdmissionException) { throw; }
                        catch (Exception error) { throw new IkeAdmissionException("IKE initial send admission could not be confirmed.", error); }
                    }
                    await _socket.SendAsync(wire, SocketFlags.None, token).ConfigureAwait(false);
                }
                Log($"IKE UDP TX; exchange={request.Exchange}; message-id={request.MessageId}; fragments={packets.Count}; target={_remoteEndpoint}.");
                if (viaPump)
                {
                    var winner = await Task.WhenAny(response.Task, Task.Delay(interval, token)).ConfigureAwait(false);
                    if (winner == response.Task) return await response.Task.ConfigureAwait(false);
                    continue;
                }
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started) < interval)
                {
                    token.ThrowIfCancellationRequested();
                    var remaining = interval - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero) break;
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(remaining);
                    try
                    {
                        var size = await _socket.ReceiveAsync(buffer, SocketFlags.None, readCts.Token).ConfigureAwait(false);
                        var raw = _socks5Client != null
                            ? Socks5Client.DecapsulateUdpDatagram(buffer.AsMemory(0, size)) : buffer.AsMemory(0, size);
                        if (raw == null) continue;
                        var memory = raw.Value;
                        if (_isFloated)
                        {
                            if (memory.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(memory.Span[..4]) != 0) continue;
                            memory = memory[4..];
                        }
                        Deliver(memory.ToArray());
                        if (response.Task.IsCompleted) return await response.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
                }
            }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"IKE exchange timed out waiting for ePDG response to Message ID {request.MessageId}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            throw new TimeoutException($"IKE exchange timed out waiting for ePDG response to Message ID {request.MessageId}.");
        }
        finally
        {
            lock (deliveryGate)
            {
                _pendingIkeResponses.TryRemove(key, out _);
                response.TrySetCanceled(); // no callback can outlive its receiver
            }
            _exchangeGate.Release();
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
                        // Before NAT-T floating these bytes may be the high
                        // half of a legitimate IKE SPI, not a Non-ESP marker.
                        if (!_isFloated && TryDeliverIkeResponse(rawMemory.Span)) continue;
                        TryDeliverIkeResponse(rawMemory.Span[4..]);
                    }
                    else if (spi == inboundSpi || inboundSpi == 0)
                    {
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
            pending.Deliver(ikePacket.ToArray());
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
            pending.Cancel();
        _pendingIkeResponses.Clear();
        _espChannel.Writer.TryComplete();
        _socket?.Dispose();
        _socket = null;
        _socks5Client?.Dispose();
        _cts.Dispose();
    }
}
