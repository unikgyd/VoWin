namespace VoSharp.Telephony.Audio;

/// <summary>
/// Handles RFC 4867 AMR-NB RTP payload unpacking and conversion to standard AMR storage format.
/// </summary>
public static class AmrNbDecoder
{
    private static readonly int[] AmrBitRates = { 4750, 5150, 5900, 6700, 7400, 7950, 10200, 12200 };
    private static readonly int[] AmrFrameSizes = { 12, 13, 15, 17, 19, 20, 26, 31, 5, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>
    /// Converts an RFC 4867 RTP packet payload into a standard octet-aligned AMR storage frame
    /// (1 TOC byte: (FT &lt;&lt; 3) | (Q &lt;&lt; 2), followed by speech data bytes).
    /// </summary>
    public static byte[] RtpPayloadToAmrFrame(byte[] payload, bool? forceOctetAligned = null)
    {
        if (payload == null || payload.Length < 2)
            return Array.Empty<byte>();

        bool octetAligned = forceOctetAligned ?? DetectOctetAligned(payload);

        if (octetAligned)
        {
            // Octet-aligned: byte 0 = CMR, byte 1.. = TOC + speech bytes
            return payload[1..];
        }

        // Bandwidth-efficient mode (RFC 4867 §4.3.2):
        // Byte 0: CMR (4 bits) | F (1 bit) | FT_high (3 bits)
        // Byte 1: FT_low (1 bit) | Q (1 bit) | Speech bits (6 bits)
        byte ft = (byte)(((payload[0] & 0x07) << 1) | ((payload[1] >> 7) & 1));
        byte q = (byte)((payload[1] >> 6) & 1);

        // Validate FT: 0-7 are valid speech modes, 8=SID, 15=NO_DATA; 9-14 are undefined/invalid
        if (ft >= 9 && ft <= 14)
            return Array.Empty<byte>();

        byte toc = (byte)((ft << 3) | (q << 2));

        // For NO_DATA (FT=15), return just the TOC byte
        if (ft == 15)
            return new byte[] { toc };

        int expectedSpeechBytes = AmrFrameSizes[ft];
        // Output: 1 TOC byte + exactly expectedSpeechBytes of speech data
        var outBytes = new byte[1 + expectedSpeechBytes];
        outBytes[0] = toc;

        // Bit-shift: speech bits start at bit offset 10 in the RTP payload
        // (CMR=4 + F=1 + FT=4 + Q=1 = 10 bits). Each output byte[i] (1-indexed)
        // maps to payload[i] << 2 | payload[i+1] >> 6.
        for (int i = 1; i <= expectedSpeechBytes && i < payload.Length; i++)
        {
            byte bCurrent = (byte)(payload[i] << 2);
            byte bNext = 0;
            if (i + 1 < payload.Length)
            {
                bNext = (byte)(payload[i + 1] >> 6);
            }
            outBytes[i] = (byte)(bCurrent | bNext);
        }

        return outBytes;
    }

    private static bool DetectOctetAligned(byte[] payload)
    {
        if ((payload[0] & 0x0F) == 0 && (payload[1] & 0x03) == 0)
        {
            int ft = (payload[1] >> 3) & 0x0F;
            if (ft < AmrFrameSizes.Length && AmrFrameSizes[ft] > 0)
            {
                int expectedLen = 2 + AmrFrameSizes[ft];
                if (payload.Length == expectedLen)
                    return true;
            }
        }
        return false;
    }

    private static readonly ThreadLocal<AmrNativeCodec> _threadCodec = new(() => new AmrNativeCodec(initEncoder: false));

    /// <summary>
    /// Decodes an RFC 4867 RTP packet payload into 160 linear PCM samples @ 8000Hz (20ms).
    /// </summary>
    public static short[] DecodeRtpPayload(byte[] payload, bool? forceOctetAligned = null, bool isWideband = false)
    {
        if (payload == null || payload.Length < 2)
            return new short[160];

        var amrFrame = RtpPayloadToAmrFrame(payload, forceOctetAligned);
        if (amrFrame.Length == 0)
            return new short[160];

        var codec = _threadCodec.Value;
        if (codec == null || !codec.IsAvailable)
            return new short[160];

        return isWideband ? codec.DecodeAmrWbFrame(amrFrame) : codec.DecodeAmrNbFrame(amrFrame);
    }
}
