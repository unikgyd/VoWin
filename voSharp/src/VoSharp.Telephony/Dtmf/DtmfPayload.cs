namespace VoSharp.Telephony.Dtmf;

public static class DtmfPayload
{
    /// <summary>
    /// Builds RFC 4733 RTP DTMF telephone-event payload (4 bytes).
    /// </summary>
    public static byte[] BuildRfc4733(char digit, bool endOfEvent, byte volume = 10, ushort duration = 160)
    {
        byte eventCode = digit switch
        {
            >= '0' and <= '9' => (byte)(digit - '0'),
            '*' => 10,
            '#' => 11,
            'A' or 'a' => 12,
            'B' or 'b' => 13,
            'C' or 'c' => 14,
            'D' or 'd' => 15,
            _ => throw new ArgumentException($"Unsupported DTMF key '{digit}'")
        };

        var payload = new byte[4];
        payload[0] = eventCode;
        payload[1] = (byte)((endOfEvent ? 0x80 : 0x00) | (volume & 0x3F));
        payload[2] = (byte)((duration >> 8) & 0xFF);
        payload[3] = (byte)(duration & 0xFF);

        return payload;
    }
}
