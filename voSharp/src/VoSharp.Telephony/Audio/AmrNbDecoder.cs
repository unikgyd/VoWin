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
        var frames = RtpPayloadToAmrFrames(payload, forceOctetAligned);
        return frames.Length == 0 ? [] : frames[0];
    }

    private static readonly int[] NbBits = [95, 103, 118, 134, 148, 159, 204, 244, 39, -1, -1, -1, -1, -1, -1, 0];
    private static readonly int[] WbBits = [132, 177, 253, 285, 317, 365, 397, 461, 477, 40, -1, -1, -1, -1, 0, 0];

    internal static bool IsStorageFrameValid(byte[] frame, bool wideband = false)
    {
        if (frame is not { Length: > 0 }) return false;
        var bits = (wideband ? WbBits : NbBits)[frame[0] >> 3 & 15];
        return bits >= 0 && frame.Length == 1 + (bits + 7) / 8;
    }

    /// <summary>Validated mono RFC 4867 payload; reserved/padding bits are ignored on receipt.</summary>
    public static byte[][] RtpPayloadToAmrFrames(byte[] payload, bool? forceOctetAligned = null, bool isWideband = false)
    {
        if (forceOctetAligned is null)
        {
            var aligned = RtpPayloadToAmrFrames(payload, true, isWideband);
            return aligned.Length != 0 ? aligned : RtpPayloadToAmrFrames(payload, false, isWideband);
        }
        if (payload is not { Length: >= 2 and <= 1024 }) return [];
        var sizes = isWideband ? WbBits : NbBits;
        var octet = forceOctetAligned.Value;
        var at = octet ? 8 : 4;
        int Bit(int offset) => payload[offset / 8] >> (7 - offset % 8) & 1;
        var frames = new List<byte[]>();
        while (true)
        {
            if (frames.Count >= 16 || at + (octet ? 8 : 6) > payload.Length * 8) return [];
            var more = Bit(at++);
            var ft = 0;
            for (var i = 0; i < 4; i++) ft = ft * 2 + Bit(at++);
            var q = Bit(at++);
            if (octet) at += 2;
            var bits = sizes[ft];
            if (bits < 0) return [];
            var frame = new byte[1 + (bits + 7) / 8];
            frame[0] = (byte)(ft << 3 | q << 2);
            frames.Add(frame);
            if (more == 0) break;
        }
        foreach (var frame in frames)
        {
            var bits = sizes[frame[0] >> 3];
            if (at + bits > payload.Length * 8) return [];
            for (var i = 0; i < bits; i++) frame[1 + i / 8] |= (byte)(Bit(at++) << (7 - i % 8));
            if (octet) at = (at + 7) / 8 * 8;
        }
        return (at + 7) / 8 == payload.Length ? frames.ToArray() : [];
    }

    private static readonly ThreadLocal<AmrNativeCodec> _threadCodec = new(() => new AmrNativeCodec(initEncoder: false));

    /// <summary>
    /// Decodes an RFC 4867 RTP packet payload into 160 linear PCM samples @ 8000Hz (20ms).
    /// </summary>
    public static short[] DecodeRtpPayload(byte[] payload, bool? forceOctetAligned = null, bool isWideband = false)
    {
        if (payload == null || payload.Length < 2)
            return new short[160];

        var frames = RtpPayloadToAmrFrames(payload, forceOctetAligned, isWideband);
        var amrFrame = frames.Length == 0 ? [] : frames[0];
        if (amrFrame.Length == 0)
            return new short[160];

        var codec = _threadCodec.Value;
        if (codec == null || !codec.IsAvailable)
            return new short[160];

        return isWideband ? codec.DecodeAmrWbFrame(amrFrame) : codec.DecodeAmrNbFrame(amrFrame);
    }
}
