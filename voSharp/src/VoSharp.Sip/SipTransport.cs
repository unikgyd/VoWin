using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using VoSharp.Sip;

namespace VoSharp.Telephony.VoWifi;

public sealed record SipDatagram(byte[] Payload, IPEndPoint LocalEndPoint, IPEndPoint RemoteEndPoint);

/// <summary>
/// Robust SIP over UDP transport for IMS/VoWiFi signaling.
/// Implements a single background receive pump and concurrent transaction registry.
/// Per RFC 3261 §17 / 3GPP TS 24.229.
/// </summary>
public class SipTransport : IDisposable
{
    private sealed record SipTransaction(
        TaskCompletionSource<SipMessage> FinalTcs,
        Action<SipMessage>? OnProvisional,
        string SentBy
    )
    { public volatile bool ReceivedProvisional; }

    private sealed record DialogAck(byte[] Bytes, string SentBy, DateTimeOffset CreatedAt);

    private sealed class ServerTransaction
    {
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public SipDatagram? CachedResponse { get; set; }
        public string? AckKey { get; set; }
        public CancellationTokenSource? RetransmitCts { get; set; }
        public bool RetransmitStarted { get; set; }
    }

    private UdpClient? _udp;
    private UdpClient? _reservedProtectedClientUdp;
    private UdpClient? _reservedProtectedServerUdp;
    private UdpClient? _protectedServerUdp;
    private IPEndPoint? _protectedServerLocalEp;
    private IPEndPoint? _remoteEp;
    private int _timeoutMs;
    private bool _disposed;
    private Func<byte[], Task>? _customSender;
    private Func<CancellationToken, Task<byte[]>>? _customReceiver;
    private Func<SipDatagram, CancellationToken, Task>? _datagramSender;
    private Func<CancellationToken, Task<SipDatagram>>? _datagramReceiver;
    private IPEndPoint? _localEp;
    private int? _boundInterfaceIndex;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _connectionGate = new();
    private Task? _rxTask;

    private readonly ConcurrentDictionary<string, SipTransaction> _transactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ServerTransaction> _serverTransactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DialogAck> _dialogAcks = new(StringComparer.Ordinal);

    public IPEndPoint? LocalEndPoint => _localEp ?? _udp?.Client.LocalEndPoint as IPEndPoint;
    public IPEndPoint? RemoteEndPoint => _remoteEp;
    public IPEndPoint? ProtectedServerEndPoint => _protectedServerLocalEp;
    public int? BoundInterfaceIndex => _boundInterfaceIndex;
    public event EventHandler<SipMessage>? IncomingRequestReceived;
    public event EventHandler<string>? ReceiveError;
    public event Action<SipMessage>? PreparingOutgoingRequest;

    public SipTransport(int timeoutMs = 5000)
    {
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Binds and holds both protected UDP ports before Security-Client is sent.
    /// The sockets do not receive or send traffic until the security backend
    /// has installed the SAs and ActivateProtectedPortsOnInterface is called.
    /// </summary>
    public (int ClientPort, int ServerPort) ReserveProtectedPortsOnInterface()
    {
        lock (_connectionGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SipTransport));
            if (_boundInterfaceIndex is not { } interfaceIndex || _udp is null || _localEp is null)
                throw new InvalidOperationException("Protected-port reservation requires a pinned Windows Host IMS UDP socket.");
            if (_reservedProtectedClientUdp is not null ||
                _reservedProtectedServerUdp is not null || _protectedServerUdp is not null)
                throw new InvalidOperationException("Host IMS protected ports are already reserved or active.");
            if (!_transactions.IsEmpty)
                throw new InvalidOperationException("Cannot reserve Host IMS ports while a client transaction is active.");

            UdpClient? client = null;
            UdpClient? server = null;
            try
            {
                do
                {
                    client?.Dispose();
                    client = new UdpClient(new IPEndPoint(_localEp.Address, 0));
                } while (!SecurityAgreementBuilder.IsValidProtectedPort(
                    ((IPEndPoint)client.Client.LocalEndPoint!).Port));
                do
                {
                    server?.Dispose();
                    server = new UdpClient(new IPEndPoint(_localEp.Address, 0));
                } while (!SecurityAgreementBuilder.IsValidProtectedPort(
                    ((IPEndPoint)server.Client.LocalEndPoint!).Port));
                WindowsUnicastInterface.Pin(client.Client, interfaceIndex);
                WindowsUnicastInterface.Pin(server.Client, interfaceIndex);
                _reservedProtectedClientUdp = client;
                _reservedProtectedServerUdp = server;
                return (((IPEndPoint)client.Client.LocalEndPoint!).Port,
                    ((IPEndPoint)server.Client.LocalEndPoint!).Port);
            }
            catch
            {
                client?.Dispose();
                server?.Dispose();
                throw;
            }
        }
    }

    /// <summary>Binds a local UDP socket and targets the remote P-CSCF/S-CSCF.</summary>
    public void Connect(IPAddress remoteIp, int remotePort = 5060, int localPort = 5060)
    {
        var any = remoteIp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        Connect(any, remoteIp, remotePort, localPort);
    }

    /// <summary>
    /// Binds SIP to an explicit bearer address. Host IMS must use the address
    /// assigned to the IMS PDN so Windows does not route REGISTER over the
    /// default Internet context.
    /// </summary>
    public void Connect(IPAddress localIp, IPAddress remoteIp, int remotePort = 5060, int localPort = 5060)
        => ConnectCore(localIp, remoteIp, remotePort, localPort, null);

    /// <summary>
    /// Pins a Windows UDP socket to a previously verified outgoing interface.
    /// Host IMS must not silently fall back to a Wi-Fi, VPN or Internet route.
    /// </summary>
    public void ConnectOnInterface(
        IPAddress localIp, IPAddress remoteIp, int interfaceIndex,
        int remotePort = 5060, int localPort = 5060)
    {
        if (interfaceIndex <= 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows interface pinning is required for Host IMS.");
        ConnectCore(localIp, remoteIp, remotePort, localPort, interfaceIndex);
    }

    private void ConnectCore(
        IPAddress localIp, IPAddress remoteIp, int remotePort, int localPort, int? interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(localIp);
        ArgumentNullException.ThrowIfNull(remoteIp);
        if (localIp.AddressFamily != remoteIp.AddressFamily &&
            !localIp.Equals(IPAddress.Any) && !localIp.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("IMS local and P-CSCF addresses must use the same address family.", nameof(remoteIp));
        lock (_connectionGate)
        {
            EnsureNotConnected();
            var udp = new UdpClient(new IPEndPoint(localIp, localPort));
            try
            {
                if (interfaceIndex is { } index)
                    WindowsUnicastInterface.Pin(udp.Client, index);
                _remoteEp = new IPEndPoint(remoteIp, remotePort);
                _udp = udp;
                _localEp = (IPEndPoint)udp.Client.LocalEndPoint!;
                _boundInterfaceIndex = interfaceIndex;
                _rxTask = Task.Run(() => RxLoopAsync());
            }
            catch
            {
                udp.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Moves a Host IMS UDP socket from the unprotected registration ports to
    /// the protected client/server port pair, without changing the selected
    /// IMS address, P-CSCF address, or Windows outgoing interface. The caller
    /// must install the matching IPsec SAs before sending on the new ports.
    /// </summary>
    public void ActivateProtectedPortsOnInterface(
        int localClientPort, int localServerPort, int pcscfServerPort)
    {
        if (!SecurityAgreementBuilder.IsValidProtectedPort(localClientPort) ||
            !SecurityAgreementBuilder.IsValidProtectedPort(localServerPort) ||
            localClientPort == localServerPort ||
            !SecurityAgreementBuilder.IsValidProtectedPort(pcscfServerPort))
            throw new ArgumentOutOfRangeException(nameof(localClientPort), "IMS protected ports must be valid non-default SIP ports.");
        lock (_connectionGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SipTransport));
            if (_boundInterfaceIndex is not { } interfaceIndex || _udp is null ||
                _localEp is null || _remoteEp is null)
                throw new InvalidOperationException("Protected-port rebinding requires a pinned Windows Host IMS UDP socket.");
            if (_protectedServerUdp is not null)
                throw new InvalidOperationException("Host IMS protected ports are already active.");
            if (!_transactions.IsEmpty)
                throw new InvalidOperationException("Cannot rebind Host IMS SIP while a client transaction is active.");

            UdpClient? replacement = null;
            UdpClient? server = null;
            if (_reservedProtectedClientUdp is not null || _reservedProtectedServerUdp is not null)
            {
                replacement = _reservedProtectedClientUdp;
                server = _reservedProtectedServerUdp;
                if (replacement is null || server is null ||
                    ((IPEndPoint)replacement.Client.LocalEndPoint!).Port != localClientPort ||
                    ((IPEndPoint)server.Client.LocalEndPoint!).Port != localServerPort)
                    throw new InvalidOperationException("IMS protected ports differ from the pre-bound reservation.");
                _reservedProtectedClientUdp = null;
                _reservedProtectedServerUdp = null;
            }
            else
            {
                try
                {
                    replacement = new UdpClient(new IPEndPoint(_localEp.Address, localClientPort));
                    server = new UdpClient(new IPEndPoint(_localEp.Address, localServerPort));
                    WindowsUnicastInterface.Pin(replacement.Client, interfaceIndex);
                    WindowsUnicastInterface.Pin(server.Client, interfaceIndex);
                }
                catch
                {
                    replacement?.Dispose();
                    server?.Dispose();
                    throw;
                }
            }
            var old = _udp;
            _udp = replacement;
            _localEp = (IPEndPoint)replacement.Client.LocalEndPoint!;
            _remoteEp = new IPEndPoint(_remoteEp.Address, pcscfServerPort);
            _protectedServerUdp = server;
            _protectedServerLocalEp = (IPEndPoint)server.Client.LocalEndPoint!;
            _ = Task.Run(() => RxLoopAsync(server));
            old.Dispose(); // Wakes the receive pump; it continues on the new socket.
        }
    }

    /// <summary>Connects via a custom sender/receiver delegate (e.g. userspace ESP encapsulation).</summary>
    public void ConnectCustom(Func<byte[], Task> sender, Func<CancellationToken, Task<byte[]>> receiver, int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_connectionGate)
        {
            EnsureNotConnected();
            _customSender = sender;
            _customReceiver = receiver;
            _timeoutMs = timeoutMs;
            _rxTask = Task.Run(() => RxLoopAsync());
        }
    }

    /// <summary>User-space IP transports retain both endpoints for per-request responses.</summary>
    public void ConnectDatagrams(
        IPEndPoint local, IPEndPoint remote,
        Func<SipDatagram, CancellationToken, Task> sender,
        Func<CancellationToken, Task<SipDatagram>> receiver, int timeoutMs = 30000)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_connectionGate)
        {
            EnsureNotConnected();
            _localEp = local;
            _remoteEp = remote;
            _datagramSender = sender;
            _datagramReceiver = receiver;
            _timeoutMs = timeoutMs;
            _rxTask = Task.Run(() => RxLoopAsync());
        }
    }

    private void EnsureNotConnected()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SipTransport));
        if (_rxTask != null) throw new InvalidOperationException("SipTransport is already connected.");
    }

    public void SetEndpoints(IPEndPoint local, IPEndPoint remote)
    {
        if (_datagramSender == null) throw new InvalidOperationException("Endpoint switching requires a datagram transport.");
        _localEp = local;
        _remoteEp = remote;
    }

    private static string GetTxKey(SipMessage message)
    {
        var parts = (message.GetHeader("CSeq") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !uint.TryParse(parts[0], out var number))
            throw new FormatException("SIP transaction requires a numeric CSeq and method.");
        var callId = message.GetHeader("Call-ID");
        if (string.IsNullOrWhiteSpace(callId)) throw new FormatException("SIP transaction requires Call-ID.");
        var topVia = (message.GetHeader("Via") ?? string.Empty).Split(',')[0];
        var branch = Regex.Match(topVia, @"(?:^|;)\s*branch\s*=\s*([^;\s,]+)", RegexOptions.IgnoreCase);
        return $"{callId.Trim()}:{number}:{parts[1].ToUpperInvariant()}:{(branch.Success ? branch.Groups[1].Value : string.Empty)}";
    }

    private static string? GetViaSentBy(SipMessage message)
    {
        var topVia = (message.GetHeader("Via") ?? string.Empty).Split(',')[0];
        var match = Regex.Match(topVia, @"^\s*SIP/2\.0/[^\s]+\s+([^;\s,]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string GetServerTxKey(SipMessage request)
    {
        var via = request.GetHeader("Via") ?? throw new FormatException("SIP request has no Via header.");
        var topVia = via.Split(',')[0];
        var branch = Regex.Match(topVia, @"(?:^|;)\s*branch\s*=\s*([^;\s,]+)", RegexOptions.IgnoreCase);
        var method = request.Method.ToUpperInvariant();
        if (branch.Success)
        {
            var sentBy = Regex.Match(topVia, @"^SIP/2\.0/[^\s]+\s+([^;\s]+)", RegexOptions.IgnoreCase);
            return $"{branch.Groups[1].Value}:{sentBy.Groups[1].Value.ToLowerInvariant()}:{method}";
        }
        return $"{topVia}:{request.GetHeader("Call-ID")}:{request.GetHeader("CSeq")}:{method}";
    }

    private static string GetAckKey(SipMessage message)
    {
        var cseq = (message.GetHeader("CSeq") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        static string Tag(string? header)
        {
            var match = Regex.Match(header ?? string.Empty, @"(?:^|;)\s*tag\s*=\s*([^;>\s]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        return $"{message.GetHeader("Call-ID")?.Trim()}:{cseq}:{Tag(message.GetHeader("From"))}:{Tag(message.GetHeader("To"))}";
    }

    private async Task ReplyAsync(
        SipMessage response,
        SipDatagram packet,
        string serverTransactionKey,
        CancellationToken ct)
    {
        var via = response.GetHeader("Via") ?? throw new FormatException("Response has no Via.");
        // Only the top Via controls the response destination (RFC 3261 / RFC 3581).
        int comma = via.IndexOf(',');
        var top = comma < 0 ? via : via[..comma];
        var tail = comma < 0 ? "" : via[comma..];
        bool rport = Regex.IsMatch(top, @";\s*rport(?:\s*=\s*\d*)?(?=\s*;|\s*$)", RegexOptions.IgnoreCase);
        int port = packet.RemoteEndPoint.Port;
        if (!rport)
        {
            var sentBy = Regex.Match(top, @"^SIP/2\.0/UDP\s+(?:\[[^\]]+\]|[^:;\s]+)(?::(\d+))?", RegexOptions.IgnoreCase);
            port = sentBy.Groups[1].Success ? int.Parse(sentBy.Groups[1].Value) : 5060;
            if (port is < 1 or > 65535) throw new FormatException("Invalid Via port.");
        }
        top = Regex.Replace(top, @";\s*received\s*=\s*[^;\s]+", "", RegexOptions.IgnoreCase);
        if (rport)
            top = Regex.Replace(top, @";\s*rport(?:\s*=\s*\d*)?(?=\s*;|\s*$)", $";rport={packet.RemoteEndPoint.Port}", RegexOptions.IgnoreCase);
        response.Headers["Via"][0] = top + ";received=" + packet.RemoteEndPoint.Address + tail;
        var reply = new SipDatagram(response.ToBytes(), packet.LocalEndPoint, new IPEndPoint(packet.RemoteEndPoint.Address, port));
        if (_serverTransactions.TryGetValue(serverTransactionKey, out var transaction))
        {
            transaction.CachedResponse = reply;
            var cseqMethod = (response.GetHeader("CSeq") ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            if (response.StatusCode is >= 200 and < 300 &&
                cseqMethod?.Equals("INVITE", StringComparison.OrdinalIgnoreCase) == true)
            {
                lock (transaction)
                {
                    transaction.AckKey = GetAckKey(response);
                    transaction.RetransmitCts ??= CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                }
            }
        }
        await SendDatagramAsync(reply, ct).ConfigureAwait(false);
        if (transaction?.RetransmitCts != null)
            StartInviteFinalRetransmission(transaction);
    }

    private void StartInviteFinalRetransmission(ServerTransaction transaction)
    {
        CancellationToken token;
        lock (transaction)
        {
            if (transaction.RetransmitStarted || transaction.RetransmitCts == null) return;
            transaction.RetransmitStarted = true;
            token = transaction.RetransmitCts.Token;
        }

        _ = Task.Run(async () =>
        {
            var interval = 500;
            var elapsed = 0;
            try
            {
                while (elapsed < 32000 && !token.IsCancellationRequested)
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                    elapsed += interval;
                    if (transaction.CachedResponse is { } cached)
                        await SendDatagramAsync(cached, token).ConfigureAwait(false);
                    interval = Math.Min(interval * 2, 4000);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
        });
    }

    private void AcknowledgeInviteFinal(SipMessage ack)
    {
        var ackKey = GetAckKey(ack);
        foreach (var transaction in _serverTransactions.Values)
        {
            if (!string.Equals(transaction.AckKey, ackKey, StringComparison.Ordinal)) continue;
            StopServerRetransmission(transaction);
        }
    }

    private static void StopServerRetransmission(ServerTransaction transaction)
    {
        CancellationTokenSource? cts;
        lock (transaction)
        {
            cts = transaction.RetransmitCts;
            transaction.RetransmitCts = null;
        }
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private async Task SendDatagramAsync(SipDatagram datagram, CancellationToken ct)
    {
        if (_datagramSender != null)
            await _datagramSender(datagram, ct).ConfigureAwait(false);
        else
        {
            var udp = _protectedServerLocalEp?.Equals(datagram.LocalEndPoint) == true
                ? _protectedServerUdp : _udp;
            await udp!.SendAsync(datagram.Payload, datagram.RemoteEndPoint, ct).ConfigureAwait(false);
        }
    }

    private async Task SendRequestBytesAsync(byte[] bytes, CancellationToken ct)
    {
        if (_datagramSender != null)
        {
            await _datagramSender(new SipDatagram(bytes, _localEp!, _remoteEp!), ct).ConfigureAwait(false);
            return;
        }
        if (_customSender != null)
        {
            await _customSender(bytes).ConfigureAwait(false);
            return;
        }
        await _udp!.SendAsync(bytes, _remoteEp!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Background pump receiving SIP datagrams and dispatching to registered transactions or incoming request events.
    /// </summary>
    private async Task RxLoopAsync(UdpClient? protectedServerSocket = null)
    {
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            try
            {
                byte[] buffer;
                SipDatagram? packet = null;
                if (protectedServerSocket != null)
                {
                    var local = (IPEndPoint)protectedServerSocket.Client.LocalEndPoint!;
                    var res = await protectedServerSocket.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    if (!ReferenceEquals(protectedServerSocket, _protectedServerUdp)) continue;
                    buffer = res.Buffer;
                    packet = new SipDatagram(buffer, local, res.RemoteEndPoint);
                }
                else if (_datagramReceiver != null)
                {
                    packet = await _datagramReceiver(_cts.Token).ConfigureAwait(false);
                    buffer = packet.Payload;
                }
                else if (_customReceiver != null)
                {
                    buffer = await _customReceiver(_cts.Token).ConfigureAwait(false);
                }
                else if (_udp is { } currentUdp)
                {
                    var local = (IPEndPoint)currentUdp.Client.LocalEndPoint!;
                    var res = await currentUdp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    if (!ReferenceEquals(currentUdp, _udp)) continue;
                    buffer = res.Buffer;
                    packet = new SipDatagram(buffer, local, res.RemoteEndPoint);
                }
                else
                {
                    break;
                }

                if (buffer == null || buffer.Length == 0) continue;

                // A Host IMS socket is deliberately pinned to one Windows IMS
                // interface and one selected P-CSCF address. Transaction headers
                // alone do not authenticate a UDP response; ignore packets from
                // another IP before they can complete REGISTER or INVITE.
                // Do not require the same source port: SIP proxies can reply
                // from a different UDP port, and ESP-backed transports apply
                // their own peer validation instead of this direct-socket rule.
                if (_boundInterfaceIndex is not null && packet is not null && _remoteEp is not null &&
                    !packet.RemoteEndPoint.Address.Equals(_remoteEp.Address))
                    continue;

                var sipMsg = SipMessage.Parse(buffer);

                if (sipMsg.IsRequest)
                {
                    if (sipMsg.Method.Equals("ACK", StringComparison.OrdinalIgnoreCase))
                        AcknowledgeInviteFinal(sipMsg);
                    if (packet != null)
                    {
                        var context = packet;
                        var serverKey = GetServerTxKey(sipMsg);
                        PurgeServerTransactions();
                        if (!_serverTransactions.TryAdd(serverKey, new ServerTransaction()))
                        {
                            if (_serverTransactions.TryGetValue(serverKey, out var duplicate) &&
                                duplicate.CachedResponse is { } cached)
                                await SendDatagramAsync(cached, _cts.Token).ConfigureAwait(false);
                            continue;
                        }
                        sipMsg.ResponseSender = (response, token) => ReplyAsync(response, context, serverKey, token);
                    }
                    try { IncomingRequestReceived?.Invoke(this, sipMsg); } catch { }
                }
                else
                {
                    var key = GetTxKey(sipMsg);
                    var sentBy = GetViaSentBy(sipMsg);
                    if (sentBy == null) continue;

                    if (_transactions.TryGetValue(key, out var tx) &&
                        string.Equals(sentBy, tx.SentBy, StringComparison.OrdinalIgnoreCase))
                    {
                        if (sipMsg.StatusCode < 200)
                        {
                            tx.ReceivedProvisional = true;
                            try { tx.OnProvisional?.Invoke(sipMsg); } catch { }
                        }
                        else
                        {
                            tx.FinalTcs.TrySetResult(sipMsg);
                        }
                    }
                    else if (sipMsg.StatusCode is >= 200 and < 300 &&
                             (sipMsg.GetHeader("CSeq") ?? string.Empty).EndsWith(" INVITE", StringComparison.OrdinalIgnoreCase) &&
                             _dialogAcks.TryGetValue(GetAckKey(sipMsg), out var ack) &&
                             string.Equals(sentBy, ack.SentBy, StringComparison.OrdinalIgnoreCase))
                    {
                        await SendRequestBytesAsync(ack.Bytes, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) when (!_disposed && _udp is not null)
            {
                // Rebinding closes the previous UDP socket while the receive
                // pump may still be blocked in ReceiveAsync on that socket.
                continue;
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested || _disposed) break;
                try { ReceiveError?.Invoke(this, ex.Message); } catch { }
                try { await Task.Delay(20, _cts.Token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private void PurgeServerTransactions()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(64);
        foreach (var transaction in _serverTransactions)
            if (transaction.Value.CreatedAt < cutoff)
            {
                if (_serverTransactions.TryRemove(transaction.Key, out var removed))
                    StopServerRetransmission(removed);
            }
        foreach (var ack in _dialogAcks)
            if (ack.Value.CreatedAt < cutoff)
                _dialogAcks.TryRemove(ack.Key, out _);
    }

    /// <summary>
    /// Sends a SIP request and waits for any matching final response.
    /// </summary>
    public async Task<SipMessage?> SendAndReceiveAsync(
        SipMessage request,
        CancellationToken ct = default)
    {
        try
        {
            return await SendAndReceiveFinalAsync(request, onProvisional: null, timeoutMs: _timeoutMs, ct: ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a SIP request without waiting for a response (e.g., ACK).
    /// </summary>
    public async Task SendAsync(SipMessage request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!request.IsRequest && request.ResponseSender != null)
        {
            await request.ResponseSender(request, ct).ConfigureAwait(false);
            return;
        }
        if (_udp == null && _customSender == null && _datagramSender == null)
            throw new InvalidOperationException("SipTransport not connected.");

        if (request.IsRequest)
            PreparingOutgoingRequest?.Invoke(request);
        var rawBytes = request.ToBytes();
        if (request.IsRequest && request.Method.Equals("ACK", StringComparison.OrdinalIgnoreCase))
        {
            var sentBy = GetViaSentBy(request);
            if (sentBy != null)
                _dialogAcks[GetAckKey(request)] = new DialogAck(rawBytes, sentBy, DateTimeOffset.UtcNow);
        }
        if (_datagramSender != null)
        {
            await _datagramSender(new SipDatagram(rawBytes, _localEp!, _remoteEp!), ct).ConfigureAwait(false);
            return;
        }
        if (_customSender != null)
        {
            await _customSender(rawBytes).ConfigureAwait(false);
            return;
        }

        await _udp!.SendAsync(rawBytes, _remoteEp!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a SIP request and processes responses via transaction registration.
    /// </summary>
    public async Task<SipMessage> SendAndReceiveFinalAsync(
        SipMessage request,
        Action<SipMessage>? onProvisional = null,
        int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        if (_udp == null && _customSender == null && _datagramSender == null)
            throw new InvalidOperationException("SipTransport not connected.");

        PreparingOutgoingRequest?.Invoke(request);
        var key = GetTxKey(request);
        var sentBy = GetViaSentBy(request) ?? throw new FormatException("SIP transaction requires a top Via sent-by value.");
        var finalTcs = new TaskCompletionSource<SipMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tx = new SipTransaction(finalTcs, onProvisional, sentBy);
        if (!_transactions.TryAdd(key, tx)) throw new InvalidOperationException($"SIP transaction already active: {key}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            cts.CancelAfter(timeoutMs);
            var encodedRequest = request.ToBytes();
            await SendRequestBytesAsync(encodedRequest, cts.Token).ConfigureAwait(false);
            int interval = 500;
            try
            {
                while (!finalTcs.Task.IsCompleted)
                {
                    var delay = Task.Delay(interval, cts.Token);
                    if (await Task.WhenAny(finalTcs.Task, delay).ConfigureAwait(false) == finalTcs.Task) break;
                    await delay.ConfigureAwait(false);
                    // A final response can win the race just after WhenAny chose
                    // the timer. Never emit one more INVITE after completion.
                    if (finalTcs.Task.IsCompleted) break;
                    if (!(tx.ReceivedProvisional && request.Method == "INVITE"))
                        await SendRequestBytesAsync(encodedRequest, cts.Token).ConfigureAwait(false);
                    interval = tx.ReceivedProvisional ? 4000 : Math.Min(interval * 2, 4000);
                }
                return await finalTcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                throw new TimeoutException($"SIP: no final response for {key} within {timeoutMs} ms.");
            }
        }
        finally
        {
            _transactions.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        lock (_connectionGate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _cts.Cancel();

                foreach (var kvp in _transactions)
                {
                    kvp.Value.FinalTcs.TrySetCanceled();
                }
                _transactions.Clear();
                foreach (var transaction in _serverTransactions.Values)
                    StopServerRetransmission(transaction);
                _serverTransactions.Clear();
                _dialogAcks.Clear();

                try { _udp?.Dispose(); } catch { }
                _udp = null;
                try { _protectedServerUdp?.Dispose(); } catch { }
                _protectedServerUdp = null;
                _protectedServerLocalEp = null;
                try { _reservedProtectedClientUdp?.Dispose(); } catch { }
                try { _reservedProtectedServerUdp?.Dispose(); } catch { }
                _reservedProtectedClientUdp = null;
                _reservedProtectedServerUdp = null;
                _boundInterfaceIndex = null;
                _cts.Dispose();
            }
        }
    }
}
