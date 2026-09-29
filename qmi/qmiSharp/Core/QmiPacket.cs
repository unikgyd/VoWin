using System.Buffers.Binary;

namespace qmiSharp.Core;

/// <summary>
/// Unified representation of a QMI message (Request, Response, or Indication).
/// </summary>
public sealed class QmiPacket
{
    public ushort ServiceType { get; set; }
    public byte ClientId { get; set; }
    public ushort TransactionId { get; set; }
    public ushort MessageId { get; set; }
    public bool IsIndication { get; set; }
    public bool IsResponse { get; set; }
    public List<QmiTlv> TLVs { get; } = new();

    public QmiServiceType KnownServiceType => (QmiServiceType)ServiceType;

    public QmiPacket()
    {
    }

    public QmiPacket(ushort serviceType, byte clientId, ushort transactionId, ushort messageId, IEnumerable<QmiTlv>? tlvs = null)
    {
        ServiceType = serviceType;
        ClientId = clientId;
        TransactionId = transactionId;
        MessageId = messageId;
        if (tlvs != null)
        {
            TLVs.AddRange(tlvs);
        }
    }

    public QmiPacket(QmiServiceType serviceType, byte clientId, ushort transactionId, ushort messageId, IEnumerable<QmiTlv>? tlvs = null)
        : this((ushort)serviceType, clientId, transactionId, messageId, tlvs)
    {
    }

    public QmiTlv? GetTlv(byte type)
    {
        for (int i = 0; i < TLVs.Count; i++)
        {
            if (TLVs[i].Type == type) return TLVs[i];
        }
        return null;
    }

    public bool TryGetResult(out QmiResultCode result, out QmiErrorCode errorCode)
    {
        var tlv = GetTlv(0x02);
        if (tlv == null || tlv.Value.Length < 4)
        {
            result = QmiResultCode.Failure;
            errorCode = QmiErrorCode.Internal;
            return false;
        }

        result = (QmiResultCode)BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span[..2]);
        errorCode = (QmiErrorCode)BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span[2..4]);
        return true;
    }

    public void CheckResult()
    {
        if (!TryGetResult(out var result, out var error))
        {
            throw new QmiException($"QMI response for {KnownServiceType}/0x{MessageId:X4} is missing or has a truncated result TLV");
        }

        if (result != QmiResultCode.Success)
        {
            throw new QmiException(KnownServiceType, MessageId, result, error);
        }
    }

    public byte[] Marshal()
    {
        int tlvBytesLen = TLVs.Sum(t => t.TotalSize);
        if (tlvBytesLen > ushort.MaxValue)
            throw new InvalidOperationException("QMI TLV payload exceeds 65535 bytes");
        byte[] tlvBytes = new byte[tlvBytesLen];
        int tlvOffset = 0;
        foreach (var tlv in TLVs)
        {
            tlv.WriteTo(tlvBytes.AsSpan(tlvOffset));
            tlvOffset += tlv.TotalSize;
        }

        byte[] body;
        if (ServiceType == (ushort)QmiServiceType.Control)
        {
            // CTL uses 6-byte header
            byte flags = 0x00;
            if (IsIndication) flags |= CtlHeader.FlagIndication;
            else if (IsResponse) flags |= CtlHeader.FlagResponse;

            var ctlHeader = new CtlHeader(flags, (byte)(TransactionId & 0xFF), MessageId, (ushort)tlvBytesLen);
            body = new byte[CtlHeader.Size + tlvBytesLen];
            ctlHeader.WriteTo(body);
            tlvBytes.CopyTo(body, CtlHeader.Size);
        }
        else
        {
            // Regular service uses 7-byte header
            byte flags = 0x00;
            if (IsIndication) flags |= ServiceHeader.FlagIndication;
            else if (IsResponse) flags |= ServiceHeader.FlagResponse;

            var svcHeader = new ServiceHeader(flags, TransactionId, MessageId, (ushort)tlvBytesLen);
            body = new byte[ServiceHeader.Size + tlvBytesLen];
            svcHeader.WriteTo(body);
            tlvBytes.CopyTo(body, ServiceHeader.Size);
        }

        // Outer header
        if (ServiceType <= 0xFF)
        {
            // Standard 6-byte QMUX header
            if (body.Length > ushort.MaxValue - 5)
                throw new InvalidOperationException("QMUX frame exceeds its 16-bit length field");
            ushort qmuxLen = (ushort)(body.Length + 5); // +5 for Length(2), CtlFlags(1), SvcType(1), ClientID(1)
            byte qmuxFlags = IsResponse || IsIndication ? (byte)0x80 : (byte)0x00;
            var qmuxHeader = new QmuxHeader(QmuxHeader.ExpectedIFType, qmuxLen, qmuxFlags, (byte)ServiceType, ClientId);

            byte[] packet = new byte[QmuxHeader.Size + body.Length];
            qmuxHeader.WriteTo(packet);
            body.CopyTo(packet, QmuxHeader.Size);
            return packet;
        }
        else
        {
            // libqmi-compatible 6-byte virtual QRTR header
            if (body.Length > ushort.MaxValue - 5)
                throw new InvalidOperationException("QRTR virtual frame exceeds its 16-bit length field");
            ushort qrtrLen = (ushort)(body.Length + 5);
            var qrtrHeader = new QrtrVirtualHeader(QrtrVirtualHeader.ExpectedIFType, qrtrLen, ServiceType, ClientId);

            byte[] packet = new byte[QrtrVirtualHeader.Size + body.Length];
            qrtrHeader.WriteTo(packet);
            body.CopyTo(packet, QrtrVirtualHeader.Size);
            return packet;
        }
    }

    public static QmiPacket Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            throw new ArgumentException("Data too short for QMI packet", nameof(data));

        byte marker = data[0];
        int headerSize;
        ushort frameLenField;
        ushort serviceType;
        byte clientId;

        if (marker == QmuxHeader.ExpectedIFType)
        {
            if (data.Length < QmuxHeader.Size)
                throw new ArgumentException($"Data too short for QMUX header: {data.Length}", nameof(data));

            var qmux = QmuxHeader.Parse(data);
            headerSize = QmuxHeader.Size;
            frameLenField = qmux.Length;
            serviceType = qmux.ServiceType;
            clientId = qmux.ClientId;
        }
        else if (marker == QrtrVirtualHeader.ExpectedIFType)
        {
            if (data.Length < QrtrVirtualHeader.Size)
                throw new ArgumentException($"Data too short for QRTR header: {data.Length}", nameof(data));

            var qrtr = QrtrVirtualHeader.Parse(data);
            headerSize = QrtrVirtualHeader.Size;
            frameLenField = qrtr.Length;
            serviceType = qrtr.ServiceType;
            clientId = qrtr.ClientId;
        }
        else
        {
            throw new FormatException($"Unknown QMI packet marker: 0x{marker:X2}");
        }

        int expectedTotal = 1 + frameLenField;
        if (data.Length != expectedTotal)
            throw new ArgumentException($"QMI packet length mismatch: expected {expectedTotal}, have {data.Length}", nameof(data));

        var body = data[headerSize..expectedTotal];
        var packet = new QmiPacket
        {
            ServiceType = serviceType,
            ClientId = clientId
        };

        if (serviceType == (ushort)QmiServiceType.Control)
        {
            if (body.Length < CtlHeader.Size)
                throw new ArgumentException("Body too short for CTL header", nameof(data));

            var ctl = CtlHeader.Parse(body);
            packet.TransactionId = ctl.TransactionId;
            packet.MessageId = ctl.MessageId;
            packet.IsIndication = ctl.IsIndication;
            packet.IsResponse = ctl.IsResponse;

            var tlvData = body[CtlHeader.Size..];
            if (tlvData.Length != ctl.Length)
                throw new ArgumentException("CTL TLV payload length mismatch", nameof(data));

            packet.TLVs.AddRange(QmiTlv.ParseMultiple(tlvData[..ctl.Length]));
        }
        else
        {
            if (body.Length < ServiceHeader.Size)
                throw new ArgumentException("Body too short for Service header", nameof(data));

            var svc = ServiceHeader.Parse(body);
            packet.TransactionId = svc.TransactionId;
            packet.MessageId = svc.MessageId;
            packet.IsIndication = svc.IsIndication;
            packet.IsResponse = svc.IsResponse;

            var tlvData = body[ServiceHeader.Size..];
            if (tlvData.Length != svc.Length)
                throw new ArgumentException("Service TLV payload length mismatch", nameof(data));

            packet.TLVs.AddRange(QmiTlv.ParseMultiple(tlvData[..svc.Length]));
        }

        return packet;
    }

    /// <summary>
    /// Checks if a buffer contains a full QMI frame, finding the start index and frame length.
    /// </summary>
    public static bool TryFindFrame(ReadOnlySpan<byte> buffer, out int frameStart, out int frameLength)
    {
        frameStart = 0;
        frameLength = 0;

        for (int i = 0; i < buffer.Length; i++)
        {
            byte b = buffer[i];
            if (b == QmuxHeader.ExpectedIFType || b == QrtrVirtualHeader.ExpectedIFType)
            {
                if (buffer.Length - i < 3)
                {
                    // Need at least 3 bytes to read length field
                    frameStart = i;
                    return false;
                }

                ushort lengthField = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(i + 1, 2));
                int totalLen = 1 + lengthField;

                if (totalLen < QmuxHeader.Size)
                {
                    // Invalid length, skip this byte
                    continue;
                }

                if (buffer.Length - i >= totalLen)
                {
                    frameStart = i;
                    frameLength = totalLen;
                    return true;
                }

                // Incomplete frame
                frameStart = i;
                return false;
            }
        }

        frameStart = buffer.Length;
        return false;
    }

    public override string ToString() =>
        $"QMI(Service={KnownServiceType}, Client={ClientId}, TxID={TransactionId}, MsgID=0x{MessageId:X4}, Resp={IsResponse}, Ind={IsIndication}, TLVs={TLVs.Count})";
}
