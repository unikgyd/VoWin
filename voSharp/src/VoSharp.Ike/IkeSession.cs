using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using VoSharp.Common.Aka;

namespace VoSharp.Ike;

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
    VoSharp.Ike.Transport.Socks5Client? Socks5Client = null
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
    IkeLivenessProbe? LivenessProbe = null
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
    private const ushort NotifyMobikeSupported = 16396;
    private const ushort NotifyEapOnlyAuthentication = 16417;
    private const ushort NotifyDeviceIdentity = 16488;

    private const byte AuthMethodSharedKeyMic = 2;

    public static async Task<IkeSessionResult> EstablishAsync(
        IkeSessionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ikeSuite = request.Suite ?? IkeSuite.Preferred;
        var childSuite = request.ChildSuite ?? EspSuite.Preferred;

        var socksClient = request.Socks5Client ?? VoSharp.Ike.Transport.Socks5Client.TryParse(request.ProxyUrl);
        var transport = new IkeTransport(request.EpdgIp, socks5Client: socksClient);
        try
        {
            // ── 1. IKE_SA_INIT ──────────────────────────────────────────────
            var initiatorSpi = GenerateNonZeroUInt64();
            var initiatorNonce = new byte[IkeDefaults.NonceLength];
            RandomNumberGenerator.Fill(initiatorNonce);

            using var dh = IkeDhKeyExchange.Create(ikeSuite.DhGroupId);

            var ikeProposal = new IkeProposal
            {
                ProposalNumber = 1,
                Protocol = IkeProtocolId.Ike,
                Transforms = ikeSuite.ToTransforms()
            };

            var saBody = IkeWire.EncodeProposals(new[] { ikeProposal });

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
                MakeNotify(NotifyMobikeSupported, Array.Empty<byte>())
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
            if (initResp.ResponderSpi == 0)
                throw new IkeFormatException("ePDG returned zero Responder SPI in IKE_SA_INIT response.");

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

            var childInboundSpi = GenerateNonZeroUInt32();
            var childInboundSpiBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(childInboundSpiBytes, childInboundSpi);

            var childProposal = new IkeProposal
            {
                ProposalNumber = 1,
                Protocol = IkeProtocolId.Esp,
                Spi = childInboundSpiBytes,
                Transforms = childSuite.ToTransforms()
            };

            var childSaBody = IkeWire.EncodeProposals(new[] { childProposal });

            // IDi: ID_RFC822_ADDR (3) + NAI
            var idiBody = Combine(new byte[] { 3, 0, 0, 0 }, eapClient.Identity);
            var idiPayload = new IkePayload(IkePayloadType.IdentificationInitiator, idiBody);

            // IDr: ID_FQDN (2) + APN
            var idrBody = Combine(new byte[] { 2, 0, 0, 0 }, Encoding.UTF8.GetBytes(request.Apn));
            var idrPayload = new IkePayload(IkePayloadType.IdentificationResponder, idrBody);

            var tsiPayload = BuildDualStackTrafficSelectors(IkePayloadType.TrafficSelectorInitiator);
            var tsrPayload = BuildDualStackTrafficSelectors(IkePayloadType.TrafficSelectorResponder);
            var cpPayload = BuildConfigurationRequest();

            var firstAuthInner = new List<IkePayload>
            {
                idiPayload,
                idrPayload,
                MakeNotify(NotifyEapOnlyAuthentication, Array.Empty<byte>()),
                MakeNotify(NotifyMobikeSupported, Array.Empty<byte>()),
                MakeNotify(NotifyInitialContact, Array.Empty<byte>()),
                new(IkePayloadType.SecurityAssociation, childSaBody),
                tsiPayload,
                tsrPayload,
                cpPayload
            };

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
                    var notifyList = currentInnerPayloads
                        .Where(p => p.Type == IkePayloadType.Notify && p.Body.Length >= 4)
                        .Select(p => $"NotifyType={BinaryPrimitives.ReadUInt16BigEndian(p.Body.AsSpan(2, 2))} (Data={Convert.ToHexString(p.Body[4..])})")
                        .ToList();
                    var types = string.Join(", ", currentInnerPayloads.Select(p => p.Type.ToString()));
                    throw new IkeFormatException($"IKE_AUTH round {round + 1} did not contain an EAP payload. Inner payloads: [{types}], Notifies: [{string.Join(", ", notifyList)}]");
                }

                Console.WriteLine($"[IKE] Round {round + 1}: EAP Body Len={eapPayload.Body.Length}, Hex={Convert.ToHexString(eapPayload.Body)}");
                var (eapResponse, isSuccess) = await eapClient.HandleAsync(eapPayload.Body, ct).ConfigureAwait(false);
                if (isSuccess)
                    break;

                if (eapResponse == null || eapResponse.Length == 0)
                    throw new InvalidOperationException("EAP state machine produced no response.");

                authMsgId++;
                var nextReqInner = new List<IkePayload> { new(IkePayloadType.Eap, eapResponse) };

                // Append DeviceIdentity notify if requested and available
                if (request.Imei != null && currentInnerPayloads.Any(p => p.Type == IkePayloadType.Notify &&
                    BinaryPrimitives.ReadUInt16BigEndian(p.Body.AsSpan(2, 2)) == NotifyDeviceIdentity))
                {
                    nextReqInner.Add(MakeDeviceIdentityNotify(request.Imei));
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

            var (outEnc, outAuth, inEnc, inAuth) = IkeCrypto.DeriveChildSaKeys(
                negotiatedSuite, negotiatedChildSuite, ikeKeys.SkD, initiatorNonce, responderNonce);

            // Parse CP (Configuration Payload)
            var cpRespPayload = finalPayloads.FirstOrDefault(p => p.Type == IkePayloadType.Configuration);
            var (assignedIp, dnsList, pcscfList) = cpRespPayload != null
                ? ParseConfigurationPayload(cpRespPayload.Body)
                : (null, new List<string>(), new List<string>());

            var finalPcscf = pcscfList.FirstOrDefault() ?? request.FallbackPcscf;
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
                    ikeKeys.SkEi, ikeKeys.SkAi, ikeKeys.SkEr, ikeKeys.SkAr)
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
                ErrorMessage: ex.Message
            );
        }
    }

    private static bool DetectNat(
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

        var expectedSrc = IkeCrypto.ComputeNatDetectionHash(initiatorSpi, responderSpi, localEp.Address, (ushort)localEp.Port);
        var expectedDst = IkeCrypto.ComputeNatDetectionHash(initiatorSpi, responderSpi, remoteEp.Address, (ushort)remoteEp.Port);

        var srcMatches = CryptographicOperations.FixedTimeEquals(srcHash, expectedSrc);
        var dstMatches = CryptographicOperations.FixedTimeEquals(dstHash, expectedDst);

        return !srcMatches || !dstMatches;
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

    private static IkePayload MakeDeviceIdentityNotify(string imei)
    {
        // 3GPP TS 24.302 §8.2.9.2: 0x01 (IMEI) followed by identity digits
        var imeiBytes = Encoding.ASCII.GetBytes(imei);
        var data = Combine(new byte[] { 0x01 }, imeiBytes);
        return MakeNotify(NotifyDeviceIdentity, data);
    }

    private static IkePayload BuildDualStackTrafficSelectors(IkePayloadType type)
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

        var body = Combine(new byte[] { 2, 0, 0, 0 }, ipv4, ipv6);
        return new IkePayload(type, body);
    }

    private static IkePayload BuildConfigurationRequest()
    {
        // CFG_REQUEST (1), reserved (3 bytes)
        var attrTypes = new ushort[]
        {
            1,  // INTERNAL_IP4_ADDRESS
            8,  // INTERNAL_IP6_ADDRESS
            3,  // INTERNAL_IP4_DNS
            10, // INTERNAL_IP6_DNS
            20, // P_CSCF_IP4_ADDRESS
            21  // P_CSCF_IP6_ADDRESS
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

    private static (string? AssignedIp, List<string> DnsList, List<string> PcscfList) ParseConfigurationPayload(ReadOnlySpan<byte> body)
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
            }
        }

        return (assignedIp, dnsList, pcscfList);
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
