using System.Buffers.Binary;
using System.Collections.Concurrent;
using qmiSharp.Core;
using qmiSharp.Transport;

namespace qmiSharp.Client;

/// <summary>Thread-safe QMI client with strict response correlation and CID lifetime management.</summary>
public sealed class QmiClient : IAsyncDisposable, IDisposable
{
    private readonly record struct PendingKey(ushort Service, byte ClientId, ushort TransactionId, ushort MessageId);
    private sealed record ClientLease(byte ClientId, int References);

    private readonly IQmiTransport _transport;
    private readonly QmiClientOptions _options;
    private readonly ConcurrentDictionary<PendingKey, TaskCompletionSource<QmiPacket>> _pendingTransactions = new();
    private readonly Dictionary<ushort, ClientLease> _allocatedClientIds = new();
    private readonly HashSet<(ushort Service, byte ClientId)> _dedicatedClientIds = [];
    private readonly SemaphoreSlim _clientIdGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;
    private int _txIdCounter;
    private bool _disposed;

    public IQmiTransport Transport => _transport;
    public bool IsConnected => !_disposed && _transport.IsConnected;
    public event Action<QmiPacket>? IndicationReceived;
    public event Action<Exception?>? TransportError;

    public QmiClient(IQmiTransport transport, QmiClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new QmiClientOptions();
        _readTask = Task.Run(ReadLoopAsync);
    }

    public static async Task<QmiClient> CreateAsync(IQmiTransport transport, QmiClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = new QmiClient(transport, options);
        try
        {
            if (client._options.SyncOnOpen)
                await client.SyncAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<QmiPacket> SendRequestAsync(QmiPacket request, CancellationToken cancellationToken = default) =>
        SendRequestAsync(request, _options.DefaultTimeout, cancellationToken);

    /// <summary>Uses an operation-specific response timeout without changing other QMI requests.</summary>
    public async Task<QmiPacket> SendRequestAsync(QmiPacket request, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        if (request.ServiceType != (ushort)QmiServiceType.Control && request.ClientId == 0)
        {
            await _clientIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_allocatedClientIds.TryGetValue(request.ServiceType, out var lease))
                    request.ClientId = lease.ClientId;
            }
            finally { _clientIdGate.Release(); }
        }

        request.IsResponse = false;
        request.IsIndication = false;
        var completion = new TaskCompletionSource<QmiPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingKey key = ReserveTransaction(request, completion);
        try
        {
            await _transport.SendAsync(request.Marshal(), cancellationToken).ConfigureAwait(false);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _pendingTransactions.TryRemove(key, out _); }
    }

    private PendingKey ReserveTransaction(QmiPacket request, TaskCompletionSource<QmiPacket> completion)
    {
        int attempts = request.ServiceType == (ushort)QmiServiceType.Control ? byte.MaxValue : ushort.MaxValue;
        for (int i = 0; i < attempts; i++)
        {
            ushort candidate = request.ServiceType == (ushort)QmiServiceType.Control
                ? (ushort)(((uint)Interlocked.Increment(ref _txIdCounter) % byte.MaxValue) + 1)
                : (ushort)(((uint)Interlocked.Increment(ref _txIdCounter) % ushort.MaxValue) + 1);
            request.TransactionId = candidate;
            var key = new PendingKey(request.ServiceType, request.ClientId, candidate, request.MessageId);
            if (_pendingTransactions.TryAdd(key, completion))
                return key;
        }
        throw new QmiException($"No free transaction ID for service 0x{request.ServiceType:X4}, client {request.ClientId}");
    }

    public Task<QmiPacket> SendMessageAsync(QmiServiceType service, ushort messageId, IEnumerable<QmiTlv>? tlvs = null, CancellationToken cancellationToken = default) =>
        SendRequestAsync(new QmiPacket(service, 0, 0, messageId, tlvs), cancellationToken);

    public Task<QmiPacket> SendMessageAsync(QmiServiceType service, ushort messageId, ReadOnlyMemory<byte> rawTlvBytes, CancellationToken cancellationToken = default)
    {
        var tlvs = rawTlvBytes.IsEmpty ? null : QmiTlv.ParseMultiple(rawTlvBytes.Span);
        return SendMessageAsync(service, messageId, tlvs, cancellationToken);
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(new QmiPacket(QmiServiceType.Control, 0, 0, 0x0027), cancellationToken).ConfigureAwait(false);
        response.CheckResult();
    }

    public async Task<byte> AllocateClientIdAsync(QmiServiceType service, CancellationToken cancellationToken = default)
    {
        ushort serviceId = (ushort)service;
        await _clientIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_allocatedClientIds.TryGetValue(serviceId, out var existing))
            {
                _allocatedClientIds[serviceId] = existing with { References = checked(existing.References + 1) };
                return existing.ClientId;
            }

            byte cid = await AllocateRawClientIdAsync(serviceId, cancellationToken).ConfigureAwait(false);
            if (_dedicatedClientIds.Contains((serviceId, cid)))
                throw new QmiException("Allocate CID response reused an active dedicated client ID");
            _allocatedClientIds.Add(serviceId, new ClientLease(cid, 1));
            return cid;
        }
        finally { _clientIdGate.Release(); }
    }

    /// <summary>Allocates a distinct CID for an independently owned WDS PDN.</summary>
    public async Task<byte> AllocateDedicatedClientIdAsync(QmiServiceType service,
        CancellationToken cancellationToken = default)
    {
        ushort serviceId = (ushort)service;
        await _clientIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte cid = await AllocateRawClientIdAsync(serviceId, cancellationToken).ConfigureAwait(false);
            if (_dedicatedClientIds.Contains((serviceId, cid)) ||
                _allocatedClientIds.TryGetValue(serviceId, out var shared) && shared.ClientId == cid)
                throw new QmiException("Allocate CID response reused an active client ID");
            _dedicatedClientIds.Add((serviceId, cid));
            return cid;
        }
        finally { _clientIdGate.Release(); }
    }

    private async Task<byte> AllocateRawClientIdAsync(ushort serviceId, CancellationToken cancellationToken)
    {
        bool qrtrService = serviceId > byte.MaxValue;
        ushort messageId = qrtrService ? (ushort)0xFF22 : (ushort)0x0022;
        QmiTlv serviceTlv = qrtrService ? QmiTlv.FromUInt16(0x01, serviceId) : QmiTlv.FromByte(0x01, (byte)serviceId);
        var response = await SendRequestAsync(new QmiPacket(QmiServiceType.Control, 0, 0, messageId,
            new[] { serviceTlv }), cancellationToken).ConfigureAwait(false);
        response.CheckResult();
        var tlv = response.GetTlv(0x01) ?? throw new QmiException("Allocate CID response missing TLV 0x01");
        int cidOffset = qrtrService ? 2 : 1;
        if (tlv.Value.Length <= cidOffset)
            throw new QmiException("Allocate CID response TLV 0x01 is truncated");
        ushort returnedService = qrtrService
            ? BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span[..2]) : tlv.AsByte(0);
        if (returnedService != serviceId)
            throw new QmiException("Allocate CID response returned a different service");
        return tlv.AsByte(cidOffset);
    }

    public async Task ReleaseClientIdAsync(QmiServiceType service, byte clientId, CancellationToken cancellationToken = default)
    {
        ushort serviceId = (ushort)service;
        await _clientIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_allocatedClientIds.TryGetValue(serviceId, out var lease)) return;
            if (lease.ClientId != clientId)
                throw new QmiException($"CID {clientId} does not own service 0x{serviceId:X4}");
            if (lease.References > 1)
            {
                _allocatedClientIds[serviceId] = lease with { References = lease.References - 1 };
                return;
            }

            await ReleaseRawClientIdAsync(serviceId, clientId, cancellationToken).ConfigureAwait(false);
            _allocatedClientIds.Remove(serviceId);
        }
        finally { _clientIdGate.Release(); }
    }

    public async Task ReleaseDedicatedClientIdAsync(QmiServiceType service, byte clientId,
        CancellationToken cancellationToken = default)
    {
        ushort serviceId = (ushort)service;
        await _clientIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_dedicatedClientIds.Contains((serviceId, clientId)))
                throw new QmiException($"CID {clientId} is not a dedicated client of service 0x{serviceId:X4}");
            await ReleaseRawClientIdAsync(serviceId, clientId, cancellationToken).ConfigureAwait(false);
            _dedicatedClientIds.Remove((serviceId, clientId));
        }
        finally { _clientIdGate.Release(); }
    }

    private async Task ReleaseRawClientIdAsync(ushort serviceId, byte clientId,
        CancellationToken cancellationToken)
    {
        bool qrtrService = serviceId > byte.MaxValue;
        ushort messageId = qrtrService ? (ushort)0xFF23 : (ushort)0x0023;
        byte[] releaseInfo = qrtrService
            ? new[] { (byte)(serviceId & 0xFF), (byte)(serviceId >> 8), clientId }
            : new[] { (byte)serviceId, clientId };
        var response = await SendRequestAsync(new QmiPacket(QmiServiceType.Control, 0, 0, messageId,
            new[] { new QmiTlv(0x01, releaseInfo) }), cancellationToken).ConfigureAwait(false);
        response.CheckResult();
    }

    private async Task ReadLoopAsync()
    {
        byte[] readBuffer = new byte[8192];
        var accumulator = new List<byte>(16384);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int bytesRead = await _transport.ReceiveAsync(readBuffer, _cts.Token).ConfigureAwait(false);
                if (bytesRead <= 0) break;
                accumulator.AddRange(readBuffer.AsSpan(0, bytesRead).ToArray());
                while (accumulator.Count >= 3)
                {
                    byte[] snapshot = accumulator.ToArray();
                    if (!QmiPacket.TryFindFrame(snapshot, out int start, out int length))
                    {
                        if (start > 0) accumulator.RemoveRange(0, Math.Min(start, accumulator.Count));
                        break;
                    }
                    if (start > 0) accumulator.RemoveRange(0, start);
                    byte[] frame = accumulator.GetRange(0, length).ToArray();
                    accumulator.RemoveRange(0, length);
                    try { DispatchPacket(QmiPacket.Unmarshal(frame)); }
                    catch (Exception ex) { RaiseTransportError(ex); }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) RaiseTransportError(ex); }
        finally
        {
            foreach (var pending in _pendingTransactions.Values)
                pending.TrySetException(new QmiException("QMI client disconnected or closed"));
        }
    }

    private void DispatchPacket(QmiPacket packet)
    {
        if (packet.IsIndication) { RaiseIndication(packet); return; }
        if (!packet.IsResponse) return;
        var key = new PendingKey(packet.ServiceType, packet.ClientId, packet.TransactionId, packet.MessageId);
        if (_pendingTransactions.TryGetValue(key, out var completion)) completion.TrySetResult(packet);
    }

    private void RaiseIndication(QmiPacket packet)
    {
        foreach (Action<QmiPacket> handler in IndicationReceived?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { handler(packet); } catch (Exception ex) { RaiseTransportError(ex); }
        }
    }

    private void RaiseTransportError(Exception exception)
    {
        foreach (Action<Exception?> handler in TransportError?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { handler(exception); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _transport.Dispose();
        try { _readTask.GetAwaiter().GetResult(); } catch { }
        _clientIdGate.Dispose();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        await _transport.DisposeAsync().ConfigureAwait(false);
        try { await _readTask.ConfigureAwait(false); } catch { }
        _clientIdGate.Dispose();
        _cts.Dispose();
    }
}
