using VoSharp.Common.Aka;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sip;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Telephony;

/// <summary>
/// Registers over an IMS PDN address that Windows already owns. This does not
/// activate a PDP context or provide ipsec-3gpp; a carrier that requires the
/// latter is rejected by SipRegisterSession before authenticated REGISTER.
/// </summary>
public sealed class HostImsRegistrationClient : IAsyncDisposable
{
    private readonly SipRegisterSession _registration;
    private readonly Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> _akaProvider;
    private IHostImsSecurityReservation? _securityReservation;
    private int _disposed;
    private int _invalidated;

    private HostImsRegistrationClient(
        HostImsEndpointCandidate endpoint,
        SipTransport transport,
        SipRegisterSession registration,
        ImsProfile profile,
        SipRegistrationResult result,
        Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> akaProvider,
        IHostImsSecurityReservation? securityReservation,
        string? cardPath)
    {
        Endpoint = endpoint;
        Transport = transport;
        _registration = registration;
        Profile = profile;
        Result = result;
        _akaProvider = akaProvider;
        _securityReservation = securityReservation;
        CardPath = cardPath;
    }

    public HostImsEndpointCandidate Endpoint { get; }
    /// <summary>The selected SIM application transport; null for direct test registrations.</summary>
    public string? CardPath { get; }
    public SipTransport Transport { get; }
    internal ImsProfile Profile { get; }
    /// <summary>The result of the initial REGISTER; use CurrentResult for live status.</summary>
    public SipRegistrationResult Result { get; }
    public SipRegistrationResult? CurrentResult =>
        Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _invalidated) == 0
            ? _registration.LastResult : null;
    public bool IsRegistered => CurrentResult is not null;
    public SipRegisterSession Registration => _registration;

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
        IHostImsSecurityBackend? securityBackend = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        return await RegisterWithCardAsync(modem.Session, modem.GetImeiAsync,
            sim, probe, endpoint, localPort, pcscfPort, ct, securityBackend,
            modem.TryProbeQmiIsimIdentityAsync, modem.AuthenticateQmiIsimAkaAsync,
            modem.TryCanAuthenticateQmiUsimAsync, modem.AuthenticateQmiUsimAkaAsync).ConfigureAwait(false);
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
        Func<AkaChallenge, CancellationToken, Task<AkaResult>>? authenticateQmiUsim = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(getImei);
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(endpoint);
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
                    localPort, pcscfPort, ct, securityBackend, qmiIdentity, "QMI ISIM")
                    .ConfigureAwait(false);
        }

        var identity = await new IsimIdentityReader(session).ReadAsync(ct).ConfigureAwait(false);
        if (identity.Readiness == IsimIdentityReadiness.Available)
            return await RegisterWithIsimAsync(session, getImei, probe, endpoint, localPort, pcscfPort, ct,
                securityBackend, sim.Iccid).ConfigureAwait(false);
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
        }, localPort, pcscfPort, ct, securityBackend, qmiUsimReady ? "QMI USIM" : "AT USIM")
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
        return new ImsProfile(impi, $"sip:{impi}", domain, imei, localIp);
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
    /// still requires an already host-owned IMS bearer and a carrier that does
    /// not require the as-yet unavailable cellular ipsec-3gpp data plane.
    /// </summary>
    public static async Task<HostImsRegistrationClient> RegisterWithIsimAsync(
        ModemDriver modem,
        HostImsProbeResult probe,
        HostImsEndpointCandidate endpoint,
        int localPort = 5060,
        int pcscfPort = 5060,
        CancellationToken ct = default,
        IHostImsSecurityBackend? securityBackend = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        var expectedIccid = await modem.GetIccidAsync(ct).ConfigureAwait(false);
        return await RegisterWithIsimAsync(modem.Session, modem.GetImeiAsync,
            probe, endpoint, localPort, pcscfPort, ct, securityBackend, expectedIccid).ConfigureAwait(false);
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
        string? expectedIccid = null)
    {
        var isim = new IsimIdentityReader(session);
        return await RegisterWithSelectedIsimAsync(session, getImei, probe, endpoint,
            async token => await isim.ReadAsync(token).ConfigureAwait(false),
            isim.AuthenticateAsync, expectedIccid,
            localPort, pcscfPort, ct, securityBackend).ConfigureAwait(false);
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
        string cardPath = "AT ISIM")
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
            identity.HomeDomain, imei, endpoint.LocalAddress.ToString());
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
        }, localPort, pcscfPort, ct, securityBackend, cardPath).ConfigureAwait(false);
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
        string? cardPath = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(akaProvider);
        if (probe.SimInserted == false || !probe.CanAttemptWindowsIms ||
            !probe.EndpointCandidates.Contains(endpoint) || !probe.IsRouteVerified(endpoint))
            throw new InvalidOperationException("The selected endpoint is not owned by an active Windows IMS context in this probe result.");
        var route = probe.RouteChecks!.FirstOrDefault(check => check.Endpoint == endpoint && check.IsVerified);
        if (route?.InterfaceIndex is not > 0)
            throw new InvalidOperationException("The selected IMS endpoint has no verified Windows outgoing interface index.");
        if (localPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        if (pcscfPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(pcscfPort));
        if (securityBackend is not null && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Host IMS IPsec requires the Windows pinned-interface transport.");
        if (profile.PAccessNetworkInfo?.StartsWith("IEEE-802.11", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("A Wi-Fi P-Access-Network-Info cannot be reused on a cellular IMS bearer.", nameof(profile));
        ct.ThrowIfCancellationRequested();

        var transport = new SipTransport(timeoutMs: 15000);
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
            var boundProfile = profile with { LocalIp = bound.Address.ToString(), LocalPort = bound.Port };
            if (securityBackend is not null)
            {
                var protectedPorts = transport.ReserveProtectedPortsOnInterface();
                reservation = await securityBackend.ReserveAsync(
                    bound.Address, endpoint.PcscfAddress, route.InterfaceIndex.Value,
                    protectedPorts.ClientPort, protectedPorts.ServerPort, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Host IMS IPsec backend did not reserve inbound SPIs.");
                if (!SecurityAgreementBuilder.IsValidProtectedPort(reservation.Proposal.PortClient) ||
                    !SecurityAgreementBuilder.IsValidProtectedPort(reservation.Proposal.PortServer) ||
                    reservation.Proposal.PortClient != protectedPorts.ClientPort ||
                    reservation.Proposal.PortServer != protectedPorts.ServerPort)
                    throw new InvalidOperationException("Host IMS IPsec backend reserved invalid protected ports.");
            }
            var registration = new SipRegisterSession(transport, boundProfile, reservation?.Proposal,
                reservation is null ? null : (agreement, ck, ik) =>
                {
                    reservation.Activate(agreement, ck, ik);
                    if (transport.ProtectedServerEndPoint is null)
                        transport.ActivateProtectedPortsOnInterface(
                            reservation.Proposal.PortClient,
                            reservation.Proposal.PortServer,
                            agreement.PcscfServerPort);
                    else if (transport.LocalEndPoint?.Port != reservation.Proposal.PortClient ||
                             transport.ProtectedServerEndPoint.Port != reservation.Proposal.PortServer ||
                             transport.RemoteEndPoint?.Port != agreement.PcscfServerPort)
                        throw new InvalidOperationException("IMS security rekey changed protected ports without a transport handover.");
                });
            try
            {
                var result = await registration.RegisterAsync(akaProvider, ct).ConfigureAwait(false);
                registration.StartRefreshing(akaProvider);
                var client = new HostImsRegistrationClient(endpoint, transport, registration,
                    registration.CurrentProfile, result, akaProvider, reservation, cardPath);
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
        try { await _registration.StopRefreshingAsync().ConfigureAwait(false); }
        finally
        {
            try { _registration.Dispose(); }
            finally { Interlocked.Exchange(ref _securityReservation, null)?.Dispose(); }
        }
    }
}
