using System.Net;
using VoSharp.Common.Aka;
using VoSharp.Ike;
using VoSharp.Ike.Transport;
using VoSharp.Ipsec;
using VoSharp.Sim;

namespace VoSharp.Telephony.VoWifi;

/// <summary>
/// C# full-stack IKEv2 / User-Space ESP backend:
/// Executes IKEv2 and EAP-AKA in C#, and runs pure user-space RFC 4303 ESP
/// encryption/decryption with dedicated SOCKS5 proxy routing per SIM slot.
/// </summary>
public sealed class CSharpIkeBackend : IIkeBackend
{
    private IkeTransport? _transport;
    private EspTunnel? _espTunnel;
    private IkeLivenessProbe? _livenessProbe;
    private bool _disposed;

    public EspTunnel? EspTunnel => _espTunnel;
    public IkeTransport? Transport => _transport;

    /// <summary>Proves the ePDG IKE SA is alive with an encrypted INFORMATIONAL exchange.</summary>
    public Task ProbeLivenessAsync(CancellationToken ct = default)
        => _livenessProbe?.ProbeAsync(ct)
           ?? throw new InvalidOperationException("IKE liveness probe is unavailable before the tunnel is established.");

    public async Task<IkeBackendResult> StartTunnelAsync(
        SimIdentity sim,
        IAkaProvider akaProvider,
        string epdgIp,
        string apn = "ims",
        string? fallbackPcscf = null,
        IkeProposalSuite suite = IkeProposalSuite.Auto,
        string? proxyUrl = null,
        string? imei = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(akaProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(epdgIp);

        if (!IPAddress.TryParse(epdgIp, out var remoteIp))
            throw new ArgumentException($"Invalid ePDG IP address: {epdgIp}", nameof(epdgIp));

        if (!string.IsNullOrWhiteSpace(proxyUrl) &&
            !proxyUrl.Equals("direct", StringComparison.OrdinalIgnoreCase) &&
            Socks5Client.TryParse(proxyUrl) == null)
        {
            throw new ArgumentException(
                "VoWiFi only supports a valid socks5:// or socks5h:// proxy because ePDG requires UDP ASSOCIATE.",
                nameof(proxyUrl));
        }

        var homePlmn = EpdgResolver.ResolveHomePlmn(sim);

        var legacyIkeSuite = new IkeSuite(
            IkeEncryptionId.AesCbc, 128, IkePrfId.HmacSha1,
            IkeIntegrityId.HmacSha1_96, IkeDhGroupId.Modp1024);
        var configuredIkeSuite = suite switch
        {
            IkeProposalSuite.Legacy => legacyIkeSuite,
            IkeProposalSuite.Modern => new IkeSuite(
                IkeEncryptionId.AesCbc, 256, IkePrfId.HmacSha2_256, IkeIntegrityId.HmacSha2_256_128, IkeDhGroupId.Ecp256),
            _ => IkeSuite.Preferred
        };

        // Offer a broad standards-compatible proposal set. A single KE can serve
        // all four proposals because every proposal in this request uses MODP-2048.
        IReadOnlyList<IkeSuite> ikeSuites = suite is IkeProposalSuite.Legacy or IkeProposalSuite.Modern
            ? new[] { configuredIkeSuite }
            : new[]
            {
                IkeSuite.Preferred,
                new IkeSuite(IkeEncryptionId.AesCbc, 128, IkePrfId.HmacSha2_256,
                    IkeIntegrityId.HmacSha2_256_128, IkeDhGroupId.Modp2048),
                new IkeSuite(IkeEncryptionId.AesCbc, 256, IkePrfId.HmacSha1,
                    IkeIntegrityId.HmacSha1_96, IkeDhGroupId.Modp2048),
                new IkeSuite(IkeEncryptionId.AesCbc, 128, IkePrfId.HmacSha1,
                    IkeIntegrityId.HmacSha1_96, IkeDhGroupId.Modp2048)
            };
        IReadOnlyList<EspSuite> childSuites = new[]
        {
            new EspSuite(IkeEncryptionId.AesCbc, 128, IkeIntegrityId.HmacSha1_96),
            new EspSuite(IkeEncryptionId.AesCbc, 256, IkeIntegrityId.HmacSha2_256_128),
            new EspSuite(IkeEncryptionId.AesCbc, 128, IkeIntegrityId.HmacSha2_256_128),
            new EspSuite(IkeEncryptionId.AesCbc, 256, IkeIntegrityId.HmacSha1_96)
        };

        IkeSessionResult? result = null;
        var familyErrors = new List<string>();
        foreach (var addressFamily in new[]
                 {
                     IkeAddressFamilyMode.Ipv6,
                     IkeAddressFamilyMode.Dual,
                     IkeAddressFamilyMode.Ipv4
                 })
        {
            result = await IkeSession.EstablishAsync(new IkeSessionRequest(
                EpdgIp: remoteIp,
                AkaProvider: akaProvider,
                Imsi: sim.Imsi,
                HomeMcc: homePlmn.Mcc,
                HomeMnc: homePlmn.Mnc,
                ExpectedIccid: sim.Iccid,
                Apn: apn,
                Imei: imei,
                FallbackPcscf: fallbackPcscf,
                Suite: configuredIkeSuite,
                ChildSuite: EspSuite.Preferred,
                ProxyUrl: proxyUrl,
                Suites: ikeSuites,
                ChildSuites: childSuites,
                AddressFamilyMode: addressFamily), ct).ConfigureAwait(false);

            // An ePDG that answers a dual-family CFG_REQUEST with a mismatched pair (an IPv6
            // address but an IPv4 P-CSCF) succeeds at IKE_AUTH yet leaves a tunnel that cannot
            // carry IMS signalling, so this rung is not a usable PDN — keep searching, as the
            // reference engine does when a family yields no usable P-CSCF.
            var usablePdn = result.Success &&
                            !string.IsNullOrEmpty(result.AssignedIp) &&
                            !string.IsNullOrEmpty(result.PcscfIp) &&
                            IkeSession.IsUsablePdn(result.AssignedIp, result.PcscfIp);
            if (usablePdn)
                break;

            familyErrors.Add(result.Success && !string.IsNullOrEmpty(result.AssignedIp)
                ? $"{addressFamily}: ePDG assigned {result.AssignedIp} but returned P-CSCF {result.PcscfIp} (address families differ)"
                : $"{addressFamily}: {result.ErrorMessage ?? "no usable assigned IP/P-CSCF"}");
            result.Transport?.Dispose();

            // As in the reference engine, only change CP/TS family after the
            // SIM has authenticated. Earlier rejection is identity/AAA, not PDN family.
            if (!result.EapSucceeded)
                break;
        }

        if (result == null)
            throw new InvalidOperationException("IKE address-family discovery did not run.");
        if (!result.Success || result.AssignedIp == null || result.PcscfIp == null)
        {
            return new IkeBackendResult(
                Success: false,
                AssignedIp: null,
                PcscfIp: null,
                DnsIps: Array.Empty<string>(),
                InboundSpi: 0,
                OutboundSpi: 0,
                ErrorMessage: familyErrors.Count > 0
                    ? string.Join(" | ", familyErrors)
                    : result.ErrorMessage ?? "IKE session establishment failed."
            );
        }

        _transport = result.Transport;
        _livenessProbe?.Dispose();
        _livenessProbe = result.LivenessProbe;
        var negotiatedEsp = result.EspSuite
            ?? throw new InvalidOperationException("IKE completed without a negotiated ESP suite.");
        Console.WriteLine(
            $"[CSharpIkeBackend] IKE DH={result.IkeSuite?.DhGroupId}, ESP=AES-CBC-{negotiatedEsp.EncryptionBits}/" +
            $"integrity-{negotiatedEsp.IntegrityId}, assigned={result.AssignedIp}, P-CSCF={result.PcscfIp}");

        // ── Initialize Userspace ESP Tunnel ────────────────────────────────
        _espTunnel?.Dispose();
        _espTunnel = new EspTunnel(
            outboundSpi: result.OutboundSpi,
            inboundSpi: result.InboundSpi,
            outboundEncKey: result.OutboundEncKey!,
            outboundAuthKey: result.OutboundAuthKey!,
            inboundEncKey: result.InboundEncKey!,
            inboundAuthKey: result.InboundAuthKey!,
            // The responder can choose any transform we offered.  These values
            // must match the CHILD_SA selection, not the initial IKE preference.
            encryption: $"AES-CBC-{negotiatedEsp.EncryptionBits}",
            integrity: negotiatedEsp.IntegrityId == IkeIntegrityId.HmacSha1_96
                ? "HMAC-SHA1-96"
                : "HMAC-SHA256-128"
        );

        if (_transport != null)
        {
            _transport.StartPacketPump(result.InboundSpi);
            Console.WriteLine($"[CSharpIkeBackend] Userspace EspTunnel initialized and packet pump started on UDP 4500 (Inbound SPI=0x{result.InboundSpi:x8})");
        }

        return new IkeBackendResult(
            Success: true,
            AssignedIp: result.AssignedIp,
            PcscfIp: result.PcscfIp,
            DnsIps: result.DnsIps,
            InboundSpi: result.InboundSpi,
            OutboundSpi: result.OutboundSpi,
            ErrorMessage: null,
            IkeSuite: result.IkeSuite,
            EspSuite: negotiatedEsp,
            EspTunnel: _espTunnel,
            Transport: _transport
        );
    }

    public Task StopTunnelAsync(CancellationToken ct = default)
    {
        _livenessProbe?.Dispose();
        _livenessProbe = null;
        _espTunnel?.Dispose();
        _espTunnel = null;

        _transport?.Dispose();
        _transport = null;

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopTunnelAsync().GetAwaiter().GetResult();
    }
}
