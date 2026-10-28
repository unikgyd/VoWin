using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VoSharp.Common.Aka;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sip;
using VoSharp.Sim;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Telephony;

/// <summary>
/// Registers over an IMS PDN address that Windows already owns. It does not
/// activate a PDP context; an explicit security backend is required when the
/// carrier negotiates ipsec-3gpp.
/// </summary>
public sealed class HostImsRegistrationClient : IAsyncDisposable
{
    private readonly SipRegisterSession _registration;
    private readonly Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> _akaProvider;
    private readonly int _interfaceIndex;
    private IHostImsSecurityReservation? _securityReservation;
    private Func<System.Net.IPAddress, CancellationToken, Task>? _mediaRouteProvisioner;
    private Func<CancellationToken, Task>? _mediaRouteClearer;
    private int _disposed;
    private int _invalidated;
    private readonly SemaphoreSlim _smsSendGate = new(1, 1);
    private int _nextSmsReference = RandomNumberGenerator.GetInt32(1, 255);
    private sealed record PendingSmsReport(byte Reference, DateTime CreatedAt,
        TaskCompletionSource<RpMessage> Completion);
    private readonly ConcurrentDictionary<string, PendingSmsReport> _pendingSmsReports =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SmsReassembler _smsReassembler = new();
    private sealed record ReceivedSmsTransaction(DateTime CreatedAt, string Gateway,
        string PayloadHash, Lazy<byte[]> DeliveryReport);
    private readonly ConcurrentDictionary<string, ReceivedSmsTransaction> _receivedSmsTransactions =
        new(StringComparer.Ordinal);
    public event Action<IncomingSms>? IncomingSmsReceived;
    public event Action<SmsStatusReport>? SmsStatusReportReceived;
    public event Action<int>? SmsDeliveryReportAccepted;

    private HostImsRegistrationClient(
        HostImsEndpointCandidate endpoint,
        SipTransport transport,
        SipRegisterSession registration,
        SipRegistrationResult result,
        int interfaceIndex,
        Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> akaProvider,
        IHostImsSecurityReservation? securityReservation,
        string? cardPath)
    {
        Endpoint = endpoint;
        Transport = transport;
        _registration = registration;
        Result = result;
        _interfaceIndex = interfaceIndex;
        _akaProvider = akaProvider;
        _securityReservation = securityReservation;
        CardPath = cardPath;
    }

    public HostImsEndpointCandidate Endpoint { get; }
    public int InterfaceIndex => _interfaceIndex;
    public ulong? InterfaceLuid { get; private init; }
    /// <summary>The selected SIM application transport; null for direct test registrations.</summary>
    public string? CardPath { get; }
    public SipTransport Transport { get; }
    internal ImsProfile Profile => _registration.CurrentProfile;
    public bool CanReceiveSmsOverIms => Profile.AdvertiseSmsOverIms;
    /// <summary>The result of the initial REGISTER; use CurrentResult for live status.</summary>
    public SipRegistrationResult Result { get; }
    public SipRegistrationResult? CurrentResult =>
        Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _invalidated) == 0
            ? _registration.CurrentResult : null;
    public bool IsRegistered => CurrentResult is not null;
    public SipRegisterSession Registration => _registration;

    internal Task<string?> RefreshAccessNetworkInfoAsync(CancellationToken ct, TimeSpan? budget = null) =>
        _registration.RefreshAccessNetworkInfoAsync(ct, budget);

    /// <summary>
    /// Experimental MO SMS on this Windows-owned IMS registration. A SIP 2xx
    /// confirms receipt of MESSAGE, not RP-ACK or delivery to the handset.
    /// This sender deliberately does not advertise MT SMS capability yet.
    /// </summary>
    public async Task<SmsSubmitResult> SendSmsOverImsAsync(
        string recipient, string text, string simSmsCenter,
        bool requestStatusReport = false, CancellationToken ct = default)
    {
        if (!IsRegistered) throw new InvalidOperationException("Host IMS is not registered.");
        if (string.IsNullOrWhiteSpace(simSmsCenter))
            throw new InvalidOperationException("The selected SIM has no verified SMS centre address.");
        var smsc = SmsPdu.NormalizeRecipient(simSmsCenter);
        var parts = SmsPdu.PrepareSubmitParts(recipient, text,
            requestStatusReport: requestStatusReport, smsc: smsc);
        await _smsSendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PurgeExpiredSmsReports();
            var result = new SmsSubmitResult(recipient, text, parts[0].Encoding,
                parts[0].ConcatReference, parts.Count, 0, 0, false,
                "pending", [], DateTime.UtcNow);
            foreach (var part in parts)
            {
                if (!IsRegistered)
                    throw new InvalidOperationException("Host IMS registration ended during SMS submission; do not automatically retry an uncertain part.");
                var reference = unchecked((byte)Interlocked.Increment(ref _nextSmsReference));
                part.Tpdu[1] = reference;
                var rpData = ImsSmsHandler.BuildRpData(reference, smsc, part.Tpdu);
                await RefreshAccessNetworkInfoAsync(ct).ConfigureAwait(false);
                var request = BuildSmsMessage("tel:" + smsc, rpData);
                var callId = request.GetHeader("Call-ID")!;
                var pending = new PendingSmsReport(reference, DateTime.UtcNow,
                    new TaskCompletionSource<RpMessage>(TaskCreationOptions.RunContinuationsAsynchronously));
                _pendingSmsReports[callId] = pending;
                result = result with { PartsAttempted = result.PartsAttempted + 1 };
                int? sipCode = null;
                string partState;
                try
                {
                    var response = await Transport.SendAndReceiveFinalAsync(request,
                        timeoutMs: 15000, ct: ct).ConfigureAwait(false);
                    sipCode = response.StatusCode;
                    if (response.StatusCode is >= 200 and < 300)
                    {
                        try
                        {
                            var report = await pending.Completion.Task.WaitAsync(
                                TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                            partState = report.MessageType == 3
                                ? "rp_ack_delivery_unconfirmed" : "rp_error";
                        }
                        catch (TimeoutException)
                        {
                            partState = "sip_accepted_rp_report_pending";
                        }
                    }
                    else
                    {
                        _pendingSmsReports.TryRemove(callId, out _);
                        partState = "sip_rejected";
                    }
                }
                catch (TimeoutException)
                {
                    // The P-CSCF may have accepted the RP-DATA even though its
                    // final SIP response was lost. Retrying can duplicate SMS.
                    partState = "unknown_no_sip_final_response";
                }
                var accepted = partState == "rp_ack_delivery_unconfirmed";
                result.PartResults.Add(new SmsSubmitPartStatus(part.PartNumber,
                    part.TotalParts, reference, accepted, SipCode: sipCode,
                    SubmissionStatus: partState, SubmittedAt: DateTime.UtcNow));
                result = result with
                {
                    PartsAccepted = result.PartsAccepted + (accepted ? 1 : 0),
                    SubmissionStatus = partState
                };
                if (!accepted) break;
            }
            return result with { AllPartsAccepted = result.PartsAccepted == result.PartsTotal };
        }
        finally { _smsSendGate.Release(); }
    }

    /// <summary>Handles MO reports, and opt-in MT RP-DATA on a registered SMS receiver.</summary>
    public async Task HandleIncomingSmsReportAsync(
        SipMessage request, Func<SipMessage, Task> replySender)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(replySender);
        PurgeExpiredSmsReports();
        if (!IsRegistered || request.Method != "MESSAGE")
        {
            await replySender(request.CreateResponse(405, "Method Not Allowed")).ConfigureAwait(false);
            return;
        }
        if (!ImsSmsHandler.SupportsContentType(request.GetHeader("Content-Type")))
        {
            await replySender(request.CreateResponse(415, "Unsupported Media Type")).ConfigureAwait(false);
            return;
        }
        RpMessage report;
        byte[] payload;
        try
        {
            payload = ImsSmsHandler.ExtractSmsPayload(request, out _)
                ?? throw new FormatException("Missing SMS RP payload.");
            report = ImsSmsHandler.ParseRpdu(payload);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            await replySender(request.CreateResponse(400, "Bad SMS RP Payload")).ConfigureAwait(false);
            return;
        }
        if (report.MessageType == 1)
        {
            if (!Profile.AdvertiseSmsOverIms)
            {
                await replySender(request.CreateResponse(405, "SMS Delivery Not Enabled")).ConfigureAwait(false);
                return;
            }
            var callId = request.GetHeader("Call-ID");
            var cseq = request.GetHeader("CSeq");
            var gateway = FirstSipOrTelUri(request.GetHeader("P-Asserted-Identity"));
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(cseq) ||
                gateway is null)
            {
                await replySender(request.CreateResponse(400, "Invalid SMS Delivery Headers")).ConfigureAwait(false);
                return;
            }
            var transactionKey = $"{callId}|{cseq}";
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
            var transaction = _receivedSmsTransactions.GetOrAdd(transactionKey,
                _ => new ReceivedSmsTransaction(DateTime.UtcNow, gateway, payloadHash,
                    new Lazy<byte[]>(() => BuildIncomingSmsDeliveryReport(report),
                        LazyThreadSafetyMode.ExecutionAndPublication)));
            if (transaction.Gateway != gateway || transaction.PayloadHash != payloadHash)
            {
                await replySender(request.CreateResponse(488, "Conflicting SMS Transaction")).ConfigureAwait(false);
                return;
            }
            await replySender(request.CreateResponse(200, "OK")).ConfigureAwait(false);
            foreach (var entry in _receivedSmsTransactions)
                if (entry.Value.CreatedAt < DateTime.UtcNow.AddMinutes(-10))
                    _receivedSmsTransactions.TryRemove(entry.Key, out _);
            if (_receivedSmsTransactions.Count > 1024)
            {
                foreach (var stale in _receivedSmsTransactions
                    .OrderBy(entry => entry.Value.CreatedAt)
                    .Take(_receivedSmsTransactions.Count - 1024))
                    _receivedSmsTransactions.TryRemove(stale.Key, out _);
            }
            var deliveryReport = transaction.DeliveryReport.Value;
            var reportRequest = BuildSmsMessage(gateway, deliveryReport, callId);
            var reportResponse = await Transport.SendAndReceiveFinalAsync(reportRequest,
                timeoutMs: 15000).ConfigureAwait(false);
            if (reportResponse.StatusCode is < 200 or >= 300)
                throw new InvalidOperationException($"Host IMS SMS delivery report rejected: SIP {reportResponse.StatusCode}.");
            SmsDeliveryReportAccepted?.Invoke(reportResponse.StatusCode);
            return;
        }
        if (report.MessageType is not (3 or 5))
        {
            await replySender(request.CreateResponse(405, "SMS Delivery Not Enabled")).ConfigureAwait(false);
            return;
        }
        var inReplyTo = request.GetHeader("In-Reply-To")?.Trim().Trim('<', '>');
        if (string.IsNullOrEmpty(inReplyTo) ||
            !_pendingSmsReports.TryGetValue(inReplyTo, out var pending) ||
            pending.Reference != report.Reference)
        {
            await replySender(request.CreateResponse(488, "Unmatched SMS Report")).ConfigureAwait(false);
            return;
        }
        await replySender(request.CreateResponse(200, "OK")).ConfigureAwait(false);
        _pendingSmsReports.TryRemove(inReplyTo, out _);
        pending.Completion.TrySetResult(report);
    }

    private byte[] BuildIncomingSmsDeliveryReport(RpMessage report)
    {
        try
        {
            if (report.Tpdu is null) throw new FormatException("Missing MT SMS TPDU.");
            ImsSmsHandler.ValidateIncomingTpdu(report.Tpdu);
            var decoded = SmsPdu.DecodePdu("00" + Convert.ToHexString(report.Tpdu));
            if (decoded.IsStatusReport && decoded.StatusReport is { } statusReport)
                SmsStatusReportReceived?.Invoke(statusReport);
            else if (_smsReassembler.ProcessIncomingPart(decoded) is { } complete)
                IncomingSmsReceived?.Invoke(complete);
            return ImsSmsHandler.BuildRpAck(report.Reference, includeUserData: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SIP 200 acknowledges MESSAGE transport only; RP-ERROR rejects
            // a TPDU the application could not accept or decode.
            return ImsSmsHandler.BuildRpError(report.Reference, 95);
        }
    }

    private void PurgeExpiredSmsReports()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-2);
        foreach (var entry in _pendingSmsReports)
            if (entry.Value.CreatedAt < cutoff &&
                _pendingSmsReports.TryRemove(entry.Key, out var expired))
                expired.Completion.TrySetCanceled();
    }

    private SipMessage BuildSmsMessage(string target, byte[] rpData, string? inReplyTo = null)
    {
        var registration = CurrentResult
            ?? throw new InvalidOperationException("Host IMS registration ended before SMS send.");
        var profile = Profile;
        var identity = FirstAssociatedIdentity(registration.PAssociatedUri)
            ?? profile.PublicIdentity;
        if (!identity.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) &&
            !identity.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Host IMS registration has no usable public identity for SMS.");
        var local = Transport.LocalEndPoint
            ?? throw new InvalidOperationException("Host IMS has no bound SIP source endpoint.");
        var receivePort = Transport.ProtectedServerEndPoint?.Port ?? local.Port;
        var localHost = ImsRegisterBuilder.FormatHost(local.Address.ToString());
        var message = new SipMessage
        {
            IsRequest = true, Method = "MESSAGE", RequestUri = target,
            RawBody = rpData
        };
        message.SetHeader("Via", $"SIP/2.0/UDP {localHost}:{receivePort};branch=z9hG4bK{Guid.NewGuid():N};rport");
        message.SetHeader("Max-Forwards", "70");
        message.SetHeader("From", $"<{identity}>;tag={Guid.NewGuid():N}");
        message.SetHeader("To", $"<{target}>");
        message.SetHeader("Call-ID", $"{Guid.NewGuid():N}@{local.Address}");
        message.SetHeader("CSeq", "1 MESSAGE");
        if (!string.IsNullOrWhiteSpace(inReplyTo))
            message.SetHeader("In-Reply-To", inReplyTo);
        message.SetHeader("P-Preferred-Identity", $"<{identity}>");
        message.SetHeader("Accept-Contact", "*;+g.3gpp.smsip");
        message.SetHeader("Request-Disposition", "no-fork");
        if (!string.IsNullOrWhiteSpace(registration.ServiceRoute))
            message.SetHeader("Route", registration.ServiceRoute);
        if (!string.IsNullOrWhiteSpace(profile.PAccessNetworkInfo))
            message.SetHeader("P-Access-Network-Info", profile.PAccessNetworkInfo);
        message.SetHeader("Content-Type", ImsSmsHandler.SmsContentType);
        message.SetHeader("Content-Transfer-Encoding", "binary");
        message.SetHeader("Content-Length", rpData.Length.ToString());
        return message;
    }

    private static string? FirstAssociatedIdentity(string? identities)
    {
        if (string.IsNullOrWhiteSpace(identities)) return null;
        // Prefer a network-associated telephone IMPU, never an IMSI-derived
        // number. The registrar's other SIP IMPU is a valid fallback.
        var tel = Regex.Match(identities, @"<\s*(tel:\+?[0-9]+)(?:[;>])", RegexOptions.IgnoreCase);
        if (tel.Success) return tel.Groups[1].Value;
        var sip = Regex.Match(identities, @"<\s*(sip:[^>\s]+)\s*>", RegexOptions.IgnoreCase);
        return sip.Success ? sip.Groups[1].Value : null;
    }

    private static string? FirstSipOrTelUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"(?:<|^)[ \t]*((?:sip:|sips:|tel:)[^>\s;,]+)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Attach the PPP owner's exact-host-route provisioner before any Host IMS call starts.</summary>
    public void AttachMediaRouteProvisioner(Func<System.Net.IPAddress, CancellationToken, Task> provisioner)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _invalidated) != 0 ||
            Interlocked.CompareExchange(ref _mediaRouteProvisioner, provisioner, null) is not null)
            throw new InvalidOperationException("Host IMS media route provisioner cannot be changed on this session.");
    }

    public void AttachMediaRouteClearer(Func<CancellationToken, Task> clearer)
    {
        ArgumentNullException.ThrowIfNull(clearer);
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _invalidated) != 0 ||
            Interlocked.CompareExchange(ref _mediaRouteClearer, clearer, null) is not null)
            throw new InvalidOperationException("Host IMS media route clearer cannot be changed on this session.");
    }

    public Task ClearMediaRoutesAsync(CancellationToken ct = default) =>
        _mediaRouteClearer?.Invoke(ct) ?? Task.CompletedTask;

    public async Task EnsureMediaRouteAsync(System.Net.IPAddress destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!IsRegistered) throw new InvalidOperationException("Host IMS media requires a live registration.");
        ct.ThrowIfCancellationRequested();
        try
        {
            if (_mediaRouteProvisioner is { } provisioner)
                await provisioner(destination, ct).ConfigureAwait(false);
            else
            {
                // A future QMI/MBIM bearer has no PPP route owner. It still
                // must prove the media destination uses this IMS interface
                // and source address rather than silently taking Wi-Fi/VPN.
                var mediaEndpoint = Endpoint with { PcscfAddress = destination };
                var route = HostImsRouteVerifier.Check([mediaEndpoint], _interfaceIndex, InterfaceLuid).Single();
                if (!route.IsVerified || route.InterfaceIndex != _interfaceIndex)
                    throw new InvalidOperationException(route.Summary);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Report only a failure category and address family, never the
            // carrier SDP address or subscriber SIP payload.
            var reason = ex switch
            {
                ArgumentException => "invalid-media-address",
                TimeoutException => "route-owner-timeout",
                InvalidOperationException => "route-verification-or-owner-failed",
                _ => ex.GetType().Name
            };
            throw new InvalidOperationException(
                $"The Host IMS media destination has no verified route on the Windows bearer " +
                $"({destination.AddressFamily}; {reason}).", ex);
        }
    }

    /// <summary>Immediately closes the bearer when the SIM or modem changes.</summary>
    public void Invalidate()
    {
        if (Interlocked.Exchange(ref _invalidated, 1) == 0)
        {
            try { _registration.Dispose(); }
            finally { Interlocked.Exchange(ref _securityReservation, null)?.Dispose(); }
        }
    }

    /// <summary>Requests removal of this registration while the same card and bearer remain valid.</summary>
    public async Task<bool> DeregisterAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _invalidated) != 0)
            return false;
        await _registration.StopRefreshingAsync().ConfigureAwait(false);
        if (CurrentResult is null) return false;
        await _registration.DeregisterAsync(_akaProvider, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Prefers card-provisioned ISIM. A USIM-only card may use the temporary
    /// 3GPP identity only when the card directory proves ISIM absent and the
    /// selected home PLMN has an authoritative MNC length.
    /// </summary>
    public static async Task<HostImsRegistrationClient> RegisterWithCardAsync(
        ModemDriver modem,
        SimIdentity sim,
        HostImsProbeResult probe,
        HostImsEndpointCandidate endpoint,
        int localPort = 5060,
        int pcscfPort = 5060,
        CancellationToken ct = default,
        IHostImsSecurityBackend? securityBackend = null,
        Action<string>? diagnosticLog = null,
        bool enableSmsReceiver = false, SipRegistrationRetryGate? retryGate = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        ArgumentNullException.ThrowIfNull(sim);
        retryGate?.ThrowIfBlocked(sim.Iccid);
        return await RegisterWithCardAsync(modem.Session, modem.GetImeiAsync,
            sim, probe, endpoint, localPort, pcscfPort, ct, securityBackend,
            modem.TryProbeQmiIsimIdentityAsync, modem.AuthenticateQmiIsimAkaAsync,
            modem.TryCanAuthenticateQmiUsimAsync, modem.AuthenticateQmiUsimAkaAsync,
            token => TryReadCellularPaniAsync(modem.Session, token), diagnosticLog,
            enableSmsReceiver, retryGate).ConfigureAwait(false);
    }

    internal static async Task<HostImsRegistrationClient> RegisterWithCardAsync(
        IAtSession session,
        Func<CancellationToken, Task<string>> getImei,
        SimIdentity sim,
        HostImsProbeResult probe,
        HostImsEndpointCandidate endpoint,
        int localPort = 5060,
        int pcscfPort = 5060,
        CancellationToken ct = default,
        IHostImsSecurityBackend? securityBackend = null,
        Func<CancellationToken, Task<IsimIdentityProbeResult?>>? tryReadQmiIsim = null,
        Func<AkaChallenge, CancellationToken, Task<AkaResult>>? authenticateQmiIsim = null,
        Func<CancellationToken, Task<bool>>? tryQmiUsim = null,
        Func<AkaChallenge, CancellationToken, Task<AkaResult>>? authenticateQmiUsim = null,
        Func<CancellationToken, Task<string?>>? readCellularPani = null,
        Action<string>? diagnosticLog = null,
        bool enableSmsReceiver = false, SipRegistrationRetryGate? retryGate = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(getImei);
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(endpoint);
        retryGate?.ThrowIfBlocked(sim.Iccid);
        if (!session.IsOpen)
            throw new InvalidOperationException("The selected modem AT channel is not open.");
        if (probe.SimInserted == false || !probe.CanAttemptWindowsIms ||
            !probe.EndpointCandidates.Contains(endpoint) || !probe.IsRouteVerified(endpoint))
            throw new InvalidOperationException("The selected endpoint is not owned by an active Windows IMS context in this probe result.");

        if (tryReadQmiIsim is not null && authenticateQmiIsim is not null)
        {
            var qmiIdentity = await tryReadQmiIsim(ct).ConfigureAwait(false);
            if (qmiIdentity is { Readiness: IsimIdentityReadiness.Available, Identity: not null })
                return await RegisterWithSelectedIsimAsync(session, getImei, probe, endpoint,
                    tryReadQmiIsim, authenticateQmiIsim, sim.Iccid,
                    localPort, pcscfPort, ct, securityBackend, qmiIdentity, "QMI ISIM",
                    readCellularPani, diagnosticLog, enableSmsReceiver, retryGate)
                    .ConfigureAwait(false);
        }

        var identity = await new IsimIdentityReader(session).ReadAsync(ct).ConfigureAwait(false);
        if (identity.Readiness == IsimIdentityReadiness.Available)
            return await RegisterWithIsimAsync(session, getImei, probe, endpoint, localPort, pcscfPort, ct,
                securityBackend, sim.Iccid, readCellularPani, diagnosticLog,
                enableSmsReceiver, retryGate).ConfigureAwait(false);
        if (identity.Readiness != IsimIdentityReadiness.ApplicationAbsent)
            throw new InvalidOperationException($"Host IMS identity is unavailable: {identity.Summary}");

        var imei = await getImei(ct).ConfigureAwait(false);
        var profile = BuildUsimFallbackProfile(sim, imei, endpoint.LocalAddress.ToString());
        var usim = new Ec25AkaProvider(session);
        var qmiUsimReady = tryQmiUsim is not null && authenticateQmiUsim is not null &&
            await tryQmiUsim(ct).ConfigureAwait(false);
        if (qmiUsimReady)
            await VerifySelectedCardIccidAsync(session, sim.Iccid, ct).ConfigureAwait(false);
        else
            await VerifySelectedUsimAsync(usim, sim.Iccid, ct).ConfigureAwait(false);
        return await RegisterAsync(probe, endpoint, profile, async (rand, autn, token) =>
        {
            AkaResult result;
            if (qmiUsimReady)
            {
                await VerifySelectedCardIccidAsync(session, sim.Iccid, token).ConfigureAwait(false);
                result = await authenticateQmiUsim!(AkaChallenge.Create(rand, autn), token)
                    .ConfigureAwait(false);
            }
            else
            {
                await VerifySelectedUsimAsync(usim, sim.Iccid, token).ConfigureAwait(false);
                result = await usim.AuthenticateAsync(AkaChallenge.Create(rand, autn), token)
                    .ConfigureAwait(false);
            }
            if (!result.Success)
            {
                var error = result.SynchronizationFailure
                    ? "USIM IMS AKA synchronization failed."
                    : $"USIM IMS AKA failed: {result.ErrorMessage}";
                result.Clear();
                throw new InvalidOperationException(error);
            }
            return (result.Res!, result.Ck!, result.Ik!);
        }, localPort, pcscfPort, ct, securityBackend, qmiUsimReady ? "QMI USIM" : "AT USIM",
            readCellularPani, diagnosticLog, enableSmsReceiver, retryGate, sim.Iccid)
            .ConfigureAwait(false);
    }

    internal static ImsProfile BuildUsimFallbackProfile(SimIdentity sim, string imei, string localIp)
    {
        ArgumentNullException.ThrowIfNull(sim);
        if (!sim.IsHomePlmnAuthoritative || sim.HasPlmnConflict ||
            sim.Iccid.Length is < 18 or > 20 || !sim.Iccid.All(char.IsAsciiDigit) ||
            sim.Mcc.Length != 3 || !sim.Mcc.All(char.IsAsciiDigit) ||
            sim.Mnc.Length is not (2 or 3) || !sim.Mnc.All(char.IsAsciiDigit) ||
            sim.Imsi.Length is < 6 or > 15 || !sim.Imsi.All(char.IsAsciiDigit) ||
            !sim.Imsi.StartsWith(sim.Mcc + sim.Mnc, StringComparison.Ordinal))
            throw new InvalidOperationException("A selected ICCID, verified home PLMN and stable IMSI are required for a USIM-only Host IMS identity.");
        if (imei.Length != 15 || !imei.All(char.IsAsciiDigit))
            throw new InvalidOperationException("Host IMS requires a valid 15-digit modem IMEI for the SIP contact.");
        var domain = EpdgResolver.BuildImsDomain(sim.Mcc, sim.Mnc);
        var impi = $"{sim.Imsi}@{domain}";
        return new ImsProfile(impi, $"sip:{impi}", domain, imei, localIp,
            AdvertiseSmsOverIms: false);
    }

    /// <summary>
    /// Reads the registered LTE cell immediately before each cellular
    /// REGISTER. QENG reports one coherent MCC/MNC/TAC/ECI tuple; combining a
    /// home-SIM PLMN with a visited cell would identify the wrong location.
    /// An unavailable or unsupported cell is omitted, never fabricated.
    /// </summary>
    private static async Task<string?> TryReadCellularPaniAsync(IAtSession session, CancellationToken ct)
    {
        try
        {
            var response = await session.ExecuteCommandAsync("AT+QENG=\"servingcell\"", 3000, ct)
                .ConfigureAwait(false);
            return response.Success ? ParseLteServingCellPani(response.Lines) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; } // PANI remains optional when this modem cannot report a usable cell.
    }

    internal static string? ParseLteServingCellPani(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (!line.TrimStart().StartsWith("+QENG:", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line[(line.IndexOf(':') + 1)..].Split(',', StringSplitOptions.TrimEntries)
                .Select(value => value.Trim(' ', '"')).ToArray();
            if (fields.Length < 13 || !fields[0].Equals("servingcell", StringComparison.OrdinalIgnoreCase) ||
                !fields[2].Equals("LTE", StringComparison.OrdinalIgnoreCase)) continue;
            if (!fields[1].Equals("NOCONN", StringComparison.OrdinalIgnoreCase) &&
                !fields[1].Equals("CONNECT", StringComparison.OrdinalIgnoreCase)) return null;
            var duplex = fields[3].ToUpperInvariant();
            if (duplex is not ("FDD" or "TDD")) return null;
            var mcc = fields[4];
            var mnc = fields[5];
            var eci = fields[6];
            var tac = fields[12];
            if (mcc.Length != 3 || !mcc.All(char.IsAsciiDigit) ||
                mnc.Length is not (2 or 3) || !mnc.All(char.IsAsciiDigit) ||
                eci.Length is < 1 or > 7 || !eci.All(char.IsAsciiHexDigit) ||
                tac.Length is < 1 or > 4 || !tac.All(char.IsAsciiHexDigit))
                return null;
            return $"3GPP-E-UTRAN-{duplex};utran-cell-id-3gpp={mcc}{mnc}" +
                $"{tac.PadLeft(4, '0').ToUpperInvariant()}{eci.PadLeft(7, '0').ToUpperInvariant()}";
        }
        return null;
    }

    private static async Task VerifySelectedUsimAsync(Ec25AkaProvider usim, string expectedIccid, CancellationToken ct)
    {
        var verified = await usim.VerifyUsimReadyAsync(expectedIccid, requireCardIccid: true, ct)
            .ConfigureAwait(false);
        if (!verified.Ready)
            throw new InvalidOperationException($"The selected USIM could not be verified from EF.ICCID: {verified.Failure}");
    }

    private static async Task VerifySelectedCardIccidAsync(IAtSession session, string expectedIccid,
        CancellationToken ct)
    {
        if (expectedIccid.Length is < 18 or > 20 || !expectedIccid.All(char.IsAsciiDigit))
            throw new InvalidOperationException("A valid selected SIM ICCID is required for Host IMS.");
        var cardIccid = await new Ec25AkaProvider(session).ReadIccidFromCardAsync(ct).ConfigureAwait(false);
        if (cardIccid is null || !string.Equals(
            Ec25AkaProvider.NormalizeIccid(cardIccid),
            Ec25AkaProvider.NormalizeIccid(expectedIccid), StringComparison.Ordinal))
            throw new InvalidOperationException("The active card's EF.ICCID does not match the selected SIM identity.");
    }

    /// <summary>
    /// Uses the selected card's provisioned ISIM identities and ISIM AKA. This
    /// still requires an already host-owned IMS bearer; carriers negotiating
    /// ipsec-3gpp also require an explicitly supplied security backend.
    /// </summary>
    public static async Task<HostImsRegistrationClient> RegisterWithIsimAsync(
        ModemDriver modem,
        HostImsProbeResult probe,
        HostImsEndpointCandidate endpoint,
        int localPort = 5060,
        int pcscfPort = 5060,
        CancellationToken ct = default,
        IHostImsSecurityBackend? securityBackend = null,
        Action<string>? diagnosticLog = null,
        bool enableSmsReceiver = false, SipRegistrationRetryGate? retryGate = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        var expectedIccid = await modem.GetIccidAsync(ct).ConfigureAwait(false);
        return await RegisterWithIsimAsync(modem.Session, modem.GetImeiAsync,
            probe, endpoint, localPort, pcscfPort, ct, securityBackend, expectedIccid,
            token => TryReadCellularPaniAsync(modem.Session, token), diagnosticLog,
            enableSmsReceiver, retryGate).ConfigureAwait(false);
    }

    internal static async Task<HostImsRegistrationClient> RegisterWithIsimAsync(
        IAtSession session,
        Func<CancellationToken, Task<string>> getImei,
        HostImsProbeResult probe,
        HostImsEndpointCandidate endpoint,
        int localPort = 5060,
        int pcscfPort = 5060,
        CancellationToken ct = default,
        IHostImsSecurityBackend? securityBackend = null,
        string? expectedIccid = null,
        Func<CancellationToken, Task<string?>>? readCellularPani = null,
        Action<string>? diagnosticLog = null,
        bool enableSmsReceiver = false, SipRegistrationRetryGate? retryGate = null)
    {
        var isim = new IsimIdentityReader(session);
        return await RegisterWithSelectedIsimAsync(session, getImei, probe, endpoint,
            async token => await isim.ReadAsync(token).ConfigureAwait(false),
            isim.AuthenticateAsync, expectedIccid,
            localPort, pcscfPort, ct, securityBackend,
            readCellularPani: readCellularPani, diagnosticLog: diagnosticLog,
            enableSmsReceiver: enableSmsReceiver, retryGate: retryGate).ConfigureAwait(false);
    }

    private static async Task<HostImsRegistrationClient> RegisterWithSelectedIsimAsync(
        IAtSession session,
        Func<CancellationToken, Task<string>> getImei,
        HostImsProbeResult probe,
        HostImsEndpointCandidate endpoint,
        Func<CancellationToken, Task<IsimIdentityProbeResult?>> readIdentity,
        Func<AkaChallenge, CancellationToken, Task<AkaResult>> authenticate,
        string? expectedIccid,
        int localPort,
        int pcscfPort,
        CancellationToken ct,
        IHostImsSecurityBackend? securityBackend,
        IsimIdentityProbeResult? initialIdentity = null,
        string cardPath = "AT ISIM",
        Func<CancellationToken, Task<string?>>? readCellularPani = null,
        Action<string>? diagnosticLog = null,
        bool enableSmsReceiver = false, SipRegistrationRetryGate? retryGate = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(getImei);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!session.IsOpen) throw new InvalidOperationException("The selected modem AT channel is not open.");
        if (probe.SimInserted == false || !probe.CanAttemptWindowsIms ||
            !probe.EndpointCandidates.Contains(endpoint) || !probe.IsRouteVerified(endpoint))
            throw new InvalidOperationException("The selected endpoint is not owned by an active Windows IMS context in this probe result.");

        var identityProbe = initialIdentity ?? await readIdentity(ct).ConfigureAwait(false);
        var identity = identityProbe?.Identity;
        if (identityProbe?.Readiness != IsimIdentityReadiness.Available || identity is null)
            throw new InvalidOperationException($"Host IMS requires a readable provisioned ISIM identity: {identityProbe?.Summary}");
        if (expectedIccid is not null)
            await VerifySelectedCardIccidAsync(session, expectedIccid, ct).ConfigureAwait(false);
        var imei = await getImei(ct).ConfigureAwait(false);
        if (imei.Length != 15 || !imei.All(char.IsAsciiDigit))
            throw new InvalidOperationException("Host IMS requires a valid 15-digit modem IMEI for the SIP contact.");

        var profile = new ImsProfile(identity.PrivateIdentity, identity.DefaultPublicIdentity,
            identity.HomeDomain, imei, endpoint.LocalAddress.ToString(),
            AdvertiseSmsOverIms: false);
        return await RegisterAsync(probe, endpoint, profile, async (rand, autn, token) =>
        {
            var currentIdentity = await readIdentity(token).ConfigureAwait(false);
            if (currentIdentity?.Readiness != IsimIdentityReadiness.Available || currentIdentity.Identity != identity)
                throw new InvalidOperationException("The selected ISIM identity changed before IMS AKA; registration was stopped.");
            if (expectedIccid is not null)
                await VerifySelectedCardIccidAsync(session, expectedIccid, token).ConfigureAwait(false);
            var result = await authenticate(AkaChallenge.Create(rand, autn), token)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                var error = result.ErrorMessage;
                result.Clear();
                throw new InvalidOperationException($"ISIM IMS AKA failed: {error}");
            }
            return (result.Res!, result.Ck!, result.Ik!);
        }, localPort, pcscfPort, ct, securityBackend, cardPath, readCellularPani,
            diagnosticLog, enableSmsReceiver, retryGate, expectedIccid)
            .ConfigureAwait(false);
    }

    public static async Task<HostImsRegistrationClient> RegisterAsync(
        HostImsProbeResult probe,
        HostImsEndpointCandidate endpoint,
        ImsProfile profile,
        Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> akaProvider,
        int localPort = 5060,
        int pcscfPort = 5060,
        CancellationToken ct = default,
        IHostImsSecurityBackend? securityBackend = null,
        string? cardPath = null,
        Func<CancellationToken, Task<string?>>? readCellularPani = null,
        Action<string>? diagnosticLog = null,
        bool enableSmsReceiver = false, SipRegistrationRetryGate? retryGate = null, string? retryScope = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(akaProvider);
        retryGate?.ThrowIfBlocked(retryScope ?? profile.PrivateIdentity);
        if (probe.SimInserted == false || !probe.CanAttemptWindowsIms ||
            !probe.EndpointCandidates.Contains(endpoint) || !probe.IsRouteVerified(endpoint))
            throw new InvalidOperationException("The selected endpoint is not owned by an active Windows IMS context in this probe result.");
        var route = probe.RouteChecks!.FirstOrDefault(check => check.Endpoint == endpoint && check.IsVerified &&
            (probe.OwnedInterfaceIndex is null || check.InterfaceIndex == probe.OwnedInterfaceIndex) &&
            (probe.OwnedInterfaceLuid is null || check.InterfaceLuid == probe.OwnedInterfaceLuid));
        if (route?.InterfaceIndex is not > 0)
            throw new InvalidOperationException("The selected IMS endpoint has no verified Windows outgoing interface index.");
        if (probe.OwnedInterfaceIndex is { } ownedIndex && route.InterfaceIndex != ownedIndex ||
            probe.OwnedInterfaceLuid is { } ownedLuid && route.InterfaceLuid != ownedLuid)
            throw new InvalidOperationException("The selected IMS route does not belong to the bearer owner.");
        if (localPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        if (pcscfPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(pcscfPort));
        if (securityBackend is not null && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Host IMS IPsec requires the Windows pinned-interface transport.");
        if (profile.PAccessNetworkInfo?.StartsWith("IEEE-802.11", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("A Wi-Fi P-Access-Network-Info cannot be reused on a cellular IMS bearer.", nameof(profile));
        ct.ThrowIfCancellationRequested();

        var transport = new SipTransport(timeoutMs: 15000);
        var receiveErrorCount = 0;
        transport.ReceiveError += (_, error) =>
        {
            if (Interlocked.Increment(ref receiveErrorCount) <= 8)
                diagnosticLog?.Invoke($"Host IMS SIP receive error: {error}");
        };
        IHostImsSecurityReservation? reservation = null;
        try
        {
            if (OperatingSystem.IsWindows())
                transport.ConnectOnInterface(endpoint.LocalAddress, endpoint.PcscfAddress,
                    route.InterfaceIndex.Value, pcscfPort, localPort);
            else
                transport.Connect(endpoint.LocalAddress, endpoint.PcscfAddress, pcscfPort, localPort);
            var bound = transport.LocalEndPoint
                ?? throw new InvalidOperationException("The Windows IMS socket has no local endpoint.");
            if (!bound.Address.Equals(endpoint.LocalAddress))
                throw new InvalidOperationException("The SIP socket did not bind to the selected IMS address.");
            if (OperatingSystem.IsWindows() && transport.BoundInterfaceIndex != route.InterfaceIndex)
                throw new InvalidOperationException("The SIP socket did not bind to the selected IMS bearer interface.");
            // MT capability is opt-in until the receiver/report path is in use.
            var boundProfile = profile with
            {
                LocalIp = bound.Address.ToString(), LocalPort = bound.Port,
                AdvertiseSmsOverIms = enableSmsReceiver
            };
            if (securityBackend is not null)
            {
                var protectedPorts = transport.ReserveProtectedPortsOnInterface();
                reservation = await securityBackend.ReserveOwnedAsync(
                    bound.Address, endpoint.PcscfAddress, route.InterfaceIndex.Value,
                    probe.OwnedInterfaceLuid ?? route.InterfaceLuid,
                    protectedPorts.ClientPort, protectedPorts.ServerPort, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Host IMS IPsec backend did not reserve inbound SPIs.");
                if (!SecurityAgreementBuilder.IsValidProtectedPort(reservation.Proposal.PortClient) ||
                    !SecurityAgreementBuilder.IsValidProtectedPort(reservation.Proposal.PortServer) ||
                    reservation.Proposal.PortClient != protectedPorts.ClientPort ||
                    reservation.Proposal.PortServer != protectedPorts.ServerPort)
                    throw new InvalidOperationException("Host IMS IPsec backend reserved invalid protected ports.");
            }
            // Keep the callback's reservation stable after ownership moves to
            // the live client (the local cleanup variable becomes null).
            var activeReservation = reservation;
            var registration = new SipRegisterSession(transport, boundProfile, activeReservation?.Proposal,
                activeReservation is null ? null : (agreement, ck, ik) =>
                {
                    if (transport.ProtectedServerEndPoint is { } protectedServer &&
                        (transport.LocalEndPoint?.Port != activeReservation.Proposal.PortClient ||
                         protectedServer.Port != activeReservation.Proposal.PortServer ||
                         transport.RemoteEndPoint?.Port != agreement.PcscfServerPort))
                        throw new InvalidOperationException("IMS security rekey changed protected ports without a transport handover.");
                    activeReservation.Activate(agreement, ck, ik);
                    if (transport.ProtectedServerEndPoint is null)
                    {
                        if (activeReservation is IHostImsUserspaceSecurityReservation userspace)
                            transport.ActivateUserspaceProtectedPortsOnInterface(
                                activeReservation.Proposal.PortClient,
                                activeReservation.Proposal.PortServer,
                                agreement.PcscfServerPort,
                                (datagram, token) =>
                                {
                                    token.ThrowIfCancellationRequested();
                                    userspace.Send(datagram);
                                    return Task.CompletedTask;
                                },
                                token => Task.Run(() => userspace.Receive(token), token));
                        else
                            transport.ActivateProtectedPortsOnInterface(
                                activeReservation.Proposal.PortClient,
                                activeReservation.Proposal.PortServer,
                                agreement.PcscfServerPort);
                    }
                }, diagnosticLog: diagnosticLog, readAccessNetworkInfo: readCellularPani,
                retryGate: retryGate, retryScope: retryScope);
            try
            {
                var result = await registration.RegisterAsync(akaProvider, ct).ConfigureAwait(false);
                registration.StartRefreshing(akaProvider);
                var client = new HostImsRegistrationClient(endpoint, transport, registration,
                    result, route.InterfaceIndex.Value, akaProvider, reservation, cardPath)
                    { InterfaceLuid = probe.OwnedInterfaceLuid ?? route.InterfaceLuid };
                reservation = null; // Ownership moved to the live Host IMS session.
                return client;
            }
            catch
            {
                registration.Dispose();
                throw;
            }
        }
        catch
        {
            try { reservation?.Dispose(); }
            finally { transport.Dispose(); }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var report in _pendingSmsReports.Values)
            report.Completion.TrySetCanceled();
        _pendingSmsReports.Clear();
        try { await _registration.StopRefreshingAsync().ConfigureAwait(false); }
        finally
        {
            try { _registration.Dispose(); }
            finally { Interlocked.Exchange(ref _securityReservation, null)?.Dispose(); }
        }
    }
}
