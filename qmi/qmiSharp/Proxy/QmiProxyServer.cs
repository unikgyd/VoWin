using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using qmiSharp.Core;
using qmiSharp.Transport;

namespace qmiSharp.Proxy;

/// <summary>Local QMI multiplexer with frame reassembly, transaction remapping, and CID ownership routing.</summary>
public sealed class QmiProxyServer : IAsyncDisposable
{
    private readonly record struct WireKey(ushort Service, byte ClientId, ushort Transaction, ushort Message);
    private sealed record Route(ClientState? Client, ushort OriginalTransaction);
    private sealed class ClientState(Guid id, TcpClient socket)
    {
        public Guid Id { get; } = id;
        public TcpClient Socket { get; } = socket;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
    }

    private readonly IQmiTransport _modemTransport;
    private readonly int _listenPort;
    private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();
    private readonly ConcurrentDictionary<WireKey, Route> _routes = new();
    private readonly ConcurrentDictionary<(ushort Service, byte Cid), ClientState> _cidOwners = new();
    private readonly SemaphoreSlim _modemWriteGate = new(1, 1);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private Task? _modemReadLoopTask;
    private int _transactionCounter;

    public bool IsRunning => _listener is not null;
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _listenPort;

    public QmiProxyServer(IQmiTransport modemTransport, int listenPort = 4765)
    {
        _modemTransport = modemTransport ?? throw new ArgumentNullException(nameof(modemTransport));
        _listenPort = listenPort;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start();
        _acceptLoopTask = AcceptClientsAsync(_cts.Token);
        _modemReadLoopTask = ReadModemLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _listener is not null)
            {
                TcpClient socket = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var state = new ClientState(Guid.NewGuid(), socket);
                _clients[state.Id] = state;
                _ = HandleClientAsync(state, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleClientAsync(ClientState client, CancellationToken cancellationToken)
    {
        var accumulator = new List<byte>(8192);
        byte[] buffer = new byte[4096];
        try
        {
            NetworkStream stream = client.Socket.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                accumulator.AddRange(buffer.AsSpan(0, read).ToArray());
                while (TryTakeFrame(accumulator, out byte[]? frame))
                    await ForwardRequestAsync(client, frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            foreach (var route in _routes.Where(x => ReferenceEquals(x.Value.Client, client)).ToArray())
                _routes.TryRemove(route.Key, out _);
            foreach (var owner in _cidOwners.Where(x => ReferenceEquals(x.Value, client)).ToArray())
            {
                _cidOwners.TryRemove(owner.Key, out _);
                try { await ReleaseOrphanedCidAsync(owner.Key.Service, owner.Key.Cid, cancellationToken).ConfigureAwait(false); }
                catch when (cancellationToken.IsCancellationRequested) { }
                catch { }
            }
            client.Socket.Dispose();
            client.WriteGate.Dispose();
        }
    }

    private async Task ForwardRequestAsync(ClientState client, byte[] frame, CancellationToken cancellationToken)
    {
        QmiPacket packet = QmiPacket.Unmarshal(frame);
        if (packet.IsResponse || packet.IsIndication)
            throw new InvalidDataException("Proxy clients may only send QMI requests");
        if (packet.ServiceType != (ushort)QmiServiceType.Control && packet.ClientId != 0 &&
            (!_cidOwners.TryGetValue((packet.ServiceType, packet.ClientId), out var owner) || !ReferenceEquals(owner, client)))
            throw new UnauthorizedAccessException("QMI CID is owned by another proxy client");

        ushort originalTransaction = packet.TransactionId;
        var route = ReserveRoute(packet, client, originalTransaction);
        try
        {
            await _modemWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _modemTransport.SendAsync(packet.Marshal(), cancellationToken).ConfigureAwait(false); }
            finally { _modemWriteGate.Release(); }
        }
        catch
        {
            _routes.TryRemove(route, out _);
            throw;
        }
    }

    private WireKey ReserveRoute(QmiPacket packet, ClientState? client, ushort originalTransaction)
    {
        int attempts = packet.ServiceType == (ushort)QmiServiceType.Control ? byte.MaxValue : ushort.MaxValue;
        for (int i = 0; i < attempts; i++)
        {
            ushort transaction = packet.ServiceType == (ushort)QmiServiceType.Control
                ? (ushort)(((uint)Interlocked.Increment(ref _transactionCounter) % byte.MaxValue) + 1)
                : (ushort)(((uint)Interlocked.Increment(ref _transactionCounter) % ushort.MaxValue) + 1);
            packet.TransactionId = transaction;
            var key = new WireKey(packet.ServiceType, packet.ClientId, transaction, packet.MessageId);
            if (_routes.TryAdd(key, new Route(client, originalTransaction))) return key;
        }
        throw new InvalidOperationException("No proxy transaction IDs are available");
    }

    private async Task ReleaseOrphanedCidAsync(ushort service, byte cid, CancellationToken cancellationToken)
    {
        bool qrtr = service > byte.MaxValue;
        byte[] value = qrtr
            ? new[] { (byte)(service & 0xFF), (byte)(service >> 8), cid }
            : new[] { (byte)service, cid };
        var request = new QmiPacket(QmiServiceType.Control, 0, 0, qrtr ? (ushort)0xFF23 : (ushort)0x0023,
            new[] { new QmiTlv(0x01, value) });
        WireKey key = ReserveRoute(request, null, 0);
        try
        {
            await _modemWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _modemTransport.SendAsync(request.Marshal(), cancellationToken).ConfigureAwait(false); }
            finally { _modemWriteGate.Release(); }
        }
        catch
        {
            _routes.TryRemove(key, out _);
            throw;
        }
    }

    private async Task ReadModemLoopAsync(CancellationToken cancellationToken)
    {
        var accumulator = new List<byte>(8192);
        byte[] buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await _modemTransport.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                accumulator.AddRange(buffer.AsSpan(0, read).ToArray());
                while (TryTakeFrame(accumulator, out byte[]? frame))
                    await RouteModemPacketAsync(QmiPacket.Unmarshal(frame), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RouteModemPacketAsync(QmiPacket packet, CancellationToken cancellationToken)
    {
        if (packet.IsResponse)
        {
            var key = new WireKey(packet.ServiceType, packet.ClientId, packet.TransactionId, packet.MessageId);
            if (!_routes.TryRemove(key, out var route)) return;
            if (route.Client is null) return;
            TrackCidOwnership(packet, route.Client);
            packet.TransactionId = route.OriginalTransaction;
            await WriteClientAsync(route.Client, packet.Marshal(), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!packet.IsIndication) return;
        if (packet.ServiceType != (ushort)QmiServiceType.Control &&
            _cidOwners.TryGetValue((packet.ServiceType, packet.ClientId), out var owner))
        {
            await WriteClientAsync(owner, packet.Marshal(), cancellationToken).ConfigureAwait(false);
            return;
        }

        // CTL/global indications have no CID owner and are intentionally broadcast.
        if (packet.ServiceType == (ushort)QmiServiceType.Control || packet.ClientId == byte.MaxValue)
            foreach (var client in _clients.Values)
                await WriteClientAsync(client, packet.Marshal(), cancellationToken).ConfigureAwait(false);
    }

    private void TrackCidOwnership(QmiPacket response, ClientState client)
    {
        if (response.ServiceType != (ushort)QmiServiceType.Control) return;
        if (response.MessageId is 0x0022 or 0xFF22)
        {
            var tlv = response.GetTlv(0x01);
            if (tlv is null) return;
            bool qrtr = response.MessageId == 0xFF22;
            if (tlv.Value.Length < (qrtr ? 3 : 2)) return;
            ushort service = qrtr ? tlv.AsUInt16() : tlv.AsByte(0);
            byte cid = tlv.AsByte(qrtr ? 2 : 1);
            _cidOwners[(service, cid)] = client;
        }
        else if (response.MessageId is 0x0023 or 0xFF23)
        {
            var tlv = response.GetTlv(0x01);
            if (tlv is null) return;
            bool qrtr = response.MessageId == 0xFF23;
            if (tlv.Value.Length < (qrtr ? 3 : 2)) return;
            ushort service = qrtr ? tlv.AsUInt16() : tlv.AsByte(0);
            byte cid = tlv.AsByte(qrtr ? 2 : 1);
            _cidOwners.TryRemove((service, cid), out _);
        }
    }

    private static bool TryTakeFrame(List<byte> accumulator, out byte[] frame)
    {
        frame = Array.Empty<byte>();
        if (accumulator.Count < 3) return false;
        byte[] snapshot = accumulator.ToArray();
        if (!QmiPacket.TryFindFrame(snapshot, out int start, out int length))
        {
            if (start > 0) accumulator.RemoveRange(0, Math.Min(start, accumulator.Count));
            return false;
        }
        if (start > 0) accumulator.RemoveRange(0, start);
        frame = accumulator.GetRange(0, length).ToArray();
        accumulator.RemoveRange(0, length);
        return true;
    }

    private static async Task WriteClientAsync(ClientState client, byte[] bytes, CancellationToken cancellationToken)
    {
        await client.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.Socket.GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await client.Socket.GetStream().FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (SocketException) { }
        finally { client.WriteGate.Release(); }
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _listener?.Stop();
        _listener = null;
        foreach (var client in _clients.Values) client.Socket.Dispose();
        if (_acceptLoopTask is not null) try { await _acceptLoopTask.ConfigureAwait(false); } catch { }
        if (_modemReadLoopTask is not null) try { await _modemReadLoopTask.ConfigureAwait(false); } catch { }
        _clients.Clear();
        _routes.Clear();
        _cidOwners.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _modemWriteGate.Dispose();
    }
}
