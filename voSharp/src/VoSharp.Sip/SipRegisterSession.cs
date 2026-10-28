using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.RegularExpressions;
using VoSharp.Crypto;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Sip;

/// <summary>
/// Result of a successful SIP IMS registration.
/// </summary>
public record SipRegistrationResult(
    string ContactUri,
    int ExpiresSeconds,
    string? ServiceRoute,
    string? PAssociatedUri,
    DateTime RegisteredAt,
    bool SmsCapabilityConfirmed = false
);

/// <summary>The registrar invalidated this flow; its owner must create a new initial registration flow.</summary>
public sealed class SipRegistrationRestartRequiredException(string message, int? statusCode = null,
    SipRegistrationFailureInfo? failure = null)
    : InvalidOperationException(message)
{
    public int? StatusCode { get; } = statusCode;
    public SipRegistrationFailureInfo? Failure { get; } = failure;
}

/// <summary>
/// Manages the full 3GPP IMS SIP registration flow:
///   1. Send initial REGISTER (no credentials)
///   2. Receive 401 Unauthorized → parse WWW-Authenticate
///   3. Compute Digest-AKA response using USIM / Milenage
///   4. Send authenticated REGISTER
///   5. Receive 200 OK → extract Contact / Expires / Service-Route
///
/// RFC 3261 §10, RFC 3310, 3GPP TS 24.229 §5.1.1.2
/// </summary>
public class SipRegisterSession : IDisposable
{
    private readonly SipTransport _transport;
    private ImsProfile _profile;
    private readonly string _callId;
    private readonly string _fromTag;
    private uint _cseq = 1;
    private int _requestedExpiresSeconds = ImsRegisterBuilder.DefaultExpiresSeconds;
    // TS 24.229 5.1.1.4.2: re-REGISTER carries the last nonce and calculated
    // response, not the empty identity-only Authorization from initial attach.
    // Keep only the wire header; the raw AKA vector is still zeroed immediately.
    private string? _lastRegisterAuthorization;
    private int _requiresNewRegistrationFlow;
    private int _registrationResponseObserved;
    internal bool HasObservedRegistrationResponse => Volatile.Read(ref _registrationResponseObserved) != 0;
    private readonly SipRegistrationRetryGate _retryGate;
    private readonly string _retryScope;
    public SipRegistrationFailureInfo? LastFailure { get; private set; }
    public bool RequiresNewRegistrationFlow => Volatile.Read(ref _requiresNewRegistrationFlow) != 0;
    private SecurityProposal? _securityProposal;
    private SecurityAgreement? _successfulAgreement;
    private readonly Func<SecurityProposal>? _createRenewalSecurityProposal;
    private readonly Action<SipRegistrationResult>? _registrationAccepted;
    private readonly Action<SecurityAgreement, byte[], byte[]>? _activateSecurity;
    private readonly IReadOnlyList<string>? _securityIntegrityAlgorithms;
    private readonly IReadOnlyList<string>? _securityEncryptionAlgorithms;
    private readonly Action<string>? _diagnosticLog;
    private readonly Func<CancellationToken, Task<string?>>? _readAccessNetworkInfo;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    public SecurityAgreement? Agreement { get; private set; }
    public event EventHandler<SipRegistrationResult>? RegistrationRefreshed;
    public event EventHandler<string>? RegistrationFailed;

    private sealed record Grant(SipRegistrationResult Result, long AcceptedTimestamp);
    private Grant? _lastGrant;
    /// <summary>Last accepted grant, retained as history; use CurrentResult for live status.</summary>
    public SipRegistrationResult? LastResult
    {
        get => Volatile.Read(ref _lastGrant)?.Result;
        private set => Volatile.Write(ref _lastGrant, value is null ? null : new Grant(value, Stopwatch.GetTimestamp()));
    }
    public SipRegistrationResult? CurrentResult
    {
        get
        {
            var grant = Volatile.Read(ref _lastGrant);
            return !_transport.IsDisposed && !RequiresNewRegistrationFlow && grant is not null &&
                IsGrantCurrent(grant.Result, grant.AcceptedTimestamp, DateTime.UtcNow, Stopwatch.GetTimestamp())
                    ? grant.Result : null;
        }
    }
    internal static bool IsGrantCurrent(SipRegistrationResult result, long acceptedTimestamp,
        DateTime utcNow, long nowTimestamp)
    {
        if (result.ExpiresSeconds <= 0 || result.RegisteredAt.Kind != DateTimeKind.Utc || nowTimestamp < acceptedTimestamp)
            return false;
        var lifetime = TimeSpan.FromSeconds(result.ExpiresSeconds);
        var wallElapsed = utcNow - result.RegisteredAt;
        // A stalled refresh must not extend validity. The monotonic clock also
        // bounds it if the wall clock moves backwards within the granted age.
        return wallElapsed >= TimeSpan.Zero && wallElapsed < lifetime &&
            Stopwatch.GetElapsedTime(acceptedTimestamp, nowTimestamp) < lifetime;
    }
    public ImsProfile CurrentProfile => Volatile.Read(ref _profile);

    /// <summary>Refreshes optional cellular PANI before a new IMS transaction.</summary>
    public async Task<string?> RefreshAccessNetworkInfoAsync(CancellationToken ct = default, TimeSpan? budget = null)
    {
        if (_readAccessNetworkInfo is null) return CurrentProfile.PAccessNetworkInfo;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget.HasValue) deadline.CancelAfter(budget.Value);
        string? current;
        try { current = await _readAccessNetworkInfo(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // Optional cell metadata must not stall call setup behind an AT
            // queue. Never fall back to a potentially stale previous cell.
            current = null;
            TraceCallSetup("Host IMS access metadata deadline exceeded; omitted stale PANI.");
        }
        ct.ThrowIfCancellationRequested();
        UpdateProfile(profile => profile with { PAccessNetworkInfo = current });
        return current;
    }

    public void TraceCallSetup(string message) => _diagnosticLog?.Invoke(message);

    private void UpdateProfile(Func<ImsProfile, ImsProfile> update)
    {
        while (true)
        {
            var old = CurrentProfile;
            var replacement = update(old);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _profile, replacement, old), old))
                return;
        }
    }

    public SipRegisterSession(SipTransport transport, ImsProfile profile,
        SecurityProposal? securityProposal = null,
        Action<SecurityAgreement, byte[], byte[]>? activateSecurity = null,
        IReadOnlyList<string>? securityIntegrityAlgorithms = null,
        IReadOnlyList<string>? securityEncryptionAlgorithms = null,
        Action<string>? diagnosticLog = null,
        Func<CancellationToken, Task<string?>>? readAccessNetworkInfo = null,
        SipRegistrationRetryGate? retryGate = null, string? retryScope = null,
        Func<SecurityProposal>? createRenewalSecurityProposal = null,
        Action<SipRegistrationResult>? registrationAccepted = null)
    {
        if ((securityProposal is null) != (activateSecurity is null))
            throw new ArgumentException("IMS Security-Client requires an SA activation callback, and vice versa.");
        _transport = transport;
        _profile = profile;
        _securityProposal = securityProposal;
        _activateSecurity = activateSecurity;
        if (createRenewalSecurityProposal is not null && securityProposal is null)
            throw new ArgumentException("Renewal security reservation requires an initial proposal.");
        _createRenewalSecurityProposal = createRenewalSecurityProposal;
        _registrationAccepted = registrationAccepted;
        _securityIntegrityAlgorithms = securityIntegrityAlgorithms;
        _securityEncryptionAlgorithms = securityEncryptionAlgorithms;
        _diagnosticLog = diagnosticLog;
        _readAccessNetworkInfo = readAccessNetworkInfo;
        _retryGate = retryGate ?? new();
        _retryScope = retryScope ?? profile.PrivateIdentity;
        _transport.PreparingOutgoingRequest += AddSecurityHeaders;
        // Call-ID and From tag are generated once and reused for the entire registration dialog
        _callId = Guid.NewGuid().ToString("N") + "@" + profile.LocalIp;
        _fromTag = Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Performs a full IMS REGISTER challenge-response exchange.
    /// </summary>
    /// <param name="akaProvider">
    ///   Callback that receives (RAND, AUTN) and returns (RES, CK, IK).
    ///   Use real USIM hardware when available, otherwise Milenage software.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SipRegistrationResult> RegisterAsync(
        Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> akaProvider,
        CancellationToken ct = default)
    {
        if (RequiresNewRegistrationFlow)
            throw new SipRegistrationRestartRequiredException("IMS registration flow is invalid; create a new initial registration flow before retrying.");
        _retryGate.ThrowIfBlocked(_retryScope);
        LastFailure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { return await RegisterCoreAsync(akaProvider, ct).ConfigureAwait(false); }
            catch (MinExpiresRequiredException ex) when (attempt == 0)
            {
                _requestedExpiresSeconds = ex.MinExpires;
                WriteDiagnostic($"REGISTER 423 requested Min-Expires={ex.MinExpires}; retrying once with a higher Contact lifetime.");
            }
            catch (MinExpiresRequiredException ex)
            {
                throw new InvalidOperationException(
                    $"IMS REGISTER still requires a longer Contact lifetime after one retry (Min-Expires={ex.MinExpires}).", ex);
            }
        }
        throw new InvalidOperationException("IMS REGISTER Min-Expires negotiation did not complete.");
    }

    private sealed class MinExpiresRequiredException(int minExpires)
        : InvalidOperationException($"IMS REGISTER still requires a longer Contact lifetime (Min-Expires={minExpires}).")
    {
        public int MinExpires { get; } = minExpires;
    }

    private void ThrowIfIntervalTooBrief(SipMessage response)
    {
        if (response.StatusCode != 423) return;
        var minExpiresText = response.GetHeader("Min-Expires");
        if (!int.TryParse(minExpiresText, out var minExpires) ||
            minExpires <= _requestedExpiresSeconds)
            throw new InvalidOperationException(
                $"SIP REGISTER received 423 without a valid Min-Expires greater than {_requestedExpiresSeconds}.");
        throw new MinExpiresRequiredException(minExpires);
    }

    private async Task<SipRegistrationResult> RegisterCoreAsync(
        Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> akaProvider,
        CancellationToken ct)
    {
        var isReregistration = LastResult is not null;
        if (isReregistration) PrepareRenewalSecurityProposal();
        var totalTimer = Stopwatch.StartNew();
        // Obtain the current access cell before every initial or refresh REGISTER.
        // A cached PANI from an earlier cell can identify the wrong location.
        await RefreshAccessNetworkInfoAsync(ct).ConfigureAwait(false);
        // ── Step 1: Send initial REGISTER ────────────────────────────────────
        var initReq = ImsRegisterBuilder.BuildInitialRegister(_profile, _callId, _cseq++, _fromTag, _requestedExpiresSeconds);
        AddSecurityHeaders(initReq);
        WriteDiagnostic($"REGISTER step=1/5 send request; local-port={_profile.LocalPort}; security-client={_securityProposal is not null}; authorization={(_lastRegisterAuthorization is null ? "initial" : "previous-aka")}.");
        if (_securityProposal is not null)
            WriteDiagnostic($"REGISTER step=1/5 Security-Client offer: {DescribeSecurityClientOffer()}.");
        var requestTimer = Stopwatch.StartNew();
        var resp1 = await ExchangeRegisterAsync(initReq, ct).ConfigureAwait(false);
        ThrowIfRegistrationFlowLost(resp1, isReregistration);
        ThrowIfIntervalTooBrief(resp1);
        WriteDiagnostic($"REGISTER step=1/5 status={resp1.StatusCode}; elapsed={requestTimer.ElapsedMilliseconds}ms; www-authenticate={HeaderCount(resp1, "WWW-Authenticate")}; security-server={HasHeader(resp1, "Security-Server")}.");

        // A 200 OK on the first REGISTER (no auth required) is allowed by some operators
        if (resp1.StatusCode == 200)
        {
            if (Agreement is null && (_securityProposal is not null ||
                HasHeader(resp1, "Security-Server") || RequiresSecAgree(resp1)))
                throw new InvalidOperationException(
                    "SIP REGISTER received 200 OK requiring IMS security, but no IMS security association was established.");
            var accepted = AcceptRegistration(resp1);
            WriteDiagnostic($"REGISTER completed without challenge; total={totalTimer.ElapsedMilliseconds}ms.");
            return accepted;
        }

        // ── Step 2: Handle 401 Unauthorized ──────────────────────────────────
        if (resp1.StatusCode != 401)
            throw new InvalidOperationException(
                $"SIP REGISTER: unexpected response {resp1.StatusCode} {resp1.ReasonPhrase}; " +
                $"headers=[{string.Join(", ", resp1.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase))}]; " +
                $"body-length={resp1.Body?.Length ?? 0}.");
        // A registered UE may be re-challenged over the existing security agreement without a
        // new Security-Server header during reauthentication. Preserve
        // the live SAs and answer the fresh AKA challenge instead of tearing the session down.
        var wwwAuthHeaders = resp1.Headers.TryGetValue("WWW-Authenticate", out var authValues)
            ? authValues.ToArray()
            : Array.Empty<string>();
        if (wwwAuthHeaders.Length == 0)
            throw new InvalidOperationException("SIP 401 missing WWW-Authenticate header.");

        // A P-CSCF may advertise several challenges. Match the first supported IMS-AKA
        // challenge instead of blindly taking the first (plain Digest is not usable with a USIM).
        var challenge = wwwAuthHeaders
            .Select(ImsRegisterBuilder.ParseWwwAuthenticate)
            .FirstOrDefault(parsed => parsed != null);
        if (challenge == null)
        {
            var advertised = string.Join(" | ", wwwAuthHeaders.Select(ImsRegisterBuilder.DescribeChallenge));
            throw new InvalidOperationException(
                $"SIP 401 did not offer a supported IMS-AKA challenge ({advertised}). " +
                "A physical USIM can answer AKAv1-MD5 only; plain Digest MD5 needs a carrier password.");
        }

        WriteDiagnostic($"REGISTER step=2/5 AKA challenge selected; algorithm={challenge.Algorithm}; qop={challenge.Qop ?? "none"}; nonce-length={challenge.Nonce.Length}; challenge-count={wwwAuthHeaders.Length}.");

        if (resp1.Headers.TryGetValue("Security-Server", out var requiredSecurity) && requiredSecurity.Count > 0 &&
            (_securityProposal == null || _activateSecurity == null))
            throw new InvalidOperationException("P-CSCF requires IMS IPsec, but this transport has no IMS security data plane.");
        if (Agreement is null && (_securityProposal is not null || RequiresSecAgree(resp1)) &&
            !HasHeader(resp1, "Security-Server"))
            throw new InvalidOperationException("IMS sec-agree was requested, but the 401 response omitted Security-Server.");

        // Reject incompatible P-CSCF security offers before invoking the SIM.
        // A real AKA challenge can consume card state; there is no reason to
        // authenticate it if the selected protection cannot be installed.
        SecurityAgreement? selectedSecurity = null;
        if (resp1.Headers.TryGetValue("Security-Server", out var offeredSecurity))
        {
            if (_securityProposal == null || _activateSecurity == null)
                throw new InvalidOperationException("P-CSCF requires IMS IPsec, but this transport has no IMS security data plane.");
            var evaluation = SecurityAgreementBuilder.EvaluateSecurityServer(
                string.Join(", ", offeredSecurity), _securityProposal,
                _securityIntegrityAlgorithms, _securityEncryptionAlgorithms);
            WriteDiagnostic($"REGISTER step=4/5 Security-Server compatibility: {string.Join(" | ", evaluation.CandidateDiagnostics)}");
            selectedSecurity = evaluation.Agreement
                ?? throw new InvalidOperationException("P-CSCF offered no IMS security mechanism that was present in Security-Client.");
        }

        // ── Step 3: Decode RAND + AUTN from nonce (3GPP TS 24.228 §5.1.1.2) ─
        var (rand, autn) = DecodeAkaNonce(challenge.Nonce);

        // ── Step 4: Perform AKA authentication ───────────────────────────────
        var akaTimer = Stopwatch.StartNew();
        byte[]? res = null;
        byte[]? ck = null;
        byte[]? ik = null;
        try
        {
            WriteDiagnostic("REGISTER step=3/5 invoking USIM AKA; RAND/AUTN are intentionally not logged.");
            (res, ck, ik) = await akaProvider(rand, autn, ct).ConfigureAwait(false);
            if (res is null || ck is null || ik is null)
                throw new InvalidOperationException("USIM AKA returned an incomplete authentication vector.");
            WriteDiagnostic($"REGISTER step=3/5 USIM AKA completed; elapsed={akaTimer.ElapsedMilliseconds}ms; result-lengths=res:{res.Length},ck:{ck.Length},ik:{ik.Length}.");
            if (selectedSecurity is not null)
            {
                _activateSecurity!(selectedSecurity, ck, ik);
                Agreement = selectedSecurity;
                UpdateProfile(profile => profile with
                {
                    LocalPort = _securityProposal!.PortClient,
                    ContactPort = _securityProposal.PortServer
                });
                WriteDiagnostic($"REGISTER step=4/5 IMS IPsec selected; integrity={selectedSecurity.Selected.IntegrityAlgorithm}; encryption={selectedSecurity.Selected.EncryptionAlgorithm}; ue-ports={selectedSecurity.Selected.PortClient}/{selectedSecurity.Selected.PortServer}; pcscf-ports={selectedSecurity.PcscfClientPort}/{selectedSecurity.PcscfServerPort}.");
            }
            else
            {
                WriteDiagnostic("REGISTER step=4/5 P-CSCF did not require an additional IMS IPsec security agreement.");
            }
            // ── Step 5: Send authenticated REGISTER ──────────────────────────────
            var authReq = ImsRegisterBuilder.BuildAuthenticatedRegister(
                _profile, _callId, _cseq++, challenge, res, _fromTag, Agreement != null, _requestedExpiresSeconds);
            AddSecurityHeaders(authReq);
            // A fresh 401 replaces the previous challenge, including when the
            // registrar next asks for a longer Contact lifetime with 423.
            _lastRegisterAuthorization = authReq.GetHeader("Authorization");

            requestTimer.Restart();
            WriteDiagnostic($"REGISTER step=5/5 send authenticated request; local-port={_profile.LocalPort}; integrity-protected={Agreement is not null}.");
            var resp2 = await ExchangeRegisterAsync(authReq, ct).ConfigureAwait(false);
            ThrowIfRegistrationFlowLost(resp2, isReregistration);
            ThrowIfIntervalTooBrief(resp2);
            WriteDiagnostic($"REGISTER step=5/5 status={resp2.StatusCode}; elapsed={requestTimer.ElapsedMilliseconds}ms; service-route={HasHeader(resp2, "Service-Route")}; associated-uri={HasHeader(resp2, "P-Associated-URI")}; expires-header={HasHeader(resp2, "Expires")}.");

            if (resp2.StatusCode != 200)
                throw new InvalidOperationException(
                    $"SIP REGISTER auth failed: {resp2.StatusCode} {resp2.ReasonPhrase}");

            var accepted = AcceptRegistration(resp2);
            Console.WriteLine("[SipRegisterSession] IMS registration accepted (200 OK).");
            WriteDiagnostic($"REGISTER completed successfully; total={totalTimer.ElapsedMilliseconds}ms; refresh={accepted.ExpiresSeconds}s; sms-capability={accepted.SmsCapabilityConfirmed}.");
            return accepted;
        }
        finally
        {
            if (res is not null) CryptographicOperations.ZeroMemory(res);
            if (ck is not null) CryptographicOperations.ZeroMemory(ck);
            if (ik is not null) CryptographicOperations.ZeroMemory(ik);
            CryptographicOperations.ZeroMemory(rand);
            CryptographicOperations.ZeroMemory(autn);
        }
    }

    private async Task<SipMessage> ExchangeRegisterAsync(SipMessage request, CancellationToken ct)
    {
        void Observe(SipMessage _) => Interlocked.Exchange(ref _registrationResponseObserved, 1);
        var response = await _transport.SendRegistrationAsync(request, Observe,
            () => HasObservedRegistrationResponse, ct).ConfigureAwait(false);
        Observe(response);
        return response;
    }

    private void WriteDiagnostic(string message) => _diagnosticLog?.Invoke(message);

    private void ThrowIfRegistrationFlowLost(SipMessage response, bool isReregistration)
    {
        // TS 24.229 5.1.1.4.1: these responses require INITIAL registration.
        // In particular, a 500/504 can delete the P-CSCF's IP association. Retrying
        // the same protected flow/Authorization cannot re-establish that association.
        var newFlow = isReregistration && response.StatusCode is 403 or 408 or 500 or 503 or 504;
        if (response.StatusCode >= 400 && response.StatusCode is not (401 or 423))
        {
            LastFailure = SipRegistrationFailureInfo.FromResponse(response, newFlow);
            _retryGate.Observe(_retryScope, LastFailure);
        }
        if (newFlow)
            throw InvalidateRegistrationFlow($"IMS re-registration received SIP {response.StatusCode}; a new initial registration flow is required.", response.StatusCode);
    }

    private SipRegistrationRestartRequiredException InvalidateRegistrationFlow(string reason, int? statusCode = null)
    {
        LastResult = null;
        _lastRegisterAuthorization = null;
        Volatile.Write(ref _requiresNewRegistrationFlow, 1);
        WriteDiagnostic(reason);
        return new(reason, statusCode, LastFailure);
    }

    private SipRegistrationResult AcceptRegistration(SipMessage response)
    {
        var result = ExtractRegistrationResult(response);
        if (response.Headers.TryGetValue("Authentication-Info", out var headers))
        {
            string? nextNonce = null;
            foreach (var header in headers)
            {
                if (!ImsRegisterBuilder.TryParseAuthenticationInfo(header, out var parameters))
                    throw InvalidateRegistrationFlow("IMS REGISTER returned malformed Authentication-Info; credentials were discarded and a new registration flow is required.");
                if (!parameters.TryGetValue("nextnonce", out var candidate)) continue;
                if (nextNonce is not null || string.IsNullOrEmpty(candidate) || candidate.Any(char.IsControl))
                    throw InvalidateRegistrationFlow("IMS REGISTER returned ambiguous or invalid nextnonce; credentials were discarded and a new registration flow is required.");
                nextNonce = candidate;
            }
            if (nextNonce is not null)
            {
                if (_lastRegisterAuthorization is not { } authorization)
                    throw InvalidateRegistrationFlow("IMS REGISTER returned nextnonce without an established AKA context; a new registration flow is required.");
                // TS 24.229 5.1.1.4.1(f), 5.1.1.4.2(a): store the latest nonce,
                // retain the LAST CALCULATED response. Do not retain/reuse RES or
                // silently run another AKA merely because a successful 200 supplied a nonce.
                // A subsequent 401 still drives a fresh USIM authentication normally.
                var nonceField = Regex.Match(authorization, @"(?:^|,\s*)nonce\s*=\s*""(?<value>(?:\\.|[^""\\])*)""", RegexOptions.IgnoreCase);
                if (!nonceField.Success)
                    throw InvalidateRegistrationFlow("IMS AKA authorization has no replaceable nonce; a new registration flow is required.");
                var field = nonceField.Groups["value"];
                var escaped = nextNonce.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
                _lastRegisterAuthorization = authorization[..field.Index] + escaped + authorization[(field.Index + field.Length)..];
                WriteDiagnostic("REGISTER accepted Authentication-Info nextnonce; previous nonce replaced, last AKA response retained; nonce value redacted.");
            }
        }
        LastFailure = null;
        _retryGate.ClearFailure(_retryScope);
        _registrationAccepted?.Invoke(result);
        _successfulAgreement = Agreement;
        return LastResult = result;
    }

    private void PrepareRenewalSecurityProposal()
    {
        if (_createRenewalSecurityProposal is null) return;
        var next = _createRenewalSecurityProposal();
        if (_securityProposal is not { } previous ||
            next.SpiClient == previous.SpiClient || next.SpiServer == previous.SpiServer ||
            next.SpiClient == previous.SpiServer || next.SpiServer == previous.SpiClient ||
            next.SpiClient < 256 || next.SpiServer < 256 ||
            next.SpiClient == next.SpiServer || next.PortClient == previous.PortClient ||
            next.PortServer != previous.PortServer || next.PortClient == next.PortServer ||
            !SecurityAgreementBuilder.IsValidProtectedPort(next.PortClient) ||
            !SecurityAgreementBuilder.IsValidProtectedPort(next.PortServer))
            throw new InvalidOperationException("Renewal reservation must provide fresh SPIs/client port while retaining the Contact server port.");
        _securityProposal = next;
        WriteDiagnostic("REGISTER prepared fresh renewal security parameters; existing protected flow remains active until reauthentication succeeds.");
    }

    private static bool HasHeader(SipMessage message, string name) =>
        message.Headers.TryGetValue(name, out var values) && values.Count > 0;

    private static bool RequiresSecAgree(SipMessage message) =>
        new[] { "Require", "Proxy-Require" }
            .SelectMany(name => message.Headers.TryGetValue(name, out var values) ? values : [])
            .SelectMany(value => value.Split(','))
            .Any(value => value.Trim().Equals("sec-agree", StringComparison.OrdinalIgnoreCase));

    private static int HeaderCount(SipMessage message, string name) =>
        message.Headers.TryGetValue(name, out var values) ? values.Count : 0;

    private string DescribeSecurityClientOffer()
    {
        if (_securityProposal is null) return "disabled";
        var integrity = _securityIntegrityAlgorithms ?? SecurityAgreementBuilder.DefaultIntegrityAlgorithms;
        var encryption = _securityEncryptionAlgorithms ?? SecurityAgreementBuilder.DefaultEncryptionAlgorithms;
        return $"mechanisms={integrity.Count * encryption.Count}; alg=[{string.Join(',', integrity)}]; ealg=[{string.Join(',', encryption)}]; prot=esp; mod=trans; protected-ports=valid; SPI values redacted";
    }

    /// <summary>
    /// Decodes the IMS AKA nonce. Some P-CSCFs use Base64(RAND || AUTN); others send the same
    /// 32 bytes as 64 hexadecimal characters. Hexadecimal text is also valid Base64 text, so the
    /// encoding must be detected before attempting Base64 or the USIM receives unrelated bytes.
    /// </summary>
    public static (byte[] Rand, byte[] Autn) DecodeAkaNonce(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            throw new InvalidOperationException("IMS AKA challenge contains an empty nonce.");

        var compact = nonce.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        byte[] decoded;
        if (compact.Length >= 64 && compact.Length % 2 == 0 && compact.All(Uri.IsHexDigit))
        {
            try { decoded = Convert.FromHexString(compact); }
            catch (FormatException ex) { throw new InvalidOperationException("IMS AKA hexadecimal nonce is malformed.", ex); }
        }
        else
        {
            try { decoded = Convert.FromBase64String(nonce.Trim()); }
            catch (FormatException ex) { throw new InvalidOperationException("IMS AKA nonce is neither valid Base64 nor hexadecimal RAND/AUTN.", ex); }
        }

        try
        {
            if (decoded.Length < 32)
                throw new InvalidOperationException($"IMS AKA nonce decoded to {decoded.Length} bytes; RAND/AUTN requires at least 32.");
            return (decoded[..16], decoded[16..32]);
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    public void AddSecurityHeaders(SipMessage request)
    {
        if (request.Method == "REGISTER" && RequiresNewRegistrationFlow)
            throw new SipRegistrationRestartRequiredException("Refusing REGISTER on an invalid IMS flow; its owner must create a new initial registration flow.");
        if (request.Method == "REGISTER" && request.GetHeader("Authorization") is null &&
            _lastRegisterAuthorization is { } cachedAuthorization)
            request.SetHeader("Authorization", cachedAuthorization);
        if (_securityProposal == null) return;
        if (request.Method == "REGISTER")
        {
            request.SetHeader("Supported", "path, sec-agree, gruu");
            request.SetHeader("Security-Client", _securityIntegrityAlgorithms != null && _securityEncryptionAlgorithms != null
                ? SecurityAgreementBuilder.BuildSecurityClient(_securityProposal, _securityIntegrityAlgorithms, _securityEncryptionAlgorithms)
                : SecurityAgreementBuilder.BuildSecurityClient(_securityProposal));

            // TS 24.229 security-agreement registration identifies the UE's intended
            // IMS AKA scheme even before the P-CSCF supplies RAND/AUTN.  This is not
            // a password response: nonce and response are deliberately empty.  VoCat
            // does the same for its initial protected REGISTER.  Omitting it lets some
            // P-CSCFs select their generic MD5 realm instead of the IMS-AKA realm.
            request.SetHeader("Require", "sec-agree");
            request.SetHeader("Proxy-Require", "sec-agree");
            if (request.GetHeader("Authorization") == null)
            {
                request.SetHeader("Authorization",
                    $"Digest username=\"{_profile.PrivateIdentity}\", realm=\"{_profile.HomeDomain}\", nonce=\"\", " +
                    $"uri=\"sip:{_profile.HomeDomain}\", response=\"\", algorithm=AKAv1-MD5, integrity-protected=no");
            }
        }
        var agreement = request.Method == "REGISTER" ? Agreement : _successfulAgreement ?? Agreement;
        if (agreement != null)
        {
            var authorization = request.GetHeader("Authorization");
            if (authorization != null)
                request.SetHeader("Authorization", authorization.Replace("integrity-protected=no", "integrity-protected=yes"));
            request.SetHeader("Security-Verify", agreement.VerifyValue);
            // Subsequent IMS transactions, including RP-ACK, remain covered by
            // the negotiated security agreement rather than silently downgrading.
            request.SetHeader("Require", "sec-agree");
            request.SetHeader("Proxy-Require", "sec-agree");
        }
    }

    public void StartRefreshing(Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> akaProvider)
    {
        if (_refreshTask != null) throw new InvalidOperationException("Registration refresh already started.");
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        _refreshTask = Task.Run(async () =>
        {
            try
            {
                while (LastResult != null)
                {
                    var granted = LastResult;
                    var expiresAt = granted.RegisteredAt.AddSeconds(granted.ExpiresSeconds);
                    // TS 24.229 5.1.1.4.1: 600 s before long grants expire;
                    // half-life for grants of 1200 s or less.
                    var refreshAt = granted.RegisteredAt.AddSeconds(granted.ExpiresSeconds > 1200
                        ? granted.ExpiresSeconds - 600 : granted.ExpiresSeconds / 2.0);
                    // Task.Delay cannot represent arbitrary SIP expiry values.
                    // Sleep in bounded chunks, then recalculate against the
                    // registration timestamp so long grants remain cancellable.
                    while (DateTime.UtcNow < refreshAt)
                    {
                        var remaining = refreshAt - DateTime.UtcNow;
                        await Task.Delay(remaining > TimeSpan.FromDays(1)
                            ? TimeSpan.FromDays(1) : remaining, token).ConfigureAwait(false);
                    }

                    Exception? lastError = null;
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            var result = await RegisterAsync(akaProvider, token).ConfigureAwait(false);
                            RegistrationRefreshed?.Invoke(this, result);
                            lastError = null;
                            break;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is SipRegistrationRestartRequiredException or SipRegistrationRetryBlockedException)
                        {
                            WriteDiagnostic("REGISTER refresh stopped retries on an invalid flow; handing off to initial-registration recovery.");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            WriteDiagnostic($"REGISTER refresh attempt={attempt + 1}/3 failed: {ex.Message}");
                            var retryWindow = expiresAt - DateTime.UtcNow;
                            if (attempt == 2 || retryWindow <= TimeSpan.Zero)
                                break;

                            var requestedDelay = attempt == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(15);
                            var delay = TimeSpan.FromMilliseconds(Math.Max(100,
                                Math.Min(requestedDelay.TotalMilliseconds, retryWindow.TotalMilliseconds / 2)));
                            await Task.Delay(delay, token).ConfigureAwait(false);
                        }
                    }

                    if (lastError != null)
                        throw new InvalidOperationException($"IMS REGISTER refresh retries were exhausted: {lastError.Message}", lastError);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LastResult = null;
                _lastRegisterAuthorization = null;
                RegistrationFailed?.Invoke(this, ex.Message);
            }
        });
    }

    public async Task StopRefreshingAsync()
    {
        _refreshCts?.Cancel();
        if (_refreshTask != null) await _refreshTask.ConfigureAwait(false);
        _refreshTask = null;
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    /// <summary>
    /// Sends a REGISTER with Expires: 0 to de-register (RFC 3261 §10.2.2).
    /// </summary>
    public Task DeregisterAsync(CancellationToken ct = default) => DeregisterCoreAsync(null, ct);

    /// <summary>Answers an IMS-AKA re-challenge while removing this UA's binding.</summary>
    public Task DeregisterAsync(
        Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> akaProvider,
        CancellationToken ct = default) => DeregisterCoreAsync(
            akaProvider ?? throw new ArgumentNullException(nameof(akaProvider)), ct);

    private async Task DeregisterCoreAsync(
        Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>>? akaProvider,
        CancellationToken ct)
    {
        if (LastResult == null) return;
        PrepareRenewalSecurityProposal();
        var req = BuildDeregisterRequest();
        WriteDiagnostic($"REGISTER removal send; local-port={_profile.LocalPort}; integrity-protected={Agreement is not null}; expires=0.");
        var response = await _transport.SendAndReceiveAsync(req, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SIP de-registration received no response.");
        WriteDiagnostic($"REGISTER removal status={response.StatusCode}; aka-rechallenge={response.StatusCode == 401}; provider-available={akaProvider is not null}.");
        if (response.StatusCode == 401 && akaProvider is not null)
        {
            var challenges = response.Headers.TryGetValue("WWW-Authenticate", out var offered)
                ? offered : [];
            var challenge = challenges.Select(ImsRegisterBuilder.ParseWwwAuthenticate)
                .FirstOrDefault(item => item is not null)
                ?? throw new InvalidOperationException("SIP de-registration received no supported IMS-AKA challenge.");
            SecurityAgreement? selectedSecurity = null;
            if (response.Headers.TryGetValue("Security-Server", out var securityHeaders) &&
                securityHeaders.Count > 0)
            {
                if (_securityProposal is null || _activateSecurity is null)
                    throw new InvalidOperationException("SIP de-registration requires an unavailable IMS IPsec security agreement.");
                selectedSecurity = SecurityAgreementBuilder.EvaluateSecurityServer(
                    string.Join(", ", securityHeaders), _securityProposal,
                    _securityIntegrityAlgorithms, _securityEncryptionAlgorithms).Agreement
                    ?? throw new InvalidOperationException(
                        "SIP de-registration offered no installable IMS security mechanism.");
            }
            else if (Agreement is null && RequiresSecAgree(response))
                throw new InvalidOperationException(
                    "SIP de-registration requires sec-agree but omitted Security-Server.");
            var (rand, autn) = DecodeAkaNonce(challenge.Nonce);
            byte[]? res = null;
            byte[]? ck = null;
            byte[]? ik = null;
            try
            {
                (res, ck, ik) = await akaProvider(rand, autn, ct).ConfigureAwait(false);
                if (res is null || ck is null || ik is null)
                    throw new InvalidOperationException("IMS AKA returned an incomplete de-registration vector.");
                if (selectedSecurity is not null)
                {
                    _activateSecurity!(selectedSecurity, ck, ik);
                    Agreement = selectedSecurity;
                    UpdateProfile(profile => profile with
                    {
                        LocalPort = _securityProposal!.PortClient,
                        ContactPort = _securityProposal.PortServer
                    });
                }
                req = ImsRegisterBuilder.BuildAuthenticatedRegister(
                    _profile, _callId, _cseq++, challenge, res, _fromTag, Agreement is not null);
                SetDeregisterExpiration(req);
                response = await _transport.SendAndReceiveAsync(req, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Authenticated SIP de-registration received no response.");
                WriteDiagnostic($"REGISTER authenticated removal status={response.StatusCode}.");
            }
            finally
            {
                if (res is not null) CryptographicOperations.ZeroMemory(res);
                if (ck is not null) CryptographicOperations.ZeroMemory(ck);
                if (ik is not null) CryptographicOperations.ZeroMemory(ik);
                CryptographicOperations.ZeroMemory(rand);
                CryptographicOperations.ZeroMemory(autn);
            }
        }
        if (response.StatusCode != 200)
            throw new InvalidOperationException($"SIP de-registration failed: {response.StatusCode} {response.ReasonPhrase}");
        var contact = req.GetHeader("Contact")!;
        if (ExtractContactUri(contact) is null)
            throw new InvalidOperationException("SIP de-registration Contact is malformed.");
        if (response.Headers.TryGetValue("Contact", out var remaining) &&
            remaining.SelectMany(SecurityAgreementBuilder.SplitHeaderValues)
                .Any(value => IsSameContactBinding(contact, value) &&
                    !HasZeroContactExpiry(value, response.GetHeader("Expires"))))
            throw new InvalidOperationException("SIP de-registration returned 200 OK but still listed this Contact binding.");
        LastResult = null;
        _lastRegisterAuthorization = null;
        WriteDiagnostic("REGISTER removal confirmed by registrar; local binding cleared.");
    }

    private static bool HasZeroContactExpiry(string contact, string? expiresHeader)
    {
        var match = Regex.Match(contact, @"(?:^|;)\s*expires\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
            return int.TryParse(match.Groups[1].Value, out var seconds) && seconds == 0;
        return int.TryParse(expiresHeader, out var headerSeconds) && headerSeconds == 0;
    }

    private SipMessage BuildDeregisterRequest()
    {
        var request = ImsRegisterBuilder.BuildInitialRegister(_profile, _callId, _cseq++, _fromTag);
        SetDeregisterExpiration(request);
        return request;
    }

    private static void SetDeregisterExpiration(SipMessage request)
    {
        var contact = request.GetHeader("Contact")
            ?? throw new InvalidOperationException("SIP de-registration has no Contact header.");
        var expiry = contact.LastIndexOf(";expires=", StringComparison.OrdinalIgnoreCase);
        if (expiry < 0)
            throw new InvalidOperationException("SIP de-registration Contact has no expiry parameter.");
        request.SetHeader("Contact", contact[..expiry] + ";expires=0");
        request.SetHeader("Expires", "0");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private SipRegistrationResult ExtractRegistrationResult(SipMessage resp200)
    {
        var expected = ImsRegisterBuilder.BuildInitialRegister(_profile, _callId, 1).GetHeader("Contact")!;
        var contacts = resp200.Headers.TryGetValue("Contact", out var all) ? all.SelectMany(SecurityAgreementBuilder.SplitHeaderValues) : [];
        var contact = contacts.FirstOrDefault(candidate => IsSameContactBinding(expected, candidate))
            ?? throw new InvalidOperationException("IMS REGISTER returned 200 OK without the selected UE Contact binding.");
        var serviceRoute = resp200.Headers.TryGetValue("Service-Route", out var routes) ? string.Join(", ", routes) : null;
        var pAssocUri = resp200.Headers.TryGetValue("P-Associated-URI", out var identities) ? string.Join(", ", identities) : null;
        var expiresHeader = resp200.GetHeader("Expires");

        // Try to get expires from Contact header parameter first, then Expires header
        int expires;
        var contactExpiresMatch = Regex.Match(contact, @"(?:^|;)\s*expires\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        if (contactExpiresMatch.Success)
        {
            if (!int.TryParse(contactExpiresMatch.Groups[1].Value, out expires))
                throw new InvalidOperationException("IMS REGISTER Contact expiry is invalid.");
        }
        else if (!string.IsNullOrEmpty(expiresHeader))
        {
            if (!int.TryParse(expiresHeader, out expires))
                throw new InvalidOperationException("IMS REGISTER Expires header is invalid.");
        }
        else
            throw new InvalidOperationException("IMS REGISTER response did not grant a Contact expiration interval.");
        if (expires <= 0) throw new InvalidOperationException("IMS registration granted no positive lifetime.");

        return new SipRegistrationResult(
            ContactUri: contact,
            ExpiresSeconds: expires,
            ServiceRoute: serviceRoute,
            PAssociatedUri: pAssocUri,
            RegisteredAt: DateTime.UtcNow,
            SmsCapabilityConfirmed: contact.Contains("+g.3gpp.smsip", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static bool IsSameContactBinding(string requestedContact, string returnedContact)
    {
        var requestedUri = ExtractContactUri(requestedContact);
        var returnedUri = ExtractContactUri(returnedContact);
        if (requestedUri is null || returnedUri is null) return false;
        var requestedBase = requestedUri.Split(';', 2)[0];
        var returnedBase = returnedUri.Split(';', 2)[0];
        if (!string.Equals(NormalizeDefaultSipPort(requestedBase),
                NormalizeDefaultSipPort(returnedBase), StringComparison.OrdinalIgnoreCase))
            return false;
        var requestedTransport = GetSipUriParameter(requestedUri, "transport") ?? "udp";
        var returnedTransport = GetSipUriParameter(returnedUri, "transport") ?? "udp";
        if (!requestedTransport.Equals(returnedTransport, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var significant in new[] { "user", "ttl", "method", "maddr" })
        {
            if (!string.Equals(GetSipUriParameter(requestedUri, significant),
                    GetSipUriParameter(returnedUri, significant), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string? ExtractContactUri(string contact)
    {
        var value = contact.Trim();
        var open = -1;
        var quoted = false;
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (quoted && value[i] == '\\') { escaped = true; continue; }
            if (value[i] == '"') { quoted = !quoted; continue; }
            if (!quoted && value[i] == '<') { open = i; break; }
        }
        if (open >= 0)
        {
            var close = value.IndexOf('>', open + 1);
            if (close < 0) return null;
            value = value[(open + 1)..close].Trim();
        }
        else
        {
            var headerExpires = value.IndexOf(";expires=", StringComparison.OrdinalIgnoreCase);
            if (headerExpires >= 0) value = value[..headerExpires];
        }
        return value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("sips:", StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    private static string NormalizeDefaultSipPort(string uriBase)
    {
        var defaultPort = uriBase.StartsWith("sips:", StringComparison.OrdinalIgnoreCase) ? ":5061" : ":5060";
        return uriBase.EndsWith(defaultPort, StringComparison.OrdinalIgnoreCase)
            ? uriBase[..^defaultPort.Length] : uriBase;
    }

    private static string? GetSipUriParameter(string uri, string name)
    {
        foreach (var parameter in uri.Split(';').Skip(1))
        {
            var parts = parameter.Split('=', 2);
            if (parts[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                return parts.Length == 2 ? parts[1] : string.Empty;
        }
        return null;
    }

    public void Dispose()
    {
        _refreshCts?.Cancel();
        _lastRegisterAuthorization = null;
        _transport.PreparingOutgoingRequest -= AddSecurityHeaders;
        _transport.Dispose();
    }
}
