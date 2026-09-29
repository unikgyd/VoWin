using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VoSharp.Ike;

/// <summary>
/// Owns an established IKE SA after IKE_AUTH. It serializes local exchanges,
/// tracks the independent peer Message ID space, answers peer DPD requests and
/// caches the last response for retransmission as required by RFC 7296 §2.1.
/// </summary>
public sealed class IkeSessionController : IDisposable
{
    private const ushort NotifyTemporaryFailure = 43;

    private readonly IkeTransport _transport;
    private readonly IkeSuite _suite;
    private readonly ulong _initiatorSpi;
    private readonly ulong _responderSpi;
    private readonly uint _childInboundSpi;
    private readonly byte[] _skEi;
    private readonly byte[] _skAi;
    private readonly byte[] _skEr;
    private readonly byte[] _skAr;
    private readonly SemaphoreSlim _localGate = new(1, 1);
    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private readonly Action<string>? _diagnosticLog;

    private uint _nextLocalMessageId;
    private uint _nextRemoteMessageId;
    private uint? _lastRemoteMessageId;
    private IkeExchangeType? _lastRemoteExchange;
    private byte[]? _lastRemoteRequest;
    private byte[]? _lastRemoteResponse;
    private bool _peerDeleted;
    private bool _childSaDeleted;
    private bool _disposed;

    internal IkeSessionController(
        IkeTransport transport,
        IkeSuite suite,
        ulong initiatorSpi,
        ulong responderSpi,
        uint lastLocalMessageId,
        uint childInboundSpi,
        byte[] skEi,
        byte[] skAi,
        byte[] skEr,
        byte[] skAr,
        Action<string>? diagnosticLog = null)
    {
        _transport = transport;
        _suite = suite;
        _initiatorSpi = initiatorSpi;
        _responderSpi = responderSpi;
        _childInboundSpi = childInboundSpi;
        _nextLocalMessageId = checked(lastLocalMessageId + 1);
        _skEi = skEi.ToArray();
        _skAi = skAi.ToArray();
        _skEr = skEr.ToArray();
        _skAr = skAr.ToArray();
        _diagnosticLog = diagnosticLog;
        _transport.SetIncomingIkeRequestHandler(HandleIncomingRequestAsync);
    }

    public bool IsPeerDeleted => Volatile.Read(ref _peerDeleted);
    public bool IsChildSaDeleted => Volatile.Read(ref _childSaDeleted);
    public DateTimeOffset LastPeerActivity { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Sends an authenticated empty INFORMATIONAL request (DPD).</summary>
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        if (IsChildSaDeleted)
            throw new InvalidOperationException("The peer has deleted the IMS CHILD_SA.");
        var payloads = await SendLocalInformationalAsync(Array.Empty<IkePayload>(), ct).ConfigureAwait(false);
        if (IsChildSaDeleted)
            throw new InvalidOperationException("The peer has deleted the IMS CHILD_SA.");
        var error = payloads.FirstOrDefault(payload =>
            payload.Type == IkePayloadType.Notify &&
            payload.Body.Length >= 4 &&
            BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2)) < 16384);
        if (error != null)
            throw new IkeFormatException("ePDG rejected the IKE DPD INFORMATIONAL exchange.");
    }

    /// <summary>
    /// Best-effort graceful teardown: delete the CHILD_SA first and then the
    /// IKE SA while the transport and keys are still alive.
    /// </summary>
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        if (_disposed || IsPeerDeleted) return;

        if (_childInboundSpi != 0)
        {
            await SendLocalInformationalAsync([MakeEspDelete(_childInboundSpi)], ct)
                .ConfigureAwait(false);
        }

        var ikeDelete = new byte[4];
        ikeDelete[0] = 1; // IKE
        await SendLocalInformationalAsync([new IkePayload(IkePayloadType.Delete, ikeDelete)], ct)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IkePayload>> SendLocalInformationalAsync(
        IReadOnlyList<IkePayload> payloads,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPeerDeleted)
            throw new InvalidOperationException("The peer has deleted the IKE security association.");

        await _localGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var messageId = _nextLocalMessageId;
            if (messageId == uint.MaxValue)
                throw new InvalidOperationException("IKE message ID exhausted; establish a fresh SA.");
            _nextLocalMessageId++;

            var request = CreateHeader(IkeExchangeType.Informational, IkeFlags.Initiator, messageId);
            var encrypted = IkeCrypto.EncryptPayloads(request, payloads, _suite, _skEi, _skAi);
            var response = await _transport.RoundTripAsync(encrypted, ct).ConfigureAwait(false);
            var (header, responsePayloads) = IkeCrypto.DecryptPayloads(response, _suite, _skEr, _skAr);
            ValidateHeader(header, IkeExchangeType.Informational, messageId, requireResponse: true);
            return responsePayloads;
        }
        finally
        {
            _localGate.Release();
        }
    }

    private async Task HandleIncomingRequestAsync(byte[] packet)
    {
        if (_disposed) return;
        await _remoteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            var (header, payloads) = IkeCrypto.DecryptPayloads(packet, _suite, _skEr, _skAr);
            ValidateHeader(header, header.Exchange, header.MessageId, requireResponse: false);
            LastPeerActivity = DateTimeOffset.UtcNow;

            if (_lastRemoteMessageId == header.MessageId &&
                _lastRemoteExchange == header.Exchange &&
                _lastRemoteRequest != null &&
                _lastRemoteResponse != null &&
                packet.AsSpan().SequenceEqual(_lastRemoteRequest))
            {
                await _transport.SendIkeAsync(_lastRemoteResponse).ConfigureAwait(false);
                Log($"Replayed cached peer response; exchange={header.Exchange}; message-id={header.MessageId}.");
                return;
            }

            if (header.MessageId != _nextRemoteMessageId)
            {
                Log($"Ignored out-of-window peer request; exchange={header.Exchange}; message-id={header.MessageId}; expected={_nextRemoteMessageId}.");
                return;
            }

            IReadOnlyList<IkePayload> responsePayloads = Array.Empty<IkePayload>();
            var hasIkeDelete = false;
            var hasChildDelete = false;
            if (header.Exchange == IkeExchangeType.Informational)
            {
                hasIkeDelete = payloads.Any(IsIkeDelete);
                hasChildDelete = !hasIkeDelete && payloads.Any(IsEspDelete);
                if (hasChildDelete && _childInboundSpi != 0)
                    responsePayloads = [MakeEspDelete(_childInboundSpi)];
            }
            else if (header.Exchange == IkeExchangeType.CreateChildSa)
            {
                // Online rekey is deliberately a separate phase. Return an
                // authenticated transient error instead of dropping the request.
                responsePayloads = [MakeNotify(NotifyTemporaryFailure)];
                Log("Peer requested CREATE_CHILD_SA; replied TEMPORARY_FAILURE until online rekey is available.");
            }
            else
            {
                responsePayloads = [MakeNotify(NotifyTemporaryFailure)];
            }

            var responseHeader = CreateHeader(
                header.Exchange,
                IkeFlags.Initiator | IkeFlags.Response,
                header.MessageId);
            var response = IkeCrypto.EncryptPayloads(responseHeader, responsePayloads, _suite, _skEi, _skAi);
            await _transport.SendIkeAsync(response).ConfigureAwait(false);

            _lastRemoteMessageId = header.MessageId;
            _lastRemoteExchange = header.Exchange;
            _lastRemoteRequest = packet;
            _lastRemoteResponse = response;
            if (_nextRemoteMessageId == uint.MaxValue)
                throw new InvalidOperationException("Peer IKE message ID exhausted; establish a fresh SA.");
            _nextRemoteMessageId++;
            if (hasIkeDelete)
            {
                Volatile.Write(ref _peerDeleted, true);
                Log("Peer deleted the IKE security association.");
            }
            else if (hasChildDelete)
            {
                Volatile.Write(ref _childSaDeleted, true);
                Log("Peer deleted the IMS CHILD_SA; IKE DPD alone no longer proves data-plane health.");
            }
            else if (header.Exchange == IkeExchangeType.Informational && payloads.Count == 0)
            {
                Log($"Answered peer DPD; message-id={header.MessageId}.");
            }
        }
        finally
        {
            _remoteGate.Release();
        }
    }

    private IkeWire.IkeMessage CreateHeader(IkeExchangeType exchange, IkeFlags flags, uint messageId) => new()
    {
        InitiatorSpi = _initiatorSpi,
        ResponderSpi = _responderSpi,
        Exchange = exchange,
        Flags = flags,
        MessageId = messageId
    };

    private void ValidateHeader(
        IkeWire.IkeMessage header,
        IkeExchangeType exchange,
        uint messageId,
        bool requireResponse)
    {
        if ((header.Version >> 4) != 2 ||
            header.InitiatorSpi != _initiatorSpi ||
            header.ResponderSpi != _responderSpi ||
            header.Exchange != exchange ||
            header.MessageId != messageId ||
            header.Flags.HasFlag(IkeFlags.Initiator) ||
            header.Flags.HasFlag(IkeFlags.Response) != requireResponse)
        {
            throw new IkeFormatException("IKE packet did not match the active security association.");
        }
    }

    private static bool IsIkeDelete(IkePayload payload) =>
        payload.Type == IkePayloadType.Delete && payload.Body.Length >= 4 && payload.Body[0] == 1;

    private static bool IsEspDelete(IkePayload payload) =>
        payload.Type == IkePayloadType.Delete && payload.Body.Length >= 8 &&
        payload.Body[0] == 3 && payload.Body[1] == 4 &&
        BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2)) > 0 &&
        payload.Body.Length == 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2));

    private static IkePayload MakeEspDelete(uint spi)
    {
        var body = new byte[8];
        body[0] = 3;
        body[1] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), spi);
        return new IkePayload(IkePayloadType.Delete, body);
    }

    private static IkePayload MakeNotify(ushort type)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), type);
        return new IkePayload(IkePayloadType.Notify, body);
    }

    private void Log(string message)
    {
        try { _diagnosticLog?.Invoke(message); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.SetIncomingIkeRequestHandler(null);
        CryptographicOperations.ZeroMemory(_skEi);
        CryptographicOperations.ZeroMemory(_skAi);
        CryptographicOperations.ZeroMemory(_skEr);
        CryptographicOperations.ZeroMemory(_skAr);
        _lastRemoteRequest = null;
        _lastRemoteResponse = null;
    }
}
