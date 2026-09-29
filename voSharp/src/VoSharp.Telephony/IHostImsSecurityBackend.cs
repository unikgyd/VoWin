using System.Net;
using VoSharp.Sip;

namespace VoSharp.Telephony;

/// <summary>
/// Two-phase cellular IMS security data plane. The reservation must obtain
/// inbound SPIs before the initial REGISTER advertises Security-Client.
/// Production use requires a backend that installs and verifies real Windows
/// IPsec SAs; this interface alone does not protect traffic.
/// </summary>
public interface IHostImsSecurityBackend
{
    Task<IHostImsSecurityReservation> ReserveAsync(
        IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex,
        int localClientPort, int localServerPort,
        CancellationToken ct = default);
}

public interface IHostImsSecurityReservation : IDisposable
{
    SecurityProposal Proposal { get; }

    /// <summary>
    /// Install both SA pairs and return only after protected traffic can be
    /// sent. The backend must copy any key material it retains because the
    /// registration session zeroes the AKA buffers after this callback.
    /// </summary>
    void Activate(SecurityAgreement agreement, byte[] ck, byte[] ik);
}
