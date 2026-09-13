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
    VoSharp.Ike.IkeTransport? Transport = null
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
        CancellationToken ct = default);

    Task StopTunnelAsync(CancellationToken ct = default);
}
