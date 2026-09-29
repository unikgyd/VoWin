using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;

namespace qmiSharp.Services;

public enum VoiceCallState : byte
{
    Unknown = 0,
    Origination = 1,
    Incoming = 2,
    Conversing = 3,
    CCInProgress = 4,
    Alerting = 5,
    Hold = 6,
    Waiting = 7,
    Disconnecting = 8,
    End = 9,
    Setup = 10
}

public readonly record struct VoiceCallInfo(
    byte CallId,
    VoiceCallState State,
    byte Type,
    byte Direction,
    byte Mode);

public sealed class VoiceService : IAsyncDisposable
{
    public const string AllowedTestNumber = "17387799413";

    private readonly QmiClient _client;
    private byte _clientId;

    public event Action<VoiceCallInfo>? CallStatusChanged;

    public VoiceService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.IndicationReceived += OnIndicationReceived;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.VOICE, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Initiates a voice call.
    /// Strictly constrained to only allow dialing the authorized test number (17387799413).
    /// </summary>
    public async Task<byte> DialCallAsync(string callingNumber, CancellationToken cancellationToken = default)
    {
        if (callingNumber != AllowedTestNumber)
        {
            throw new InvalidOperationException(
                $"Safety policy violation: Dialing '{callingNumber}' is strictly prohibited. Only '{AllowedTestNumber}' is authorized.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // VOICE DIAL_CALL (0x0020)
        var req = new QmiPacket(QmiServiceType.VOICE, _clientId, 0, (ushort)VoiceMessageId.DialCall, new[]
        {
            QmiTlv.FromString(0x01, callingNumber)
        });

        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var callIdTlv = resp.GetTlv(0x10) ?? throw new QmiException("DialCall response missing CallId TLV 0x10");
        return callIdTlv.AsByte(0);
    }

    /// <summary>
    /// Ends an active or pending call (0x0021).
    /// </summary>
    public async Task<byte> EndCallAsync(byte callId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var req = new QmiPacket(QmiServiceType.VOICE, _clientId, 0, (ushort)VoiceMessageId.EndCall, new[]
        {
            QmiTlv.FromByte(0x01, callId)
        });

        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var callIdTlv = resp.GetTlv(0x10);
        return callIdTlv?.AsByte(0) ?? callId;
    }

    /// <summary>
    /// Answers an incoming call (0x0022).
    /// </summary>
    public async Task<byte> AnswerCallAsync(byte callId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var req = new QmiPacket(QmiServiceType.VOICE, _clientId, 0, (ushort)VoiceMessageId.AnswerCall, new[]
        {
            QmiTlv.FromByte(0x01, callId)
        });

        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var callIdTlv = resp.GetTlv(0x10);
        return callIdTlv?.AsByte(0) ?? callId;
    }

    /// <summary>
    /// Manages active/held calls (Hold, Resume, Multiparty conference) (0x0031).
    /// </summary>
    public async Task ManageCallsAsync(byte serviceType, byte? callId = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var builder = new TlvBuilder().AddByte(0x01, serviceType);
        if (callId.HasValue)
        {
            builder.AddByte(0x10, callId.Value);
        }

        var resp = await _client.SendMessageAsync(QmiServiceType.VOICE, (ushort)VoiceMessageId.ManageCalls, builder.Build(), cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Sends a DTMF burst during active voice call (0x0028).
    /// </summary>
    public async Task<byte> BurstDtmfAsync(byte callId, string digits, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] payload = new byte[2 + digits.Length];
        payload[0] = callId;
        payload[1] = (byte)digits.Length;
        Encoding.ASCII.GetBytes(digits).CopyTo(payload.AsSpan(2));

        var req = new QmiPacket(QmiServiceType.VOICE, _clientId, 0, (ushort)VoiceMessageId.BurstDtmf, new[]
        {
            new QmiTlv(0x01, payload)
        });

        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var callIdTlv = resp.GetTlv(0x10);
        return callIdTlv?.AsByte(0) ?? callId;
    }

    /// <summary>
    /// Starts continuous DTMF tone (0x0029).
    /// </summary>
    public async Task StartContinuousDtmfAsync(byte callId, char digit, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder().AddBytes(0x01, new[] { callId, (byte)digit }).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.VOICE, (ushort)VoiceMessageId.StartContinuousDtmf, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Stops continuous DTMF tone (0x002A).
    /// </summary>
    public async Task StopContinuousDtmfAsync(byte callId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder().AddByte(0x01, callId).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.VOICE, (ushort)VoiceMessageId.StopContinuousDtmf, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Originates a USSD session (e.g. *100#, *#06#) (0x003A).
    /// </summary>
    public async Task<string> OriginateUssdAsync(string ussdCode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] bytes = Encoding.UTF8.GetBytes(ussdCode);
        byte[] payload = new byte[2 + bytes.Length];
        payload[0] = 0x01; // UTF-8 encoding
        payload[1] = (byte)bytes.Length;
        bytes.CopyTo(payload.AsSpan(2));

        var reqTlv = new TlvBuilder().AddBytes(0x01, payload).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.VOICE, (ushort)VoiceMessageId.OriginateUssd, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var respTlv = resp.GetTlv(0x10);
        if (respTlv != null && respTlv.Value.Length >= 2)
        {
            byte len = respTlv.AsByte(1);
            if (respTlv.Value.Length >= 2 + len)
            {
                return Encoding.UTF8.GetString(respTlv.Value.Span.Slice(2, len));
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Answers an active USSD request (0x003B).
    /// </summary>
    public async Task AnswerUssdAsync(string ussdResponse, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] bytes = Encoding.UTF8.GetBytes(ussdResponse);
        byte[] payload = new byte[2 + bytes.Length];
        payload[0] = 0x01;
        payload[1] = (byte)bytes.Length;
        bytes.CopyTo(payload.AsSpan(2));

        var reqTlv = new TlvBuilder().AddBytes(0x01, payload).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.VOICE, (ushort)VoiceMessageId.AnswerUssd, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Cancels an active USSD session (0x003C).
    /// </summary>
    public async Task CancelUssdAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.VOICE, (ushort)VoiceMessageId.CancelUssd, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Gets call waiting configuration (0x0034).
    /// </summary>
    public async Task<bool> GetCallWaitingAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var request = new[] { QmiTlv.FromByte(0x10, 0x01) }; // voice service class
        var resp = await _client.SendMessageAsync(QmiServiceType.VOICE, (ushort)VoiceMessageId.GetCallWaiting, request, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x10);
        return tlv != null && tlv.Value.Length >= 1 && (tlv.AsByte(0) & 0x01) != 0;
    }

    /// <summary>
    /// Retrieves all current active calls (0x0024).
    /// </summary>
    public async Task<List<VoiceCallInfo>> GetAllCallInfoAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var req = new QmiPacket(QmiServiceType.VOICE, _clientId, 0, (ushort)VoiceMessageId.GetAllCallInfo);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var list = new List<VoiceCallInfo>();
        var tlv = resp.GetTlv(0x10);
        if (tlv == null || tlv.Value.Length < 1) return list;

        byte count = tlv.AsByte(0);
        const int entrySize = 7;
        for (int i = 0; i < count; i++)
        {
            int off = 1 + i * entrySize;
            if (tlv.Value.Length >= off + entrySize)
            {
                byte id = tlv.AsByte(off);
                byte state = tlv.AsByte(off + 1);
                byte type = tlv.AsByte(off + 2);
                byte dir = tlv.AsByte(off + 3);
                byte mode = tlv.AsByte(off + 4);
                list.Add(new VoiceCallInfo(id, (VoiceCallState)state, type, dir, mode));
            }
        }
        return list;
    }

    private void OnIndicationReceived(QmiPacket ind)
    {
        if (ind.KnownServiceType != QmiServiceType.VOICE) return;

        // ALL_CALL_STATUS_IND (0x002E)
        if (ind.MessageId == 0x002E)
        {
            var tlv = ind.GetTlv(0x01);
            if (tlv != null && tlv.Value.Length >= 1)
            {
                byte count = tlv.AsByte(0);
                const int entrySize = 7;
                for (int i = 0; i < count; i++)
                {
                    int offset = 1 + i * entrySize;
                    if (tlv.Value.Length < offset + entrySize) break;
                    CallStatusChanged?.Invoke(new VoiceCallInfo(
                        tlv.AsByte(offset),
                        (VoiceCallState)tlv.AsByte(offset + 1),
                        tlv.AsByte(offset + 2),
                        tlv.AsByte(offset + 3),
                        tlv.AsByte(offset + 4)));
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.IndicationReceived -= OnIndicationReceived;
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.VOICE, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
