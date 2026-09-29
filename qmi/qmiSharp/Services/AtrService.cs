using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

/// <summary>
/// AT Relay Service (ATR, 0xED).
/// Allows tunneling Hayes AT command strings and receiving responses directly over QMI packets.
/// </summary>
public sealed class AtrService : IAsyncDisposable
{
    private const ushort AtrMsgSend = 0x0000;
    private const ushort AtrMsgReceivedInd = 0x0001;

    private readonly QmiClient _client;
    private byte _clientId;

    public event Action<string>? ResponseReceived;

    public AtrService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.IndicationReceived += OnIndicationReceived;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.ATR, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends an AT command string tunnelled through QMI ATR service.
    /// </summary>
    public async Task SendAtCommandAsync(string atCommand, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(atCommand);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // String with 2-byte length prefix
        byte[] strBytes = Encoding.ASCII.GetBytes(atCommand);
        byte[] buf = new byte[2 + strBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), (ushort)strBytes.Length);
        strBytes.CopyTo(buf.AsSpan(2));

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, buf)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.ATR, AtrMsgSend, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    private void OnIndicationReceived(QmiPacket packet)
    {
        if (packet.KnownServiceType != QmiServiceType.ATR) return;

        if (packet.MessageId == AtrMsgReceivedInd)
        {
            var tlv = packet.GetTlv(0x01);
            if (tlv != null && tlv.Value.Length >= 2)
            {
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span.Slice(0, 2));
                if (tlv.Value.Length >= 2 + len)
                {
                    string text = Encoding.ASCII.GetString(tlv.Value.Span.Slice(2, len));
                    ResponseReceived?.Invoke(text);
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
                await _client.ReleaseClientIdAsync(QmiServiceType.ATR, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
