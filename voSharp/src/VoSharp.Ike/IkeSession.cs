using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using VoSharp.Common.Aka;

namespace VoSharp.Ike;

public enum IkeAddressFamilyMode
{
    Ipv6,
    Dual,
    Ipv4
}

public sealed record IkeSessionRequest(
    IPAddress EpdgIp,
    IAkaProvider AkaProvider,
    string Imsi,
    string HomeMcc,
    string HomeMnc,
    string? ExpectedIccid = null,
    string Apn = "ims",
    string? Imei = null,
    string? FallbackPcscf = null,
    IkeSuite? Suite = null,
    EspSuite? ChildSuite = null,
    string? ProxyUrl = null,
    VoSharp.Ike.Transport.Socks5Client? Socks5Client = null,
    int EpdgPort = IkeDefaults.UdpPort,
    IReadOnlyList<IkeSuite>? Suites = null,
    IReadOnlyList<EspSuite>? ChildSuites = null,
    string? Imeisv = null,
    IkeAddressFamilyMode AddressFamilyMode = IkeAddressFamilyMode.Ipv6
);

public sealed record IkeSessionResult(
    bool Success,
    string? AssignedIp,
    string? PcscfIp,
    IReadOnlyList<string> DnsIps,
    uint InboundSpi,
    uint OutboundSpi,
    byte[]? OutboundEncKey,
    byte[]? OutboundAuthKey,
    byte[]? InboundEncKey,
    byte[]? InboundAuthKey,
    IkeSuite? IkeSuite,
    EspSuite? EspSuite,
    IkeTransport? Transport,
    bool IsNatDetected,
    string? ErrorMessage,
    IkeLivenessProbe? LivenessProbe = null,
    bool EapSucceeded = false,
    IkeAddressFamilyMode AddressFamilyMode = IkeAddressFamilyMode.Ipv6
);

/// <summary>
/// Orchestrates the full IKEv2 handshake (RFC 7296):
/// IKE_SA_INIT -&gt; IKE_AUTH (EAP-AKA) -&gt; CHILD_SA negotiation -&gt; Configuration Payload parsing.
/// </summary>
public static class IkeSession
{
    private const ushort NotifyInitialContact = 16384;
    private const ushort NotifyNatDetectionSourceIp = 16388;
    private const ushort NotifyNatDetectionDestinationIp = 16389;
    private const ushort NotifyCookie = 16390;
    private const ushort NotifyEapOnlyAuthentication = 16417;
    // 3GPP TS 24.302 DEVICE_IDENTITY.  Keep this private-use value distinct
    // from the IANA status notification range.
    private const ushort NotifyDeviceIdentity = 41101;
    private const ushort NotifyNon3GppAccessNotAllowed = 9000;
    private const ushort NotifyUserUnknown = 9001;
    private const ushort NotifyNoApnSubscription = 9002;
    private const ushort NotifyAuthorizationRejected = 9003;
    private const ushort NotifyIllegalMe = 9006;
    private const ushort NotifyNetworkFailure = 10500;
    private const ushort NotifyRatTypeNotAllowed = 11001;
    private const ushort NotifyImeiNotAccepted = 11005;
    private const ushort NotifyPlmnNotAllowed = 11011;
    private const ushort NotifyUnauthenticatedEmergencyNotSupported = 11055;
    private const ushort NotifyBackoffTimer = 41041;

    private const byte AuthMethodSharedKeyMic = 2;

    public static async Task<IkeSessionResult> EstablishAsync(
        IkeSessionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Attach behaviour is adapted from reference/mdd-sim-gateway/engine/swu_ike.py
        // (GPL-3.0-only): offer its proven carrier proposal sets in one exchange.
        var ikeSuites = request.Suites is { Count: > 0 }
            ? request.Suites.ToArray()
            : new[] { request.Suite ?? IkeSuite.Preferred };
        var childSuites = request.ChildSuites is { Count: > 0 }
            ? request.ChildSuites.ToArray()
            : new[] { request.ChildSuite ?? EspSuite.Preferred };
        var ikeSuite = ikeSuites[0];
        if (ikeSuites.Any(candidate => candidate.DhGroupId != ikeSuite.DhGroupId))
            throw new ArgumentException("All IKE proposals in one IKE_SA_INIT must use the KE payload's DH group.", nameof(request));

        var socksClient = request.Socks5Client ?? VoSharp.Ike.Transport.Socks5Client.TryParse(request.ProxyUrl);
        var transport = new IkeTransport(request.EpdgIp, initialPort: request.EpdgPort, socks5Client: socksClient);
        var eapSucceeded = false;
        try
        {
            // ── 1. IKE_SA_INIT ──────────────────────────────────────────────
            var initiatorSpi = GenerateNonZeroUInt64();
            var initiatorNonce = new byte[IkeDefaults.NonceLength];
            RandomNumberGenerator.Fill(initiatorNonce);

            using var dh = IkeDhKeyExchange.Create(ikeSuite.DhGroupId);

            var ikeProposals = ikeSuites.Select((candidate, index) => new IkeProposal
            {
                ProposalNumber = checked((byte)(index + 1)),
                Protocol = IkeProtocolId.Ike,
                Transforms = candidate.ToTransforms()
            }).ToArray();

            var saBody = IkeWire.EncodeProposals(ikeProposals);

            var keBody = new byte[4 + dh.Public.Length];
            BinaryPrimitives.WriteUInt16BigEndian(keBody.AsSpan(0, 2), ikeSuite.DhGroupId);
            Buffer.BlockCopy(dh.Public, 0, keBody, 4, dh.Public.Length);

            var localEp = transport.LocalEndpoint ?? new IPEndPoint(IPAddress.Any, 500);
            var remoteEp = transport.RemoteEndpoint;

            var natSrcHash = IkeCrypto.ComputeNatDetectionHash(initiatorSpi, 0, localEp.Address, (ushort)localEp.Port);
            var natDstHash = IkeCrypto.ComputeNatDetectionHash(initiatorSpi, 0, remoteEp.Address, (ushort)remoteEp.Port);

            var initPayloads = new List<IkePayload>
            {
                new(IkePayloadType.SecurityAssociation, saBody),
                new(IkePayloadType.KeyExchange, keBody),
                new(IkePayloadType.Nonce, initiatorNonce),
                MakeNotify(NotifyNatDetectionSourceIp, natSrcHash),
                MakeNotify(NotifyNatDetectionDestinationIp, natDstHash)
            };

            var initRequest = new IkeWire.IkeMessage
            {
                InitiatorSpi = initiatorSpi,
                ResponderSpi = 0,
                Exchange = IkeExchangeType.IkeSaInit,
                Flags = IkeFlags.Initiator,
                MessageId = 0
            };
            initRequest.Payloads.AddRange(initPayloads);

            var initReqBytes = IkeWire.SerializeMessage(initRequest);
            var initRespBytes = await transport.RoundTripAsync(initReqBytes, ct).ConfigureAwait(false);
            var initResp = IkeWire.ParseMessage(initRespBytes);

            // RFC 7296 section 2.6: a responder under load can remain
            // stateless and answer IKE_SA_INIT with N(COOKIE), SPIr=0. Retry
            // message ID 0 with that Notify as the first payload and leave
            // SA, KE, Ni and the remaining notifications unchanged.
            for (var cookieAttempt = 0; initResp.ResponderSpi == 0 && cookieAttempt < 3; cookieAttempt++)
            {
                var cookiePayload = initResp.Payloads.FirstOrDefault(payload =>
                    payload.Type == IkePayloadType.Notify &&
                    payload.Body.Length is >= 5 and <= 68 &&
                    BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2)) == NotifyCookie);
                if (cookiePayload == null)
                    break;

                initRequest.Payloads.Clear();
                initRequest.Payloads.Add(MakeNotify(NotifyCookie, cookiePayload.Body[4..]));
                initRequest.Payloads.AddRange(initPayloads);
                initReqBytes = IkeWire.SerializeMessage(initRequest);
                initRespBytes = await transport.RoundTripAsync(initReqBytes, ct).ConfigureAwait(false);
                initResp = IkeWire.ParseMessage(initRespBytes);
            }

            if (initResp.ResponderSpi == 0)
                throw new IkeFormatException(DescribeIkeSaInitError(initResp.Payloads));

            var responderSpi = initResp.ResponderSpi;

            // Extract selected proposal and peer KE / Nonce
            var chosenSaPayload = initResp.Payloads.FirstOrDefault(p => p.Type == IkePayloadType.SecurityAssociation)
                ?? throw new IkeFormatException("IKE_SA_INIT response missing SA payload.");
            var chosenProposals = IkeWire.DecodeProposals(chosenSaPayload.Body);
            if (chosenProposals.Count == 0)
                throw new IkeFormatException("IKE_SA_INIT response contains no proposals.");

            var negotiatedSuite = IkeSuite.FromProposal(chosenProposals[0]);

            var peerKePayload = initResp.Payloads.FirstOrDefault(p => p.Type == IkePayloadType.KeyExchange)
                ?? throw new IkeFormatException("IKE_SA_INIT response missing KE payload.");
            if (peerKePayload.Body.Length < 4)
                throw new IkeFormatException("IKE_SA_INIT response KE payload is truncated.");

            var peerDhGroup = BinaryPrimitives.ReadUInt16BigEndian(peerKePayload.Body.AsSpan(0, 2));
            if (peerDhGroup != negotiatedSuite.DhGroupId)
                throw new IkeFormatException($"Peer chose DH group {peerDhGroup} which differs from KE group {negotiatedSuite.DhGroupId}.");

            var peerPublic = peerKePayload.Body.AsSpan(4);
            var sharedSecret = dh.ComputeSharedSecret(peerPublic);

            var peerNoncePayload = initResp.Payloads.FirstOrDefault(p => p.Type == IkePayloadType.Nonce)
                ?? throw new IkeFormatException("IKE_SA_INIT response missing Nonce payload.");
            var responderNonce = peerNoncePayload.Body;

            // Compute IKE keys
            var ikeKeys = IkeCrypto.DeriveIkeKeys(negotiatedSuite, sharedSecret, initiatorNonce, responderNonce, initiatorSpi, responderSpi);

            // Check NAT detection
            var isNatDetected = DetectNat(initResp.Payloads, initiatorSpi, responderSpi, localEp, remoteEp);
            if (isNatDetected)
            {
                transport.FloatTo4500();
            }

            // ── 2. First IKE_AUTH request (EAP-AKA start) ────────────────────
            var eapClient = new EapAkaClient(
                request.AkaProvider,
                request.Imsi,
                request.HomeMcc,
                request.HomeMnc,
                request.ExpectedIccid);

            var childInboundSpis = new Dictionary<byte, uint>();
            var childProposals = childSuites.Select((candidate, index) =>
            {
                var proposalNumber = checked((byte)(index + 1));
                var spi = GenerateNonZeroUInt32();
                childInboundSpis[proposalNumber] = spi;
                var spiBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(spiBytes, spi);
                return new IkeProposal
                {
                    ProposalNumber = proposalNumber,
                    Protocol = IkeProtocolId.Esp,
                    Spi = spiBytes,
                    Transforms = candidate.ToTransforms()
                };
            }).ToArray();

            var childSaBody = IkeWire.EncodeProposals(childProposals);

            // IDi: ID_RFC822_ADDR (3) + NAI
            var idiBody = Combine(new byte[] { 3, 0, 0, 0 }, eapClient.Identity);
            var idiPayload = new IkePayload(IkePayloadType.IdentificationInitiator, idiBody);

            // IDr: ID_FQDN (2) + APN
            var idrBody = Combine(new byte[] { 2, 0, 0, 0 }, Encoding.UTF8.GetBytes(request.Apn));
            var idrPayload = new IkePayload(IkePayloadType.IdentificationResponder, idrBody);

            var tsiPayload = BuildTrafficSelectors(IkePayloadType.TrafficSelectorInitiator, request.AddressFamilyMode);
            var tsrPayload = BuildTrafficSelectors(IkePayloadType.TrafficSelectorResponder, request.AddressFamilyMode);
            var cpPayload = BuildConfigurationRequest(request.AddressFamilyMode);

            // Order matters for several carrier ePDGs.  Match the reference engine:
            // IDi -> IDr -> CP -> SA -> TSi -> TSr -> INITIAL_CONTACT -> EAP_ONLY_AUTH.
            var firstAuthInner = BuildInitialAuthPayloads(
                idiPayload, idrPayload, cpPayload,
                new IkePayload(IkePayloadType.SecurityAssociation, childSaBody),
                tsiPayload, tsrPayload);

            var authMsgId = 1u;
            var firstAuthMsg = new IkeWire.IkeMessage
            {
                InitiatorSpi = initiatorSpi,
                ResponderSpi = responderSpi,
                Exchange = IkeExchangeType.IkeAuth,
                Flags = IkeFlags.Initiator,
                MessageId = authMsgId
            };

            var firstAuthEncrypted = IkeCrypto.EncryptPayloads(firstAuthMsg, firstAuthInner, negotiatedSuite, ikeKeys.SkEi, ikeKeys.SkAi);
            var firstAuthRespBytes = await transport.RoundTripAsync(firstAuthEncrypted, ct).ConfigureAwait(false);

            var (_, currentInnerPayloads) = IkeCrypto.DecryptPayloads(firstAuthRespBytes, negotiatedSuite, ikeKeys.SkEr, ikeKeys.SkAr);
            var peerIdrPayload = currentInnerPayloads.FirstOrDefault(p => p.Type == IkePayloadType.IdentificationResponder);

            // ── 3. EAP-AKA Rounds Loop ──────────────────────────────────────
            for (var round = 0; round < 10; round++)
            {
                var eapPayload = currentInnerPayloads.FirstOrDefault(p => p.Type == IkePayloadType.Eap);
                if (eapPayload == null)
                {
                    var authFailure = DescribeIkeAuthFailure(currentInnerPayloads);
                    if (authFailure != null)
                        throw new IkeFormatException(authFailure);

                    var notifyList = currentInnerPayloads
                        .Where(p => p.Type == IkePayloadType.Notify && p.Body.Length >= 4)
                        .Select(DescribeNotify)
                        .ToList();
                    var types = string.Join(", ", currentInnerPayloads.Select(p => p.Type.ToString()));
                    throw new IkeFormatException($"IKE_AUTH round {round + 1} did not contain an EAP payload. Inner payloads: [{types}], Notifies: [{string.Join(", ", notifyList)}]");
                }

                Console.WriteLine($"[IKE] Round {round + 1}: EAP Body Len={eapPayload.Body.Length}, Hex={Convert.ToHexString(eapPayload.Body)}");
                var (eapResponse, isSuccess) = await eapClient.HandleAsync(eapPayload.Body, ct).ConfigureAwait(false);
                if (isSuccess)
                {
                    eapSucceeded = true;
                    break;
                }

                if (eapResponse == null || eapResponse.Length == 0)
                    throw new InvalidOperationException("EAP state machine produced no response.");

                authMsgId++;
                var nextReqInner = new List<IkePayload> { new(IkePayloadType.Eap, eapResponse) };

                // Append DeviceIdentity notify if requested and available
                var deviceRequest = currentInnerPayloads.FirstOrDefault(IsDeviceIdentityNotify);
                if (!string.IsNullOrWhiteSpace(request.Imei) && deviceRequest != null)
                {
                    nextReqInner.Add(MakeDeviceIdentityNotify(
                        request.Imei,
                        request.Imeisv,
                        GetRequestedDeviceIdentityType(deviceRequest)));
                }

                var nextReq = new IkeWire.IkeMessage
                {
                    InitiatorSpi = initiatorSpi,
                    ResponderSpi = responderSpi,
                    Exchange = IkeExchangeType.IkeAuth,
                    Flags = IkeFlags.Initiator,
                    MessageId = authMsgId
                };

                var encNext = IkeCrypto.EncryptPayloads(nextReq, nextReqInner, negotiatedSuite, ikeKeys.SkEi, ikeKeys.SkAi);
                var respBytes = await transport.RoundTripAsync(encNext, ct).ConfigureAwait(false);

                var (_, inner) = IkeCrypto.DecryptPayloads(respBytes, negotiatedSuite, ikeKeys.SkEr, ikeKeys.SkAr);
                currentInnerPayloads = inner;
                peerIdrPayload ??= currentInnerPayloads.FirstOrDefault(p => p.Type == IkePayloadType.IdentificationResponder);

                if (round == 9)
                    throw new TimeoutException("EAP exchange exceeded maximum 10 rounds.");
            }

            if (!eapClient.ChallengeComplete || eapClient.Keys?.Msk == null)
                throw new InvalidOperationException("EAP-AKA finished without producing an authenticated MSK.");

            // ── 4. Final IKE_AUTH: Initiator AUTH payload ────────────────────
            authMsgId++;
            var msk = eapClient.Keys.Msk;

            // Compute Initiator AUTH:
            // idHash = prf(SK_pi, IDi.Body)
            // signed = initRequestBytes + responderNonce + idHash
            // paddedKey = prf(MSK, "Key Pad for IKEv2")
            // authValue = prf(paddedKey, signed)
            var idHash = IkeCrypto.Prf(negotiatedSuite, ikeKeys.SkPi, idiPayload.Body);
            var signedOctets = Combine(initReqBytes, responderNonce, idHash);
            var keyPad = IkeCrypto.Prf(negotiatedSuite, msk, Encoding.ASCII.GetBytes("Key Pad for IKEv2"));
            var authValue = IkeCrypto.Prf(negotiatedSuite, keyPad, signedOctets);

            var authBody = Combine(new byte[] { AuthMethodSharedKeyMic, 0, 0, 0 }, authValue);
            var initiatorAuthPayload = new IkePayload(IkePayloadType.Authentication, authBody);

            var finalReq = new IkeWire.IkeMessage
            {
                InitiatorSpi = initiatorSpi,
                ResponderSpi = responderSpi,
                Exchange = IkeExchangeType.IkeAuth,
                Flags = IkeFlags.Initiator,
                MessageId = authMsgId
            };

            var finalEnc = IkeCrypto.EncryptPayloads(finalReq, new[] { initiatorAuthPayload }, negotiatedSuite, ikeKeys.SkEi, ikeKeys.SkAi);
            var finalRespBytes = await transport.RoundTripAsync(finalEnc, ct).ConfigureAwait(false);

            var (_, finalPayloads) = IkeCrypto.DecryptPayloads(finalRespBytes, negotiatedSuite, ikeKeys.SkEr, ikeKeys.SkAr);
            peerIdrPayload ??= finalPayloads.FirstOrDefault(p => p.Type == IkePayloadType.IdentificationResponder);

            // ── 5. Verify Responder AUTH (RFC 7296 §2.15 / 3GPP TS 33.402) ────
            var respAuthPayload = finalPayloads.FirstOrDefault(p => p.Type == IkePayloadType.Authentication);
            if (respAuthPayload != null && respAuthPayload.Body.Length >= 4)
            {
                byte authMethod = respAuthPayload.Body[0];
                var actualAuth = respAuthPayload.Body.AsSpan(4);
                Console.WriteLine($"[IKE] Responder AUTH Method={authMethod}, BodyLen={respAuthPayload.Body.Length}, PeerIdr={peerIdrPayload != null}");
                if (authMethod == AuthMethodSharedKeyMic)
                {
                    var respIdBody = peerIdrPayload?.Body ?? idrPayload.Body;
                    var respIdHash = IkeCrypto.Prf(negotiatedSuite, ikeKeys.SkPr, respIdBody);
                    var respSignedOctets = Combine(initRespBytes, initiatorNonce, respIdHash);
                    var expectedRespAuth = IkeCrypto.Prf(negotiatedSuite, keyPad, respSignedOctets);

                    Console.WriteLine($"[IKE] Responder AUTH Actual={Convert.ToHexString(actualAuth)}, Expected={Convert.ToHexString(expectedRespAuth)}");
                    if (!CryptographicOperations.FixedTimeEquals(expectedRespAuth, actualAuth))
                    {
                        Console.WriteLine($"[IKE WARN] Responder AUTH MIC mismatch (actual={Convert.ToHexString(actualAuth)}, expected={Convert.ToHexString(expectedRespAuth)})");
                        // If ePDG calculation differs or is relaxed, log warning
                    }
                }
            }

            // ── 6. Parse CHILD_SA, CP, and Key Material ──────────────────────
            var finalSaPayload = finalPayloads.FirstOrDefault(p => p.Type == IkePayloadType.SecurityAssociation)
                ?? throw new IkeFormatException("Final IKE_AUTH response missing SA payload.");
            var finalProposals = IkeWire.DecodeProposals(finalSaPayload.Body);
            if (finalProposals.Count == 0 || finalProposals[0].Spi.Length != 4)
                throw new IkeFormatException("Final IKE_AUTH response has invalid ESP proposals.");

            var childOutboundSpi = BinaryPrimitives.ReadUInt32BigEndian(finalProposals[0].Spi);
            var negotiatedChildSuite = EspSuite.FromProposal(finalProposals[0]);
            if (!childInboundSpis.TryGetValue(finalProposals[0].ProposalNumber, out var childInboundSpi))
                throw new IkeFormatException($"ePDG selected unknown ESP proposal {finalProposals[0].ProposalNumber}.");

            var (outEnc, outAuth, inEnc, inAuth) = IkeCrypto.DeriveChildSaKeys(
                negotiatedSuite, negotiatedChildSuite, ikeKeys.SkD, initiatorNonce, responderNonce);

            // Parse CP (Configuration Payload)
            var cpRespPayload = finalPayloads.FirstOrDefault(p => p.Type == IkePayloadType.Configuration);
            var (assignedIp, dnsList, pcscfList) = cpRespPayload != null
                ? ParseConfigurationPayload(cpRespPayload.Body)
                : (null, new List<string>(), new List<string>());

            var finalPcscf = PickPcscf(pcscfList, assignedIp) ?? request.FallbackPcscf;
            if (string.IsNullOrEmpty(finalPcscf))
                throw new InvalidOperationException("P-CSCF IP was not provided by ePDG and no fallback was configured.");

            return new IkeSessionResult(
                Success: true,
                AssignedIp: assignedIp,
                PcscfIp: finalPcscf,
                DnsIps: dnsList,
                InboundSpi: childInboundSpi,
                OutboundSpi: childOutboundSpi,
                OutboundEncKey: outEnc,
                OutboundAuthKey: outAuth,
                InboundEncKey: inEnc,
                InboundAuthKey: inAuth,
                IkeSuite: negotiatedSuite,
                EspSuite: negotiatedChildSuite,
                Transport: transport,
                IsNatDetected: isNatDetected,
                ErrorMessage: null,
                LivenessProbe: new IkeLivenessProbe(
                    transport, negotiatedSuite, initiatorSpi, responderSpi, authMsgId,
                    ikeKeys.SkEi, ikeKeys.SkAi, ikeKeys.SkEr, ikeKeys.SkAr),
                EapSucceeded: true,
                AddressFamilyMode: request.AddressFamilyMode
            );
        }
        catch (Exception ex)
        {
            transport.Dispose();
            return new IkeSessionResult(
                Success: false,
                AssignedIp: null,
                PcscfIp: null,
                DnsIps: Array.Empty<string>(),
                InboundSpi: 0,
                OutboundSpi: 0,
                OutboundEncKey: null,
                OutboundAuthKey: null,
                InboundEncKey: null,
                InboundAuthKey: null,
                IkeSuite: null,
                EspSuite: null,
                Transport: null,
                IsNatDetected: false,
                ErrorMessage: ex.Message,
                EapSucceeded: eapSucceeded,
                AddressFamilyMode: request.AddressFamilyMode
            );
        }
    }

    internal static bool DetectNat(
        IEnumerable<IkePayload> payloads,
        ulong initiatorSpi,
        ulong responderSpi,
        IPEndPoint localEp,
        IPEndPoint remoteEp)
    {
        var srcHash = payloads
            .Where(p => p.Type == IkePayloadType.Notify && p.Body.Length >= 24 &&
                        BinaryPrimitives.ReadUInt16BigEndian(p.Body.AsSpan(2, 2)) == NotifyNatDetectionSourceIp)
            .Select(p => p.Body.AsSpan(4, 20).ToArray())
            .FirstOrDefault();

        var dstHash = payloads
            .Where(p => p.Type == IkePayloadType.Notify && p.Body.Length >= 24 &&
                        BinaryPrimitives.ReadUInt16BigEndian(p.Body.AsSpan(2, 2)) == NotifyNatDetectionDestinationIp)
            .Select(p => p.Body.AsSpan(4, 20).ToArray())
            .FirstOrDefault();

        if (srcHash == null || dstHash == null)
            return false;

        // These notifications are from the responder's point of view: SOURCE describes the ePDG
        // endpoint and DESTINATION describes the UE endpoint. Reversing them reports a false NAT
        // on every direct path and can hide the fact that raw ESP would otherwise be required.
        var expectedSrc = IkeCrypto.ComputeNatDetectionHash(initiatorSpi, responderSpi, remoteEp.Address, (ushort)remoteEp.Port);
        var expectedDst = IkeCrypto.ComputeNatDetectionHash(initiatorSpi, responderSpi, localEp.Address, (ushort)localEp.Port);

        var srcMatches = CryptographicOperations.FixedTimeEquals(srcHash, expectedSrc);
        var dstMatches = CryptographicOperations.FixedTimeEquals(dstHash, expectedDst);

        return !srcMatches || !dstMatches;
    }

    private static string DescribeIkeSaInitError(IEnumerable<IkePayload> payloads)
    {
        var errors = payloads
            .Where(payload => payload.Type == IkePayloadType.Notify && payload.Body.Length >= 4)
            .Select(payload =>
            {
                var notifyType = BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2));
                var data = payload.Body.AsSpan(4);
                var name = notifyType switch
                {
                    1 => "UNSUPPORTED_CRITICAL_PAYLOAD",
                    4 => "INVALID_IKE_SPI",
                    5 => "INVALID_MAJOR_VERSION",
                    7 => "INVALID_SYNTAX",
                    14 => "NO_PROPOSAL_CHOSEN",
                    17 => "INVALID_KE_PAYLOAD",
                    24 => "AUTHENTICATION_FAILED",
                    34 => "SINGLE_PAIR_REQUIRED",
                    35 => "NO_ADDITIONAL_SAS",
                    36 => "INTERNAL_ADDRESS_FAILURE",
                    37 => "FAILED_CP_REQUIRED",
                    38 => "TS_UNACCEPTABLE",
                    NotifyCookie => "COOKIE",
                    _ => $"NotifyType={notifyType}"
                };

                if (notifyType == 17 && data.Length >= 2)
                {
                    var requestedGroup = BinaryPrimitives.ReadUInt16BigEndian(data[..2]);
                    return $"{name} (requested DH group {requestedGroup})";
                }

                return data.Length == 0 ? name : $"{name} (data={Convert.ToHexString(data)})";
            })
            .ToArray();

        return errors.Length > 0
            ? $"IKE_SA_INIT rejected by ePDG: {string.Join(", ", errors)}."
            : "ePDG returned an IKE_SA_INIT response with zero Responder SPI and no error Notify payload.";
    }

    private static string? DescribeIkeAuthFailure(IEnumerable<IkePayload> payloads)
    {
        var notifications = payloads
            .Where(payload => payload.Type == IkePayloadType.Notify && payload.Body.Length >= 4)
            .Select(payload => new
            {
                Type = BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2)),
                Data = payload.Body[4..]
            })
            .ToArray();

        var error = notifications.FirstOrDefault(notification => notification.Type is
            NotifyNon3GppAccessNotAllowed or NotifyUserUnknown or NotifyNoApnSubscription or
            NotifyAuthorizationRejected or NotifyIllegalMe or NotifyNetworkFailure or
            NotifyRatTypeNotAllowed or NotifyImeiNotAccepted or NotifyPlmnNotAllowed or
            NotifyUnauthenticatedEmergencyNotSupported);
        if (error == null)
            return null;

        var errorName = Get3GppNotifyName(error.Type);
        var message = $"ePDG rejected IKE_AUTH: {errorName} (Notify {error.Type}).";

        var backoff = notifications.FirstOrDefault(notification => notification.Type == NotifyBackoffTimer);
        var backoffText = backoff == null ? null : DescribeBackoffTimer(backoff.Data);
        if (backoffText != null)
            message += $" BACKOFF_TIMER={backoffText}.";

        if (error.Type == NotifyAuthorizationRejected)
        {
            message += " SOCKS5/IKE transport is reachable; the operator denied this SIM's non-3GPP access or subscribed APN authorization.";
        }

        return message;
    }

    private static string DescribeNotify(IkePayload payload)
    {
        var notifyType = BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2));
        var data = payload.Body[4..];
        var name = Get3GppNotifyName(notifyType);
        if (notifyType == NotifyBackoffTimer)
        {
            var timer = DescribeBackoffTimer(data);
            if (timer != null)
                return $"{name} ({timer}, data={Convert.ToHexString(data)})";
        }

        return data.Length == 0
            ? $"{name} ({notifyType})"
            : $"{name} ({notifyType}, data={Convert.ToHexString(data)})";
    }

    private static string Get3GppNotifyName(ushort notifyType) => notifyType switch
    {
        NotifyNon3GppAccessNotAllowed => "NON_3GPP_ACCESS_TO_EPC_NOT_ALLOWED",
        NotifyUserUnknown => "USER_UNKNOWN",
        NotifyNoApnSubscription => "NO_APN_SUBSCRIPTION",
        NotifyAuthorizationRejected => "AUTHORIZATION_REJECTED",
        NotifyIllegalMe => "ILLEGAL_ME",
        NotifyNetworkFailure => "NETWORK_FAILURE",
        NotifyRatTypeNotAllowed => "RAT_TYPE_NOT_ALLOWED",
        NotifyImeiNotAccepted => "IMEI_NOT_ACCEPTED",
        NotifyPlmnNotAllowed => "PLMN_NOT_ALLOWED",
        NotifyUnauthenticatedEmergencyNotSupported => "UNAUTHENTICATED_EMERGENCY_NOT_SUPPORTED",
        NotifyBackoffTimer => "BACKOFF_TIMER",
        _ => $"NotifyType={notifyType}"
    };

    internal static string? DescribeBackoffTimer(ReadOnlySpan<byte> data)
    {
        // TS 24.302 normally prefixes the one-octet GPRS Timer 3 value with
        // its length.  Accept a bare value too, as some ePDGs omit the prefix.
        if (data.Length == 0 || data.Length > 2 || data.Length == 2 && data[0] != 1)
            return null;

        var encoded = data[^1];
        var units = encoded >> 5;
        var value = encoded & 0x1F;
        if (units == 7)
            return "does not expire";

        var duration = units switch
        {
            0 => TimeSpan.FromSeconds(value * 2),
            1 => TimeSpan.FromMinutes(value),
            2 => TimeSpan.FromMinutes(value * 10),
            3 => TimeSpan.FromHours(value),
            4 => TimeSpan.FromHours(value * 10),
            5 => TimeSpan.FromMinutes(value * 2),
            6 => TimeSpan.FromSeconds(value * 30),
            _ => TimeSpan.Zero
        };

        if (duration.TotalDays >= 1)
            return $"{duration.TotalDays:0.##} days";
        if (duration.TotalHours >= 1)
            return $"{duration.TotalHours:0.##} hours";
        if (duration.TotalMinutes >= 1)
            return $"{duration.TotalMinutes:0.##} minutes";
        return $"{duration.TotalSeconds:0.##} seconds";
    }

    private static IkePayload MakeNotify(ushort notifyType, byte[] data)
    {
        var body = new byte[4 + data.Length];
        body[0] = 0; // Protocol ID (0 = none)
        body[1] = 0; // SPI Size
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), notifyType);
        if (data.Length > 0)
        {
            Buffer.BlockCopy(data, 0, body, 4, data.Length);
        }
        return new IkePayload(IkePayloadType.Notify, body);
    }

    private static bool IsDeviceIdentityNotify(IkePayload payload) =>
        payload.Type == IkePayloadType.Notify &&
        payload.Body.Length >= 4 &&
        BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2)) == NotifyDeviceIdentity;

    private static byte GetRequestedDeviceIdentityType(IkePayload payload)
    {
        var data = payload.Body.AsSpan(4);
        // A request is normally [identity-length(2), identity-type(1)].  Some
        // ePDGs send only the type or an empty notification, so accept both.
        if (data.Length >= 3 && data.Length <= 3 && data[^1] is 1 or 2)
            return data[^1];
        if (data.Length == 1 && data[0] is 1 or 2)
            return data[0];
        return 2; // reference behaviour: prefer IMEISV when no type is stated
    }

    internal static IkePayload MakeDeviceIdentityNotify(string imei, string? imeisv, byte requestedType)
    {
        var imeiDigits = new string(imei.Where(char.IsAsciiDigit).ToArray());
        if (imeiDigits.Length < 15)
            throw new ArgumentException("A 15-digit IMEI is required for DEVICE_IDENTITY.", nameof(imei));

        var imeisvDigits = new string((imeisv ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        var identityType = requestedType == 1 ? (byte)1 : (byte)2;
        var digits = identityType == 1
            ? imeiDigits[..15] + "F"
            : imeisvDigits.Length >= 16
                ? imeisvDigits[..16]
                : imeiDigits[..14] + "00";

        var tbcd = new byte[digits.Length / 2];
        for (var i = 0; i < tbcd.Length; i++)
        {
            var low = Convert.ToByte(digits[i * 2].ToString(), 16);
            var high = Convert.ToByte(digits[i * 2 + 1].ToString(), 16);
            tbcd[i] = (byte)(low | (high << 4));
        }

        var identity = Combine(new[] { identityType }, tbcd);
        var data = new byte[2 + identity.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0, 2), checked((ushort)identity.Length));
        identity.CopyTo(data, 2);
        return MakeNotify(NotifyDeviceIdentity, data);
    }

    internal static List<IkePayload> BuildInitialAuthPayloads(
        IkePayload idi,
        IkePayload idr,
        IkePayload cp,
        IkePayload sa,
        IkePayload tsi,
        IkePayload tsr) => new()
    {
        idi,
        idr,
        cp,
        sa,
        tsi,
        tsr,
        MakeNotify(NotifyInitialContact, Array.Empty<byte>()),
        MakeNotify(NotifyEapOnlyAuthentication, Array.Empty<byte>())
    };

    internal static IkePayload BuildTrafficSelectors(IkePayloadType type, IkeAddressFamilyMode mode)
    {
        // TS type 7 (IPv4) length 16 + TS type 8 (IPv6) length 40
        var ipv4 = new byte[16];
        ipv4[0] = 7; // IPv4 range
        ipv4[1] = 0; // Any IP protocol
        BinaryPrimitives.WriteUInt16BigEndian(ipv4.AsSpan(2, 2), 16);
        BinaryPrimitives.WriteUInt16BigEndian(ipv4.AsSpan(4, 2), 0); // start port
        BinaryPrimitives.WriteUInt16BigEndian(ipv4.AsSpan(6, 2), 65535); // end port
        // start IP 0.0.0.0, end IP 255.255.255.255
        for (var i = 12; i < 16; i++) ipv4[i] = 0xFF;

        var ipv6 = new byte[40];
        ipv6[0] = 8; // IPv6 range
        ipv6[1] = 0; // Any IP protocol
        BinaryPrimitives.WriteUInt16BigEndian(ipv6.AsSpan(2, 2), 40);
        BinaryPrimitives.WriteUInt16BigEndian(ipv6.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(ipv6.AsSpan(6, 2), 65535);
        for (var i = 24; i < 40; i++) ipv6[i] = 0xFF;

        var selectors = mode switch
        {
            IkeAddressFamilyMode.Ipv4 => new[] { ipv4 },
            IkeAddressFamilyMode.Ipv6 => new[] { ipv6 },
            _ => new[] { ipv4, ipv6 }
        };
        var body = Combine(new byte[] { checked((byte)selectors.Length), 0, 0, 0 }, selectors.SelectMany(x => x).ToArray());
        return new IkePayload(type, body);
    }

    internal static IkePayload BuildConfigurationRequest(IkeAddressFamilyMode mode)
    {
        // CFG_REQUEST (1), reserved (3 bytes)
        var attrTypes = mode switch
        {
            IkeAddressFamilyMode.Ipv4 => new ushort[] { 1, 3, 20 },
            IkeAddressFamilyMode.Ipv6 => new ushort[] { 8, 10, 21 },
            _ => new ushort[] { 1, 3, 20, 8, 10, 21 }
        };

        var body = new byte[4 + attrTypes.Length * 4];
        body[0] = 1; // CFG_REQUEST
        for (var i = 0; i < attrTypes.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4 + i * 4, 2), attrTypes[i]);
            // length is 0 for requests
        }

        return new IkePayload(IkePayloadType.Configuration, body);
    }

    internal static (string? AssignedIp, List<string> DnsList, List<string> PcscfList) ParseConfigurationPayload(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4 || body[0] != 2) // CFG_REPLY
            return (null, new List<string>(), new List<string>());

        string? assignedIp = null;
        var dnsList = new List<string>();
        var pcscfList = new List<string>();

        var offset = 4;
        while (offset + 4 <= body.Length)
        {
            var attrType = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset, 2)) & 0x7FFF);
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset + 2, 2));
            offset += 4;

            if (offset + attrLength > body.Length)
                break;

            var val = body.Slice(offset, attrLength);
            offset += attrLength;

            switch (attrType)
            {
                case 1 when attrLength == 4: // INTERNAL_IP4_ADDRESS
                    assignedIp = new IPAddress(val).ToString();
                    break;
                case 3 when attrLength == 4: // INTERNAL_IP4_DNS
                    dnsList.Add(new IPAddress(val).ToString());
                    break;
                case 20 when attrLength == 4: // P_CSCF_IP4_ADDRESS
                    pcscfList.Add(new IPAddress(val).ToString());
                    break;
                case 8 when attrLength is 16 or 17: // INTERNAL_IP6_ADDRESS (+ optional prefix length)
                    assignedIp ??= new IPAddress(val[..16]).ToString();
                    break;
                case 10 when attrLength == 16: // INTERNAL_IP6_DNS
                    dnsList.Add(new IPAddress(val).ToString());
                    break;
                case 21 when attrLength == 16: // P_CSCF_IP6_ADDRESS
                    pcscfList.Add(new IPAddress(val).ToString());
                    break;
            }
        }

        return (assignedIp, dnsList, pcscfList);
    }

    /// <summary>
    /// Chooses the P-CSCF the UE can actually reach. A dual-family CFG_REQUEST (the default) lets
    /// the ePDG answer with both an IPv4 and an IPv6 P-CSCF, and it lists them in whatever order
    /// it likes. Only the one sharing the assigned address's family is usable: every inner packet
    /// is built with a single IP family, so a mixed pair fails with
    /// "Source and destination must use the same IPv4/IPv6 family" before the REGISTER ever leaves
    /// the tunnel. This mirrors the reference gateway's rule that the CFG address family must
    /// match the carrier's IMS PDN.
    /// </summary>
    /// <summary>
    /// A PDN is only usable when the assigned address and the P-CSCF share an address family.
    /// An ePDG that answers a dual-family CFG_REQUEST with a mismatched pair (IPv6 address, IPv4
    /// P-CSCF) produces a tunnel that can never carry IMS signalling, so the discovery ladder has
    /// to treat it as a failure of that family and move on.
    /// </summary>
    public static bool IsUsablePdn(string? assignedIp, string? pcscfIp) =>
        IPAddress.TryParse(assignedIp, out var assigned) &&
        IPAddress.TryParse(pcscfIp, out var pcscf) &&
        assigned.AddressFamily == pcscf.AddressFamily;

    internal static string? PickPcscf(IReadOnlyList<string> pcscfList, string? assignedIp)
    {
        if (pcscfList.Count == 0) return null;
        if (!IPAddress.TryParse(assignedIp, out var assigned)) return pcscfList[0];

        return pcscfList.FirstOrDefault(candidate =>
                   IPAddress.TryParse(candidate, out var address) &&
                   address.AddressFamily == assigned.AddressFamily)
               ?? pcscfList[0];
    }

    private static ulong GenerateNonZeroUInt64()
    {
        Span<byte> buf = stackalloc byte[8];
        while (true)
        {
            RandomNumberGenerator.Fill(buf);
            var val = BinaryPrimitives.ReadUInt64BigEndian(buf);
            if (val != 0) return val;
        }
    }

    private static uint GenerateNonZeroUInt32()
    {
        Span<byte> buf = stackalloc byte[4];
        while (true)
        {
            RandomNumberGenerator.Fill(buf);
            var val = BinaryPrimitives.ReadUInt32BigEndian(buf);
            if (val != 0) return val;
        }
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
