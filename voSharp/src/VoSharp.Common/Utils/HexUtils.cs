namespace VoSharp.Common.Utils;

public static class HexUtils
{
    public static string ToHexString(this byte[] bytes, bool lowerCase = false)
    {
        return lowerCase ? Convert.ToHexString(bytes).ToLowerInvariant() : Convert.ToHexString(bytes);
    }

    public static byte[] FromHexString(string hex)
    {
        hex = hex.Replace(" ", "").Replace("-", "");
        if (hex.Length % 2 != 0)
        {
            hex = "0" + hex;
        }
        return Convert.FromHexString(hex);
    }

    public static byte[] Xor(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Arrays must have identical lengths for XOR operation");

        var result = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = (byte)(a[i] ^ b[i]);
        }
        return result;
    }
}
