using System.Security.Cryptography;

namespace VoSharp.Crypto;

/// <summary>
/// Result of 3GPP AKA calculation containing all authentication vectors.
/// </summary>
public record AkaVector(
    byte[] MacA,
    byte[] Res,
    byte[] Ck,
    byte[] Ik,
    byte[] Ak,
    byte[] Autn
);

/// <summary>
/// Pure C# implementation of 3GPP TS 35.206 / TS 35.208 Milenage algorithm.
/// Zero native C/CGO dependencies.
/// </summary>
public static class Milenage
{
    private static readonly byte[] C1 = new byte[16];
    private static readonly byte[] C2 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];
    private static readonly byte[] C3 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2];
    private static readonly byte[] C4 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4];
    private static readonly byte[] C5 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8];

    /// <summary>
    /// Computes OPc from K and OP according to 3GPP TS 35.206.
    /// OPc = AES_128(K, OP) ^ OP
    /// </summary>
    public static byte[] ComputeOPc(ReadOnlySpan<byte> k, ReadOnlySpan<byte> op)
    {
        if (k.Length != 16 || op.Length != 16)
            throw new ArgumentException("K and OP must be 16 bytes each.");

        Span<byte> encrypted = stackalloc byte[16];
        Aes128EncryptBlock(k, op, encrypted);

        var opc = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            opc[i] = (byte)(encrypted[i] ^ op[i]);
        }
        return opc;
    }

    /// <summary>
    /// Computes f1 (MAC-A) and f1* (MAC-S).
    /// </summary>
    public static (byte[] MacA, byte[] MacS) ComputeF1(
        ReadOnlySpan<byte> opc,
        ReadOnlySpan<byte> k,
        ReadOnlySpan<byte> rand,
        ReadOnlySpan<byte> sqn,
        ReadOnlySpan<byte> amf)
    {
        if (opc.Length != 16 || k.Length != 16 || rand.Length != 16 || sqn.Length != 6 || amf.Length != 2)
            throw new ArgumentException("Invalid argument lengths for f1.");

        Span<byte> tmp1 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            tmp1[i] = (byte)(rand[i] ^ opc[i]);

        Span<byte> temp = stackalloc byte[16];
        Aes128EncryptBlock(k, tmp1, temp);

        Span<byte> in1 = stackalloc byte[16];
        sqn.CopyTo(in1[..6]);
        amf.CopyTo(in1.Slice(6, 2));
        sqn.CopyTo(in1.Slice(8, 6));
        amf.CopyTo(in1.Slice(14, 2));

        Span<byte> in1XorOpc = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            in1XorOpc[i] = (byte)(in1[i] ^ opc[i]);

        // Rotate by r1 = 64 bits = 8 bytes
        Span<byte> rotated = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            rotated[(i + 8) % 16] = in1XorOpc[i];

        // XOR with TEMP and c1
        Span<byte> tmp3 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            tmp3[i] = (byte)(rotated[i] ^ temp[i] ^ C1[i]);

        Span<byte> out1 = stackalloc byte[16];
        Aes128EncryptBlock(k, tmp3, out1);

        for (int i = 0; i < 16; i++)
            out1[i] = (byte)(out1[i] ^ opc[i]);

        var macA = out1[..8].ToArray();
        var macS = out1[8..16].ToArray();
        return (macA, macS);
    }

    /// <summary>
    /// Computes f2 (RES), f3 (CK), f4 (IK), f5 (AK), f5* (AK*).
    /// </summary>
    public static (byte[] Res, byte[] Ck, byte[] Ik, byte[] Ak, byte[] AkStar) ComputeF2345(
        ReadOnlySpan<byte> opc,
        ReadOnlySpan<byte> k,
        ReadOnlySpan<byte> rand)
    {
        if (opc.Length != 16 || k.Length != 16 || rand.Length != 16)
            throw new ArgumentException("Invalid argument lengths for f2345.");

        Span<byte> tmp1 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            tmp1[i] = (byte)(rand[i] ^ opc[i]);

        Span<byte> temp = stackalloc byte[16];
        Aes128EncryptBlock(k, tmp1, temp);

        Span<byte> tempXorOpc = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            tempXorOpc[i] = (byte)(temp[i] ^ opc[i]);

        // f2 and f5 (r2 = 0 bytes rotation)
        Span<byte> block2 = stackalloc byte[16];
        tempXorOpc.CopyTo(block2);
        for (int i = 0; i < 16; i++) block2[i] ^= C2[i];

        Span<byte> out2 = stackalloc byte[16];
        Aes128EncryptBlock(k, block2, out2);
        for (int i = 0; i < 16; i++) out2[i] ^= opc[i];

        var res = out2.Slice(8, 8).ToArray();
        var ak = out2[..6].ToArray();

        // f3 (CK: r3 = 32 bits = 4 bytes rotation -> shift left by 12 / shift right by 4)
        Span<byte> block3 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            block3[(i + 12) % 16] = tempXorOpc[i];
        for (int i = 0; i < 16; i++) block3[i] ^= C3[i];

        Span<byte> ck = stackalloc byte[16];
        Aes128EncryptBlock(k, block3, ck);
        for (int i = 0; i < 16; i++) ck[i] ^= opc[i];

        // f4 (IK: r4 = 64 bits = 8 bytes rotation)
        Span<byte> block4 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            block4[(i + 8) % 16] = tempXorOpc[i];
        for (int i = 0; i < 16; i++) block4[i] ^= C4[i];

        Span<byte> ik = stackalloc byte[16];
        Aes128EncryptBlock(k, block4, ik);
        for (int i = 0; i < 16; i++) ik[i] ^= opc[i];

        // f5* (AK*: r5 = 96 bits = 12 bytes rotation -> shift right by 12 / shift left by 4)
        Span<byte> block5 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            block5[(i + 4) % 16] = tempXorOpc[i];
        for (int i = 0; i < 16; i++) block5[i] ^= C5[i];

        Span<byte> out5 = stackalloc byte[16];
        Aes128EncryptBlock(k, block5, out5);
        for (int i = 0; i < 16; i++) out5[i] ^= opc[i];

        var akStar = out5[..6].ToArray();

        return (res, ck.ToArray(), ik.ToArray(), ak, akStar);
    }

    /// <summary>
    /// Performs full 3GPP AKA generation: produces MacA, Res, Ck, Ik, Ak and Autn = (SQN ^ AK) || AMF || MAC-A.
    /// </summary>
    public static AkaVector GenerateAkaVectors(
        ReadOnlySpan<byte> opc,
        ReadOnlySpan<byte> k,
        ReadOnlySpan<byte> rand,
        ReadOnlySpan<byte> sqn,
        ReadOnlySpan<byte> amf)
    {
        var (macA, _) = ComputeF1(opc, k, rand, sqn, amf);
        var (res, ck, ik, ak, _) = ComputeF2345(opc, k, rand);

        var autn = new byte[16];
        for (int i = 0; i < 6; i++)
            autn[i] = (byte)(sqn[i] ^ ak[i]);
        amf.CopyTo(autn.AsSpan(6, 2));
        macA.CopyTo(autn.AsSpan(8, 8));

        return new AkaVector(macA, res, ck, ik, ak, autn);
    }

    private static void Aes128EncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();

        using var encryptor = aes.CreateEncryptor();
        encryptor.TransformBlock(input.ToArray(), 0, 16, output.ToArray(), 0);
        // Note: transform output array back into span if needed
        var encArray = new byte[16];
        encryptor.TransformBlock(input.ToArray(), 0, 16, encArray, 0);
        encArray.CopyTo(output);
    }
}
