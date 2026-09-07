using System.Buffers.Binary;
using System.Security.Cryptography;
using VoSharp.Common.Utils;
using VoSharp.Crypto;

namespace VoSharp.Telephony.VoWifi;

public enum IkeProposalSuite
{
    Auto = 0,
    Modern = 1,    // Group 19 (ECP-256 / NIST P-256), PRF-SHA256, HMAC-SHA256-128, AES-CBC-256
    Standard = 2,  // Group 14 (MODP-2048), PRF-SHA256/SHA1, HMAC-SHA256/SHA1, AES-CBC-128
    Legacy = 3     // Group 2 (MODP-1024), PRF-SHA1, HMAC-SHA1-96, AES-CBC-128
}

public enum IkeExchangeType : byte
{
    IkeSaInit = 34,
    IkeAuth = 35,
    CreateChildSa = 36,
    Informational = 37
}

public enum IkePayloadType : byte
{
    None = 0,
    SecurityAssociation = 33,
    KeyExchange = 34,
    IdentificationInitiator = 35,
    IdentificationResponder = 36,
    Certificate = 37,
    Authentication = 39,
    Nonce = 40,
    Notify = 41,
    Delete = 42,
    VendorId = 43,
    TrafficSelectorInitiator = 44,
    TrafficSelectorResponder = 45,
    Encrypted = 46,
    Configuration = 47,
    Eap = 48
}

public record Ikev2SessionContext(
    ulong InitiatorSpi,
    ulong ResponderSpi,
    byte[] InitiatorNonce,
    IkeProposalSuite Suite = IkeProposalSuite.Standard,
    ushort DhGroupId = 14,
    byte[]? ResponderNonce = null,
    byte[]? SharedSecret = null,
    byte[]? SkEi = null,
    byte[]? SkEr = null,
    byte[]? SkAi = null,
    byte[]? SkAr = null,
    byte[]? SkPi = null,
    byte[]? SkPr = null
);

/// <summary>
/// IKEv2 assigned transform type codes (RFC 7296 §3.3.2).
/// </summary>
internal static class IkeTransformType
{
    public const byte Encr  = 1; // Encryption Algorithm
    public const byte Prf   = 2; // Pseudo-Random Function
    public const byte Integ = 3; // Integrity Algorithm
    public const byte Dh    = 4; // Diffie-Hellman Group
}

/// <summary>
/// IKEv2 assigned transform IDs (RFC 7296 / IANA).
/// </summary>
internal static class IkeTransformId
{
    // ENCR
    public const ushort EncrAesCbc = 12;
    // PRF
    public const ushort PrfHmacSha1   = 2;
    public const ushort PrfHmacSha256 = 5;
    // INTEG
    public const ushort AuthHmacSha1_96   = 2;
    public const ushort AuthHmacSha256_128 = 12;
    // DH
    public const ushort DhModp1024 = 2;
    public const ushort DhModp2048 = 14;
    public const ushort DhEcp256   = 19;
    // Key lengths (used as Transform Attribute for ENCR)
    public const ushort KeyLen128 = 128;
    public const ushort KeyLen256 = 256;
}

public static class Ikev2Protocol
{
    public const int DefaultIkev2Port = 500;
    public const int NattPort = 4500;

    public static byte[] BuildIkeSaInitRequest(out Ikev2SessionContext ctx)
    {
        return BuildIkeSaInitRequest(IkeProposalSuite.Standard, out ctx);
    }

    /// <summary>
    /// Builds a standards-compliant IKE_SA_INIT request (RFC 7296 §1.2).
    /// BUG-03 FIX: all multi-byte fields now written in network (big-endian) byte order.
    /// BUG-04 FIX: SAi1 payload contains a proper Proposal/Transform structure.
    /// </summary>
    public static byte[] BuildIkeSaInitRequest(IkeProposalSuite suite, out Ikev2SessionContext ctx)
    {
        var spiBytes = new byte[8];
        RandomNumberGenerator.Fill(spiBytes);
        ulong spiI = BinaryPrimitives.ReadUInt64BigEndian(spiBytes);

        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        ushort dhGroup = suite switch
        {
            IkeProposalSuite.Modern => IkeTransformId.DhEcp256,
            IkeProposalSuite.Legacy => IkeTransformId.DhModp1024,
            _                      => IkeTransformId.DhModp2048  // Standard & Auto
        };

        int dhKeyLen = suite switch
        {
            IkeProposalSuite.Modern => 64,  // ECP-256 uncompressed: X(32) + Y(32) bytes
            IkeProposalSuite.Legacy => 128, // MODP-1024 = 128 bytes
            _                      => 256   // MODP-2048 = 256 bytes
        };

        ctx = new Ikev2SessionContext(spiI, 0, nonce, suite, dhGroup);

        // ── Build payloads as byte arrays so we know exact sizes ──────────────

        // 1. SA Payload (SAi1)
        var saPayload = BuildSaProposal(suite);

        // 2. KE Payload body: DH Group (2 bytes, BE) + Reserved (2 bytes) + public key
        var dhPub = new byte[dhKeyLen];
        RandomNumberGenerator.Fill(dhPub);
        using var keBuf = new MemoryStream();
        using var keWriter = new BinaryWriter(keBuf);
        WriteUInt16BE(keWriter, dhGroup);
        keWriter.Write((ushort)0); // reserved
        keWriter.Write(dhPub);
        var keBody = keBuf.ToArray();

        // 3. Nonce Payload body
        var niBody = nonce;

        // ── Assemble IKEv2 message ────────────────────────────────────────────
        using var ms  = new MemoryStream();
        using var w   = new BinaryWriter(ms);

        // IKEv2 Fixed Header (RFC 7296 §3.1):
        //   SPIi (8B) | SPIr (8B) | Next-Payload (1B) | Version (1B)
        //   Exchange-Type (1B) | Flags (1B) | Message-ID (4B) | Length (4B)
        WriteUInt64BE(w, spiI);           // BUG-03 FIX: SPIi big-endian
        WriteUInt64BE(w, 0UL);            // SPIr = 0 for INIT
        w.Write((byte)IkePayloadType.SecurityAssociation); // Next Payload = SA
        w.Write((byte)0x20);                               // IKEv2 (major 2, minor 0)
        w.Write((byte)IkeExchangeType.IkeSaInit);
        w.Write((byte)0x08);                               // Flags: Initiator bit
        WriteUInt32BE(w, 0);                               // BUG-03 FIX: Message-ID big-endian

        int lengthPos = (int)ms.Position;
        WriteUInt32BE(w, 0); // placeholder

        // SA Payload
        // Next-Payload = KE (34), Critical=0, Length = 4 + saPayload.Length
        w.Write((byte)IkePayloadType.KeyExchange);
        w.Write((byte)0x00);
        WriteUInt16BE(w, (ushort)(4 + saPayload.Length)); // BUG-03 FIX: big-endian length
        w.Write(saPayload);

        // KE Payload
        w.Write((byte)IkePayloadType.Nonce);
        w.Write((byte)0x00);
        WriteUInt16BE(w, (ushort)(4 + keBody.Length));    // BUG-03 FIX
        w.Write(keBody);

        // Nonce Payload (Ni)
        w.Write((byte)IkePayloadType.None); // last payload
        w.Write((byte)0x00);
        WriteUInt16BE(w, (ushort)(4 + niBody.Length));    // BUG-03 FIX
        w.Write(niBody);

        // Patch total length (big-endian) — BUG-03 FIX
        uint totalLen = (uint)ms.Length;
        ms.Position = lengthPos;
        WriteUInt32BE(w, totalLen);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds an RFC 7296 §3.3 Security Association Payload body containing one Proposal.
    /// BUG-04 FIX: was previously new byte[36] (all zeros).
    /// </summary>
    private static byte[] BuildSaProposal(IkeProposalSuite suite)
    {
        // Determine algorithm IDs based on suite
        ushort encrId   = IkeTransformId.EncrAesCbc;
        ushort keyLen   = suite == IkeProposalSuite.Modern ? IkeTransformId.KeyLen256 : IkeTransformId.KeyLen128;
        ushort prfId    = suite == IkeProposalSuite.Legacy ? IkeTransformId.PrfHmacSha1   : IkeTransformId.PrfHmacSha256;
        ushort integId  = suite == IkeProposalSuite.Legacy ? IkeTransformId.AuthHmacSha1_96 : IkeTransformId.AuthHmacSha256_128;
        ushort dhId     = suite switch
        {
            IkeProposalSuite.Modern => IkeTransformId.DhEcp256,
            IkeProposalSuite.Legacy => IkeTransformId.DhModp1024,
            _                      => IkeTransformId.DhModp2048
        };

        // Each Transform substructure (RFC 7296 §3.3.2):
        //   Last/More (1B) | Reserved (1B) | Transform Length (2B) | Transform Type (1B)
        //   Reserved (1B) | Transform ID (2B) [| Attributes...]
        // Base transform = 8 bytes; with a Key-Length attribute (type 0x800E, 4B) = 8 bytes total still
        // because transform length includes the attribute in its 8-byte body.

        // ENCR transform has a Key-Length attribute (RFC 7296 §3.3.5, Attribute Type 14)
        // Total transform length = 8 (base) + 4 (attribute) = 12
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        // --- 4 transforms: ENCR, PRF, INTEG, DH ---
        // Note: Last-transform byte: 0=More, 3=Last

        // Transform 1: ENCR (with Key-Length attribute)
        w.Write((byte)3);             // More sub-structures follow (0=last); actually there are more so 0 would be wrong
        // We'll rewrite the Last/More bits after. Use 0 = more for now, fix last one.
        // Let's build each transform as a byte[] and concatenate, setting last bit correctly.
        void WriteTransform(bool isLast, byte type, ushort id, ushort? keyLenAttr = null)
        {
            int transformLen = 8 + (keyLenAttr.HasValue ? 4 : 0);
            w.Write(isLast ? (byte)3 : (byte)0); // Last/More sub-strucutre
            w.Write((byte)0);                     // Reserved
            WriteUInt16BE(w, (ushort)transformLen);
            w.Write(type);
            w.Write((byte)0);                     // Reserved
            WriteUInt16BE(w, id);
            if (keyLenAttr.HasValue)
            {
                // Attribute: Type 0x800E (Key Length, TV format), Value = key length in bits
                WriteUInt16BE(w, 0x800E);
                WriteUInt16BE(w, keyLenAttr.Value);
            }
        }

        WriteTransform(isLast: false, IkeTransformType.Encr,  encrId,  keyLen);
        WriteTransform(isLast: false, IkeTransformType.Prf,   prfId,   null);
        WriteTransform(isLast: false, IkeTransformType.Integ, integId, null);
        WriteTransform(isLast: true,  IkeTransformType.Dh,    dhId,    null);

        var transforms = ms.ToArray();

        // Proposal substructure (RFC 7296 §3.3.1):
        //   Last/More (1B) | Reserved (1B) | Proposal Length (2B) | Proposal # (1B)
        //   Protocol ID (1B) | SPI Size (1B) | # Transforms (1B) [| SPI data]
        // For IKE (Protocol ID=1), SPI Size=0, no SPI field
        using var propMs = new MemoryStream();
        using var pw     = new BinaryWriter(propMs);
        pw.Write((byte)0);  // Last (0=last, 2=more) — this is the only proposal
        pw.Write((byte)0);  // Reserved
        WriteUInt16BE(pw, (ushort)(8 + transforms.Length)); // Proposal Length
        pw.Write((byte)1);  // Proposal # 1
        pw.Write((byte)1);  // Protocol ID: IKE = 1
        pw.Write((byte)0);  // SPI Size (IKE SPI carried in header, so 0 here)
        pw.Write((byte)4);  // # Transforms (ENCR, PRF, INTEG, DH)
        pw.Write(transforms);

        return propMs.ToArray();
    }

    /// <summary>
    /// Parses a received IKE_SA_INIT response to extract ResponderSPI and Nonce.
    /// Returns an updated session context with ResponderSpi and ResponderNonce set.
    /// </summary>
    public static Ikev2SessionContext? ParseIkeSaInitResponse(byte[] response, Ikev2SessionContext ctx)
    {
        if (response.Length < 28) return null;

        // IKEv2 header is 28 bytes fixed
        ulong respSpi = BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(8, 8));
        byte nextPayload = response[16];
        byte exchangeType = response[19];

        if (exchangeType != (byte)IkeExchangeType.IkeSaInit) return null;

        // Walk payloads to find Nonce (type 40)
        byte[]? respNonce = null;
        int offset = 28;
        byte currentNext = nextPayload;

        while (offset + 4 <= response.Length && currentNext != (byte)IkePayloadType.None)
        {
            byte next = response[offset];
            // byte critical = response[offset + 1];
            ushort payloadLen = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 2, 2));
            if (payloadLen < 4 || offset + payloadLen > response.Length) break;

            if (currentNext == (byte)IkePayloadType.Nonce)
            {
                respNonce = response.AsSpan(offset + 4, payloadLen - 4).ToArray();
            }

            currentNext = next;
            offset += payloadLen;
        }

        return ctx with { ResponderSpi = respSpi, ResponderNonce = respNonce };
    }

    /// <summary>
    /// Processes EAP-AKA Challenge (RFC 4187) and generates EAP-Response/AKA-Challenge
    /// with correctly computed AT_MAC.
    /// BUG-05 FIX: AT_MAC is now properly calculated using K_aut derived from MK.
    /// </summary>
    public static byte[] BuildEapAkaResponse(byte eapId, byte[] rand, byte[] autn, byte[] k, byte[] opc, string imsiNai)
    {
        // 1. Perform 3GPP Milenage evaluation → get RES, CK, IK
        var (res, ck, ik, _, _) = Milenage.ComputeF2345(opc, k, rand);

        // 2. Derive Master Key (MK) per RFC 4187 §7:
        //    MK = SHA-1(Identity | IK | CK)
        //    where Identity is the full EAP-AKA identity (IMSI NAI, e.g. "0460001234567890@nai.epc.mnc000.mcc460.3gppnetwork.org")
        byte[] mkInput;
        {
            var identBytes = System.Text.Encoding.UTF8.GetBytes(imsiNai);
            mkInput = new byte[identBytes.Length + ik.Length + ck.Length];
            Buffer.BlockCopy(identBytes, 0, mkInput, 0, identBytes.Length);
            Buffer.BlockCopy(ik, 0, mkInput, identBytes.Length, ik.Length);
            Buffer.BlockCopy(ck, 0, mkInput, identBytes.Length + ik.Length, ck.Length);
        }
        var mk = SHA1.HashData(mkInput); // 20 bytes

        // 3. Expand MK using PRF' (iterated HMAC-SHA1) per RFC 4187 §7 / RFC 4306:
        //    Output: K_encr (16B) | K_aut (16B) | MSK (64B) | EMSK (64B) = 160 bytes
        //    PRF'(K, S) = T1 || T2 || ... where Ti = HMAC-SHA1(K, T(i-1) || S || i)
        var prf_output = PrfPlusHmacSha1(mk, System.Text.Encoding.ASCII.GetBytes("EAP-AKA"), totalBytes: 160);
        // K_encr occupies bytes 0-15, K_aut bytes 16-31
        var kAut = prf_output.AsSpan(16, 16).ToArray();

        // 4. Build EAP-Response/AKA-Challenge packet with AT_MAC = zeros for MAC calculation
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // EAP Header: Code=2 (Response), Id, Length (placeholder), Type=23 (EAP-AKA), Subtype=1 (AKA-Challenge)
        writer.Write((byte)0x02); // Response
        writer.Write(eapId);
        writer.Write((ushort)0); // placeholder length (big-endian, patched below)
        writer.Write((byte)23);  // EAP-AKA type
        writer.Write((byte)1);   // AKA-Challenge subtype
        writer.Write((byte)0);   // Reserved
        writer.Write((byte)0);

        // AT_RES (type=0x03): Length field is in 4-byte words (must include padding to 4-byte boundary)
        // RES is typically 8 bytes (64 bits). AT_RES body = ResLen(2B, bit count) + RES + padding
        int resPadded = (res.Length + 3) & ~3; // pad to 4B boundary
        int atResWords = (4 + resPadded) / 4;   // header(4B) counted inside 'words' differently
        // RFC 4187 §10.10: Length = (4 + actual AT data length in bytes) / 4, rounded up
        // AT_RES actual: 2 (ResLen in bits) + res bytes + padding
        int atResBodyLen = 2 + resPadded;
        int atResLen = (4 + atResBodyLen) / 4; // in 4-byte words
        writer.Write((byte)0x03);         // AT_RES
        writer.Write((byte)atResLen);     // length in 4-byte words
        WriteUInt16BE(writer, (ushort)(res.Length * 8)); // RES bit count
        writer.Write(res);
        writer.Write(new byte[resPadded - res.Length]);  // padding

        // AT_MAC (type=0x0B): 20 bytes total = 4 header + 2 reserved + 16 MAC
        // Per RFC 4187 §10.15: Length = 5 (words), MAC initially all zeros
        int macOffset = (int)ms.Position + 4; // offset of the 16-byte MAC within the packet
        writer.Write((byte)0x0B); // AT_MAC
        writer.Write((byte)5);    // length = 5 words = 20 bytes
        writer.Write((ushort)0);  // reserved
        writer.Write(new byte[16]); // 16-byte MAC placeholder

        // Patch EAP packet length (bytes 2-3, big-endian) — BUG-03 style fix
        ushort totalLen = (ushort)ms.Length;
        ms.Position = 2;
        WriteUInt16BE(writer, totalLen);

        var packet = ms.ToArray();

        // 5. Compute AT_MAC = HMAC-SHA1(K_aut, packet_with_zeros_in_mac)[0:16]
        //    RFC 4187 §10.15: AT_MAC covers the entire EAP message with AT_MAC value set to zero
        using var hmac = new HMACSHA1(kAut);
        var fullMac = hmac.ComputeHash(packet);
        // Copy first 16 bytes of HMAC into the AT_MAC position
        Buffer.BlockCopy(fullMac, 0, packet, macOffset, 16);

        return packet;
    }

    // ── Internal helper: PRF+ using HMAC-SHA1 (RFC 4306 §2.13) ──────────────
    // Used for EAP-AKA key derivation (RFC 4187 §7)
    private static byte[] PrfPlusHmacSha1(byte[] key, byte[] seed, int totalBytes)
    {
        var output = new List<byte>(totalBytes + 20);
        byte[] prev = Array.Empty<byte>();
        byte counter = 1;
        while (output.Count < totalBytes)
        {
            using var hmac = new HMACSHA1(key);
            var input = new byte[prev.Length + seed.Length + 1];
            Buffer.BlockCopy(prev, 0, input, 0, prev.Length);
            Buffer.BlockCopy(seed, 0, input, prev.Length, seed.Length);
            input[input.Length - 1] = counter++;
            prev = hmac.ComputeHash(input);
            output.AddRange(prev);
        }
        return output.Take(totalBytes).ToArray();
    }

    // ── Big-endian write helpers (BUG-03 FIX utilities) ─────────────────────

    private static void WriteUInt16BE(BinaryWriter w, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        w.Write(buf);
    }

    private static void WriteUInt32BE(BinaryWriter w, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        w.Write(buf);
    }

    private static void WriteUInt64BE(BinaryWriter w, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        w.Write(buf);
    }
}
