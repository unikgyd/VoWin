using System.Security.Cryptography;

namespace VoSharp.Ike;

/// <summary>
/// Sends authenticated empty IKE INFORMATIONAL exchanges (RFC 7296 §1.4) after
/// IKE_AUTH has completed.  Unlike a NAT-T 0xFF packet, a response proves the
/// remote IKE SA and its cryptographic state still exist.
/// </summary>
public sealed class IkeLivenessProbe : IDisposable
{
    private readonly IkeTransport _transport;
    private readonly IkeSuite _suite;
    private readonly ulong _initiatorSpi;
    private readonly ulong _responderSpi;
    private readonly byte[] _skEi;
    private readonly byte[] _skAi;
    private readonly byte[] _skEr;
    private readonly byte[] _skAr;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private uint _nextMessageId;
    private bool _disposed;

    internal IkeLivenessProbe(
        IkeTransport transport,
        IkeSuite suite,
        ulong initiatorSpi,
        ulong responderSpi,
        uint lastMessageId,
        byte[] skEi,
        byte[] skAi,
        byte[] skEr,
        byte[] skAr)
    {
        _transport = transport;
        _suite = suite;
        _initiatorSpi = initiatorSpi;
        _responderSpi = responderSpi;
        _nextMessageId = checked(lastMessageId + 1);
        _skEi = skEi.ToArray();
        _skAi = skAi.ToArray();
        _skEr = skEr.ToArray();
        _skAr = skAr.ToArray();
    }

    public async Task ProbeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var messageId = _nextMessageId;
            if (messageId == uint.MaxValue)
                throw new InvalidOperationException("IKE message ID exhausted; establish a fresh SA.");
            _nextMessageId++;

            var request = new IkeWire.IkeMessage
            {
                InitiatorSpi = _initiatorSpi,
                ResponderSpi = _responderSpi,
                Exchange = IkeExchangeType.Informational,
                Flags = IkeFlags.Initiator,
                MessageId = messageId
            };
            var encrypted = IkeCrypto.EncryptPayloads(request, Array.Empty<IkePayload>(), _suite, _skEi, _skAi);
            var response = await _transport.RoundTripAsync(encrypted, ct).ConfigureAwait(false);
            var (header, payloads) = IkeCrypto.DecryptPayloads(response, _suite, _skEr, _skAr);

            if (!header.Flags.HasFlag(IkeFlags.Response) ||
                header.Exchange != IkeExchangeType.Informational ||
                header.MessageId != messageId ||
                header.InitiatorSpi != _initiatorSpi ||
                header.ResponderSpi != _responderSpi)
            {
                throw new IkeFormatException("IKE DPD response did not match the active security association.");
            }

            var notify = payloads.FirstOrDefault(payload => payload.Type == IkePayloadType.Notify && payload.Body.Length >= 4);
            if (notify != null)
                throw new IkeFormatException("ePDG rejected the IKE DPD INFORMATIONAL exchange.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_skEi);
        CryptographicOperations.ZeroMemory(_skAi);
        CryptographicOperations.ZeroMemory(_skEr);
        CryptographicOperations.ZeroMemory(_skAr);
        _gate.Dispose();
    }
}
