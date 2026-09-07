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
        Action<SipMessage>? OnProvisional
    )
    { public volatile bool ReceivedProvisional; }

    private UdpClient? _udp;
    private IPEndPoint? _remoteEp;
    private int _timeoutMs;
    private bool _disposed;
    private Func<byte[], Task>? _customSender;
    private Func<CancellationToken, Task<byte[]>>? _customReceiver;
    private Func<SipDatagram, CancellationToken, Task>? _datagramSender;
    private Func<CancellationToken, Task<SipDatagram>>? _datagramReceiver;
    private IPEndPoint? _localEp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _rxTask;

    private readonly ConcurrentDictionary<string, SipTransaction> _transactions = new(StringComparer.Ordinal);

    public IPEndPoint? LocalEndPoint => _localEp ?? _udp?.Client.LocalEndPoint as IPEndPoint;
    public IPEndPoint? RemoteEndPoint => _remoteEp;
    public event EventHandler<SipMessage>? IncomingRequestReceived;
    public event EventHandler<string>? ReceiveError;
    public event Action<SipMessage>? PreparingOutgoingRequest;

    public SipTransport(int timeoutMs = 5000)
    {
        _timeoutMs = timeoutMs;
    }

    /// <summary>Binds a local UDP socket and targets the remote P-CSCF/S-CSCF.</summary>
    public void Connect(IPAddress remoteIp, int remotePort = 5060, int localPort = 5060)
    {
        _remoteEp = new IPEndPoint(remoteIp, remotePort);
        _udp = new UdpClient(localPort);
        _rxTask = Task.Run(RxLoopAsync);
    }

    /// <summary>Connects via a custom sender/receiver delegate (e.g. userspace ESP encapsulation).</summary>
    public void ConnectCustom(Func<byte[], Task> sender, Func<CancellationToken, Task<byte[]>> receiver, int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(receiver);
        _customSender = sender;
        _customReceiver = receiver;
        _timeoutMs = timeoutMs;
        _rxTask = Task.Run(RxLoopAsync);
    }

    /// <summary>User-space IP transports retain both endpoints for per-request responses.</summary>
    public void ConnectDatagrams(
        IPEndPoint local, IPEndPoint remote,
        Func<SipDatagram, CancellationToken, Task> sender,
        Func<CancellationToken, Task<SipDatagram>> receiver, int timeoutMs = 30000)
    {
        _localEp = local;
        _remoteEp = remote;
        _datagramSender = sender;
        _datagramReceiver = receiver;
        _timeoutMs = timeoutMs;
        _rxTask = Task.Run(RxLoopAsync);
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
        return $"{callId.Trim()}:{number}:{parts[1].ToUpperInvariant()}";
    }

    private async Task ReplyAsync(SipMessage response, SipDatagram packet, CancellationToken ct)
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
        if (_datagramSender != null) await _datagramSender(reply, ct).ConfigureAwait(false);
        else await _udp!.SendAsync(reply.Payload, reply.RemoteEndPoint, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Background pump receiving SIP datagrams and dispatching to registered transactions or incoming request events.
    /// </summary>
    private async Task RxLoopAsync()
    {
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            try
            {
                byte[] buffer;
                SipDatagram? packet = null;
                if (_datagramReceiver != null)
                {
                    packet = await _datagramReceiver(_cts.Token).ConfigureAwait(false);
                    buffer = packet.Payload;
                }
                else if (_customReceiver != null)
                {
                    buffer = await _customReceiver(_cts.Token).ConfigureAwait(false);
                }
                else if (_udp != null)
                {
                    var res = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    buffer = res.Buffer;
                    packet = new SipDatagram(buffer, LocalEndPoint!, res.RemoteEndPoint);
                }
                else
                {
                    break;
                }

                if (buffer == null || buffer.Length == 0) continue;

                var sipMsg = SipMessage.Parse(buffer);

                if (sipMsg.IsRequest)
                {
                    if (packet != null)
                    {
                        var context = packet;
                        sipMsg.ResponseSender = (response, token) => ReplyAsync(response, context, token);
                    }
                    try { IncomingRequestReceived?.Invoke(this, sipMsg); } catch { }
                }
                else
                {
                    var key = GetTxKey(sipMsg);

                    if (_transactions.TryGetValue(key, out var tx))
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
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested || _disposed) break;
                try { ReceiveError?.Invoke(this, ex.Message); } catch { }
                try { await Task.Delay(20, _cts.Token).ConfigureAwait(false); } catch { break; }
            }
        }
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

        var key = GetTxKey(request);

        var finalTcs = new TaskCompletionSource<SipMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tx = new SipTransaction(finalTcs, onProvisional);
        if (!_transactions.TryAdd(key, tx)) throw new InvalidOperationException($"SIP transaction already active: {key}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            cts.CancelAfter(timeoutMs);
            await SendAsync(request, cts.Token).ConfigureAwait(false);
            int interval = 500;
            try
            {
                while (!finalTcs.Task.IsCompleted)
                {
                    var delay = Task.Delay(interval, cts.Token);
                    if (await Task.WhenAny(finalTcs.Task, delay).ConfigureAwait(false) == finalTcs.Task) break;
                    await delay.ConfigureAwait(false);
                    if (!(tx.ReceivedProvisional && request.Method == "INVITE"))
                        await SendAsync(request, cts.Token).ConfigureAwait(false);
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
        if (!_disposed)
        {
            _disposed = true;
            _cts.Cancel();

            foreach (var kvp in _transactions)
            {
                kvp.Value.FinalTcs.TrySetCanceled();
            }
            _transactions.Clear();

            try { _udp?.Dispose(); } catch { }
            _udp = null;
            _cts.Dispose();
        }
    }
}
