using VoSharp.Common.Aka;
using VoSharp.Sim;

namespace VoSharp.Telephony.VoWifi;

public sealed record IkeBackendResult(
    bool Success,
    string? AssignedIp,
    string? PcscfIp,
    IReadOnlyList<string> DnsIps,
    uint InboundSpi,
    uint OutboundSpi,
    string? ErrorMessage,
    VoSharp.Ike.IkeSuite? IkeSuite = null,
    VoSharp.Ike.EspSuite? EspSuite = null,
    EspTunnel? EspTunnel = null,
    VoSharp.Ike.IkeTransport? Transport = null,
    bool RetryableTransportFailure = false,
    bool RetryableAddressFamilyFailure = false,
    bool RetryableIdentityRejection = false,
    bool RetryableInitialTimeout = false,
    bool RetryableInitialProposal = false,
    // May retain an earlier attempt's final AUTH for home-binding protection.
    bool FinalAuthVerified = false,
    bool InitialResponderAuthVerified = false,
    // Must belong to this result's authenticated attempt, not historical AUTH.
    VoSharp.Ike.IkeNetworkRejection? NetworkRejection = null
);

public interface IIkeBackend : IDisposable
{
    EspTunnel? EspTunnel { get; }
    VoSharp.Ike.IkeTransport? Transport { get; }

    Task ProbeLivenessAsync(CancellationToken ct = default);

    Task<IkeBackendResult> StartTunnelAsync(
        SimIdentity sim,
        IAkaProvider akaProvider,
        string epdgIp,
        string apn = "ims",
        string? fallbackPcscf = null,
        IkeProposalSuite suite = IkeProposalSuite.Auto,
        string? proxyUrl = null,
        string? imei = null,
        Action<string>? diagnosticLog = null,
        CancellationToken ct = default,
        bool forceNatt = false,
        System.Net.IPAddress? localAddress = null);

    Task StopTunnelAsync(CancellationToken ct = default);
}
