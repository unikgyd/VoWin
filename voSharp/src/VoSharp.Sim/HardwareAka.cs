using VoSharp.Common.Aka;

namespace VoSharp.Sim;

public record HardwareAkaResult(
    bool Success,
    bool SyncFailure,
    byte[]? Res,
    byte[]? Ck,
    byte[]? Ik,
    byte[]? Auts,
    string? ErrorMessage
)
{
    /// <summary>Projects this codec result onto the common AKA provider contract.</summary>
    public AkaResult ToAkaResult()
    {
        if (Success) return AkaResult.Succeeded(Res!, Ck!, Ik!);
        if (SyncFailure) return AkaResult.SyncFailed(Auts!);
        return AkaResult.Failed(ErrorMessage ?? "USIM AUTHENTICATE failed.");
    }
}

/// <summary>
/// 3GPP TS 31.102 §7.1.2 USIM / ISIM AUTHENTICATE command and response codec.
/// Transport-agnostic so it can be exercised offline by the modem and PC/SC providers alike.
/// </summary>
public static class HardwareAka
{
    public const byte InsAuthenticate = 0x88;
    public const byte InsGetResponse = 0xC0;

    /// <summary>Synchronisation-failure AUTS is always 14 bytes.</summary>
    public const int AutsLength = 14;

    private const byte TagSuccess = 0xDB;
    private const byte TagSyncFailure = 0xDC;

    /// <summary>Status word returned when the AKA MAC check fails.</summary>
    public const ushort SwMacFailure = 0x9862;

    /// <summary>Maximum GET RESPONSE round trips before giving up on a chained response.</summary>
    public const int MaxChainedExchanges = 4;

    /// <summary>
    /// Builds the USIM AUTHENTICATE APDU:
    /// <c>00 88 00 81 22 10&lt;RAND 16B&gt; 10&lt;AUTN 16B&gt;</c>.
    /// Callers using a logical channel must overwrite byte 0 with the channel number.
    /// </summary>
    public static byte[] BuildAuthenticateApdu(byte[] rand, byte[] autn, bool isContext3G = true)
    {
        if (rand.Length != 16 || autn.Length != 16)
            throw new ArgumentException("RAND and AUTN must be 16 bytes each.");

        var apdu = new byte[5 + 34];
        apdu[0] = 0x00;                                     // CLA
        apdu[1] = InsAuthenticate;                          // INS
        apdu[2] = 0x00;                                     // P1
        apdu[3] = (byte)(isContext3G ? 0x81 : 0x80);        // P2: 3G context (0x80 = GSM)
        apdu[4] = 0x22;                                     // Lc: 34

        apdu[5] = 0x10;                                     // RAND tag length
        Buffer.BlockCopy(rand, 0, apdu, 6, 16);

        apdu[22] = 0x10;                                    // AUTN tag length
        Buffer.BlockCopy(autn, 0, apdu, 23, 16);

        return apdu;
    }

    /// <summary>
    /// Sends an APDU and follows ISO 7816-4 GET RESPONSE chaining, concatenating the bodies and
    /// appending the final status word.
    /// </summary>
    /// <param name="transmit">Sends one APDU and returns the raw bytes (body + SW1 SW2).</param>
    /// <param name="cla">CLA byte to use for the GET RESPONSE commands.</param>
    public static async Task<byte[]?> TransmitWithChainingAsync(
        Func<byte[], CancellationToken, Task<byte[]?>> transmit,
        byte[] apdu,
        byte cla,
        CancellationToken ct = default)
    {
        var collected = new List<byte>(256);
        var current = apdu;

        for (var exchange = 0; exchange < MaxChainedExchanges; exchange++)
        {
            var raw = await transmit(current, ct).ConfigureAwait(false);
            if (raw is null || raw.Length < 2) return null;

            var sw1 = raw[^2];
            var sw2 = raw[^1];

            // 9862 is terminal — stop rather than trying to drain a response that will not come.
            if (((ushort)((sw1 << 8) | sw2)) == SwMacFailure)
                return new byte[] { sw1, sw2 };

            collected.AddRange(raw.AsSpan(0, raw.Length - 2));

            // 61 xx / 9F xx: more data pending, ask for it with GET RESPONSE.
            if (sw1 == 0x61 || sw1 == 0x9F)
            {
                current = new byte[] { cla, InsGetResponse, 0x00, 0x00, sw2 };
                continue;
            }

            collected.Add(sw1);
            collected.Add(sw2);
            return collected.ToArray();
        }

        return null;
    }

    /// <summary>
    /// Synchronous variant of <see cref="TransmitWithChainingAsync"/>, for transports such as
    /// PC/SC whose send/receive is a blocking call.
    /// </summary>
    public static byte[]? TransmitWithChaining(
        Func<byte[], byte[]?> transmit, byte[] apdu, byte cla)
    {
        var collected = new List<byte>(256);
        var current = apdu;

        for (var exchange = 0; exchange < MaxChainedExchanges; exchange++)
        {
            var raw = transmit(current);
            if (raw is null || raw.Length < 2) return null;

            var sw1 = raw[^2];
            var sw2 = raw[^1];

            if (((ushort)((sw1 << 8) | sw2)) == SwMacFailure)
                return new byte[] { sw1, sw2 };

            collected.AddRange(raw.AsSpan(0, raw.Length - 2));

            if (sw1 == 0x61 || sw1 == 0x9F)
            {
                current = new byte[] { cla, InsGetResponse, 0x00, 0x00, sw2 };
                continue;
            }

            collected.Add(sw1);
            collected.Add(sw2);
            return collected.ToArray();
        }

        return null;
    }

    /// <summary>
    /// Parses a reassembled USIM AUTHENTICATE response (body followed by SW1 SW2).
    /// Tag DB = success (RES/CK/IK), tag DC = synchronisation failure (AUTS).
    /// </summary>
    public static HardwareAkaResult ParseAuthenticateResponse(byte[] rawResponse)
    {
        if (rawResponse.Length < 2)
            return new HardwareAkaResult(false, false, null, null, null, null, "Response too short");

        int len = rawResponse.Length;
        byte sw1 = rawResponse[len - 2];
        byte sw2 = rawResponse[len - 1];
        var sw = (ushort)((sw1 << 8) | sw2);

        if (sw == SwMacFailure)
        {
            return new HardwareAkaResult(false, false, null, null, null, null,
                "USIM rejected AUTHENTICATE with SW=9862 (MAC incorrect). " +
                "In practice this usually means the wrong card is selected — verify ICCID first.");
        }
        if (sw == 0x6982)
            return new HardwareAkaResult(false, false, null, null, null, null,
                "USIM security status not satisfied (SW=6982). CHV1/PIN may be required.");
        if (sw == 0x6A82)
            return new HardwareAkaResult(false, false, null, null, null, null,
                "USIM application not found (SW=6A82). Is this a 3G/4G USIM?");
        if (sw == 0x6E00)
            return new HardwareAkaResult(false, false, null, null, null, null,
                "USIM class not supported (SW=6E00).");
        if (sw1 != 0x90 && sw1 != 0x61)
            return new HardwareAkaResult(false, false, null, null, null, null,
                $"Card error SW1=0x{sw1:x2} SW2=0x{sw2:x2}");

        var data = rawResponse.AsSpan(0, len - 2);
        if (data.Length == 0)
            return new HardwareAkaResult(false, false, null, null, null, null, "Empty response data");

        return data[0] switch
        {
            TagSuccess => ParseSuccess(data),
            TagSyncFailure => ParseSyncFailure(data),
            _ => new HardwareAkaResult(false, false, null, null, null, null,
                $"Unknown response tag 0x{data[0]:x2}")
        };
    }

    /// <summary>
    /// Parses tag 0xDB: RES, CK and IK.
    /// </summary>
    /// <remarks>
    /// Two encodings occur in the field and stacks disagree on which one a real USIM emits:
    /// <list type="bullet">
    ///   <item><description>A — <c>DB | totalLen | RES(len) | CK(len) | IK(len)</c> (BER-TLV)</description></item>
    ///   <item><description>B — <c>DB |            RES(len) | CK(len) | IK(len)</c> (no outer length)</description></item>
    /// </list>
    /// Rather than guess, try A then B and accept whichever walks the TLV stream exactly to the end
    /// of the buffer <em>and</em> yields 16-byte CK/IK. Milenage fixes those lengths, so that
    /// invariant disambiguates the two encodings reliably.
    /// </remarks>
    private static HardwareAkaResult ParseSuccess(ReadOnlySpan<byte> data)
    {
        if (TryParseSuccess(data, outerLength: true, out var withOuter) ||
            TryParseSuccess(data, outerLength: false, out withOuter))
        {
            var (res, ck, ik) = withOuter!.Value;
            return new HardwareAkaResult(true, false, res, ck, ik, null, null);
        }

        return new HardwareAkaResult(false, false, null, null, null, null,
            "Malformed DB payload: no well-formed RES/CK/IK triple found");
    }

    private static bool TryParseSuccess(
        ReadOnlySpan<byte> data, bool outerLength, out (byte[] Res, byte[] Ck, byte[] Ik)? result)
    {
        result = null;
        var offset = outerLength ? 2 : 1;
        if (!HasPlausibleOuterLength(data, outerLength)) return false;

        if (!TryReadTlv(data, ref offset, out var res)) return false;
        if (!TryReadTlv(data, ref offset, out var ck)) return false;
        if (!TryReadTlv(data, ref offset, out var ik)) return false;

        // Optional Kc (GSM cipher key, 8 bytes) per 3GPP TS 31.102 §7.1.2.1
        if (offset < data.Length)
        {
            _ = TryReadTlv(data, ref offset, out _);
        }

        // The TLV stream must finish exactly at the end of the data.
        if (offset != data.Length) return false;
        if (res.Length is < 4 or > 16) return false;
        if (ck.Length != 16 || ik.Length != 16) return false;

        result = (res, ck, ik);
        return true;
    }

    /// <summary>Parses tag 0xDC: the 14-byte AUTS. Accepts the same two encodings.</summary>
    private static HardwareAkaResult ParseSyncFailure(ReadOnlySpan<byte> data)
    {
        foreach (var outerLength in new[] { true, false })
        {
            if (!HasPlausibleOuterLength(data, outerLength)) continue;

            var offset = outerLength ? 2 : 1;
            if (!TryReadTlv(data, ref offset, out var auts)) continue;
            if (offset != data.Length || auts.Length != AutsLength) continue;

            return new HardwareAkaResult(false, true, null, null, null, auts, "Synchronization failure");
        }

        return new HardwareAkaResult(false, false, null, null, null, null,
            $"Synchronization failure without a {AutsLength}-byte AUTS");
    }

    /// <summary>
    /// With an outer length present, the byte after the tag must equal the length of the TLV
    /// stream that follows. That single check is what separates encoding A from B.
    /// </summary>
    private static bool HasPlausibleOuterLength(ReadOnlySpan<byte> data, bool outerLength)
    {
        if (data.Length < 3) return false;
        if (!outerLength) return true;
        return data[1] == data.Length - 2;
    }

    private static bool TryReadTlv(ReadOnlySpan<byte> data, ref int offset, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (offset >= data.Length) return false;

        int length = data[offset++];
        if (length == 0 || offset + length > data.Length) return false;

        value = data.Slice(offset, length).ToArray();
        offset += length;
        return true;
    }
}
