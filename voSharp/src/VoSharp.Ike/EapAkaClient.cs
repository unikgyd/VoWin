using System.Buffers.Binary;
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

public sealed class EapMethodMismatchException : AuthenticationException
{
    public byte RequestedType { get; }
    internal bool IdentityOnlyExchange { get; }

    public EapMethodMismatchException(byte requestedType, byte selectedType)
        : this(requestedType, selectedType, false) { }

    internal EapMethodMismatchException(byte requestedType, byte selectedType, bool identityOnlyExchange)
        : base($"ePDG requested EAP type {requestedType}, but this IKE identity was configured for type {selectedType}.")
    {
        RequestedType = requestedType;
        IdentityOnlyExchange = identityOnlyExchange;
    }
}

/// <summary>
/// Dual-mode EAP-AKA client supporting RFC 4187 (type 23) and RFC 5448 (type 50).
/// Integrates with <see cref="IAkaProvider"/> (e.g. EC25 / PC/SC).
/// </summary>
public sealed class EapAkaClient : IDisposable
{
    private readonly IAkaProvider _akaProvider;
    private readonly string _imsi;
    private readonly string _homeMnc;
    private readonly string _homeMcc;
    private readonly string? _expectedIccid;
    private readonly Action<string>? _diagnosticLog;
    private readonly byte _selectedEapMethod;
    private readonly string? _expectedNetworkName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool[] _seenIds = new bool[256];
    private readonly HashSet<string> _usedVectors = [];
    private byte? _lastId;
    private byte[] _lastRequest = [], _lastResponse = [], _identityTranscript = [];
    private byte? _identityRank;
    private bool _checkcodeRequired, _failed, _succeeded, _terminalResponse, _disposed;
    private bool _identityOnlyExchange = true;
    private ushort[]? _pendingKdfs, _activeKdfs;

    public byte[] Identity { get; }
    public AkaDerivedKeys? Keys { get; private set; }
    public bool ChallengeComplete { get; private set; }
    public bool ResultIndication { get; private set; }
    public bool ProtectedSuccess { get; private set; }
    public string? LastAkaFailure { get; private set; }
    internal bool IdentityRejectedBeforeChallenge { get; private set; }

    public EapAkaClient(
        IAkaProvider akaProvider,
        string imsi,
        string homeMcc,
        string homeMnc,
        string? expectedIccid = null,
        Action<string>? diagnosticLog = null,
        byte selectedEapMethod = EapType.Aka,
        string? expectedNetworkName = null)
    {
        _akaProvider = akaProvider ?? throw new ArgumentNullException(nameof(akaProvider));
        _imsi = imsi?.Trim() ?? throw new ArgumentNullException(nameof(imsi));
        _homeMcc = homeMcc?.Trim() ?? throw new ArgumentNullException(nameof(homeMcc));
        _homeMnc = homeMnc?.Trim() ?? throw new ArgumentNullException(nameof(homeMnc));
        _expectedIccid = expectedIccid;
        _diagnosticLog = diagnosticLog;
        if (selectedEapMethod is not (EapType.Aka or EapType.AkaPrime))
            throw new ArgumentOutOfRangeException(nameof(selectedEapMethod));
        _selectedEapMethod = selectedEapMethod;
        if (expectedNetworkName != null && (expectedNetworkName.Length is 0 or > 1016 || expectedNetworkName.Any(char.IsControl)))
            throw new ArgumentException("Invalid expected AKA' network name.", nameof(expectedNetworkName));
        _expectedNetworkName = expectedNetworkName;

        Identity = BuildPermanentIdentity(_imsi, _homeMcc, _homeMnc, selectedEapMethod);
    }

    /// <summary>
    /// Builds permanent 3GPP NAI identity:
    /// 0&lt;IMSI&gt;@nai.epc.mnc&lt;MNC3&gt;.mcc&lt;MCC&gt;.3gppnetwork.org
    /// </summary>
    public static byte[] BuildPermanentIdentity(string imsi, string mcc, string mnc, byte eapMethod = EapType.Aka)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imsi);
        ArgumentException.ThrowIfNullOrWhiteSpace(mcc);
        ArgumentException.ThrowIfNullOrWhiteSpace(mnc);

        if (imsi.Length < 5 || imsi.Length > 16 || !imsi.All(char.IsAsciiDigit))
            throw new ArgumentException("Invalid IMSI for EAP-AKA.", nameof(imsi));

        if (mcc.Length != 3 || (mnc.Length != 2 && mnc.Length != 3) || !mcc.All(char.IsAsciiDigit) || !mnc.All(char.IsAsciiDigit))
            throw new ArgumentException("MCC must be 3 digits, MNC must be 2 or 3 digits.", nameof(mcc));
        if (eapMethod is not (EapType.Aka or EapType.AkaPrime))
            throw new ArgumentOutOfRangeException(nameof(eapMethod));

        var mnc3 = mnc.PadLeft(3, '0');
        var nai = $"{(eapMethod == EapType.AkaPrime ? '6' : '0')}{imsi}@nai.epc.mnc{mnc3}.mcc{mcc}.3gppnetwork.org";
        return Encoding.UTF8.GetBytes(nai);
    }

    /// <summary>
    /// Processes an incoming EAP packet from ePDG and produces an EAP response or indicates success.
    /// </summary>
    public async Task<(byte[]? Response, bool IsSuccess)> HandleAsync(
        byte[] encodedPacket,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failed) throw new AuthenticationException("EAP run is invalidated; establish a fresh session.");
            _failed = true; // cancellation during USIM AUTHENTICATE cannot retry it
            var packet = ParseEapPacket(encodedPacket);
            if (packet.Code == EapCode.Success && _lastId != packet.Identifier)
                throw new AuthenticationException("EAP Success identifier does not match the last request.");
            if (packet.Code == EapCode.Request)
            {
                if (_succeeded) throw new AuthenticationException("EAP run is already complete.");
                if (_lastId == packet.Identifier)
                {
                    if (!_lastRequest.AsSpan().SequenceEqual(encodedPacket))
                        throw new AuthenticationException("EAP identifier reused with different content.");
                    _failed = false;
                    return (_lastResponse.ToArray(), false);
                }
                if (_seenIds[packet.Identifier] || _seenIds.Count(value => value) >= 16 || _terminalResponse)
                    throw new AuthenticationException("EAP request is outside the current method state.");
            }
            var result = await HandleCoreAsync(encodedPacket, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (result.Response != null)
            {
                _seenIds[packet.Identifier] = true; _lastId = packet.Identifier;
                CryptographicOperations.ZeroMemory(_lastRequest); CryptographicOperations.ZeroMemory(_lastResponse);
                _lastRequest = encodedPacket.ToArray(); _lastResponse = result.Response.ToArray();
            }
            _succeeded = result.IsSuccess; _failed = false;
            return result;
        }
        catch { ClearKeys(); CryptographicOperations.ZeroMemory(_lastResponse); throw; }
        finally { _gate.Release(); }
    }

    private async Task<(byte[]? Response, bool IsSuccess)> HandleCoreAsync(byte[] encodedPacket, CancellationToken ct)
    {
        var packet = ParseEapPacket(encodedPacket);
        Trace($"inbound code={DescribeCode(packet.Code)}; id={packet.Identifier}; type={DescribeType(packet.Type)}; data-bytes={packet.Data.Length}.");

        switch (packet.Code)
        {
            case EapCode.Failure:
                var stage = ChallengeComplete ? "after AKA challenge response" : "before AKA challenge response";
                Trace($"server rejected authentication {stage}.");
                if (!string.IsNullOrWhiteSpace(LastAkaFailure))
                    throw new AuthenticationException(
                        $"EAP-AKA authentication rejected by ePDG {stage} after local failure: {LastAkaFailure}");
                var rejection = $"EAP-AKA authentication rejected by ePDG ({stage}).";
                IdentityRejectedBeforeChallenge = _identityOnlyExchange && !_terminalResponse;
                throw new AuthenticationException(rejection);

            case EapCode.Success:
                if (!ChallengeComplete || _terminalResponse)
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
                if (ChallengeComplete) throw new AuthenticationException("Identity request after AKA challenge.");
                Trace("responding with permanent 3GPP NAI identity (identity value redacted).");
                var idResp = MarshalEapPacket(new EapPacket(
                    Code: EapCode.Response,
                    Identifier: packet.Identifier,
                    Type: EapType.Identity,
                    Data: Identity));
                return (idResp, false);

            case EapType.Aka:
            case EapType.AkaPrime:
                if (packet.Type != _selectedEapMethod)
                    throw new EapMethodMismatchException(packet.Type, _selectedEapMethod, _identityOnlyExchange);
                var akaResp = await HandleAkaRequestAsync(packet, ct).ConfigureAwait(false);
                return (akaResp, false);

            default:
                throw new AuthenticationException($"ePDG requested unsupported EAP type: {packet.Type}.");
        }
    }

    private async Task<byte[]> HandleAkaRequestAsync(EapPacket packet, CancellationToken ct)
    {
        // A malformed challenge is still past the identity-only boundary,
        // even when no vector is consumed and a client-error is returned.
        _identityOnlyExchange &= packet.Data.Length >= 3 && packet.Data[0] == AkaSubtype.Identity;
        if (packet.Data.Length < 3)
            return ClientErrorResponse(packet.Identifier, packet.Type);

        var subtype = packet.Data[0];
        var attributes = ParseAkaAttributes(packet.Data.AsSpan(3));
        Trace($"request subtype={DescribeSubtype(subtype)}; attributes=[{string.Join(',', attributes.Select(attribute => DescribeAttribute(attribute.Type)))}].");

        switch (subtype)
        {
            case AkaSubtype.Identity:
                var identityResponse = RespondAkaIdentity(packet.Identifier, packet.Type, attributes);
                var transcript = Combine(_identityTranscript, MarshalEapPacket(packet), identityResponse);
                if (transcript.Length > 32768) throw new AuthenticationException("AKA identity transcript exceeds limit.");
                CryptographicOperations.ZeroMemory(_identityTranscript); _identityTranscript = transcript;
                return identityResponse;

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
        if (ChallengeComplete || _usedVectors.Count != 0) throw new AuthenticationException("Unexpected AKA identity round.");
        EapAkaValidation.Attributes(attributes, 10, 13, 17);
        var requests = attributes.Where(a => a.Type is 10 or 13 or 17).ToArray();
        if (requests.Length != 1) throw new AuthenticationException("AKA identity request must select one identity class.");
        byte rank = requests[0].Type switch { 13 => 0, 17 => 1, _ => 2 };
        if (_identityRank.HasValue && rank <= _identityRank.Value) throw new AuthenticationException("AKA identity request did not advance.");
        _identityRank = rank; _checkcodeRequired |= attributes.Any(a => a.Type is not (10 or 13 or 17));
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
        if (ChallengeComplete) throw new AuthenticationException("A new challenge cannot replace completed AKA keys.");
        EapAkaValidation.Attributes(attributes, 1, 2, 11, 23, 24, 129, 130, 134, 135, 136);
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
        var isPrime = request.Type == EapType.AkaPrime;
        EapAkaValidation.Checkcode(attributes, _identityTranscript, _checkcodeRequired, isPrime);
        byte[]? kdfInputName = null;
        if (isPrime)
        {
            if ((autn[6] & 0x80) == 0 || attributes.Any(a => a.Type == 136))
                throw new AuthenticationException("Invalid AKA' separation or bidding attribute.");
            var negotiation = NegotiateKdf(request.Identifier, attributes);
            if (negotiation != null) return negotiation;
            kdfInputName = EapAkaValidation.NetworkName(attributes.FirstOrDefault(a => a.Type == 23), _expectedNetworkName);
        }
        else
        {
            if (attributes.Any(a => a.Type is 23 or 24)) throw new AuthenticationException("AKA' KDF in legacy AKA request.");
            if (attributes.Any(a => a.Type == 136 && (a.Raw[2] & 0x80) != 0))
                throw new EapMethodMismatchException(EapType.AkaPrime, EapType.Aka);
        }
        var iv = attributes.FirstOrDefault(a => a.Type == 129);
        var encrypted = attributes.FirstOrDefault(a => a.Type == 130);
        if ((iv == null) != (encrypted == null) || encrypted != null && (encrypted.Raw.Length < 20 || (encrypted.Raw.Length - 4) % 16 != 0))
            throw new AuthenticationException("Invalid EAP-AKA encrypted attribute framing.");
        if (_usedVectors.Count >= 3 || !_usedVectors.Add(Convert.ToHexString(SHA256.HashData(Combine(rand, autn)))))
            throw new AuthenticationException("AKA vector was already used or resynchronization limit reached.");
        var challenge = AkaChallenge.Create(rand, autn);

        if (!await _akaProvider.CheckReadyAsync(_expectedIccid, ct).ConfigureAwait(false))
            throw new AuthenticationException("Selected SIM is no longer ready for AKA.");
        Trace("AKA challenge complete; invoking USIM AUTHENTICATE (RAND/AUTN redacted).");
        var result = await _akaProvider.AuthenticateAsync(challenge, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!await _akaProvider.CheckReadyAsync(_expectedIccid, ct).ConfigureAwait(false))
            throw new AuthenticationException("Selected SIM changed during AKA.");
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
            if (auts.Length != 14) throw new AuthenticationException("SIM returned invalid AUTS length.");
            var autsAttr = MarshalAkaAttribute(AkaAttributeType.Auts, auts);
            var syncData = Combine(new byte[] { AkaSubtype.SynchronizationFailure, 0, 0 }, autsAttr,
                isPrime ? attributes.Where(a => a.Type == 24).SelectMany(a => a.Raw).ToArray() : []);
            return MarshalEapPacket(new EapPacket(EapCode.Response, request.Identifier, request.Type, syncData));
        }

        if (!result.Success || result.Res == null || result.Ck == null || result.Ik == null)
        {
            LastAkaFailure = $"USIM AUTHENTICATE failed: {result.ErrorMessage ?? "no authentication vector was returned"}";
            _terminalResponse = true;
            Trace("USIM result cannot form EAP-AKA response; sending AuthenticationReject.");
            // Authentication reject
            var rejData = new byte[] { AkaSubtype.AuthenticationReject, 0, 0 };
            return MarshalEapPacket(new EapPacket(EapCode.Response, request.Identifier, request.Type, rejData));
        }

        var res = result.Res.ToArray();
        var ck = result.Ck.ToArray();
        var ik = result.Ik.ToArray();
        LastAkaFailure = null;
        AkaDerivedKeys? pendingKeys = null;
        try
        {
            if (res.Length is < 4 or > 16 || ck.Length != 16 || ik.Length != 16)
                throw new AuthenticationException("SIM returned invalid AKA key lengths.");
            var keys = isPrime
                ? DeriveAkaPrimeKeys(Identity, ik, ck, autn, kdfInputName!)
                : DeriveAkaKeys(Identity, ik, ck);
            pendingKeys = keys;

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
            EapAkaValidation.EncryptedAttributes(attributes, keys);

            // Normal challenge response: RES + optional RESULT_IND + AT_MAC.
            // AT_KDF alone is used only for negotiation, or copied on prime AUTS.
            var resVal = new byte[2 + res.Length];
            BinaryPrimitives.WriteUInt16BigEndian(resVal.AsSpan(0, 2), (ushort)(res.Length * 8));
            Buffer.BlockCopy(res, 0, resVal, 2, res.Length);
            var resAttr = MarshalAkaAttribute(AkaAttributeType.Res, resVal);

            var responsePayload = new List<byte> { AkaSubtype.Challenge, 0, 0 };
            responsePayload.AddRange(resAttr);

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
            pendingKeys = null;
            ChallengeComplete = true;
            ResultIndication = hasResultInd;
            Trace($"AKA challenge response prepared; res-bytes={res.Length}; aka-prime={isPrime}; result-indication={hasResultInd}.");

            return responseBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(res); CryptographicOperations.ZeroMemory(ck); CryptographicOperations.ZeroMemory(ik);
            Wipe(pendingKeys);
        }
    }

    private byte[]? NegotiateKdf(byte identifier, List<AkaAttribute> attributes)
    {
        var offered = attributes.Where(a => a.Type == 24).Select(a => BinaryPrimitives.ReadUInt16BigEndian(a.Raw.AsSpan(2, 2))).ToArray();
        if (_pendingKdfs != null)
        {
            if (!offered.SequenceEqual(new ushort[] { 1 }.Concat(_pendingKdfs)))
                throw new AuthenticationException("AKA' KDF negotiation changed the original offer.");
            _pendingKdfs = null; _activeKdfs = offered; return null;
        }
        if (_activeKdfs != null)
        {
            if (!offered.SequenceEqual(_activeKdfs)) throw new AuthenticationException("AKA' KDF changed during authentication.");
            return null;
        }
        if (offered.Length == 0 || offered.Distinct().Count() != offered.Length || !offered.Contains((ushort)1))
            throw new AuthenticationException("AKA' has no valid supported KDF offer.");
        if (offered[0] == 1) { _activeKdfs = offered; return null; }
        _pendingKdfs = offered;
        return MarshalEapPacket(new(EapCode.Response, identifier, EapType.AkaPrime,
            Combine([AkaSubtype.Challenge, 0, 0], MarshalAkaAttribute(24, [0, 1]))));
    }

    private void Trace(string message) { try { _diagnosticLog?.Invoke(message); } catch { } }

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
        EapAkaValidation.Attributes(attributes, 12, 11);
        var notification = attributes.FirstOrDefault(a => a.Type == 12)
            ?? throw new AuthenticationException("Missing AT_NOTIFICATION.");
        var code = BinaryPrimitives.ReadUInt16BigEndian(notification.Raw.AsSpan(2, 2));
        if ((code & 0x4000) != 0)
        {
            if (ChallengeComplete || (code & 0x8000) != 0 || attributes.Any(a => a.Type == 11))
                throw new AuthenticationException("Invalid pre-authentication AKA notification.");
            _terminalResponse = true;
            return MarshalEapPacket(new(EapCode.Response, request.Identifier, request.Type, [AkaSubtype.Notification, 0, 0]));
        }
        if (!ChallengeComplete || Keys == null) throw new AuthenticationException("Protected AKA notification before challenge.");
        var mac = attributes.FirstOrDefault(a => a.Type == 11)
            ?? throw new AuthenticationException("Protected AKA notification missing MAC.");
        var zeroed = MarshalEapPacket(request);
        try
        {
            zeroed.AsSpan(12 + mac.Offset, 16).Clear();
            if (!CryptographicOperations.FixedTimeEquals(ComputeMac(Keys.KAut, zeroed, request.Type == EapType.AkaPrime), mac.Raw.AsSpan(4, 16)))
                throw new AuthenticationException("Protected AKA notification MAC is invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(zeroed); }
        if (code == 32768)
        {
            if (!ResultIndication || ProtectedSuccess) throw new AuthenticationException("Unexpected protected EAP success.");
            ProtectedSuccess = true;
        }
        else _terminalResponse = true;
        var response = MarshalEapPacket(new(EapCode.Response, request.Identifier, request.Type,
            Combine([AkaSubtype.Notification, 0, 0], MarshalAkaAttribute(11, new byte[18]))));
        ComputeMac(Keys.KAut, response, request.Type == EapType.AkaPrime).CopyTo(response, 12);
        return response;
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
        CryptographicOperations.ZeroMemory(full);
        return mac;
    }

    // ── RFC 4187 Key Derivation (FIPS 186-2 PRF) ─────────────────────────────

    public static AkaDerivedKeys DeriveAkaKeys(byte[] identity, byte[] ik, byte[] ck)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(ik);
        ArgumentNullException.ThrowIfNull(ck);

        if (ik.Length != 16 || ck.Length != 16 || identity.Length is 0 or > 1024)
            throw new ArgumentException("IK and CK must be exactly 16 bytes.");

        var material = new byte[identity.Length + 32];
        Buffer.BlockCopy(identity, 0, material, 0, identity.Length);
        Buffer.BlockCopy(ik, 0, material, identity.Length, 16);
        Buffer.BlockCopy(ck, 0, material, identity.Length + 16, 16);

        var mk = SHA1.HashData(material);
        var stream = Fips1862Prf(mk, 160);
        try
        {
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
        finally
        {
            CryptographicOperations.ZeroMemory(material); CryptographicOperations.ZeroMemory(mk); CryptographicOperations.ZeroMemory(stream);
        }
    }

    public static byte[] Fips1862Prf(byte[] seed, int length)
    {
        if (seed.Length != 20 || length is < 0 or > 4096) throw new ArgumentException("Invalid FIPS PRF seed or output length.");
        var key = seed.ToArray(); var result = new byte[length];
        try
        {
            for (var offset = 0; offset < length; offset += 20)
            {
                var word = FipsSha1G(key);
                try
                {
                    word.AsSpan(0, Math.Min(20, length - offset)).CopyTo(result.AsSpan(offset));
                    var carry = 1;
                    for (var i = 19; i >= 0; i--)
                    {
                        var sum = key[i] + word[i] + carry; key[i] = (byte)sum; carry = sum >> 8;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(word); }
            }
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
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
        CryptographicOperations.ZeroMemory(block); Array.Clear(words);
        return result;
    }

    // ── RFC 5448 Key Derivation (EAP-AKA') ─────────────────────────────────────

    public static AkaDerivedKeys DeriveAkaPrimeKeys(byte[] identity, byte[] ik, byte[] ck, byte[] autn, byte[] networkName)
    {
        if (identity.Length is 0 or > 1024 || ik.Length != 16 || ck.Length != 16 || autn.Length != 16 || networkName.Length is 0 or > 1016)
            throw new ArgumentException("Invalid EAP-AKA' key derivation input.");
        if ((autn[6] & 0x80) == 0) throw new AuthenticationException("AKA' AUTN lacks separation bit.");
        var input = Combine([0x20], networkName, [(byte)(networkName.Length >> 8), (byte)networkName.Length], autn[..6], [0, 6]);
        var key = Combine(ck, ik); byte[] derived = [], reordered = [], stream = [], seed = [];
        try
        {
            derived = HMACSHA256.HashData(key, input);
            reordered = new byte[32];
            derived.AsSpan(16, 16).CopyTo(reordered); derived.AsSpan(0, 16).CopyTo(reordered.AsSpan(16));
            seed = Combine(Encoding.ASCII.GetBytes("EAP-AKA'"), identity);
            stream = IkeCrypto.PrfPlus(IkeSuite.Preferred, reordered, seed, 208);
            return new(stream[..16], stream[16..48], stream[80..144], stream[144..208]);
        }
        finally
        {
            foreach (var value in new[] { input, key, derived, reordered, stream, seed }) CryptographicOperations.ZeroMemory(value);
        }
    }

    // ── Packet & Attribute Codec ──────────────────────────────────────────────

    public static EapPacket ParseEapPacket(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length is < 4 or > EapAkaValidation.MaximumPacket)
            throw new IkeFormatException($"Truncated EAP header ({buffer.Length} bytes).");

        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        if (length != buffer.Length)
            throw new IkeFormatException($"EAP length {length} != buffer length {buffer.Length}.");

        var code = buffer[0];
        var id = buffer[1];
        if (code is < 1 or > 4) throw new IkeFormatException("Invalid EAP code.");

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
        if (packet.Code is < 1 or > 4 || packet.Data.Length + 5 > EapAkaValidation.MaximumPacket ||
            packet.Code is EapCode.Success or EapCode.Failure && packet.Data.Length != 0)
            throw new IkeFormatException("Invalid EAP packet code or size.");

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
        var seen = new HashSet<byte>();
        if (buffer.Length > EapAkaValidation.MaximumPacket) throw new IkeFormatException("Excessive AKA attributes.");

        while (offset < buffer.Length)
        {
            if (offset + 2 > buffer.Length)
                throw new IkeFormatException("Truncated AKA attribute header.");

            var type = buffer[offset];
            if (attributes.Count >= 64 || !seen.Add(type) && type != AkaAttributeType.Kdf)
                throw new IkeFormatException("Duplicate or excessive AKA attributes.");
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

    private static void Wipe(AkaDerivedKeys? keys)
    {
        if (keys == null) return;
        foreach (var key in new[] { keys.KEncr, keys.KAut, keys.Msk, keys.Emsk }) CryptographicOperations.ZeroMemory(key);
    }
    private void ClearKeys() { Wipe(Keys); Keys = null; ChallengeComplete = false; ResultIndication = false; ProtectedSuccess = false; }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _failed = true; ClearKeys();
        foreach (var bytes in new[] { Identity, _lastRequest, _lastResponse, _identityTranscript }) CryptographicOperations.ZeroMemory(bytes);
    }
}
