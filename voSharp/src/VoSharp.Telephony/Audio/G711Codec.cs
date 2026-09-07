namespace VoSharp.Telephony.Audio;

public static class G711Codec
{
    private static readonly short[] SegAEnd = [0x1F, 0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF];

    public static short AlawToPcm(byte alaw)
    {
        alaw ^= 0x55;
        int sign = (alaw & 0x80);
        int exponent = (alaw >> 4) & 0x07;
        int mantissa = alaw & 0x0F;
        int sample = (mantissa << 4) + 8;
        if (exponent > 0) sample += 0x100;
        if (exponent > 1) sample <<= (exponent - 1);
        // BUG-18 FIX: In A-law, sign bit=1 means POSITIVE, sign bit=0 means NEGATIVE (ITU-T G.711 §A.2)
        return (short)(sign != 0 ? sample : -sample);
    }

    public static short UlawToPcm(byte ulaw)
    {
        ulaw = (byte)~ulaw;
        int sign = (ulaw & 0x80);
        int exponent = (ulaw >> 4) & 0x07;
        int mantissa = ulaw & 0x0F;
        int sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;
        return (short)(sign == 0 ? sample : -sample);
    }

    public static byte PcmToAlaw(short pcm)
    {
        int mask;
        if (pcm >= 0)
        {
            mask = 0xD5;
        }
        else
        {
            mask = 0x55;
            pcm = (short)-pcm;
            if (pcm < 0) pcm = short.MaxValue;
        }

        int seg;
        for (seg = 0; seg < 8; seg++)
        {
            if (pcm <= SegAEnd[seg]) break;
        }

        if (seg >= 8)
        {
            return (byte)(0x7F ^ mask);
        }

        byte aval = (byte)(seg << 4);
        if (seg < 2)
        {
            aval |= (byte)((pcm >> 4) & 0x0F);
        }
        else
        {
            aval |= (byte)((pcm >> (seg + 3)) & 0x0F);
        }
        return (byte)(aval ^ mask);
    }

    public static byte PcmToUlaw(short pcm)
    {
        const int BIAS = 0x84;
        const int CLIP = 32635;

        int sign = (pcm >> 8) & 0x80;
        if (sign != 0) pcm = (short)-pcm;
        if (pcm > CLIP) pcm = CLIP;
        pcm += BIAS;

        int exponent = 7;
        for (int expMask = 0x4000; (pcm & expMask) == 0 && exponent > 0; expMask >>= 1)
        {
            exponent--;
        }

        int mantissa = (pcm >> (exponent + 3)) & 0x0F;
        byte ulaw = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)~ulaw;
    }

    public static short[] DecodeAlaw(byte[] data)
    {
        var result = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = AlawToPcm(data[i]);
        return result;
    }

    public static short[] DecodeUlaw(byte[] data)
    {
        var result = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = UlawToPcm(data[i]);
        return result;
    }

    public static byte[] EncodeAlaw(short[] samples)
    {
        var result = new byte[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            result[i] = PcmToAlaw(samples[i]);
        return result;
    }

    public static byte[] EncodeUlaw(short[] samples)
    {
        var result = new byte[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            result[i] = PcmToUlaw(samples[i]);
        return result;
    }
}
