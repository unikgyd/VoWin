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
    private readonly SecurityProposal? _securityProposal;
    private readonly Action<SecurityAgreement, byte[], byte[]>? _activateSecurity;
    private readonly IReadOnlyList<string>? _securityIntegrityAlgorithms;
    private readonly IReadOnlyList<string>? _securityEncryptionAlgorithms;
    private readonly Action<string>? _diagnosticLog;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    public SecurityAgreement? Agreement { get; private set; }
    public event EventHandler<SipRegistrationResult>? RegistrationRefreshed;
    public event EventHandler<string>? RegistrationFailed;

    public SipRegistrationResult? LastResult { get; private set; }
    public ImsProfile CurrentProfile => _profile;

    public SipRegisterSession(SipTransport transport, ImsProfile profile,
        SecurityProposal? securityProposal = null,
        Action<SecurityAgreement, byte[], byte[]>? activateSecurity = null,
        IReadOnlyList<string>? securityIntegrityAlgorithms = null,
        IReadOnlyList<string>? securityEncryptionAlgorithms = null,
        Action<string>? diagnosticLog = null)
    {
        if ((securityProposal is null) != (activateSecurity is null))
            throw new ArgumentException("IMS Security-Client requires an SA activation callback, and vice versa.");
        _transport = transport;
        _profile = profile;
        _securityProposal = securityProposal;
        _activateSecurity = activateSecurity;
        _securityIntegrityAlgorithms = securityIntegrityAlgorithms;
        _securityEncryptionAlgorithms = securityEncryptionAlgorithms;
        _diagnosticLog = diagnosticLog;
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
        var totalTimer = Stopwatch.StartNew();
        // ── Step 1: Send initial REGISTER ────────────────────────────────────
        var initReq = ImsRegisterBuilder.BuildInitialRegister(_profile, _callId, _cseq++, _fromTag, _requestedExpiresSeconds);
        AddSecurityHeaders(initReq);
        WriteDiagnostic($"REGISTER step=1/5 send initial request; local={ImsRegisterBuilder.FormatHost(_profile.LocalIp)}:{_profile.LocalPort}; security-client={_securityProposal is not null}.");
        if (_securityProposal is not null)
            WriteDiagnostic($"REGISTER step=1/5 Security-Client offer: {DescribeSecurityClientOffer()}.");
        var requestTimer = Stopwatch.StartNew();
        var resp1 = await _transport.SendAndReceiveAsync(initReq, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("SIP REGISTER: no response from P-CSCF.");
        ThrowIfIntervalTooBrief(resp1);
        WriteDiagnostic($"REGISTER step=1/5 response={resp1.StatusCode} {resp1.ReasonPhrase}; elapsed={requestTimer.ElapsedMilliseconds}ms; www-authenticate={HeaderCount(resp1, "WWW-Authenticate")}; security-server={HasHeader(resp1, "Security-Server")}.");

        // A 200 OK on the first REGISTER (no auth required) is allowed by some operators
        if (resp1.StatusCode == 200)
        {
            if (Agreement is null && (_securityProposal is not null ||
                HasHeader(resp1, "Security-Server") || RequiresSecAgree(resp1)))
                throw new InvalidOperationException(
                    "SIP REGISTER received 200 OK requiring IMS security, but no IMS security association was established.");
            WriteDiagnostic($"REGISTER completed without challenge; total={totalTimer.ElapsedMilliseconds}ms.");
            return LastResult = ExtractRegistrationResult(resp1);
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

        // ── Step 3: Decode RAND + AUTN from nonce (3GPP TS 24.228 §5.1.1.2) ─
        var (rand, autn) = DecodeAkaNonce(challenge.Nonce);

        // ── Step 4: Perform AKA authentication ───────────────────────────────
        var akaTimer = Stopwatch.StartNew();
        WriteDiagnostic("REGISTER step=3/5 invoking USIM AKA; RAND/AUTN are intentionally not logged.");
        var (res, ck, ik) = await akaProvider(rand, autn, ct).ConfigureAwait(false);
        try
        {
            if (res is null || ck is null || ik is null)
                throw new InvalidOperationException("USIM AKA returned an incomplete authentication vector.");
            WriteDiagnostic($"REGISTER step=3/5 USIM AKA completed; elapsed={akaTimer.ElapsedMilliseconds}ms; result-lengths=res:{res.Length},ck:{ck.Length},ik:{ik.Length}.");
            if (resp1.Headers.TryGetValue("Security-Server", out var offered))
            {
                if (_securityProposal == null || _activateSecurity == null)
                    throw new InvalidOperationException("P-CSCF requires IMS IPsec, but this transport has no IMS security data plane.");
                var evaluation = SecurityAgreementBuilder.EvaluateSecurityServer(
                    string.Join(", ", offered), _securityProposal,
                    _securityIntegrityAlgorithms, _securityEncryptionAlgorithms);
                WriteDiagnostic($"REGISTER step=4/5 Security-Server compatibility: {string.Join(" | ", evaluation.CandidateDiagnostics)}");
                var agreement = evaluation.Agreement
                    ?? throw new InvalidOperationException("P-CSCF offered no IMS security mechanism that was present in Security-Client.");
                _activateSecurity(agreement, ck, ik);
                Agreement = agreement;
                _profile = _profile with { LocalPort = _securityProposal.PortClient, ContactPort = _securityProposal.PortServer };
                WriteDiagnostic($"REGISTER step=4/5 IMS IPsec selected; integrity={agreement.Selected.IntegrityAlgorithm}; encryption={agreement.Selected.EncryptionAlgorithm}; ue-ports={agreement.Selected.PortClient}/{agreement.Selected.PortServer}; pcscf-ports={agreement.PcscfClientPort}/{agreement.PcscfServerPort}.");
            }
            else
            {
                WriteDiagnostic("REGISTER step=4/5 P-CSCF did not require an additional IMS IPsec security agreement.");
            }
            // ── Step 5: Send authenticated REGISTER ──────────────────────────────
            var authReq = ImsRegisterBuilder.BuildAuthenticatedRegister(
                _profile, _callId, _cseq++, challenge, res, _fromTag, Agreement != null, _requestedExpiresSeconds);
            AddSecurityHeaders(authReq);

            requestTimer.Restart();
            WriteDiagnostic($"REGISTER step=5/5 send authenticated request; local-port={_profile.LocalPort}; integrity-protected={Agreement is not null}.");
            var resp2 = await _transport.SendAndReceiveAsync(authReq, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("SIP authenticated REGISTER: no response.");
            ThrowIfIntervalTooBrief(resp2);
            WriteDiagnostic($"REGISTER step=5/5 response={resp2.StatusCode} {resp2.ReasonPhrase}; elapsed={requestTimer.ElapsedMilliseconds}ms; service-route={HasHeader(resp2, "Service-Route")}; associated-uri={HasHeader(resp2, "P-Associated-URI")}; expires={GetHeaderValue(resp2, "Expires") ?? "contact/default"}.");

            if (resp2.StatusCode != 200)
                throw new InvalidOperationException(
                    $"SIP REGISTER auth failed: {resp2.StatusCode} {resp2.ReasonPhrase}");

            Console.WriteLine("[SipRegisterSession] IMS registration accepted (200 OK).");
            LastResult = ExtractRegistrationResult(resp2);
            WriteDiagnostic($"REGISTER completed successfully; total={totalTimer.ElapsedMilliseconds}ms; refresh={LastResult.ExpiresSeconds}s; sms-capability={LastResult.SmsCapabilityConfirmed}.");
            return LastResult;
        }
        finally
        {
            if (res is not null) CryptographicOperations.ZeroMemory(res);
            if (ck is not null) CryptographicOperations.ZeroMemory(ck);
            if (ik is not null) CryptographicOperations.ZeroMemory(ik);
        }
    }

    private void WriteDiagnostic(string message) => _diagnosticLog?.Invoke(message);

    private static bool HasHeader(SipMessage message, string name) =>
        message.Headers.TryGetValue(name, out var values) && values.Count > 0;

    private static bool RequiresSecAgree(SipMessage message) =>
        new[] { "Require", "Proxy-Require" }
            .SelectMany(name => message.Headers.TryGetValue(name, out var values) ? values : [])
            .SelectMany(value => value.Split(','))
            .Any(value => value.Trim().Equals("sec-agree", StringComparison.OrdinalIgnoreCase));

    private static int HeaderCount(SipMessage message, string name) =>
        message.Headers.TryGetValue(name, out var values) ? values.Count : 0;

    private static string? GetHeaderValue(SipMessage message, string name) =>
        message.Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

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

        if (decoded.Length < 32)
            throw new InvalidOperationException($"IMS AKA nonce decoded to {decoded.Length} bytes; RAND/AUTN requires at least 32.");
        return (decoded[..16], decoded[16..32]);
    }

    public void AddSecurityHeaders(SipMessage request)
    {
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
        if (Agreement != null)
        {
            var authorization = request.GetHeader("Authorization");
            if (authorization != null)
                request.SetHeader("Authorization", authorization.Replace("integrity-protected=no", "integrity-protected=yes"));
            request.SetHeader("Security-Verify", Agreement.VerifyValue);
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
                    var refreshAt = granted.RegisteredAt.AddSeconds(granted.ExpiresSeconds * 0.8);
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
                        catch (Exception ex)
                        {
                            lastError = ex;
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
                        throw new InvalidOperationException("IMS REGISTER refresh retries were exhausted.", lastError);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LastResult = null;
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
        var req = BuildDeregisterRequest();
        var response = await _transport.SendAndReceiveAsync(req, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SIP de-registration received no response.");
        if (response.StatusCode == 401 && akaProvider is not null)
        {
            if (response.GetHeader("Security-Server") is not null && Agreement is null)
                throw new InvalidOperationException("SIP de-registration requires an unavailable IMS IPsec security agreement.");
            var challenges = response.Headers.TryGetValue("WWW-Authenticate", out var offered)
                ? offered : [];
            var challenge = challenges.Select(ImsRegisterBuilder.ParseWwwAuthenticate)
                .FirstOrDefault(item => item is not null)
                ?? throw new InvalidOperationException("SIP de-registration received no supported IMS-AKA challenge.");
            var (rand, autn) = DecodeAkaNonce(challenge.Nonce);
            byte[]? res = null;
            byte[]? ck = null;
            byte[]? ik = null;
            try
            {
                (res, ck, ik) = await akaProvider(rand, autn, ct).ConfigureAwait(false);
                if (res is null || ck is null || ik is null)
                    throw new InvalidOperationException("IMS AKA returned an incomplete de-registration vector.");
                req = ImsRegisterBuilder.BuildAuthenticatedRegister(
                    _profile, _callId, _cseq++, challenge, res, _fromTag, Agreement is not null);
                SetDeregisterExpiration(req);
                response = await _transport.SendAndReceiveAsync(req, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Authenticated SIP de-registration received no response.");
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
                .Any(value => IsSameContactBinding(contact, value)))
            throw new InvalidOperationException("SIP de-registration returned 200 OK but still listed this Contact binding.");
        LastResult = null;
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
        _transport.PreparingOutgoingRequest -= AddSecurityHeaders;
        _transport.Dispose();
    }
}
