using System.Text;
using System.Text.RegularExpressions;
using System.Net.Mime;
using VoSharp.Common.Utils;
using VoSharp.Sip;
using VoSharp.Telephony.Sms;

namespace VoSharp.Telephony.VoWifi;

public record RpMessage(
    byte MessageType,
    byte Reference,
    byte[]? Tpdu,
    byte[]? RawPayload = null
);

/// <summary>
/// Handles 3GPP TS 24.341 / TS 24.011 SMS over IP (VoWiFi / IMS) delivery via SIP MESSAGE.
/// Supports RP-DATA (MO/MT), RP-ACK, RP-ERROR, SMS-DELIVER, and SMS-STATUS-REPORT.
/// </summary>
public static class ImsSmsHandler
{
    public const string SmsContentType = "application/vnd.3gpp.sms";

    public static void ValidateIncomingTpdu(byte[] tpdu)
    {
        if (tpdu.Length < 3) throw new FormatException("Truncated SMS TPDU.");
        int mti = tpdu[0] & 3;
        if (mti is not 0 and not 2) throw new FormatException("Expected SMS-DELIVER or SMS-STATUS-REPORT.");
        int addressOffset = mti == 0 ? 1 : 2;
        int addressLength = (tpdu[addressOffset] + 1) / 2;
        int afterAddress = addressOffset + 2 + addressLength;
        if (mti == 2)
        {
            if (tpdu.Length < afterAddress + 15) throw new FormatException("Truncated SMS status report.");
            return;
        }
        int udlOffset = afterAddress + 9; // PID, DCS, SCTS
        if (tpdu.Length <= udlOffset) throw new FormatException("Truncated SMS-DELIVER header.");
        int dcs = tpdu[afterAddress + 1];
        int udl = tpdu[udlOffset];
        int octets = (dcs & 0x0c) == 0 ? (udl * 7 + 7) / 8 : udl;
        int start = udlOffset + 1;
        if (tpdu.Length - start < octets) throw new FormatException("Truncated SMS user data.");
        if ((tpdu[0] & 0x40) != 0 && (octets == 0 || tpdu[start] + 1 > octets))
            throw new FormatException("Invalid SMS user data header.");
    }

    /// <summary>
    /// Parses a 3GPP TS 24.011 Relay Protocol Data Unit (RPDU).
    /// </summary>
    public static RpMessage ParseRpdu(byte[] data)
    {
        if (data == null || data.Length < 2)
            throw new ArgumentException("RPDU is truncated.", nameof(data));

        byte msgType = (byte)(data[0] & 0x07);
        byte reference = data[1];

        // 0 = RP-MO-DATA, 1 = RP-MT-DATA
        if (msgType != 0 && msgType != 1)
        {
            return new RpMessage(msgType, reference, null, data);
        }

        int index = 2;
        // Skip RP-Originator Address and RP-Destination Address
        for (int count = 0; count < 2; count++)
        {
            if (index >= data.Length)
                throw new ArgumentException("RP-DATA address is truncated.", nameof(data));
            int len = data[index++];
            if (len > data.Length - index)
                throw new ArgumentException("RP-DATA address length is invalid.", nameof(data));
            index += len;
        }

        if (index >= data.Length)
            throw new ArgumentException("RP-DATA omitted user data.", nameof(data));

        int tpduLen = data[index++];
        if (tpduLen == 0 || tpduLen > data.Length - index)
            throw new ArgumentException("RP-DATA user-data length is invalid.", nameof(data));

        var tpdu = data.AsSpan(index, tpduLen).ToArray();
        return new RpMessage(msgType, reference, tpdu, data);
    }

    /// <summary>
    /// Builds a 3GPP TS 24.011 RP-MO-DATA frame for Mobile-Originated SMS over IMS.
    /// </summary>
    public static byte[] BuildRpData(byte reference, string smsc, byte[] tpdu)
    {
        if (tpdu == null || tpdu.Length == 0 || tpdu.Length > 232)
            throw new ArgumentException("Invalid SMS TPDU length.", nameof(tpdu));

        var address = EncodeRpAddress(smsc);
        var result = new List<byte>(4 + address.Length + 1 + tpdu.Length)
        {
            0x00, // RP-MO-DATA (Type 0, MS to Network)
            reference,
            0x00, // RP-Originator Address length (0 for MS)
            (byte)address.Length
        };
        result.AddRange(address);
        result.Add((byte)tpdu.Length);
        result.AddRange(tpdu);
        return result.ToArray();
    }

    /// <summary>
    /// Builds a standard 3GPP TS 24.011 §7.3.1 / TS 24.341 RP-ACK response frame.
    /// Format: [0x02, ref, 0x41 (IEI: RP-User-Data), 0x02 (Len), 0x00 (TP-MTI=00), 0x00 (TP-PI=00)].
    /// </summary>
    public static byte[] BuildRpAck(byte reference, bool includeUserData = true)
    {
        if (includeUserData)
        {
            return [0x02, reference, 0x41, 0x02, 0x00, 0x00];
        }
        return [0x02, reference];
    }

    /// <summary>
    /// Builds a 3GPP TS 24.011 RP-ERROR frame (Type 0x04).
    /// </summary>
    public static byte[] BuildRpError(byte reference, byte cause = 95)
    {
        return [0x04, reference, 0x01, (byte)(cause & 0x7F)];
    }

    /// <summary>
    /// Encodes a phone number / SMSC address into 3GPP TS 24.011 BCD format.
    /// </summary>
    public static byte[] EncodeRpAddress(string value)
    {
        var clean = value.Trim();
        var digits = clean.TrimStart('+');
        if (digits.Length < 1 || digits.Length > 20)
            digits = "13800100500"; // fallback

        byte toa = clean.StartsWith("+") ? (byte)0x91 : (byte)0x81;
        int byteCount = (digits.Length + 1) / 2;
        var encoded = new byte[byteCount];

        for (int i = 0; i < digits.Length; i += 2)
        {
            byte low = (byte)(digits[i] - '0');
            byte high = (byte)0x0F;
            if (i + 1 < digits.Length)
            {
                high = (byte)(digits[i + 1] - '0');
            }
            encoded[i / 2] = (byte)((high << 4) | low);
        }

        var result = new byte[1 + encoded.Length];
        result[0] = toa;
        Array.Copy(encoded, 0, result, 1, encoded.Length);
        return result;
    }

    /// <summary>
    /// Extracts SMS RPDU payload bytes from a SIP MESSAGE request.
    /// Supports direct binary (application/vnd.3gpp.sms) and multipart/mixed bodies.
    /// </summary>
    public static byte[]? ExtractSmsPayload(SipMessage request, out string payloadType)
    {
        payloadType = string.Empty;
        if (request == null) return null;
        if (!TryContentType(request.GetHeader("Content-Type"), out var ct)) return null;
        var encoding = request.GetHeader("Content-Transfer-Encoding") ?? "binary";
        var bytes = request.RawBody ?? Encoding.Latin1.GetBytes(request.Body);
        if (ct!.MediaType.Equals(SmsContentType, StringComparison.OrdinalIgnoreCase))
        {
            payloadType = SmsContentType;
            return DecodeContentTransfer(bytes, encoding);
        }
        if (ct.MediaType.Equals("multipart/mixed", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = ct.Boundary;
            if (string.IsNullOrEmpty(boundary) || boundary.Length > 70 || boundary.Contains('\r') || boundary.Contains('\n'))
                throw new FormatException("Missing or invalid multipart SMS boundary.");
            var marker = Encoding.ASCII.GetBytes("--" + boundary);
            int position = FindBoundary(bytes, marker, 0, out int partStart, out bool closing);
            while (position >= 0 && !closing)
            {
                int next = FindBoundary(bytes, marker, partStart, out int nextStart, out bool nextClosing);
                if (next < 0) throw new FormatException("Unterminated multipart SMS.");
                // The CRLF immediately preceding a MIME boundary belongs to the delimiter.
                int partEnd = next - 2;
                var part = bytes.AsSpan(partStart, partEnd - partStart);
                int split = part.IndexOf("\r\n\r\n"u8);
                if (split < 0) throw new FormatException("Malformed MIME part headers.");
                var headers = Regex.Replace(Encoding.ASCII.GetString(part[..split]), @"\r\n[ \t]+", " ");
                var type = Regex.Match(headers, @"(?im)^Content-Type:[ \t]*([^\r\n]+)");
                if (type.Success && TryContentType(type.Groups[1].Value, out var partType) &&
                    partType!.MediaType.Equals(SmsContentType, StringComparison.OrdinalIgnoreCase))
                {
                    var transfer = Regex.Match(headers, @"(?im)^Content-Transfer-Encoding:[ \t]*([^\r\n]+)");
                    payloadType = "multipart/mixed";
                    return DecodeContentTransfer(part[(split + 4)..].ToArray(), transfer.Success ? transfer.Groups[1].Value : "binary");
                }
                position = next;
                partStart = nextStart;
                closing = nextClosing;
            }
            throw new FormatException("Multipart MESSAGE has no SMS part.");
        }
        return null;
    }

    public static bool SupportsContentType(string? value) => TryContentType(value, out var type) &&
        (type!.MediaType.Equals(SmsContentType, StringComparison.OrdinalIgnoreCase) ||
         type.MediaType.Equals("multipart/mixed", StringComparison.OrdinalIgnoreCase));

    private static bool TryContentType(string? value, out ContentType? type)
    {
        type = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { type = new ContentType(value); return true; }
        catch (FormatException) { return false; }
    }

    private static int FindBoundary(byte[] body, byte[] marker, int start, out int after, out bool closing)
    {
        for (int i = start; i <= body.Length - marker.Length; i++)
        {
            if (i != 0 && (i < 2 || body[i - 2] != 13 || body[i - 1] != 10)) continue;
            if (!body.AsSpan(i, marker.Length).SequenceEqual(marker)) continue;
            int end = i + marker.Length;
            closing = end + 1 < body.Length && body[end] == '-' && body[end + 1] == '-';
            if (closing) end += 2;
            while (end < body.Length && body[end] is 32 or 9) end++;
            if (end + 1 < body.Length && body[end] == 13 && body[end + 1] == 10)
            { after = end + 2; return i; }
            if (closing && end == body.Length) { after = end; return i; }
        }
        after = 0;
        closing = false;
        return -1;
    }

    private static byte[] DecodeContentTransfer(byte[] body, string encoding)
    {
        var encLower = encoding.Trim().ToLowerInvariant();
        if (encLower is "" or "binary" or "8bit") return body;
        if (encLower == "base64") return Convert.FromBase64String(Encoding.ASCII.GetString(body));
        if (encLower == "quoted-printable")
        {
            var decoded = new List<byte>();
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] != '=') { decoded.Add(body[i]); continue; }
                if (i + 2 >= body.Length) throw new FormatException("Truncated quoted-printable escape.");
                if (body[i + 1] == 13 && body[i + 2] == 10) { i += 2; continue; }
                if (!byte.TryParse(Encoding.ASCII.GetString(body, i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) throw new FormatException("Invalid quoted-printable escape.");
                decoded.Add(value);
                i += 2;
            }
            return decoded.ToArray();
        }
        throw new FormatException($"Unsupported SMS transfer encoding: {encoding}");
    }

    /// <summary>
    /// Builds a SIP response (e.g. 200 OK) for an incoming SIP request, optionally embedding an RPDU body.
    /// </summary>
    public static SipMessage BuildSipResponse(SipMessage request, int statusCode, string fromTag, byte[]? body = null)
    {
        string reason = statusCode switch
        {
            200 => "OK",
            202 => "Accepted",
            405 => "Method Not Allowed",
            415 => "Unsupported Media Type",
            488 => "Not Acceptable Here",
            _ => "Status " + statusCode
        };

        var resp = request.CreateResponse(statusCode, reason);

        var from = request.GetHeader("From") ?? string.Empty;
        var to = request.GetHeader("To") ?? string.Empty;
        if (!to.Contains(";tag=", StringComparison.OrdinalIgnoreCase))
        {
            to += ";tag=" + fromTag;
        }

        resp.SetHeader("From", from);
        resp.SetHeader("To", to);
        resp.SetHeader("Call-ID", request.GetHeader("Call-ID") ?? string.Empty);
        resp.SetHeader("CSeq", request.GetHeader("CSeq") ?? string.Empty);

        if (statusCode == 405) resp.SetHeader("Allow", "REGISTER, MESSAGE, OPTIONS");
        if (statusCode == 415) resp.SetHeader("Accept", SmsContentType);

        if (body != null && body.Length > 0)
        {
            resp.SetHeader("Content-Type", SmsContentType);
            resp.SetHeader("Content-Transfer-Encoding", "binary");
            resp.SetHeader("Content-Length", body.Length.ToString());
            resp.RawBody = body;
            resp.Body = Encoding.Latin1.GetString(body);
        }
        else
        {
            resp.SetHeader("Content-Length", "0");
        }

        return resp;
    }

    /// <summary>
    /// Decodes an incoming 3GPP RP-DATA payload carried in SIP MESSAGE body into an SMS message.
    /// Supports both SMS-DELIVER and SMS-STATUS-REPORT.
    /// </summary>
    public static IncomingSms? DecodeImsSms(byte[] rpDataPayload, int messageIndex = 0)
    {
        if (rpDataPayload == null || rpDataPayload.Length < 6)
            return null;

        try
        {
            var rpdu = ParseRpdu(rpDataPayload);
            if (rpdu.Tpdu == null || rpdu.Tpdu.Length == 0) return null;

            var pduHex = "00" + Convert.ToHexString(rpdu.Tpdu);
            var incoming = SmsPdu.DecodePdu(pduHex);
            return incoming;
        }
        catch
        {
            return null;
        }
    }
}
