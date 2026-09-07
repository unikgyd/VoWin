using System.Net;
using System.Security.Cryptography;
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
    private readonly SecurityProposal? _securityProposal;
    private readonly Action<SecurityAgreement, byte[], byte[]>? _activateSecurity;
    private readonly IReadOnlyList<string>? _securityIntegrityAlgorithms;
    private readonly IReadOnlyList<string>? _securityEncryptionAlgorithms;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    public SecurityAgreement? Agreement { get; private set; }
    public event EventHandler<SipRegistrationResult>? RegistrationRefreshed;
    public event EventHandler<string>? RegistrationFailed;

    public SipRegistrationResult? LastResult { get; private set; }

    public SipRegisterSession(SipTransport transport, ImsProfile profile,
        SecurityProposal? securityProposal = null,
        Action<SecurityAgreement, byte[], byte[]>? activateSecurity = null,
        IReadOnlyList<string>? securityIntegrityAlgorithms = null,
        IReadOnlyList<string>? securityEncryptionAlgorithms = null)
    {
        _transport = transport;
        _profile = profile;
        _securityProposal = securityProposal;
        _activateSecurity = activateSecurity;
        _securityIntegrityAlgorithms = securityIntegrityAlgorithms;
        _securityEncryptionAlgorithms = securityEncryptionAlgorithms;
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
        // ── Step 1: Send initial REGISTER ────────────────────────────────────
        var initReq = ImsRegisterBuilder.BuildInitialRegister(_profile, _callId, _cseq++, _fromTag);
        AddSecurityHeaders(initReq);
        var resp1 = await _transport.SendAndReceiveAsync(initReq, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("SIP REGISTER: no response from P-CSCF.");

        // A 200 OK on the first REGISTER (no auth required) is allowed by some operators
        if (resp1.StatusCode == 200)
            return LastResult = ExtractRegistrationResult(resp1);

        // ── Step 2: Handle 401 Unauthorized ──────────────────────────────────
        if (resp1.StatusCode != 401)
            throw new InvalidOperationException(
                $"SIP REGISTER: unexpected response {resp1.StatusCode} {resp1.ReasonPhrase}\nHeaders:\n{string.Join("\n", resp1.Headers.Select(h => $"  {h.Key}: {string.Join(", ", h.Value)}"))}\nBody:\n{resp1.Body}");
        if (Agreement != null)
            throw new InvalidOperationException("Protected IMS registration was challenged again; reconnect to negotiate fresh SAs.");

        var wwwAuth = resp1.GetHeader("WWW-Authenticate")
                     ?? throw new InvalidOperationException("SIP 401 missing WWW-Authenticate header.");

        var challenge = ImsRegisterBuilder.ParseWwwAuthenticate(wwwAuth)
                        ?? throw new InvalidOperationException("Failed to parse WWW-Authenticate challenge.");

        // ── Step 3: Decode RAND + AUTN from nonce (3GPP TS 24.228 §5.1.1.2) ─
        // The Digest nonce for IMS AKA is Base64(RAND || AUTN)
        byte[] rand, autn;
        try
        {
            var nonceBytes = Convert.FromBase64String(challenge.Nonce);
            if (nonceBytes.Length < 32)
                throw new InvalidOperationException($"AKA nonce too short: {nonceBytes.Length} bytes (expected ≥32).");
            rand = nonceBytes[..16];
            autn = nonceBytes[16..32];
        }
        catch (FormatException)
        {
            // Some operators send RAND+AUTN as hex instead of Base64
            try
            {
                var hex = challenge.Nonce.Replace("-", "");
                if (hex.Length < 64)
                    throw new InvalidOperationException("AKA nonce hex too short.");
                rand = Convert.FromHexString(hex[..32]);
                autn = Convert.FromHexString(hex[32..64]);
            }
            catch
            {
                throw new InvalidOperationException(
                    $"Cannot decode RAND/AUTN from nonce: '{challenge.Nonce}'");
            }
        }

        // ── Step 4: Perform AKA authentication ───────────────────────────────
        var (res, ck, ik) = await akaProvider(rand, autn, ct).ConfigureAwait(false);
        try
        {
            if (resp1.Headers.TryGetValue("Security-Server", out var offered))
            {
                if (_securityProposal == null || _activateSecurity == null)
                    throw new InvalidOperationException("P-CSCF requires IMS IPsec, but this transport has no IMS security data plane.");
                var agreement = SecurityAgreementBuilder.ParseSecurityServer(string.Join(", ", offered), _securityProposal)
                    ?? throw new InvalidOperationException("P-CSCF offered no supported IMS security agreement.");
                _activateSecurity(agreement, ck, ik);
                Agreement = agreement;
                _profile = _profile with { LocalPort = _securityProposal.PortClient, ContactPort = _securityProposal.PortServer };
            }
            // ── Step 5: Send authenticated REGISTER ──────────────────────────────
            var authReq = ImsRegisterBuilder.BuildAuthenticatedRegister(
                _profile, _callId, _cseq++, challenge, res, _fromTag, Agreement != null);
            AddSecurityHeaders(authReq);

            var resp2 = await _transport.SendAndReceiveAsync(authReq, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("SIP authenticated REGISTER: no response.");

            if (resp2.StatusCode != 200)
                throw new InvalidOperationException(
                    $"SIP REGISTER auth failed: {resp2.StatusCode} {resp2.ReasonPhrase}");

            Console.WriteLine($"[SipRegisterSession] 200 OK Headers:\n{string.Join("\n", resp2.Headers.Select(h => $"  {h.Key}: {string.Join(", ", h.Value)}"))}");
            LastResult = ExtractRegistrationResult(resp2);
            return LastResult;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(res);
            CryptographicOperations.ZeroMemory(ck);
            CryptographicOperations.ZeroMemory(ik);
        }
    }

    public void AddSecurityHeaders(SipMessage request)
    {
        if (_securityProposal == null) return;
        request.SetHeader("Supported", "path, sec-agree, gruu");
        if (request.Method == "REGISTER")
            request.SetHeader("Security-Client", _securityIntegrityAlgorithms != null && _securityEncryptionAlgorithms != null
                ? SecurityAgreementBuilder.BuildSecurityClient(_securityProposal, _securityIntegrityAlgorithms, _securityEncryptionAlgorithms)
                : SecurityAgreementBuilder.BuildSecurityClient(_securityProposal));
        if (Agreement != null)
        {
            var authorization = request.GetHeader("Authorization");
            if (authorization != null)
                request.SetHeader("Authorization", authorization.Replace("integrity-protected=no", "integrity-protected=yes"));
            request.SetHeader("Security-Verify", Agreement.VerifyValue);
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
                    var remaining = expiresAt - DateTime.UtcNow;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(100, remaining.TotalMilliseconds * 0.8)), token).ConfigureAwait(false);

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
    public async Task DeregisterAsync(CancellationToken ct = default)
    {
        var req = ImsRegisterBuilder.BuildInitialRegister(_profile, _callId, _cseq++, _fromTag);
        // Override Contact expires and Expires header to 0
        req.SetHeader("Contact", $"<sip:{_profile.PrivateIdentity}@{_profile.LocalIp}:{_profile.LocalPort}>;expires=0");
        req.SetHeader("Expires", "0");
        await _transport.SendAsync(req, ct).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private SipRegistrationResult ExtractRegistrationResult(SipMessage resp200)
    {
        var expected = ImsRegisterBuilder.BuildInitialRegister(_profile, _callId, 1).GetHeader("Contact")!.Split('>')[0] + ">";
        var contacts = resp200.Headers.TryGetValue("Contact", out var all) ? all.SelectMany(SecurityAgreementBuilder.SplitHeaderValues) : [];
        var contact = contacts.FirstOrDefault(c => c.Contains(expected, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var serviceRoute = resp200.Headers.TryGetValue("Service-Route", out var routes) ? string.Join(", ", routes) : null;
        var pAssocUri = resp200.Headers.TryGetValue("P-Associated-URI", out var identities) ? string.Join(", ", identities) : null;
        var expiresHeader = resp200.GetHeader("Expires");

        // Try to get expires from Contact header parameter first, then Expires header
        int expires = 3600;
        var contactExpiresMatch = Regex.Match(contact, @"(?:^|;)\s*expires\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        if (contactExpiresMatch.Success)
            int.TryParse(contactExpiresMatch.Groups[1].Value, out expires);
        else if (!string.IsNullOrEmpty(expiresHeader))
            int.TryParse(expiresHeader, out expires);
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

    public void Dispose()
    {
        _refreshCts?.Cancel();
        _transport.PreparingOutgoingRequest -= AddSecurityHeaders;
        _transport.Dispose();
    }
}
