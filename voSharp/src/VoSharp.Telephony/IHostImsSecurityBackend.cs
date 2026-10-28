using System.Net;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Telephony;

/// <summary>
/// Two-phase cellular IMS security data plane. The reservation must obtain
/// inbound SPIs before the initial REGISTER advertises Security-Client.
/// Production use requires a backend that either installs verified Windows
/// IPsec SAs or owns a verified raw packet path with user-space ESP; this
/// interface alone does not protect traffic.
/// </summary>
public interface IHostImsSecurityBackend
{
    Task<IHostImsSecurityReservation> ReserveAsync(
        IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex,
        int localClientPort, int localServerPort,
        CancellationToken ct = default);

    /// <summary>Preserve a bearer owner's native identity when available; never silently discard its LUID.</summary>
    Task<IHostImsSecurityReservation> ReserveOwnedAsync(
        IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex, ulong? interfaceLuid,
        int localClientPort, int localServerPort, CancellationToken ct = default)
    {
        if (interfaceLuid is not null)
            throw new NotSupportedException("This Host IMS security backend cannot verify the bearer owner's LUID.");
        return ReserveAsync(localAddress, pcscfAddress, interfaceIndex, localClientPort, localServerPort, ct);
    }
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

internal interface IHostImsUserspaceSecurityReservation : IHostImsSecurityReservation
{
    void Send(SipDatagram datagram);
    SipDatagram Receive(CancellationToken cancellationToken);
}
