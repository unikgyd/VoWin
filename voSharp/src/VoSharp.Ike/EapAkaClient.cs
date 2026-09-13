using System.Buffers.Binary;
using System.Numerics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using VoSharp.Common.Aka;

namespace VoSharp.Ike;

public static class EapCode
{
    public const byte Request = 1;
    public const byte Response = 2;
    public const byte Success = 3;
    public const byte Failure = 4;
}

public static class EapType
{
    public const byte Identity = 1;
    public const byte Notification = 2;
    public const byte Nak = 3;
    public const byte Aka = 23;
    public const byte AkaPrime = 50;
}

public static class AkaSubtype
{
    public const byte Challenge = 1;
    public const byte AuthenticationReject = 2;
    public const byte SynchronizationFailure = 4;
    public const byte Identity = 5;
    public const byte Notification = 12;
    public const byte Reauthentication = 13;
    public const byte ClientError = 14;
}

public static class AkaAttributeType
{
    public const byte Rand = 1;
    public const byte Autn = 2;
    public const byte Res = 3;
    public const byte Auts = 4;
    public const byte PermanentIdReq = 10;
    public const byte Mac = 11;
    public const byte Notification = 12;
    public const byte AnyIdReq = 13;
    public const byte Identity = 14;
    public const byte FullAuthIdReq = 17;
    public const byte ClientError = 22;
    public const byte KdfInput = 23;
    public const byte Kdf = 24;
    public const byte ResultInd = 135;
}

public sealed record EapPacket(byte Code, byte Identifier, byte Type, byte[] Data);

public sealed record AkaAttribute(byte Type, byte[] Raw, int Offset);

public sealed record AkaDerivedKeys(byte[] KEncr, byte[] KAut, byte[] Msk, byte[] Emsk);

/// <summary>
/// Dual-mode EAP-AKA client supporting RFC 4187 (type 23) and RFC 5448 (type 50).
/// Integrates with <see cref="IAkaProvider"/> (e.g. EC25 / PC/SC).
/// </summary>
public sealed class EapAkaClient
{
    private readonly IAkaProvider _akaProvider;
    private readonly string _imsi;
    private readonly string _homeMnc;
    private readonly string _homeMcc;
    private readonly string? _expectedIccid;
    private readonly Action<string>? _diagnosticLog;

    public byte[] Identity { get; }
    public AkaDerivedKeys? Keys { get; private set; }
    public bool ChallengeComplete { get; private set; }
    public bool ResultIndication { get; private set; }
    public bool ProtectedSuccess { get; private set; }
    public string? LastAkaFailure { get; private set; }

    public EapAkaClient(
        IAkaProvider akaProvider,
        string imsi,
        string homeMcc,
        string homeMnc,
        string? expectedIccid = null,
        Action<string>? diagnosticLog = null)
    {
        _akaProvider = akaProvider ?? throw new ArgumentNullException(nameof(akaProvider));
        _imsi = imsi?.Trim() ?? throw new ArgumentNullException(nameof(imsi));
        _homeMcc = homeMcc?.Trim() ?? throw new ArgumentNullException(nameof(homeMcc));
        _homeMnc = homeMnc?.Trim() ?? throw new ArgumentNullException(nameof(homeMnc));
        _expectedIccid = expectedIccid;
        _diagnosticLog = diagnosticLog;

        Identity = BuildPermanentIdentity(_imsi, _homeMcc, _homeMnc);
    }

    /// <summary>
    /// Builds permanent 3GPP NAI identity:
    /// 0&lt;IMSI&gt;@nai.epc.mnc&lt;MNC3&gt;.mcc&lt;MCC&gt;.3gppnetwork.org
    /// </summary>
    public static byte[] BuildPermanentIdentity(string imsi, string mcc, string mnc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imsi);
        ArgumentException.ThrowIfNullOrWhiteSpace(mcc);
        ArgumentException.ThrowIfNullOrWhiteSpace(mnc);

        if (imsi.Length < 5 || imsi.Length > 16 || !imsi.All(char.IsAsciiDigit))
            throw new ArgumentException("Invalid IMSI for EAP-AKA.", nameof(imsi));

        if (mcc.Length != 3 || (mnc.Length != 2 && mnc.Length != 3))
            throw new ArgumentException("MCC must be 3 digits, MNC must be 2 or 3 digits.", nameof(mcc));

        var mnc3 = mnc.PadLeft(3, '0');
        var nai = $"0{imsi}@nai.epc.mnc{mnc3}.mcc{mcc}.3gppnetwork.org";
        return Encoding.UTF8.GetBytes(nai);
    }

    /// <summary>
    /// Processes an incoming EAP packet from ePDG and produces an EAP response or indicates success.
    /// </summary>
    public async Task<(byte[]? Response, bool IsSuccess)> HandleAsync(
        byte[] encodedPacket,
        CancellationToken ct = default)
    {
        var packet = ParseEapPacket(encodedPacket);
        Trace($"inbound code={DescribeCode(packet.Code)}; id={packet.Identifier}; type={DescribeType(packet.Type)}; data-bytes={packet.Data.Length}.");

        switch (packet.Code)
        {
            case EapCode.Failure:
                var stage = ChallengeComplete ? "after AKA challenge response" : "before AKA challenge response";
                Trace($"server rejected authentication {stage}.");
                throw new AuthenticationException($"EAP-AKA authentication rejected by ePDG ({stage}).");

            case EapCode.Success:
                if (!ChallengeComplete)
                    throw new AuthenticationException("EAP Success received before authenticated AKA challenge.");
                if (ResultIndication && !ProtectedSuccess)
                    throw new AuthenticationException("Unprotected EAP Success received after AT_RESULT_IND.");
                Trace($"server returned EAP Success; challenge-complete={ChallengeComplete}; protected-success={ProtectedSuccess}.");
                return (null, true);

            case EapCode.Request:
                break;

            default:
                throw new AuthenticationException($"Unexpected EAP code {packet.Code} from ePDG.");
        }

        switch (packet.Type)
        {
            case EapType.Identity:
                Trace("responding with permanent 3GPP NAI identity (identity value redacted).");
                var idResp = MarshalEapPacket(new EapPacket(
                    Code: EapCode.Response,
                    Identifier: packet.Identifier,
                    Type: EapType.Identity,
                    Data: Identity));
                return (idResp, false);

            case EapType.Aka:
            case EapType.AkaPrime:
                var akaResp = await HandleAkaRequestAsync(packet, ct).ConfigureAwait(false);
                return (akaResp, false);

            default:
                throw new AuthenticationException($"ePDG requested unsupported EAP type: {packet.Type}.");
        }
    }

    private async Task<byte[]> HandleAkaRequestAsync(EapPacket packet, CancellationToken ct)
    {
        if (packet.Data.Length < 3 || packet.Data[1] != 0 || packet.Data[2] != 0)
            return ClientErrorResponse(packet.Identifier, packet.Type);

        var subtype = packet.Data[0];
        var attributes = ParseAkaAttributes(packet.Data.AsSpan(3));
        Trace($"request subtype={DescribeSubtype(subtype)}; attributes=[{string.Join(',', attributes.Select(attribute => DescribeAttribute(attribute.Type)))}].");

        switch (subtype)
        {
            case AkaSubtype.Identity:
                return RespondAkaIdentity(packet.Identifier, packet.Type, attributes);

            case AkaSubtype.Challenge:
                return await RespondAkaChallengeAsync(packet, attributes, ct).ConfigureAwait(false);

            case AkaSubtype.Notification:
                return RespondAkaNotification(packet, attributes);

            default:
                return ClientErrorResponse(packet.Identifier, packet.Type);
        }
    }

    private byte[] RespondAkaIdentity(byte identifier, byte eapType, List<AkaAttribute> attributes)
    {
        var identityAttr = MarshalAkaAttribute(AkaAttributeType.Identity,
            Combine(new byte[] { (byte)(Identity.Length >> 8), (byte)Identity.Length }, Identity));

        var data = Combine(new byte[] { AkaSubtype.Identity, 0, 0 }, identityAttr);
        return MarshalEapPacket(new EapPacket(EapCode.Response, identifier, eapType, data));
    }

    private async Task<byte[]> RespondAkaChallengeAsync(
        EapPacket request,
        List<AkaAttribute> attributes,
        CancellationToken ct)
    {
        var randAttr = attributes.FirstOrDefault(a => a.Type == AkaAttributeType.Rand)
            ?? throw new AuthenticationException("EAP-AKA challenge missing AT_RAND.");
        var autnAttr = attributes.FirstOrDefault(a => a.Type == AkaAttributeType.Autn)
            ?? throw new AuthenticationException("EAP-AKA challenge missing AT_AUTN.");
        var macAttr = attributes.FirstOrDefault(a => a.Type == AkaAttributeType.Mac)
            ?? throw new AuthenticationException("EAP-AKA challenge missing AT_MAC.");

        if (randAttr.Raw.Length != 20 || autnAttr.Raw.Length != 20 || macAttr.Raw.Length != 20)
            throw new AuthenticationException("EAP-AKA RAND, AUTN, or MAC has invalid length.");

        var rand = randAttr.Raw[4..20];
        var autn = autnAttr.Raw[4..20];
        var challenge = AkaChallenge.Create(rand, autn);

        Trace("AKA challenge complete; invoking USIM AUTHENTICATE (RAND/AUTN redacted).");
        var result = await _akaProvider.AuthenticateAsync(challenge, ct).ConfigureAwait(false);
        // AkaResult deliberately throws when AUTS is read outside a sync-failure
        // response.  Diagnostics must observe that contract rather than turning
        // a successful AKA exchange into a logging failure.
        var autsPresent = result.SynchronizationFailure && result.Auts is not null;
        // RES/CK/IK have the same guarded-access contract as AUTS: a failed
        // AKA result must be classified and answered, not accidentally
        // converted into "RES is unavailable" by diagnostic formatting.
        var resLength = result.Success ? result.Res?.Length ?? 0 : 0;
        var ckLength = result.Success ? result.Ck?.Length ?? 0 : 0;
        var ikLength = result.Success ? result.Ik?.Length ?? 0 : 0;
        Trace($"USIM AUTHENTICATE result: success={result.Success}; sync-failure={result.SynchronizationFailure}; res-bytes={resLength}; ck-bytes={ckLength}; ik-bytes={ikLength}; auts-present={autsPresent}; error={result.ErrorMessage ?? "none"}; transport-note={result.DiagnosticMessage ?? "none"}.");

        if (result.SynchronizationFailure)
        {
            LastAkaFailure = "USIM AUTHENTICATE reported synchronization failure; AUTS was returned to the ePDG.";
            var auts = result.Auts ?? throw new AuthenticationException("SIM reported sync failure without AUTS.");
            var autsAttr = MarshalAkaAttribute(AkaAttributeType.Auts, auts);
            var syncData = Combine(new byte[] { AkaSubtype.SynchronizationFailure, 0, 0 }, autsAttr);
            return MarshalEapPacket(new EapPacket(EapCode.Response, request.Identifier, request.Type, syncData));
        }

        if (!result.Success || result.Res == null || result.Ck == null || result.Ik == null)
        {
            LastAkaFailure = $"USIM AUTHENTICATE failed: {result.ErrorMessage ?? "no authentication vector was returned"}";
            Trace("USIM result cannot form EAP-AKA response; sending AuthenticationReject.");
            // Authentication reject
            var rejData = new byte[] { AkaSubtype.AuthenticationReject, 0, 0 };
            return MarshalEapPacket(new EapPacket(EapCode.Response, request.Identifier, request.Type, rejData));
        }

        var res = result.Res;
        var ck = result.Ck;
        var ik = result.Ik;
        LastAkaFailure = null;

        // Derive keys based on EAP type (type 23 vs type 50)
        var isPrime = request.Type == EapType.AkaPrime;
        byte[]? kdfInputName = null;
        if (isPrime)
        {
            var kdfAttr = attributes.FirstOrDefault(a => a.Type == AkaAttributeType.Kdf);
            if (kdfAttr != null && kdfAttr.Raw.Length >= 4)
            {
                var kdfVal = BinaryPrimitives.ReadUInt16BigEndian(kdfAttr.Raw.AsSpan(2, 2));
                if (kdfVal != 1)
                {
                    throw new AuthenticationException($"Unsupported EAP-AKA' KDF function {kdfVal}.");
                }
            }

            var kdfInputAttr = attributes.FirstOrDefault(a => a.Type == AkaAttributeType.KdfInput);
            if (kdfInputAttr != null && kdfInputAttr.Raw.Length >= 4)
            {
                var nameLen = BinaryPrimitives.ReadUInt16BigEndian(kdfInputAttr.Raw.AsSpan(2, 2));
                if (kdfInputAttr.Raw.Length >= 4 + nameLen)
                {
                    kdfInputName = kdfInputAttr.Raw.AsSpan(4, nameLen).ToArray();
                }
            }
        }

        var keys = isPrime
            ? DeriveAkaPrimeKeys(Identity, ik, ck, kdfInputName)
            : DeriveAkaKeys(Identity, ik, ck);

        // Verify server AT_MAC
        var requestBytes = MarshalEapPacket(request);
        var zeroed = (byte[])requestBytes.Clone();
        var macOffset = 4 + 1 + 3 + macAttr.Offset; // 4 header + 1 type + 3 subtype/reserved + offset
        for (var i = macOffset + 4; i < macOffset + 20; i++)
            zeroed[i] = 0;

        var expectedMac = ComputeMac(keys.KAut, zeroed, isPrime);
        var actualMac = macAttr.Raw.AsSpan(4, 16);
        var serverMacValid = CryptographicOperations.FixedTimeEquals(expectedMac, actualMac);
        Trace($"server AT_MAC verified={serverMacValid}.");
        if (!serverMacValid)
            throw new AuthenticationException("EAP-AKA server AT_MAC verification failed.");

        // Build Response: RES + optional RESULT_IND + AT_KDF (if prime) + AT_MAC
        var resVal = new byte[2 + res.Length];
        BinaryPrimitives.WriteUInt16BigEndian(resVal.AsSpan(0, 2), (ushort)(res.Length * 8));
        Buffer.BlockCopy(res, 0, resVal, 2, res.Length);
        var resAttr = MarshalAkaAttribute(AkaAttributeType.Res, resVal);

        var responsePayload = new List<byte> { AkaSubtype.Challenge, 0, 0 };
        responsePayload.AddRange(resAttr);

        if (isPrime)
        {
            responsePayload.AddRange(MarshalAkaAttribute(AkaAttributeType.Kdf, new byte[] { 0, 1 }));
        }

        var hasResultInd = attributes.Any(a => a.Type == AkaAttributeType.ResultInd);
        if (hasResultInd)
        {
            responsePayload.AddRange(MarshalAkaAttribute(AkaAttributeType.ResultInd, Array.Empty<byte>()));
        }

        // Placeholder MAC (16 bytes of zeros inside attribute)
        var emptyMacAttr = MarshalAkaAttribute(AkaAttributeType.Mac, new byte[18]); // 2 bytes reserved + 16 bytes MAC
        responsePayload.AddRange(emptyMacAttr);

        var responsePacket = new EapPacket(EapCode.Response, request.Identifier, request.Type, responsePayload.ToArray());
        var responseBytes = MarshalEapPacket(responsePacket);

        // Compute MAC over responseBytes with zeroed MAC
        var responseAttrs = ParseAkaAttributes(responsePayload.ToArray().AsSpan(3));
        var respMacAttr = responseAttrs.First(a => a.Type == AkaAttributeType.Mac);
        var respMacOffset = 4 + 1 + 3 + respMacAttr.Offset;

        var computedMac = ComputeMac(keys.KAut, responseBytes, isPrime);
        Buffer.BlockCopy(computedMac, 0, responseBytes, respMacOffset + 4, 16);

        Keys = keys;
        ChallengeComplete = true;
        ResultIndication = hasResultInd;
        Trace($"AKA challenge response prepared; res-bytes={res.Length}; aka-prime={isPrime}; result-indication={hasResultInd}.");

        return responseBytes;
    }

    private void Trace(string message) => _diagnosticLog?.Invoke(message);

    private static string DescribeCode(byte code) => code switch
    {
        EapCode.Request => "Request",
        EapCode.Response => "Response",
        EapCode.Success => "Success",
        EapCode.Failure => "Failure",
        _ => $"Unknown({code})"
    };

    private static string DescribeType(byte type) => type switch
    {
        EapType.Identity => "Identity",
        EapType.Aka => "AKA",
        EapType.AkaPrime => "AKA-prime",
        _ => $"Unknown({type})"
    };

    private static string DescribeSubtype(byte subtype) => subtype switch
    {
        AkaSubtype.Identity => "Identity",
        AkaSubtype.Challenge => "Challenge",
        AkaSubtype.Notification => "Notification",
        AkaSubtype.SynchronizationFailure => "SynchronizationFailure",
        AkaSubtype.AuthenticationReject => "AuthenticationReject",
        _ => $"Unknown({subtype})"
    };

    private static string DescribeAttribute(byte type) => type switch
    {
        AkaAttributeType.Rand => "AT_RAND",
        AkaAttributeType.Autn => "AT_AUTN",
        AkaAttributeType.Res => "AT_RES",
        AkaAttributeType.Auts => "AT_AUTS",
        AkaAttributeType.Mac => "AT_MAC",
        AkaAttributeType.Identity => "AT_IDENTITY",
        AkaAttributeType.Notification => "AT_NOTIFICATION",
        AkaAttributeType.ResultInd => "AT_RESULT_IND",
        AkaAttributeType.Kdf => "AT_KDF",
        AkaAttributeType.KdfInput => "AT_KDF_INPUT",
        _ => $"AT_{type}"
    };

    private byte[] RespondAkaNotification(EapPacket request, List<AkaAttribute> attributes)
    {
        var notifAttr = attributes.FirstOrDefault(a => a.Type == AkaAttributeType.Notification);
        if (notifAttr == null || notifAttr.Raw.Length != 4)
            return ClientErrorResponse(request.Identifier, request.Type);

        var code = BinaryPrimitives.ReadUInt16BigEndian(notifAttr.Raw.AsSpan(2, 2));
        var isPrime = request.Type == EapType.AkaPrime;

        if (code == 32768) // Protected success notification
        {
            if (!ChallengeComplete || !ResultIndication || Keys == null)
                throw new AuthenticationException("Unexpected protected EAP success notification.");

            var macAttr = attributes.FirstOrDefault(a => a.Type == AkaAttributeType.Mac)
                ?? throw new AuthenticationException("Protected success notification missing AT_MAC.");

            var requestBytes = MarshalEapPacket(request);
            var zeroed = (byte[])requestBytes.Clone();
            var macOffset = 4 + 1 + 3 + macAttr.Offset;
            for (var i = macOffset + 4; i < macOffset + 20; i++)
                zeroed[i] = 0;

            var expectedMac = ComputeMac(Keys.KAut, zeroed, isPrime);
            if (!CryptographicOperations.FixedTimeEquals(expectedMac, macAttr.Raw.AsSpan(4, 16)))
                throw new AuthenticationException("Protected success notification AT_MAC is invalid.");

            // Acknowledge with empty notification response containing MAC
            var emptyMac = MarshalAkaAttribute(AkaAttributeType.Mac, new byte[18]);
            var respData = Combine(new byte[] { AkaSubtype.Notification, 0, 0 }, emptyMac);
            var responseBytes = MarshalEapPacket(new EapPacket(EapCode.Response, request.Identifier, request.Type, respData));

            var respAttrs = ParseAkaAttributes(respData.AsSpan(3));
            var respMac = respAttrs.First(a => a.Type == AkaAttributeType.Mac);
            var offset = 4 + 1 + 3 + respMac.Offset;
            Buffer.BlockCopy(ComputeMac(Keys.KAut, responseBytes, isPrime), 0, responseBytes, offset + 4, 16);

            ProtectedSuccess = true;
            return responseBytes;
        }

        // Other notification codes
        var data = new byte[] { AkaSubtype.Notification, 0, 0 };
        return MarshalEapPacket(new EapPacket(EapCode.Response, request.Identifier, request.Type, data));
    }

    private static byte[] ClientErrorResponse(byte identifier, byte eapType)
    {
        var errAttr = MarshalAkaAttribute(AkaAttributeType.ClientError, new byte[] { 0, 0 });
        var data = Combine(new byte[] { AkaSubtype.ClientError, 0, 0 }, errAttr);
        return MarshalEapPacket(new EapPacket(EapCode.Response, identifier, eapType, data));
    }

    private static byte[] ComputeMac(byte[] key, byte[] packet, bool isPrime)
    {
        var full = isPrime
            ? HMACSHA256.HashData(key, packet)
            : HMACSHA1.HashData(key, packet);

        var mac = new byte[16];
        Buffer.BlockCopy(full, 0, mac, 0, 16);
        return mac;
    }

    // ── RFC 4187 Key Derivation (FIPS 186-2 PRF) ─────────────────────────────

    public static AkaDerivedKeys DeriveAkaKeys(byte[] identity, byte[] ik, byte[] ck)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(ik);
        ArgumentNullException.ThrowIfNull(ck);

        if (ik.Length != 16 || ck.Length != 16)
            throw new ArgumentException("IK and CK must be exactly 16 bytes.");

        var material = new byte[identity.Length + 32];
        Buffer.BlockCopy(identity, 0, material, 0, identity.Length);
        Buffer.BlockCopy(ik, 0, material, identity.Length, 16);
        Buffer.BlockCopy(ck, 0, material, identity.Length + 16, 16);

        var mk = SHA1.HashData(material);
        var stream = Fips1862Prf(mk, 160);

        var kEncr = new byte[16];
        var kAut = new byte[16];
        var msk = new byte[64];
        var emsk = new byte[64];

        Buffer.BlockCopy(stream, 0, kEncr, 0, 16);
        Buffer.BlockCopy(stream, 16, kAut, 0, 16);
        Buffer.BlockCopy(stream, 32, msk, 0, 64);
        Buffer.BlockCopy(stream, 96, emsk, 0, 64);

        return new AkaDerivedKeys(kEncr, kAut, msk, emsk);
    }

    public static byte[] Fips1862Prf(byte[] seed, int length)
    {
        // BigInteger in C# is little-endian signed by default, so we ensure positive big-endian
        var unsignedSeed = new byte[seed.Length + 1];
        Buffer.BlockCopy(seed, 0, unsignedSeed, 1, seed.Length); // prepend 0 for positive
        Array.Reverse(unsignedSeed); // to little-endian for BigInteger constructor
        var xkey = new BigInteger(unsignedSeed);

        var modulus = BigInteger.One << 160;
        var result = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var xval = new byte[20];
            var keyBytes = xkey.ToByteArray(); // little-endian
            for (var i = 0; i < Math.Min(20, keyBytes.Length); i++)
            {
                xval[19 - i] = keyBytes[i];
            }

            var word = FipsSha1G(xval);
            var toCopy = Math.Min(20, length - offset);
            Buffer.BlockCopy(word, 0, result, offset, toCopy);
            offset += toCopy;

            var wordUnsigned = new byte[21];
            Buffer.BlockCopy(word, 0, wordUnsigned, 1, 20);
            Array.Reverse(wordUnsigned);
            var increment = new BigInteger(wordUnsigned);

            xkey = (xkey + increment + BigInteger.One) % modulus;
        }

        return result;
    }

    private static byte[] FipsSha1G(byte[] xval)
    {
        var block = new byte[64];
        Buffer.BlockCopy(xval, 0, block, 0, 20);

        var words = new uint[80];
        for (var i = 0; i < 16; i++)
            words[i] = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(i * 4, 4));

        for (var i = 16; i < 80; i++)
        {
            var val = words[i - 3] ^ words[i - 8] ^ words[i - 14] ^ words[i - 16];
            words[i] = (val << 1) | (val >> 31);
        }

        uint a = 0x67452301, b = 0xEFCDAB89, c = 0x98BADCFE, d = 0x10325476, e = 0xC3D2E1F0;
        uint initA = a, initB = b, initC = c, initD = d, initE = e;

        for (var i = 0; i < 80; i++)
        {
            uint f, k;
            if (i < 20)
            {
                f = (b & c) | (~b & d);
                k = 0x5A827999;
            }
            else if (i < 40)
            {
                f = b ^ c ^ d;
                k = 0x6ED9EBA1;
            }
            else if (i < 60)
            {
                f = (b & c) | (b & d) | (c & d);
                k = 0x8F1BBCDC;
            }
            else
            {
                f = b ^ c ^ d;
                k = 0xCA62C1D6;
            }

            var rotA = (a << 5) | (a >> 27);
            var temp = rotA + f + e + k + words[i];
            e = d;
            d = c;
            c = (b << 30) | (b >> 2);
            b = a;
            a = temp;
        }

        var result = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), initA + a);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), initB + b);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), initC + c);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12, 4), initD + d);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16, 4), initE + e);
        return result;
    }

    // ── RFC 5448 Key Derivation (EAP-AKA') ─────────────────────────────────────

    public static AkaDerivedKeys DeriveAkaPrimeKeys(byte[] identity, byte[] ik, byte[] ck, byte[]? networkName = null)
    {
        // RFC 5448 §3.2 / 3GPP TS 33.402: KDF for CK' and IK'
        // S = FC || NetworkName || L0 || Identity || L1
        var netBytes = networkName ?? Encoding.UTF8.GetBytes("WLAN");
        var ikPrime = new byte[16];
        var ckPrime = new byte[16];

        var ikck = Combine(ik, ck);
        var s = Combine(new byte[] { 0x20 }, netBytes, new byte[] { (byte)(netBytes.Length >> 8), (byte)(netBytes.Length & 0xFF) }, identity);
        var kdfOut = HMACSHA256.HashData(ikck, s);
        Buffer.BlockCopy(kdfOut, 0, ckPrime, 0, 16);
        Buffer.BlockCopy(kdfOut, 16, ikPrime, 0, 16);

        var mk = HMACSHA256.HashData(Combine(identity, ikPrime, ckPrime), Encoding.UTF8.GetBytes("EAP-AKA'"));
        // PRF' to generate 160 bytes: K_encr(16), K_aut(32->16), MSK(64), EMSK(64)
        var stream = PrfSha256(mk, Combine(Encoding.UTF8.GetBytes("EAP-AKA'"), identity), 160);

        var kEncr = new byte[16];
        var kAut = new byte[16];
        var msk = new byte[64];
        var emsk = new byte[64];

        Buffer.BlockCopy(stream, 0, kEncr, 0, 16);
        Buffer.BlockCopy(stream, 16, kAut, 0, 16);
        Buffer.BlockCopy(stream, 32, msk, 0, 64);
        Buffer.BlockCopy(stream, 96, emsk, 0, 64);

        return new AkaDerivedKeys(kEncr, kAut, msk, emsk);
    }

    private static byte[] PrfSha256(byte[] key, byte[] seed, int length)
    {
        var result = new byte[length];
        var offset = 0;
        byte[]? prev = null;
        for (byte counter = 1; offset < length; counter++)
        {
            var input = Combine(prev ?? Array.Empty<byte>(), seed, new[] { counter });
            var block = HMACSHA256.HashData(key, input);
            var toCopy = Math.Min(block.Length, length - offset);
            Buffer.BlockCopy(block, 0, result, offset, toCopy);
            offset += toCopy;
            prev = block;
        }
        return result;
    }

    // ── Packet & Attribute Codec ──────────────────────────────────────────────

    public static EapPacket ParseEapPacket(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 4)
            throw new IkeFormatException($"Truncated EAP header ({buffer.Length} bytes).");

        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        if (length != buffer.Length)
            throw new IkeFormatException($"EAP length {length} != buffer length {buffer.Length}.");

        var code = buffer[0];
        var id = buffer[1];

        if (code is EapCode.Success or EapCode.Failure)
        {
            if (buffer.Length != 4)
                throw new IkeFormatException("EAP success/failure cannot have body data.");
            return new EapPacket(code, id, 0, Array.Empty<byte>());
        }

        if (buffer.Length < 5)
            throw new IkeFormatException("Typed EAP packet is truncated.");

        var type = buffer[4];
        var data = buffer[5..].ToArray();
        return new EapPacket(code, id, type, data);
    }

    public static byte[] MarshalEapPacket(EapPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var length = 4;
        if (packet.Code is EapCode.Request or EapCode.Response)
        {
            length += 1 + packet.Data.Length;
        }

        var buffer = new byte[length];
        buffer[0] = packet.Code;
        buffer[1] = packet.Identifier;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)length);

        if (length > 4)
        {
            buffer[4] = packet.Type;
            Buffer.BlockCopy(packet.Data, 0, buffer, 5, packet.Data.Length);
        }

        return buffer;
    }

    public static List<AkaAttribute> ParseAkaAttributes(ReadOnlySpan<byte> buffer)
    {
        var attributes = new List<AkaAttribute>();
        var offset = 0;

        while (offset < buffer.Length)
        {
            if (offset + 2 > buffer.Length)
                throw new IkeFormatException("Truncated AKA attribute header.");

            var type = buffer[offset];
            var lenUnits = buffer[offset + 1];
            var totalLen = lenUnits * 4;

            if (totalLen < 4 || offset + totalLen > buffer.Length)
                throw new IkeFormatException($"Invalid AKA attribute length {totalLen} for type {type}.");

            var raw = buffer.Slice(offset, totalLen).ToArray();
            attributes.Add(new AkaAttribute(type, raw, offset));
            offset += totalLen;
        }

        return attributes;
    }

    public static byte[] MarshalAkaAttribute(byte type, byte[] value)
    {
        var rawLen = 2 + value.Length;
        var paddedLen = (rawLen + 3) & ~3; // round up to multiple of 4
        var lenUnits = paddedLen / 4;

        if (lenUnits > 255)
            throw new InvalidOperationException("AKA attribute exceeds maximum length.");

        var buffer = new byte[paddedLen];
        buffer[0] = type;
        buffer[1] = (byte)lenUnits;
        Buffer.BlockCopy(value, 0, buffer, 2, value.Length);
        return buffer;
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
        var res = new byte[total];
        var offset = 0;
        foreach (var arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, res, offset, arr.Length);
            offset += arr.Length;
        }
        return res;
    }
}
