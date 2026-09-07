using System.Security.Cryptography;
using System.Text;
using VoSharp.Common.Utils;

namespace VoSharp.Telephony.Sms;

public enum SmsDirection
{
    Received = 0,
    Submitted = 1,
    StatusReport = 2,
    Unknown = 99
}

public enum SmsEncoding
{
    Gsm7Bit = 0,
    EightBit = 1,
    Ucs2 = 2,
    Unknown = 99
}

public enum SmsStatus
{
    Unread = 0,
    Read = 1,
    Unsent = 2,
    Sent = 3,
    All = 4
}

public enum SmsDeliveryStatus
{
    Delivered,
    TemporaryError,
    PermanentError,
    TemporaryErrorNoRetry,
    Reserved,
    Unknown
}

public record SmsConcatInfo(
    int Reference,
    int Total,
    int Sequence
);

public record IncomingSms(
    string SenderNumber,
    string Text,
    DateTime Timestamp,
    DateTime? ServiceCenterTimestamp = null,
    SmsEncoding Encoding = SmsEncoding.Gsm7Bit,
    SmsConcatInfo? Concat = null,
    string? RawPdu = null,
    string? RawTpdu = null,
    int? ProtocolId = null,
    int? DataCodingScheme = null,
    bool IsMachinePayload = false,
    bool IsStatusReport = false,
    SmsStatusReport? StatusReport = null
);

public record SmsStatusReport(
    int MessageReference,
    string Recipient,
    int StatusCode,
    string DeliveryStatus,
    DateTime? ServiceCenterTimestamp,
    DateTime? DischargeTimestamp,
    DateTime Timestamp,
    string? RawPdu = null,
    string? RawTpdu = null
);

public record SmsSubmitPart(
    int PartNumber,
    int TotalParts,
    int Reference,
    byte[] Tpdu,
    string TpduHex,
    string PduHex,
    int TpduLength,
    string Recipient,
    SmsEncoding Encoding,
    int? ConcatReference = null
);

public record SmsSubmitPartStatus(
    int Part,
    int Total,
    int Reference,
    bool Accepted,
    int? SipCode = null,
    string? AtResponse = null,
    string SubmissionStatus = "pending",
    DateTime SubmittedAt = default
);

public record SmsSubmitResult(
    string Recipient,
    string Text,
    SmsEncoding Encoding,
    int? ConcatReference,
    int PartsTotal,
    int PartsAccepted,
    int PartsAttempted,
    bool AllPartsAccepted,
    string SubmissionStatus,
    List<SmsSubmitPartStatus> PartResults,
    DateTime SubmittedAt
);

public record SmsMessage(
    int Index,
    SmsStatus Status,
    string SenderOrRecipient,
    string Text,
    DateTime Timestamp,
    string? RawPdu = null,
    SmsDirection Direction = SmsDirection.Received,
    DateTime? ServiceCenterTimestamp = null,
    DateTime? DischargeTimestamp = null,
    int? MessageReference = null,
    int? StatusCode = null,
    string? DeliveryStatus = null,
    SmsConcatInfo? Concat = null,
    string? Id = null
);

public record OutgoingSmsPdu(
    string DestinationNumber,
    string PduHex,
    int TpduLength
);

public static class SmsPdu
{
    /// <summary>
    /// Normalizes and cleans an SMS recipient phone number into E.164-compatible digits.
    /// </summary>
    public static string NormalizeRecipient(string recipient)
    {
        recipient = (recipient ?? string.Empty).Trim();
        var sb = new StringBuilder();
        for (int i = 0; i < recipient.Length; i++)
        {
            char c = recipient[i];
            if (c >= '0' && c <= '9')
            {
                sb.Append(c);
            }
            else if (c == '+' && sb.Length == 0)
            {
                sb.Append(c);
            }
        }
        var result = sb.ToString();
        var digits = result.TrimStart('+');
        if (digits.Length < 1 || digits.Length > 20)
            throw new ArgumentException($"Invalid SMS recipient destination: '{recipient}'");
        return result;
    }

    /// <summary>
    /// Prepares single-part or multi-part concatenated SMS-SUBMIT TPDUs for transmission over IMS or AT Modem.
    /// By default sets TP-SRR (Status Report Request) so network delivery receipts are requested.
    /// </summary>
    public static List<SmsSubmitPart> PrepareSubmitParts(
        string recipient,
        string text,
        byte? concatReference = null,
        bool requestStatusReport = true,
        string? smsc = null)
    {
        recipient = NormalizeRecipient(recipient);
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("SMS message text cannot be empty.", nameof(text));

        bool isGsm7 = Gsm7Alphabet.CanEncode(text);
        var parts = new List<SmsSubmitPart>();

        if (isGsm7)
        {
            var septets = Gsm7Alphabet.EncodeToSeptets(text);
            if (septets.Length <= 160)
            {
                // Single-part GSM-7
                var pduHex = EncodeSingleSubmitPdu(recipient, septets, null, null, requestStatusReport, smsc, out int tpduLen, out byte[] tpduBytes);
                parts.Add(new SmsSubmitPart(1, 1, 0, tpduBytes, Convert.ToHexString(tpduBytes), pduHex, tpduLen, recipient, SmsEncoding.Gsm7Bit));
            }
            else
            {
                // Multipart GSM-7 (153 septets per segment)
                byte refByte = concatReference ?? (byte)RandomNumberGenerator.GetInt32(1, 255);
                var chunks = Gsm7Alphabet.SplitSeptets(septets, 153);
                int total = chunks.Count;

                for (int i = 0; i < total; i++)
                {
                    int seq = i + 1;
                    byte[] udh = [0x05, 0x00, 0x03, refByte, (byte)total, (byte)seq];
                    var pduHex = EncodeSingleSubmitPdu(recipient, chunks[i], null, udh, requestStatusReport, smsc, out int tpduLen, out byte[] tpduBytes, refByte);
                    parts.Add(new SmsSubmitPart(seq, total, refByte, tpduBytes, Convert.ToHexString(tpduBytes), pduHex, tpduLen, recipient, SmsEncoding.Gsm7Bit, refByte));
                }
            }
        }
        else
        {
            // UCS-2 encoding
            var runes = text.EnumerateRunes().ToArray();
            if (runes.Length <= 70 && Encoding.BigEndianUnicode.GetByteCount(text) <= 140)
            {
                // Single-part UCS-2
                var ucs2Bytes = Encoding.BigEndianUnicode.GetBytes(text);
                var pduHex = EncodeSingleSubmitPdu(recipient, null, ucs2Bytes, null, requestStatusReport, smsc, out int tpduLen, out byte[] tpduBytes);
                parts.Add(new SmsSubmitPart(1, 1, 0, tpduBytes, Convert.ToHexString(tpduBytes), pduHex, tpduLen, recipient, SmsEncoding.Ucs2));
            }
            else
            {
                // Multipart UCS-2 (67 characters per segment, surrogate safe)
                byte refByte = concatReference ?? (byte)RandomNumberGenerator.GetInt32(1, 255);
                var textSegments = SplitUcs2Text(text, 67);
                int total = textSegments.Count;

                for (int i = 0; i < total; i++)
                {
                    int seq = i + 1;
                    byte[] udh = [0x05, 0x00, 0x03, refByte, (byte)total, (byte)seq];
                    var segmentUcs2 = Encoding.BigEndianUnicode.GetBytes(textSegments[i]);
                    var pduHex = EncodeSingleSubmitPdu(recipient, null, segmentUcs2, udh, requestStatusReport, smsc, out int tpduLen, out byte[] tpduBytes, refByte);
                    parts.Add(new SmsSubmitPart(seq, total, refByte, tpduBytes, Convert.ToHexString(tpduBytes), pduHex, tpduLen, recipient, SmsEncoding.Ucs2, refByte));
                }
            }
        }

        return parts;
    }

    /// <summary>
    /// Encodes a text message into single SMS-SUBMIT PDU format (backward compatibility helper).
    /// </summary>
    public static OutgoingSmsPdu EncodeSubmitPdu(string destinationNumber, string messageText, string? smsc = null, bool requestStatusReport = true)
    {
        var parts = PrepareSubmitParts(destinationNumber, messageText, null, requestStatusReport, smsc);
        var first = parts[0];
        return new OutgoingSmsPdu(destinationNumber, first.PduHex, first.TpduLength);
    }

    private static string EncodeSingleSubmitPdu(
        string recipient,
        byte[]? gsm7Septets,
        byte[]? ucs2Bytes,
        byte[]? udh,
        bool requestStatusReport,
        string? smsc,
        out int tpduLength,
        out byte[] tpduBytes,
        byte messageReference = 0)
    {
        var sb = new StringBuilder();

        // 1. SCA (Service Centre Address)
        if (string.IsNullOrEmpty(smsc))
        {
            sb.Append("00");
        }
        else
        {
            var smscBytes = EncodeAddress(smsc, true);
            sb.Append(smscBytes.Length.ToString("X2"));
            sb.Append(Convert.ToHexString(smscBytes));
        }

        int tpduHexStart = sb.Length;

        // 2. First octet: TP-MTI = 01 (SMS-SUBMIT) | TP-VPF = 00 | TP-SRR (0x20) | TP-UDHI (0x40)
        byte firstOctet = 0x01;
        if (requestStatusReport) firstOctet |= 0x20;
        if (udh != null && udh.Length > 0) firstOctet |= 0x40;
        sb.Append(firstOctet.ToString("X2"));

        // 3. TP-MR (Message Reference)
        sb.Append(messageReference.ToString("X2"));

        // 4. TP-DA (Destination Address)
        var clean = recipient.Trim().TrimStart('+');
        sb.Append(clean.Length.ToString("X2"));
        sb.Append(recipient.StartsWith("+") ? "91" : "81");
        sb.Append(EncodeSemiOctets(clean));

        // 5. TP-PID
        sb.Append("00");

        // 6. TP-DCS & TP-UDL & User Data
        if (gsm7Septets != null)
        {
            sb.Append("00"); // GSM 7-bit DCS
            if (udh != null && udh.Length > 0)
            {
                int udhSeptets = ((udh.Length + 1) * 8 + 6) / 7;
                int startBit = udhSeptets * 7;
                int totalSeptets = udhSeptets + gsm7Septets.Length;

                var packed = Gsm7Alphabet.PackSeptets(gsm7Septets, startBit);
                // Copy UDH into the first bytes of the packed array
                packed[0] = (byte)udh.Length;
                Array.Copy(udh, 0, packed, 1, udh.Length);

                sb.Append(totalSeptets.ToString("X2"));
                sb.Append(Convert.ToHexString(packed));
            }
            else
            {
                var packed = Gsm7Alphabet.PackSeptets(gsm7Septets, 0);
                sb.Append(gsm7Septets.Length.ToString("X2"));
                sb.Append(Convert.ToHexString(packed));
            }
        }
        else if (ucs2Bytes != null)
        {
            sb.Append("08"); // UCS-2 DCS
            if (udh != null && udh.Length > 0)
            {
                int totalOctets = 1 + udh.Length + ucs2Bytes.Length;
                sb.Append(totalOctets.ToString("X2"));
                sb.Append(((byte)udh.Length).ToString("X2"));
                sb.Append(Convert.ToHexString(udh));
                sb.Append(Convert.ToHexString(ucs2Bytes));
            }
            else
            {
                sb.Append(ucs2Bytes.Length.ToString("X2"));
                sb.Append(Convert.ToHexString(ucs2Bytes));
            }
        }
        else
        {
            throw new ArgumentException("No payload bytes provided for SMS PDU encoding.");
        }

        var fullPdu = sb.ToString();
        var tpduHex = fullPdu[tpduHexStart..];
        tpduBytes = HexUtils.FromHexString(tpduHex);
        tpduLength = tpduBytes.Length;

        return fullPdu;
    }

    /// <summary>
    /// Decodes a generic SMS PDU (auto-detecting whether DELIVER, STATUS-REPORT, or SUBMIT).
    /// </summary>
    public static IncomingSms DecodePdu(string pduHex)
    {
        pduHex = (pduHex ?? string.Empty).Trim();
        var bytes = HexUtils.FromHexString(pduHex);
        if (bytes.Length < 2)
            throw new ArgumentException("PDU is too short.");

        int offset = 0;
        int scaLen = bytes[offset++];
        offset += scaLen;
        if (offset >= bytes.Length)
            throw new ArgumentException("PDU truncated after SCA.");

        byte firstOctet = bytes[offset];
        int mti = firstOctet & 0x03;

        if (mti == 2)
        {
            // SMS-STATUS-REPORT
            var report = DecodeStatusReportPdu(pduHex);
            return new IncomingSms(
                SenderNumber: report.Recipient,
                Text: $"[Delivery Report: {report.DeliveryStatus} (Code: {report.StatusCode}, MR: {report.MessageReference})]",
                Timestamp: report.Timestamp,
                ServiceCenterTimestamp: report.ServiceCenterTimestamp,
                RawPdu: pduHex,
                IsStatusReport: true,
                StatusReport: report
            );
        }
        else
        {
            return DecodeDeliverPdu(pduHex);
        }
    }

    /// <summary>
    /// Parses an incoming 3GPP SMS-DELIVER PDU string.
    /// </summary>
    public static IncomingSms DecodeDeliverPdu(string pduHex)
    {
        var bytes = HexUtils.FromHexString(pduHex);
        int offset = 0;

        // 1. SCA
        int scaLen = bytes[offset++];
        offset += scaLen;

        // 2. First octet
        byte firstOctet = bytes[offset++];
        bool hasUdhi = (firstOctet & 0x40) != 0;

        // 3. TP-OA (Originating Address)
        int oaDigitCount = bytes[offset++];
        byte oaType = bytes[offset++];
        int oaByteLen = (oaDigitCount + 1) / 2;
        var oaBytes = bytes.AsSpan(offset, oaByteLen).ToArray();
        offset += oaByteLen;

        string senderNumber;
        if ((oaType & 0x70) == 0x50) // Alphanumeric address
        {
            int septetCount = oaDigitCount * 4 / 7;
            var septets = Gsm7Alphabet.UnpackSeptets(oaBytes, septetCount, 0);
            senderNumber = Gsm7Alphabet.DecodeSeptets(septets);
        }
        else
        {
            senderNumber = DecodeSemiOctets(oaBytes, oaDigitCount);
            if (oaType == 0x91) senderNumber = "+" + senderNumber;
        }

        // 4. TP-PID
        byte pid = offset < bytes.Length ? bytes[offset++] : (byte)0;

        // 5. TP-DCS
        byte dcs = offset < bytes.Length ? bytes[offset++] : (byte)0;

        // 6. TP-SCTS (Service Centre Time Stamp - 7 bytes)
        int sctsLen = Math.Min(7, Math.Max(0, bytes.Length - offset));
        var sctsBytes = bytes.AsSpan(offset, sctsLen).ToArray();
        offset += sctsLen;
        var timestamp = DecodeScts(sctsBytes);

        // 7. TP-UDL & TP-UD
        int udl = offset < bytes.Length ? bytes[offset++] : 0;
        var udBytes = offset < bytes.Length ? bytes.AsSpan(offset).ToArray() : Array.Empty<byte>();

        int? concatRef = null;
        int? concatTotal = null;
        int? concatIdx = null;
        string text = string.Empty;

        var dcsAlphabet = dcs & 0x0C;
        var isUcs2 = (dcs == 0x08) || (dcsAlphabet == 0x08);
        var encoding = isUcs2 ? SmsEncoding.Ucs2 : (dcsAlphabet == 0x04 ? SmsEncoding.EightBit : SmsEncoding.Gsm7Bit);

        if (hasUdhi && udBytes.Length > 0)
        {
            int udhLen = udBytes[0];
            int udhOffset = 1;
            while (udhOffset < 1 + udhLen && udhOffset + 2 <= udBytes.Length)
            {
                byte iei = udBytes[udhOffset++];
                byte ieiLen = udBytes[udhOffset++];
                if (iei == 0x00 && ieiLen == 3 && udhOffset + 3 <= udBytes.Length) // 8-bit concat reference
                {
                    concatRef = udBytes[udhOffset++];
                    concatTotal = udBytes[udhOffset++];
                    concatIdx = udBytes[udhOffset++];
                }
                else if (iei == 0x08 && ieiLen == 4 && udhOffset + 4 <= udBytes.Length) // 16-bit concat reference
                {
                    concatRef = (udBytes[udhOffset++] << 8) | udBytes[udhOffset++];
                    concatTotal = udBytes[udhOffset++];
                    concatIdx = udBytes[udhOffset++];
                }
                else
                {
                    udhOffset += ieiLen;
                }
            }

            int contentOffset = 1 + udhLen;
            if (isUcs2)
            {
                if (contentOffset < udBytes.Length)
                {
                    var textBytes = udBytes.AsSpan(contentOffset).ToArray();
                    text = Encoding.BigEndianUnicode.GetString(textBytes);
                }
            }
            else
            {
                int skipSeptets = ((udhLen + 1) * 8 + 6) / 7;
                int validSeptetsCount = udl - skipSeptets;
                if (validSeptetsCount > 0)
                {
                    var validSeptets = Gsm7Alphabet.UnpackSeptets(udBytes, validSeptetsCount, skipSeptets * 7);
                    text = Gsm7Alphabet.DecodeSeptets(validSeptets);
                }
            }
        }
        else
        {
            if (isUcs2)
            {
                text = Encoding.BigEndianUnicode.GetString(udBytes);
            }
            else
            {
                text = Gsm7Alphabet.Decode7Bit(udBytes, udl);
            }
        }

        bool isMachine = IsMachinePayload(pid, dcs, text);
        SmsConcatInfo? concatInfo = (concatRef.HasValue && concatTotal.HasValue && concatIdx.HasValue)
            ? new SmsConcatInfo(concatRef.Value, concatTotal.Value, concatIdx.Value)
            : null;

        return new IncomingSms(
            SenderNumber: senderNumber,
            Text: text,
            Timestamp: timestamp,
            ServiceCenterTimestamp: timestamp,
            Encoding: encoding,
            Concat: concatInfo,
            RawPdu: pduHex,
            RawTpdu: pduHex,
            ProtocolId: pid,
            DataCodingScheme: dcs,
            IsMachinePayload: isMachine
        );
    }

    /// <summary>
    /// Decodes an incoming 3GPP SMS-STATUS-REPORT PDU.
    /// </summary>
    public static SmsStatusReport DecodeStatusReportPdu(string pduHex)
    {
        var bytes = HexUtils.FromHexString(pduHex);
        int offset = 0;

        // 1. SCA
        int scaLen = bytes[offset++];
        offset += scaLen;

        // 2. First octet
        byte firstOctet = bytes[offset++]; // MTI should be 02 (SMS-STATUS-REPORT)

        // 3. TP-MR (Message Reference)
        byte mr = bytes[offset++];

        // 4. TP-RA (Recipient Address)
        int raDigitCount = bytes[offset++];
        byte raType = bytes[offset++];
        int raByteLen = (raDigitCount + 1) / 2;
        var raBytes = bytes.AsSpan(offset, raByteLen).ToArray();
        offset += raByteLen;

        string recipient;
        if ((raType & 0x70) == 0x50)
        {
            int septetCount = raDigitCount * 4 / 7;
            var septets = Gsm7Alphabet.UnpackSeptets(raBytes, septetCount, 0);
            recipient = Gsm7Alphabet.DecodeSeptets(septets);
        }
        else
        {
            recipient = DecodeSemiOctets(raBytes, raDigitCount);
            if (raType == 0x91) recipient = "+" + recipient;
        }

        // 5. TP-SCTS (Service Centre Time Stamp - 7 bytes)
        int sctsLen = Math.Min(7, Math.Max(0, bytes.Length - offset));
        var sctsBytes = bytes.AsSpan(offset, sctsLen).ToArray();
        offset += sctsLen;
        var scts = DecodeScts(sctsBytes);

        // 6. TP-DT (Discharge Time Stamp - 7 bytes)
        int dtLen = Math.Min(7, Math.Max(0, bytes.Length - offset));
        var dtBytes = bytes.AsSpan(offset, dtLen).ToArray();
        offset += dtLen;
        var dischargeTime = DecodeScts(dtBytes);

        // 7. TP-ST (Status code)
        byte status = offset < bytes.Length ? bytes[offset++] : (byte)0;
        string deliveryStatus = FormatDeliveryStatus(status);

        return new SmsStatusReport(
            MessageReference: mr,
            Recipient: recipient,
            StatusCode: status,
            DeliveryStatus: deliveryStatus,
            ServiceCenterTimestamp: scts,
            DischargeTimestamp: dischargeTime,
            Timestamp: DateTime.UtcNow,
            RawPdu: pduHex,
            RawTpdu: pduHex
        );
    }

    /// <summary>
    /// Categorizes 3GPP TS 23.040 §9.2.3.15 TP-Status code into human-readable delivery status string.
    /// </summary>
    public static string FormatDeliveryStatus(byte status)
    {
        return status switch
        {
            <= 0x1F => "delivered",
            <= 0x3F => "temporary_error",
            <= 0x5F => "permanent_error",
            <= 0x7F => "temporary_error_no_retry",
            _ => "reserved"
        };
    }

    /// <summary>
    /// Determines if an incoming SMS PDU is a machine payload, SIM OTA data download,
    /// or binary control message rather than human-facing text per 3GPP TS 23.038 / TS 23.040.
    /// </summary>
    public static bool IsMachinePayload(int pid, int dcs, string text)
    {
        // 1. TP-PID 0x7F = (U)SIM Data Download
        if (pid == 0x7F) return true;

        // 2. TP-DCS 8-bit binary data coding
        if ((dcs >> 6) == 0b00 && ((dcs >> 2) & 0b11) == 0b01) return true;
        if ((dcs >> 4) == 0b1111 && (dcs & 0b100) != 0) return true;

        // 3. Message Class 2 = (U)SIM Specific Message (OTA / Toolkit)
        if ((dcs >> 6) == 0b00 && (dcs & 0b10000) != 0 && (dcs & 0b11) == 2) return true;
        if ((dcs >> 4) == 0b1111 && (dcs & 0b11) == 2) return true;

        // 4. Stray / non-printable control byte density check
        if (HasUnprintableStrayBytes(text)) return true;

        return false;
    }

    public static bool HasUnprintableStrayBytes(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int strayCount = 0;
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r' || c == '\t' || c == '\v' || c == '\f') continue;
            if (c < 0x20 || (c >= 0x7F && c <= 0x9F))
            {
                strayCount++;
            }
        }
        return strayCount > 0 && ((double)strayCount / text.Length > 0.15 || strayCount >= 2);
    }

    private static List<string> SplitUcs2Text(string text, int maxRunes)
    {
        var result = new List<string>();
        var runes = text.EnumerateRunes().ToArray();
        int offset = 0;

        while (offset < runes.Length)
        {
            int count = Math.Min(maxRunes, runes.Length - offset);
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                sb.Append(runes[offset + i].ToString());
            }
            result.Add(sb.ToString());
            offset += count;
        }

        return result;
    }

    private static string EncodeSemiOctets(string digits)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < digits.Length; i += 2)
        {
            char first = digits[i];
            char second = (i + 1 < digits.Length) ? digits[i + 1] : 'F';
            sb.Append(second);
            sb.Append(first);
        }
        return sb.ToString();
    }

    private static string DecodeSemiOctets(byte[] bytes, int digitCount)
    {
        var sb = new StringBuilder(digitCount);
        foreach (byte b in bytes)
        {
            int low = b & 0x0F;
            int high = (b >> 4) & 0x0F;
            if (sb.Length < digitCount && low <= 9) sb.Append(low);
            if (sb.Length < digitCount && high <= 9) sb.Append(high);
        }
        return sb.ToString();
    }

    private static byte[] EncodeAddress(string number, bool isSmsc = false)
    {
        var clean = number.Trim().TrimStart('+');
        var hex = (number.StartsWith("+") ? "91" : "81") + EncodeSemiOctets(clean);
        return HexUtils.FromHexString(hex);
    }

    private static DateTime DecodeScts(byte[] scts)
    {
        if (scts.Length < 7) return DateTime.UtcNow;
        int Bcd(byte b) => ((b & 0x0F) * 10) + ((b >> 4) & 0x0F);

        int year = 2000 + Bcd(scts[0]);
        if (Bcd(scts[0]) >= 90) year = 1900 + Bcd(scts[0]);
        int month = Math.Clamp(Bcd(scts[1]), 1, 12);
        int day = Math.Clamp(Bcd(scts[2]), 1, 31);
        int hour = Math.Clamp(Bcd(scts[3]), 0, 23);
        int minute = Math.Clamp(Bcd(scts[4]), 0, 59);
        int second = Math.Clamp(Bcd(scts[5]), 0, 59);

        // Timezone quarter-hours
        byte zoneByte = scts[6];
        bool negative = (zoneByte & 0x08) != 0;
        byte cleanZone = (byte)(zoneByte & ~0x08);
        int quarters = Bcd(cleanZone);
        int offsetMinutes = quarters * 15 * (negative ? -1 : 1);

        try
        {
            var offset = TimeSpan.FromMinutes(offsetMinutes);
            var dto = new DateTimeOffset(year, month, day, hour, minute, second, offset);
            return dto.UtcDateTime;
        }
        catch
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
    }
}
