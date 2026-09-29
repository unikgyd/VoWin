using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public enum WmsMessageProtocol : byte
{
    Cdma = 0x00,
    WcdmaGsm = 0x01
}

public enum WmsMessageFormat : byte
{
    Cdma = 0x00,
    GsmWcdma = 0x06
}

public readonly record struct WmsMessageSummary(
    uint MemoryIndex,
    byte Tag);

public readonly record struct WmsIncomingMessage(
    byte Format,
    byte[] RawData);

public readonly record struct WmsRoute(
    byte MessageType,
    byte MessageClass,
    byte StorageType,
    byte ReceiptAction);

public sealed class WmsService : IAsyncDisposable
{
    private const ushort WmsMsgSetEventReport = 0x0001;
    private const ushort WmsMsgEventReportInd = 0x0001;
    private const ushort WmsMsgRawSend = 0x0020;
    private const ushort WmsMsgRawWrite = 0x0021;
    private const ushort WmsMsgRawRead = 0x0022;
    private const ushort WmsMsgModifyTag = 0x0023;
    private const ushort WmsMsgDeleteMessage = 0x0024;
    private const ushort WmsMsgGetMessageProtocol = 0x0030;
    private const ushort WmsMsgListMessages = 0x0031;
    private const ushort WmsMsgSetRoutes = 0x0032;
    private const ushort WmsMsgGetRoutes = 0x0033;
    private const ushort WmsMsgSendFromStorage = 0x0042;

    private readonly QmiClient _client;
    private byte _clientId;

    public event Action<WmsIncomingMessage>? MessageReceived;

    public WmsService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.IndicationReceived += OnIndicationReceived;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.WMS, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enables or disables incoming MT message event reporting.
    /// </summary>
    public async Task SetEventReportAsync(bool enableNewMessageReporting = true, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x10: New MT Message Indicator (1 byte: 1 = enable, 0 = disable)
        var reqTlv = new TlvBuilder()
            .AddByte(0x10, enableNewMessageReporting ? (byte)1 : (byte)0)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgSetEventReport, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Gets the current SMS protocol (CDMA or WCDMA/GSM).
    /// </summary>
    public async Task<WmsMessageProtocol> GetMessageProtocolAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgGetMessageProtocol, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01) ?? throw new QmiException("GetMessageProtocol missing TLV 0x01");
        return (WmsMessageProtocol)tlv.AsByte();
    }

    /// <summary>
    /// Lists stored messages in SIM or NV storage.
    /// </summary>
    public async Task<List<WmsMessageSummary>> ListMessagesAsync(byte storageType = 1, byte tagType = 0, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x01: Storage Type (1 byte, 0 = UIM/SIM, 1 = NV)
        // TLV 0x10: Message Mode (1 byte, 0 = CDMA, 1 = GW)
        var reqTlv = new TlvBuilder()
            .AddByte(0x01, storageType)
            .AddByte(0x10, 1) // GW/GSM mode
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgListMessages, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var list = new List<WmsMessageSummary>();
        var tlv = resp.GetTlv(0x01);
        if (tlv != null && tlv.Value.Length >= 4)
        {
            var span = tlv.Value.Span;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4));
            int offset = 4;
            for (int i = 0; i < count && offset + 5 <= span.Length; i++)
            {
                uint idx = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                byte tag = span[offset + 4];
                list.Add(new WmsMessageSummary(idx, tag));
                offset += 5;
            }
        }

        return list;
    }

    /// <summary>
    /// Reads a stored message by memory index.
    /// </summary>
    public async Task<byte[]> ReadMessageAsync(uint memoryIndex, byte storageType = 1, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] payload = new byte[5];
        payload[0] = storageType;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), memoryIndex);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, payload)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgRawRead, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return tlv != null ? tlv.Value.ToArray() : Array.Empty<byte>();
    }

    /// <summary>
    /// Deletes a stored message by memory index.
    /// </summary>
    public async Task DeleteMessageAsync(uint memoryIndex, byte storageType = 1, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] payload = new byte[5];
        payload[0] = storageType;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), memoryIndex);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, payload)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgDeleteMessage, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Sends a raw PDU message (e.g. 3GPP GSM/WCDMA PDU or 3GPP2 CDMA PDU) via QMI WMS.
    /// </summary>
    /// <param name="pdu">Raw PDU payload bytes.</param>
    /// <param name="format">PDU format (default: GsmWcdma 0x06).</param>
    /// <param name="smsOnIms">Whether to send SMS over IMS (VoLTE).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Assigned QMI Message ID from modem.</returns>
    public async Task<ushort> SendRawMessageAsync(
        byte[] pdu,
        WmsMessageFormat format = WmsMessageFormat.GsmWcdma,
        bool smsOnIms = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x01: Raw Message Data
        // Format (1 byte) + Length (uint16, 2 bytes) + PDU bytes
        byte[] buf = new byte[3 + pdu.Length];
        buf[0] = (byte)format;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1, 2), (ushort)pdu.Length);
        pdu.CopyTo(buf.AsSpan(3));

        var builder = new TlvBuilder().AddBytes(0x01, buf);
        if (smsOnIms)
        {
            // TLV 0x13: SMS on IMS (1 byte boolean)
            builder.AddByte(0x13, 1);
        }

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgRawSend, builder.Build(), cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        // TLV 0x01: Message ID (uint16)
        var tlv = resp.GetTlv(0x01);
        return tlv != null && tlv.Value.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span)
            : (ushort)0;
    }

    /// <summary>
    /// Writes a raw PDU message into modem storage (SIM or NV).
    /// </summary>
    public async Task<uint> RawWriteMessageAsync(
        byte storageType, // 0 = UIM/SIM, 1 = NV
        byte[] pdu,
        WmsMessageFormat format = WmsMessageFormat.GsmWcdma,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x01: Storage Type (1) + Format (1) + Length (2) + Data
        byte[] buf = new byte[4 + pdu.Length];
        buf[0] = storageType;
        buf[1] = (byte)format;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), (ushort)pdu.Length);
        pdu.CopyTo(buf.AsSpan(4));

        var reqTlv = new TlvBuilder().AddBytes(0x01, buf).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgRawWrite, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return tlv != null && tlv.Value.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span)
            : 0;
    }

    /// <summary>
    /// Sends a message from storage index.
    /// </summary>
    public async Task<ushort> SendFromStorageAsync(
        uint memoryIndex,
        byte storageType = 1,
        bool smsOnIms = false,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x01: Storage Type (1) + Memory Index (4) + Mode (1)
        byte[] buf = new byte[6];
        buf[0] = storageType;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), memoryIndex);
        buf[5] = 1; // GW mode

        var builder = new TlvBuilder().AddBytes(0x01, buf);
        if (smsOnIms)
        {
            builder.AddByte(0x10, 1);
        }

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgSendFromStorage, builder.Build(), cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x10) ?? resp.GetTlv(0x01);
        return tlv != null && tlv.Value.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span)
            : (ushort)0;
    }

    /// <summary>
    /// Gets active SMS routing configuration from the modem.
    /// </summary>
    public async Task<List<WmsRoute>> GetRoutesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, WmsMsgGetRoutes, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var list = new List<WmsRoute>();
        var tlv = resp.GetTlv(0x01);
        if (tlv != null && tlv.Value.Length >= 2)
        {
            var span = tlv.Value.Span;
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2));
            int offset = 2;
            for (int i = 0; i < count && offset + 4 <= span.Length; i++)
            {
                byte msgType = span[offset];
                byte msgClass = span[offset + 1];
                byte storage = span[offset + 2];
                byte action = span[offset + 3];
                list.Add(new WmsRoute(msgType, msgClass, storage, action));
                offset += 4;
            }
        }
        return list;
    }

    /// <summary>
    /// Gets the current SMSC (Short Message Service Center) address (0x0034).
    /// </summary>
    public async Task<string> GetSmscAddressAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, 0x0034, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        if (tlv != null && tlv.Value.Length > 1)
        {
            byte len = tlv.AsByte(0);
            if (tlv.Value.Length >= 1 + len)
            {
                return Encoding.ASCII.GetString(tlv.Value.Span.Slice(1, len));
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Sets the SMSC address (0x0035).
    /// </summary>
    public async Task SetSmscAddressAsync(string smscAddress, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] addrBytes = Encoding.ASCII.GetBytes(smscAddress);
        var payload = new byte[1 + addrBytes.Length];
        payload[0] = (byte)addrBytes.Length;
        addrBytes.CopyTo(payload.AsSpan(1));

        var reqTlv = new TlvBuilder().AddBytes(0x01, payload).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, 0x0035, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Resets WMS service state (0x0000).
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.WMS, 0x0000, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Convenience helper to encode a text message and recipient phone number into a standard 3GPP SMS-SUBMIT PDU (UCS2 encoding).
    /// </summary>
    public static byte[] Create3GppSubmitPdu(string destinationNumber, string messageText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationNumber);
        ArgumentNullException.ThrowIfNull(messageText);

        string cleanNumber = destinationNumber.Trim().TrimStart('+');
        bool isInternational = destinationNumber.Trim().StartsWith('+');

        // BCD encode destination address
        int numDigits = cleanNumber.Length;
        int bcdLen = (numDigits + 1) / 2;
        byte[] bcdDigits = new byte[bcdLen];
        for (int i = 0; i < bcdLen; i++)
        {
            int d1 = cleanNumber[i * 2] - '0';
            int d2 = (i * 2 + 1 < numDigits) ? (cleanNumber[i * 2 + 1] - '0') : 0x0F;
            bcdDigits[i] = (byte)((d2 << 4) | (d1 & 0x0F));
        }

        // Encode user data as UCS-2 (Big Endian)
        byte[] udBytes = Encoding.BigEndianUnicode.GetBytes(messageText);

        using var ms = new MemoryStream();
        ms.WriteByte(0x00); // SMSC Info Length: 0x00 = Use modem default SMSC
        ms.WriteByte(0x01); // First octet of SMS-SUBMIT (TP-MTI=01, TP-RD=0, TP-VPF=00, TP-RP=0, TP-UDHI=0, TP-SRR=0)
        ms.WriteByte(0x00); // TP-MR (Message Reference)
        ms.WriteByte((byte)numDigits); // TP-DA Length (Number of digits)
        ms.WriteByte(isInternational ? (byte)0x91 : (byte)0x81); // Type of Address (0x91=International, 0x81=National)
        ms.Write(bcdDigits, 0, bcdDigits.Length); // Destination digits
        ms.WriteByte(0x00); // TP-PID (Protocol Identifier: Standard SMS)
        ms.WriteByte(0x08); // TP-DCS (Data Coding Scheme: 0x08 = UCS2)
        ms.WriteByte((byte)udBytes.Length); // TP-UDL (User Data Length in bytes for UCS2)
        ms.Write(udBytes, 0, udBytes.Length); // TP-UD (User Data)

        return ms.ToArray();
    }

    private void OnIndicationReceived(QmiPacket packet)
    {
        if (packet.KnownServiceType != QmiServiceType.WMS) return;

        if (packet.MessageId == WmsMsgEventReportInd)
        {
            // TLV 0x11: Transfer Route MT Message (Format uint8, RawData bytes)
            var tlv = packet.GetTlv(0x11);
            if (tlv != null && tlv.Value.Length >= 5)
            {
                byte format = tlv.AsByte(2);
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span.Slice(3, 2));
                byte[] raw = tlv.Value.Slice(5).ToArray();
                MessageReceived?.Invoke(new WmsIncomingMessage(format, raw));
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
                await _client.ReleaseClientIdAsync(QmiServiceType.WMS, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
