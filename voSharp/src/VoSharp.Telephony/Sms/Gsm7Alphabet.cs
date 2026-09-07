using System.Text;

namespace VoSharp.Telephony.Sms;

/// <summary>
/// Implements 3GPP TS 23.038 7-bit default alphabet and extension table encoding/decoding.
/// </summary>
public static class Gsm7Alphabet
{
    public static readonly char[] DefaultAlphabet = [
        '@', '£', '$', '¥', 'è', 'é', 'ù', 'ì', 'ò', 'Ç', '\n', 'Ø', 'ø', '\r', 'Å', 'å',
        'Δ', '_', 'Φ', 'Γ', 'Λ', 'Ω', 'Π', 'Ψ', 'Σ', 'Θ', 'Ξ', '\x1b', 'Æ', 'æ', 'ß', 'É',
        ' ', '!', '"', '#', '¤', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.', '/',
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ':', ';', '<', '=', '>', '?',
        '¡', 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O',
        'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z', 'Ä', 'Ö', 'Ñ', 'Ü', '§',
        '¿', 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o',
        'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z', 'ä', 'ö', 'ñ', 'ü', 'à'
    ];

    public static readonly Dictionary<byte, char> ExtensionAlphabet = new()
    {
        [0x0A] = '\f',
        [0x14] = '^',
        [0x28] = '{',
        [0x29] = '}',
        [0x2F] = '\\',
        [0x3C] = '[',
        [0x3D] = '~',
        [0x3E] = ']',
        [0x40] = '|',
        [0x65] = '€'
    };

    private static readonly Dictionary<char, byte[]> CharToSeptets = new();

    static Gsm7Alphabet()
    {
        for (int i = 0; i < DefaultAlphabet.Length; i++)
        {
            if (i == 0x1B) continue; // escape code
            CharToSeptets[DefaultAlphabet[i]] = [(byte)i];
        }

        foreach (var (b, c) in ExtensionAlphabet)
        {
            CharToSeptets[c] = [0x1B, b];
        }
    }

    /// <summary>
    /// Returns true if all characters in the text can be encoded in 3GPP GSM 7-bit (basic or extension).
    /// </summary>
    public static bool CanEncode(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        foreach (char c in text)
        {
            if (!CharToSeptets.ContainsKey(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Encodes a string into raw septets (unpacked bytes, each 0-127).
    /// Extension characters will expand to two septets (0x1B followed by extension code).
    /// </summary>
    public static byte[] EncodeToSeptets(string text)
    {
        var result = new List<byte>(text.Length);
        foreach (char c in text)
        {
            if (CharToSeptets.TryGetValue(c, out var bytes))
            {
                result.AddRange(bytes);
            }
            else
            {
                // Fallback to '?'
                result.Add(0x3F);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Decodes raw septets into a string, interpreting 0x1B escape sequences.
    /// </summary>
    public static string DecodeSeptets(byte[] septets)
    {
        var sb = new StringBuilder(septets.Length);
        for (int i = 0; i < septets.Length; i++)
        {
            byte code = septets[i];
            if (code == 0x1B)
            {
                if (i + 1 < septets.Length)
                {
                    byte extCode = septets[++i];
                    if (ExtensionAlphabet.TryGetValue(extCode, out char extChar))
                    {
                        sb.Append(extChar);
                    }
                    else
                    {
                        sb.Append(' ');
                    }
                }
                else
                {
                    sb.Append(' ');
                }
            }
            else if (code < DefaultAlphabet.Length)
            {
                sb.Append(DefaultAlphabet[code]);
            }
            else
            {
                sb.Append('?');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Packs septets into an 8-bit octet array, optionally starting at a given bit offset (for UDH alignment).
    /// </summary>
    public static byte[] PackSeptets(byte[] septets, int startBit = 0)
    {
        if (septets.Length == 0) return [];
        int bitLength = startBit + septets.Length * 7;
        int byteCount = (bitLength + 7) / 8;
        var result = new byte[byteCount];

        for (int i = 0; i < septets.Length; i++)
        {
            int bit = startBit + i * 7;
            int byteIndex = bit / 8;
            int shift = bit % 8;

            result[byteIndex] |= (byte)((septets[i] & 0x7F) << shift);
            if (shift > 1 && byteIndex + 1 < byteCount)
            {
                result[byteIndex + 1] |= (byte)((septets[i] & 0x7F) >> (8 - shift));
            }
        }

        return result;
    }

    /// <summary>
    /// Unpacks an 8-bit octet array into septets, starting from the specified bit offset.
    /// </summary>
    public static byte[] UnpackSeptets(byte[] packed, int septetCount, int startBit = 0)
    {
        if (septetCount <= 0 || packed.Length == 0) return [];
        var result = new byte[septetCount];

        for (int i = 0; i < septetCount; i++)
        {
            int bit = startBit + i * 7;
            int byteIndex = bit / 8;
            int shift = bit % 8;

            if (byteIndex >= packed.Length) break;

            byte val = (byte)(packed[byteIndex] >> shift);
            if (shift > 1 && byteIndex + 1 < packed.Length)
            {
                val |= (byte)((packed[byteIndex + 1] << (8 - shift)) & 0x7F);
            }

            result[i] = (byte)(val & 0x7F);
        }

        return result;
    }

    /// <summary>
    /// Packs text directly into 7-bit packed bytes (for single-part SMS without UDH).
    /// </summary>
    public static byte[] Encode7Bit(string text)
    {
        var septets = EncodeToSeptets(text);
        return PackSeptets(septets, 0);
    }

    /// <summary>
    /// Decodes 7-bit packed bytes into a string.
    /// </summary>
    public static string Decode7Bit(byte[] packed, int septetCount)
    {
        var septets = UnpackSeptets(packed, septetCount, 0);
        return DecodeSeptets(septets);
    }

    /// <summary>
    /// Splits raw septets into chunks of maximum size without breaking an extension escape pair (0x1B + char).
    /// </summary>
    public static List<byte[]> SplitSeptets(byte[] septets, int maxChunkSize)
    {
        var list = new List<byte[]>();
        int offset = 0;

        while (offset < septets.Length)
        {
            int count = Math.Min(maxChunkSize, septets.Length - offset);
            // If the last byte in the chunk is an escape (0x1B) and there are more bytes left, don't split the pair
            if (count < septets.Length - offset && septets[offset + count - 1] == 0x1B)
            {
                count--;
            }

            var chunk = new byte[count];
            Array.Copy(septets, offset, chunk, 0, count);
            list.Add(chunk);
            offset += count;
        }

        return list;
    }
}
