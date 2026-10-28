using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
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
    IkeAddressFamilyMode AddressFamilyMode = IkeAddressFamilyMode.Ipv6,
    Action<string>? DiagnosticLog = null,
    bool ForceNatt = false,
    IPAddress? LocalAddress = null,
    byte EapMethod = EapType.Aka,
    IkeResponderAuthenticationOptions? ResponderAuthentication = null,
    Func<IPAddress, CancellationToken, Task>? BeforeInitialSend = null,
    Func<IkeNetworkRejection, IPAddress, CancellationToken, Task>? ObserveNetworkRejection = null
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
    IkeAddressFamilyMode AddressFamilyMode = IkeAddressFamilyMode.Ipv6,
    IkeSessionController? Controller = null,
    byte? RequestedEapType = null,
    bool RetryableTransportFailure = false,
    bool RetryableAddressFamilyFailure = false,
    IkeChildPolicy? ChildPolicy = null,
    bool RetryableIdentityRejection = false,
    bool RetryableInitialTimeout = false,
    bool RetryableInitialProposal = false,
    bool PreEapFamilyFailure = false,
    bool FinalAuthVerified = false,
    bool FailureCleanupComplete = false,
    bool InitialResponderAuthVerified = false,
    IkeNetworkRejection? NetworkRejection = null
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
    // RFC 7383.  Carrier ePDGs commonly include this in their IKE_SA_INIT
    // fingerprint and may fragment later IKE_AUTH responses.
    private const ushort NotifyIkeFragmentationSupported = 16430;
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
        IkeTransport? transport = null;
        var eapSucceeded = false;
        var retryableFamilyFailure = false;
        var preEapFamilyFailure = false;
        var backoffSeen = false;
        var finalAuthVerified = false;
        var initialResponderAuthVerified = false;
        IkeNetworkRejection? networkRejection = null;
        var initialTimeout = false;
        var initialRouteFailure = false;
        var initialProposalFailure = false;
        var identityRejectedBeforeChallenge = false;
        Func<CancellationToken, Task>? deletePendingIke = null;
        uint nextCleanupMessageId = 1;
        var stage = "IKE_SA_INIT";
        IkeKeys? ownedIkeKeys = null;
        byte[][]? uncommittedChildKeys = null;
        var temporarySecrets = new List<byte[]>();
        byte[] Secret(byte[] value) { temporarySecrets.Add(value); return value; }
        void Trace(string message) => request.DiagnosticLog?.Invoke(message);
        try
        {
            ct.ThrowIfCancellationRequested();
            try { transport = new IkeTransport(request.EpdgIp, initialPort: request.EpdgPort,
                socks5Client: socksClient, diagnosticLog: message => request.DiagnosticLog?.Invoke(message),
                localAddress: request.LocalAddress, beforeInitialSend: request.BeforeInitialSend); }
            catch (Exception error) { initialRouteFailure = IsRouteFailure(error); throw; }
            async Task<byte[]> ExchangeInit(byte[] packet)
            {
                try { return await transport.RoundTripAsync(packet, ct).ConfigureAwait(false); }
                catch (Exception error)
                {
                    initialTimeout = error is TimeoutException;
                    initialRouteFailure = IsRouteFailure(error);
                    throw;
                }
            }
            if (request.ForceNatt)
            {
                transport.FloatTo4500();
                Trace("IKE stage=IKE_SA_INIT forcing NAT-T UDP/4500 for this diagnostic attempt.");
            }

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
                MakeNotify(NotifyNatDetectionDestinationIp, natDstHash),
                MakeNotify(NotifyIkeFragmentationSupported, Array.Empty<byte>()),
                MakeNotify(IkeResponderAuthentication.SignatureHashAlgorithms, [0, 2, 0, 3, 0, 4])
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
            Trace($"IKE stage=IKE_SA_INIT send; endpoint-family={request.EpdgIp.AddressFamily}; port={request.EpdgPort}; dh={ikeSuite.DhGroupId}; proposals={ikeSuites.Count()}; proxy={socksClient is not null}; packet-bytes={initReqBytes.Length}.");
            var initRespBytes = await ExchangeInit(initReqBytes).ConfigureAwait(false);
            var initResp = IkeWire.ParseMessage(initRespBytes);
            ValidateHandshakeResponse(initResp, initiatorSpi, 0, IkeExchangeType.IkeSaInit, 0, allowZeroResponderSpi: true);

            // RFC 7296 section 2.6: a responder under load can remain
            // stateless and answer IKE_SA_INIT with N(COOKIE), usually SPIr=0.
            // A nonzero response SPI alone does not invalidate the challenge. Retry
            // message ID 0 with that Notify as the first payload and leave
            // SA, KE, Ni and the remaining notifications unchanged.
            for (var cookieAttempt = 0; ; cookieAttempt++)
            {
                var cookiePayload = ReadInitCookie(initResp);
                if (cookiePayload == null)
                    break;
                if (cookieAttempt >= 3)
                    throw new IkeFormatException("ePDG requested too many COOKIE challenges.");

                initRequest.Payloads.Clear();
                initRequest.Payloads.Add(MakeNotify(NotifyCookie, cookiePayload.Body[4..]));
                initRequest.Payloads.AddRange(initPayloads);
                initReqBytes = IkeWire.SerializeMessage(initRequest);
                Trace($"IKE stage=IKE_SA_INIT retry-cookie; attempt={cookieAttempt + 1}; packet-bytes={initReqBytes.Length}.");
                initRespBytes = await ExchangeInit(initReqBytes).ConfigureAwait(false);
                initResp = IkeWire.ParseMessage(initRespBytes);
                ValidateHandshakeResponse(initResp, initiatorSpi, 0, IkeExchangeType.IkeSaInit, 0, allowZeroResponderSpi: true);
            }

            // RFC 7296 2.6 also accepts these error responses with a nonzero
            // responder SPI. The precise Notify, not SPI allocation, proves it.
            if (IsInitialProposalRejection(initResp.Payloads))
            {
                initialProposalFailure = true;
                throw new IkeFormatException(DescribeIkeSaInitError(initResp.Payloads));
            }
            if (initResp.ResponderSpi == 0)
                throw new IkeFormatException(DescribeIkeSaInitError(initResp.Payloads));

            var responderSpi = initResp.ResponderSpi;

            // Extract selected proposal and peer KE / Nonce
            var chosenSaPayload = RequireSingleInitPayload(initResp, IkePayloadType.SecurityAssociation);
            var chosenProposals = IkeWire.DecodeProposals(chosenSaPayload.Body);
            if (chosenProposals.Count != 1)
                throw new IkeFormatException("IKE_SA_INIT response must select exactly one proposal.");

            var negotiatedSuite = IkeSuite.FromProposal(chosenProposals[0]);
            ValidateChosenSuite(chosenProposals[0].ProposalNumber, negotiatedSuite, ikeSuites);

            var peerKePayload = RequireSingleInitPayload(initResp, IkePayloadType.KeyExchange);
            if (peerKePayload.Body.Length < 4)
                throw new IkeFormatException("IKE_SA_INIT response KE payload is truncated.");

            var peerDhGroup = BinaryPrimitives.ReadUInt16BigEndian(peerKePayload.Body.AsSpan(0, 2));
            if (peerDhGroup != negotiatedSuite.DhGroupId)
                throw new IkeFormatException($"Peer chose DH group {peerDhGroup} which differs from KE group {negotiatedSuite.DhGroupId}.");

            var peerNoncePayload = RequireSingleInitPayload(initResp, IkePayloadType.Nonce);
            var responderNonce = peerNoncePayload.Body;
            ValidateInitNonce(responderNonce, negotiatedSuite);
            var fragmentationNegotiated = ReadFragmentationSupport(initResp.Payloads);
            var signatureHashesNegotiated = IkeResponderAuthentication.ReadSignatureHashSupport(initResp.Payloads);

            // Compute IKE keys
            var sharedSecret = dh.ComputeSharedSecret(peerKePayload.Body.AsSpan(4));
            IkeKeys ikeKeys;
            try { ikeKeys = IkeCrypto.DeriveIkeKeys(negotiatedSuite, sharedSecret, initiatorNonce, responderNonce, initiatorSpi, responderSpi); }
            finally { CryptographicOperations.ZeroMemory(sharedSecret); }
            ownedIkeKeys = ikeKeys;
            deletePendingIke = cleanupToken => transport.SendIkeAsync(IkeCrypto.EncryptPayloads(new()
            {
                InitiatorSpi = initiatorSpi, ResponderSpi = responderSpi,
                Exchange = IkeExchangeType.Informational, Flags = IkeFlags.Initiator,
                MessageId = nextCleanupMessageId
            }, [new(IkePayloadType.Delete, [1, 0, 0, 0])], negotiatedSuite, ikeKeys.SkEi, ikeKeys.SkAi), cleanupToken);

            // Check NAT detection
            var isNatDetected = DetectNat(initResp.Payloads, initiatorSpi, responderSpi, localEp, remoteEp);
            if (isNatDetected)
            {
                transport.FloatTo4500();
            }
            Trace($"IKE stage=IKE_SA_INIT complete; negotiated-dh={negotiatedSuite.DhGroupId}; nat-detected={isNatDetected}; transport-port={(isNatDetected ? 4500 : request.EpdgPort)}; response-payloads={currentPayloadNames(initResp.Payloads)}.");

            // ── 2. First IKE_AUTH request (EAP-AKA start) ────────────────────
            stage = "IKE_AUTH/EAP-AKA";
            using var eapClient = new EapAkaClient(
                request.AkaProvider,
                request.Imsi,
                request.HomeMcc,
                request.HomeMnc,
                request.ExpectedIccid,
                message => Trace($"EAP-AKA {message}"),
                request.EapMethod);

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

            Trace($"IKE stage=IKE_AUTH send initial EAP request; address-family={request.AddressFamilyMode}; child-proposals={childSuites.Count()}; fragmentation={fragmentationNegotiated}.");
            nextCleanupMessageId = authMsgId + 1;
            var firstAuthResponse = await transport.RoundTripEncryptedAsync(firstAuthMsg, firstAuthInner, negotiatedSuite,
                ikeKeys.SkEi, ikeKeys.SkAi, ikeKeys.SkEr, ikeKeys.SkAr, fragmentationNegotiated, ct).ConfigureAwait(false);
            var (firstAuthHeader, currentInnerPayloads) = (firstAuthResponse.Header, firstAuthResponse.Payloads);
            ValidateHandshakeResponse(firstAuthHeader, initiatorSpi, responderSpi, IkeExchangeType.IkeAuth, authMsgId);
            var peerIdrPayload = IkeChildNegotiation.MergeIdentity(null, currentInnerPayloads);
            initialResponderAuthVerified = IkeResponderAuthentication.Verify(currentInnerPayloads, initRespBytes, initiatorNonce,
                negotiatedSuite, ikeKeys.SkPr, request.ResponderAuthentication ?? new(request.EpdgIp.ToString()), signatureHashesNegotiated);

            // ── 3. EAP-AKA Rounds Loop ──────────────────────────────────────
            for (var round = 0; round < 10; round++)
            {
                var eapPayloads = currentInnerPayloads.Where(p => p.Type == IkePayloadType.Eap).ToArray();
                if (eapPayloads.Length > 1) throw new IkeFormatException("Duplicate EAP payloads in IKE_AUTH.");
                networkRejection = IkeNetworkRejection.Capture(currentInnerPayloads, initialResponderAuthVerified);
                if (networkRejection != null && request.ObserveNetworkRejection != null)
                {
                    using var commit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await request.ObserveNetworkRejection(networkRejection, transport.GatewayAddress, commit.Token).ConfigureAwait(false);
                }
                foreach (var note in currentInnerPayloads.Where(p => p.Type == IkePayloadType.Notify))
                {
                    if (note.Body.Length < 4 || note.Body[1] > note.Body.Length - 4)
                        throw new IkeFormatException("Malformed IKE_AUTH notification.");
                    var kind = BinaryPrimitives.ReadUInt16BigEndian(note.Body.AsSpan(2, 2));
                    backoffSeen |= kind == NotifyBackoffTimer;
                    if (kind < 16384 && !(round == 0 && eapPayloads.Length == 0 && IsAddressFamilyRejection(currentInnerPayloads)))
                        throw new IkeFormatException(DescribeIkeAuthFailure(currentInnerPayloads) ?? $"ePDG returned fatal IKE_AUTH Notify {kind}.");
                }
                var eapPayload = eapPayloads.SingleOrDefault();
                if (eapPayload == null)
                {
                    if (round == 0 && IsAddressFamilyRejection(currentInnerPayloads))
                    {
                        retryableFamilyFailure = true;
                        preEapFamilyFailure = true;
                        throw new IkeFormatException("ePDG rejected the requested inner IP family (INTERNAL_ADDRESS_FAILURE / TS_UNACCEPTABLE) before any AKA challenge.");
                    }
                    var authFailure = DescribeIkeAuthFailure(currentInnerPayloads);
                    if (authFailure != null)
                        throw new IkeFormatException(authFailure);

                    var notifyList = currentInnerPayloads
                        .Where(p => p.Type == IkePayloadType.Notify && p.Body.Length >= 4)
                        .Select(DescribeNotify)
                        .ToList();
                    var types = string.Join(", ", currentInnerPayloads.Select(p => p.Type.ToString()));
                    if (!string.IsNullOrWhiteSpace(eapClient.LastAkaFailure))
                    {
                        throw new IkeFormatException(
                            $"IKE_AUTH round {round + 1} received no EAP payload after a local EAP-AKA response. " +
                            $"{eapClient.LastAkaFailure} ePDG response payloads: [{types}], Notifies: [{string.Join(", ", notifyList)}]");
                    }
                    throw new IkeFormatException($"IKE_AUTH round {round + 1} did not contain an EAP payload. Inner payloads: [{types}], Notifies: [{string.Join(", ", notifyList)}]");
                }

                Trace($"IKE stage=IKE_AUTH EAP round={round + 1}; payload-bytes={eapPayload.Body.Length}.");
                (byte[]? eapResponse, bool isSuccess) eapStep;
                try { eapStep = await eapClient.HandleAsync(eapPayload.Body, ct).ConfigureAwait(false); }
                catch (AuthenticationException)
                {
                    identityRejectedBeforeChallenge = eapClient.IdentityRejectedBeforeChallenge;
                    throw;
                }
                var (eapResponse, isSuccess) = eapStep;
                if (isSuccess)
                {
                    eapSucceeded = true;
                    Trace($"IKE stage=IKE_AUTH EAP mutual authentication complete; rounds={round + 1}; result-indication={eapClient.ResultIndication}; protected-success={eapClient.ProtectedSuccess}.");
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

                Trace($"IKE stage=IKE_AUTH send EAP response; round={round + 1}; response-bytes={eapResponse.Length}; device-identity={deviceRequest is not null && !string.IsNullOrWhiteSpace(request.Imei)}.");
                nextCleanupMessageId = authMsgId + 1;
                var roundResponse = await transport.RoundTripEncryptedAsync(nextReq, nextReqInner, negotiatedSuite,
                    ikeKeys.SkEi, ikeKeys.SkAi, ikeKeys.SkEr, ikeKeys.SkAr, fragmentationNegotiated, ct).ConfigureAwait(false);
                var (roundHeader, inner) = (roundResponse.Header, roundResponse.Payloads);
                ValidateHandshakeResponse(roundHeader, initiatorSpi, responderSpi, IkeExchangeType.IkeAuth, authMsgId);
                currentInnerPayloads = inner;
                peerIdrPayload = IkeChildNegotiation.MergeIdentity(peerIdrPayload, currentInnerPayloads);

                if (round == 9)
                    throw new TimeoutException("EAP exchange exceeded maximum 10 rounds.");
            }

            if (!eapClient.ChallengeComplete || eapClient.Keys?.Msk == null)
                throw new InvalidOperationException("EAP-AKA finished without producing an authenticated MSK.");

            // ── 4. Final IKE_AUTH: Initiator AUTH payload ────────────────────
            stage = "IKE_AUTH/final-AUTH";
            authMsgId++;
            var msk = eapClient.Keys.Msk;

            // Compute Initiator AUTH:
            // idHash = prf(SK_pi, IDi.Body)
            // signed = initRequestBytes + responderNonce + idHash
            // paddedKey = prf(MSK, "Key Pad for IKEv2")
            // authValue = prf(paddedKey, signed)
            var idHash = Secret(IkeCrypto.Prf(negotiatedSuite, ikeKeys.SkPi, idiPayload.Body));
            var signedOctets = Secret(Combine(initReqBytes, responderNonce, idHash));
            var keyPad = Secret(IkeCrypto.Prf(negotiatedSuite, msk, Encoding.ASCII.GetBytes("Key Pad for IKEv2")));
            var authValue = Secret(IkeCrypto.Prf(negotiatedSuite, keyPad, signedOctets));

            var authBody = Secret(Combine(new byte[] { AuthMethodSharedKeyMic, 0, 0, 0 }, authValue));
            var initiatorAuthPayload = new IkePayload(IkePayloadType.Authentication, authBody);

            var finalReq = new IkeWire.IkeMessage
            {
                InitiatorSpi = initiatorSpi,
                ResponderSpi = responderSpi,
                Exchange = IkeExchangeType.IkeAuth,
                Flags = IkeFlags.Initiator,
                MessageId = authMsgId
            };

            Trace("IKE stage=final-AUTH send.");
            nextCleanupMessageId = authMsgId + 1;
            var finalResponse = await transport.RoundTripEncryptedAsync(finalReq, new[] { initiatorAuthPayload }, negotiatedSuite,
                ikeKeys.SkEi, ikeKeys.SkAi, ikeKeys.SkEr, ikeKeys.SkAr, fragmentationNegotiated, ct).ConfigureAwait(false);
            var (finalHeader, finalPayloads) = (finalResponse.Header, finalResponse.Payloads);
            ValidateHandshakeResponse(finalHeader, initiatorSpi, responderSpi, IkeExchangeType.IkeAuth, authMsgId);
            peerIdrPayload = IkeChildNegotiation.MergeIdentity(peerIdrPayload, finalPayloads);
            void VerifyCurrentFinalAUTH()
            {
                var respAuthPayload = IkeChildNegotiation.Require(finalPayloads, IkePayloadType.Authentication);
                if (peerIdrPayload?.Body is not { Length: > 4 })
                    throw new AuthenticationException("Responder did not provide an identity for final AUTH verification.");
                var respIdBody = peerIdrPayload.Body;
                var respIdHash = Secret(IkeCrypto.Prf(negotiatedSuite, ikeKeys.SkPr, respIdBody));
                var respSignedOctets = Secret(Combine(initRespBytes, initiatorNonce, respIdHash));
                var expectedRespAuth = Secret(IkeCrypto.Prf(negotiatedSuite, keyPad, respSignedOctets));
                VerifyFinalResponderAuth(respAuthPayload, expectedRespAuth);
                finalAuthVerified = true;
            }
            // A coherent Notify alone is not evidence: defer its policy until
            // the current MSK AUTH authenticates an EAP-only responder.
            if (!initialResponderAuthVerified && IkeNetworkRejection.Capture(finalPayloads, true) != null)
                VerifyCurrentFinalAUTH();
            networkRejection = IkeNetworkRejection.Capture(finalPayloads, initialResponderAuthVerified || finalAuthVerified);
            if (networkRejection != null && request.ObserveNetworkRejection != null)
            {
                using var commit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await request.ObserveNetworkRejection(networkRejection, transport.GatewayAddress, commit.Token).ConfigureAwait(false);
            }
            foreach (var note in finalPayloads.Where(p => p.Type == IkePayloadType.Notify))
            {
                if (note.Body.Length < 4 || note.Body[1] > note.Body.Length - 4)
                    throw new IkeFormatException("Malformed final IKE_AUTH notification.");
                var kind = BinaryPrimitives.ReadUInt16BigEndian(note.Body.AsSpan(2, 2));
                backoffSeen |= kind == NotifyBackoffTimer;
                if (kind < 16384)
                    throw new IkeFormatException(DescribeIkeAuthFailure(finalPayloads) ?? $"ePDG returned fatal final IKE_AUTH Notify {kind}.");
            }

            // ── 5. Verify Responder AUTH (RFC 7296 §2.15 / 3GPP TS 33.402) ────
            VerifyCurrentFinalAUTH();

            // ── 6. Parse CHILD_SA, CP, and Key Material ──────────────────────
            stage = "CHILD_SA/configuration";
            var finalSaPayload = IkeChildNegotiation.Require(finalPayloads, IkePayloadType.SecurityAssociation);
            var finalProposals = IkeWire.DecodeProposals(finalSaPayload.Body);
            if (finalProposals.Count != 1 || finalProposals[0].Spi.Length != 4)
                throw new IkeFormatException("Final IKE_AUTH response has invalid ESP proposals.");

            var childOutboundSpi = BinaryPrimitives.ReadUInt32BigEndian(finalProposals[0].Spi);
            var negotiatedChildSuite = EspSuite.FromProposal(finalProposals[0]);
            if (!childInboundSpis.TryGetValue(finalProposals[0].ProposalNumber, out var childInboundSpi))
                throw new IkeFormatException($"ePDG selected unknown ESP proposal {finalProposals[0].ProposalNumber}.");
            ValidateChosenSuite(finalProposals[0].ProposalNumber, negotiatedChildSuite, childSuites);

            IkeChildConfiguration config;
            try { config = IkeChildNegotiation.Configuration(finalPayloads, tsiPayload, tsrPayload, request.AddressFamilyMode, request.FallbackPcscf); }
            catch (IkeChildAddressFamilyException) { retryableFamilyFailure = true; throw; }
            var assignedIp = config.Assigned;
            var finalPcscf = config.Pcscf;
            var dnsList = config.Dns;
            var (outEnc, outAuth, inEnc, inAuth) = IkeCrypto.DeriveChildSaKeys(
                negotiatedSuite, negotiatedChildSuite, ikeKeys.SkD, initiatorNonce, responderNonce);
            uncommittedChildKeys = [outEnc, outAuth, inEnc, inAuth];
            var assignedFamily = IPAddress.TryParse(assignedIp, out var assignedAddress)
                ? assignedAddress.AddressFamily.ToString()
                : "unknown";
            var pcscfFamily = IPAddress.TryParse(finalPcscf, out var pcscfAddress)
                ? pcscfAddress.AddressFamily.ToString()
                : "unknown";
            Trace($"IKE stage=CHILD_SA complete; esp-encryption={negotiatedChildSuite.EncryptionId}-{negotiatedChildSuite.EncryptionBits}; esp-integrity={negotiatedChildSuite.IntegrityId}; assigned-family={assignedFamily}; dns-count={dnsList.Count}; pcscf-family={pcscfFamily}.");

            var controller = new IkeSessionController(
                transport, negotiatedSuite, initiatorSpi, responderSpi, authMsgId,
                childInboundSpi, childOutboundSpi, ikeKeys.SkEi, ikeKeys.SkAi, ikeKeys.SkEr, ikeKeys.SkAr,
                request.DiagnosticLog, fragmentationNegotiated);
            uncommittedChildKeys = null;

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
                LivenessProbe: new IkeLivenessProbe(controller),
                EapSucceeded: true,
                AddressFamilyMode: request.AddressFamilyMode,
                Controller: controller,
                ChildPolicy: config.Policy,
                FinalAuthVerified: true,
                InitialResponderAuthVerified: initialResponderAuthVerified
            );
        }
        catch (Exception ex)
        {
            var cleanupError = await CleanupFailedAttemptAsync(deletePendingIke, () => transport?.Dispose()).ConfigureAwait(false);
            var retryProofAllowed = cleanupError == null && !backoffSeen && !ct.IsCancellationRequested && !IkeAdmissionException.Contains(ex);
            Trace($"IKE stage={stage} failed; exception={ex.GetType().Name}; detail={ex.Message}");
            ct.ThrowIfCancellationRequested();
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
                ErrorMessage: cleanupError == null ? ex.Message : $"{ex.Message} Pending IKE cleanup failed: {cleanupError.GetType().Name}.",
                EapSucceeded: eapSucceeded,
                AddressFamilyMode: request.AddressFamilyMode,
                RequestedEapType: retryProofAllowed && ex is EapMethodMismatchException { IdentityOnlyExchange: true } mismatch ? mismatch.RequestedType : null,
                RetryableTransportFailure: retryProofAllowed && initialRouteFailure,
                RetryableAddressFamilyFailure: retryProofAllowed && retryableFamilyFailure,
                RetryableIdentityRejection: retryProofAllowed && identityRejectedBeforeChallenge && ex is AuthenticationException,
                RetryableInitialTimeout: retryProofAllowed && initialTimeout,
                RetryableInitialProposal: retryProofAllowed && initialProposalFailure,
                PreEapFamilyFailure: retryProofAllowed && preEapFamilyFailure,
                FinalAuthVerified: finalAuthVerified,
                FailureCleanupComplete: cleanupError == null,
                InitialResponderAuthVerified: initialResponderAuthVerified,
                NetworkRejection: networkRejection
            );
        }

        finally
        {
            foreach (var secret in temporarySecrets) CryptographicOperations.ZeroMemory(secret);
            if (uncommittedChildKeys != null) foreach (var key in uncommittedChildKeys) CryptographicOperations.ZeroMemory(key);
            if (ownedIkeKeys != null) foreach (var key in new[] { ownedIkeKeys.SkD, ownedIkeKeys.SkAi, ownedIkeKeys.SkAr, ownedIkeKeys.SkEi, ownedIkeKeys.SkEr, ownedIkeKeys.SkPi, ownedIkeKeys.SkPr }) CryptographicOperations.ZeroMemory(key);
        }

        static string currentPayloadNames(IEnumerable<IkePayload> payloads) =>
            string.Join(',', payloads.Select(payload => payload.Type.ToString()));
    }

    internal static void VerifyFinalResponderAuth(IkePayload? authPayload, ReadOnlySpan<byte> expectedMic)
    {
        if (authPayload?.Type != IkePayloadType.Authentication || authPayload.Body is not { Length: >= 5 } body ||
            body[0] != AuthMethodSharedKeyMic ||
            expectedMic.Length is not (20 or 32 or 48 or 64) ||
            body.Length != expectedMic.Length + 4 ||
            !CryptographicOperations.FixedTimeEquals(body.AsSpan(4), expectedMic))
        {
            throw new AuthenticationException("Responder final AUTH is missing or invalid.");
        }
    }

    internal static void ValidateHandshakeResponse(
        IkeWire.IkeMessage header,
        ulong initiatorSpi,
        ulong responderSpi,
        IkeExchangeType exchange,
        uint messageId,
        bool allowZeroResponderSpi = false)
    {
        if ((header.Version >> 4) != 2 ||
            header.InitiatorSpi != initiatorSpi ||
            (!allowZeroResponderSpi && header.ResponderSpi != responderSpi) ||
            header.Exchange != exchange ||
            header.MessageId != messageId ||
            !header.Flags.HasFlag(IkeFlags.Response) ||
            header.Flags.HasFlag(IkeFlags.Initiator))
        {
            throw new IkeFormatException("IKE response did not match the active exchange.");
        }
    }

    internal static void ValidateChosenSuite(byte proposalNumber, IkeSuite chosen, IReadOnlyList<IkeSuite> offered)
    {
        if (proposalNumber == 0 || proposalNumber > offered.Count || chosen != offered[proposalNumber - 1])
            throw new IkeFormatException("Responder selected an IKE suite that was not offered.");
    }

    internal static void ValidateChosenSuite(byte proposalNumber, EspSuite chosen, IReadOnlyList<EspSuite> offered)
    {
        if (proposalNumber == 0 || proposalNumber > offered.Count || chosen != offered[proposalNumber - 1])
            throw new IkeFormatException("Responder selected an ESP suite that was not offered.");
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
        var authenticationFailed = notifications.FirstOrDefault(notification => notification.Type == 24);
        if (authenticationFailed != null)
        {
            return "ePDG rejected the initial IKE_AUTH with AUTHENTICATION_FAILED (Notify 24) " +
                   "before sending an EAP-AKA challenge; the local SIM was not queried. " +
                   "This is an ePDG/AAA identity or access-policy decision (for example VoWiFi " +
                   "provisioning, non-3GPP access policy, or the current exit IP), not a SOCKS5 " +
                   "reachability or USIM-AKA calculation failure.";
        }

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
        24 => "AUTHENTICATION_FAILED",
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
        _ when notifyType is >= 8192 and <= 16383 => "PRIVATE_USE_ERROR_NOTIFY",
        _ when notifyType >= 40960 => "PRIVATE_USE_STATUS_NOTIFY",
        _ => $"NotifyType={notifyType}"
    };

    internal static string? DescribeBackoffTimer(ReadOnlySpan<byte> data)
    {
        // TS 24.302 normally prefixes the one-octet GPRS Timer 3 value with
        // its length.  Accept a bare value too, as some ePDGs omit the prefix.
        if (data.Length == 0 || data.Length > 2 || data.Length == 2 && data[0] != 1)
            return null;

        var timer = IkeBackoffTimer.DecodeValue(data[^1]);
        if (timer.Status == IkeBackoffTimerStatus.Deactivated)
            return "does not expire";

        var duration = timer.Duration!.Value;

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

    internal static (string? AssignedIp, List<string> DnsList, List<string> PcscfList) ParseConfigurationPayload(ReadOnlySpan<byte> body,
        IkeAddressFamilyMode mode = IkeAddressFamilyMode.Dual, string? fallbackPcscf = null)
    {
        if (body.Length < 4 || body[0] != 2) // CFG_REPLY
            return (null, new List<string>(), new List<string>());

        // Parse atomically: a malformed suffix must not leave a usable-looking
        // address/P-CSCF pair from the prefix of an invalid payload.
        var config = IkeConfigurationReply.Parse(body);
        var assignedIps = config.AssignedAddresses.Select(value => value.ToString()).ToList();
        var dnsList = config.DnsAddresses.Select(value => value.ToString()).ToList();
        var pcscfList = config.PcscfAddresses.Select(value => value.ToString()).ToList();

        // CP attribute order is not a preference. Preserve both families until
        // choosing a complete assigned-address/P-CSCF pair, independently of
        // the outer IKE socket's family. One IMS flow uses one coherent pair.
        var preferred = mode == IkeAddressFamilyMode.Ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        var ordered = assignedIps.Distinct().OrderBy(value => IPAddress.Parse(value).AddressFamily == preferred ? 0 : 1).ToArray();
        var assignedIp = ordered.FirstOrDefault(value => IsUsablePdn(value, PickCompatiblePcscf(pcscfList, value, fallbackPcscf)))
            ?? ordered.FirstOrDefault();
        return (assignedIp, dnsList, pcscfList);
    }

    internal static bool IsRouteFailure(Exception error)
    {
        if (IkeAdmissionException.Contains(error)) return false;
        if (error is SocketException socket)
            return socket.SocketErrorCode is SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable or
                SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or
                SocketError.ConnectionRefused or SocketError.ConnectionReset;
        return error is InvalidOperationException && error.InnerException != null && IsRouteFailure(error.InnerException);
    }

    internal static bool IsAddressFamilyRejection(IReadOnlyList<IkePayload> payloads)
    {
        var found = false;
        foreach (var payload in payloads)
        {
            if (payload.Type is IkePayloadType.IdentificationResponder or IkePayloadType.Authentication or IkePayloadType.Certificate)
            {
                if (payload.Body.Length < (payload.Type == IkePayloadType.Certificate ? 1 : 5)) return false;
                continue;
            }
            if (payload.Type != IkePayloadType.Notify || payload.Body.Length < 4 || payload.Body[1] != 0) return false;
            // Protocol ID is ignored on receipt when the SPI is empty.
            var type = BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2));
            if (type is 36 or 38)
            {
                if (payload.Body.Length != 4) return false;
                found = true;
            }
            else if (type < 16384 || type == NotifyBackoffTimer) return false;
        }
        return found;
    }

    internal static async Task<Exception?> CleanupFailedAttemptAsync(Func<CancellationToken, Task>? delete, Action close)
    {
        Exception? failure = null;
        if (delete != null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await delete(cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
        }
        try { close(); }
        catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
        return failure;
    }

    private static bool IsInitialProposalRejection(IReadOnlyList<IkePayload> payloads)
    {
        var found = false;
        foreach (var note in payloads)
        {
            if (note.Type != IkePayloadType.Notify || note.Body.Length < 4 || note.Body[1] != 0) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(note.Body.AsSpan(2, 2));
            if (type == 14 && note.Body.Length == 4 || type == 17 && note.Body.Length == 6) found = true;
            else if (type < 16384 || type == NotifyBackoffTimer) return false;
        }
        return found;
    }

    internal static IkePayload RequireSingleInitPayload(IkeWire.IkeMessage message, IkePayloadType type)
    {
        var values = message.Payloads.Where(payload => payload.Type == type).ToArray();
        if (values.Length != 1) throw new IkeFormatException("IKE_SA_INIT requires exactly one SA, KE and Nonce payload.");
        return values[0];
    }

    internal static bool ReadFragmentationSupport(IReadOnlyList<IkePayload> payloads)
    {
        var notes = payloads.Where(p => p.Type == IkePayloadType.Notify && p.Body.Length >= 4 &&
            BinaryPrimitives.ReadUInt16BigEndian(p.Body.AsSpan(2, 2)) == NotifyIkeFragmentationSupported).ToArray();
        if (notes.Length > 1 || notes.Any(p => p.Body.Length != 4 || p.Body[0] != 0 || p.Body[1] != 0))
            throw new IkeFormatException("Invalid IKE fragmentation support notification.");
        return notes.Length == 1;
    }

    internal static IkePayload? ReadInitCookie(IkeWire.IkeMessage message)
    {
        var cookies = message.Payloads.Where(payload => payload.Type == IkePayloadType.Notify && payload.Body.Length >= 4 &&
            BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2)) == NotifyCookie).ToArray();
        if (cookies.Length == 0) return null;
        // RFC 7296 3.10: Protocol ID is ignored on receipt when SPI is empty.
        if (cookies.Length != 1 || cookies[0].Body.Length is < 5 or > 68 || cookies[0].Body[1] != 0 ||
            message.Payloads.Any(payload => payload.Type is IkePayloadType.SecurityAssociation or IkePayloadType.KeyExchange or IkePayloadType.Nonce ||
                payload.Type == IkePayloadType.Notify && (payload.Body.Length < 4 || payload.Body[1] > payload.Body.Length - 4 ||
                    BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2)) < 16384)))
            throw new IkeFormatException("Invalid or ambiguous IKE stateless cookie challenge.");
        return cookies[0];
    }

    internal static void ValidateInitNonce(ReadOnlySpan<byte> nonce, IkeSuite suite)
    {
        if (nonce.Length < Math.Max(16, suite.PrfLength / 2) || nonce.Length > 256)
            throw new IkeFormatException("IKE_SA_INIT nonce length is invalid.");
    }

    private static string? PickCompatiblePcscf(IReadOnlyList<string> candidates, string? assigned, string? fallback)
    {
        var selected = PickPcscf(candidates, assigned);
        return IsUsablePdn(assigned, selected) ? selected : IsUsablePdn(assigned, fallback) ? fallback : selected ?? fallback;
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
            if (val >= 256) return val;
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
