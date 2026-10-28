using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Telephony;

/// <summary>
/// Explicit-only cellular IMS security backend that keeps ESP cryptography in
/// C# and uses WinDivert solely for raw IPv6 packet I/O on a verified bearer.
/// The caller still owns SIP consent, modem/PPP lifetime and driver packaging.
/// </summary>
public sealed class WindowsHostImsUserspaceSecurityBackend(string winDivertDll)
    : IHostImsSecurityBackend
{
    public Task<IHostImsSecurityReservation> ReserveAsync(
        IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex,
        int localClientPort, int localServerPort, CancellationToken ct = default)
        => ReserveOwnedAsync(localAddress, pcscfAddress, interfaceIndex, null,
            localClientPort, localServerPort, ct);

    public Task<IHostImsSecurityReservation> ReserveOwnedAsync(
        IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex, ulong? interfaceLuid,
        int localClientPort, int localServerPort, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(pcscfAddress);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Host IMS user-space ESP requires native Windows.");
        if (interfaceIndex <= 0 ||
            !SecurityAgreementBuilder.IsValidProtectedPort(localClientPort) ||
            !SecurityAgreementBuilder.IsValidProtectedPort(localServerPort) ||
            localClientPort == localServerPort)
            throw new ArgumentException("Host IMS ESP requires one verified interface and two distinct protected ports.");

        var random = RandomNumberGenerator.GetBytes(8);
        var spiClient = BinaryPrimitives.ReadUInt32BigEndian(random.AsSpan(0, 4)) | 0x100u;
        var spiServer = BinaryPrimitives.ReadUInt32BigEndian(random.AsSpan(4, 4)) | 0x100u;
        if (spiServer == spiClient) spiServer ^= 0x80000000u;
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            spiClient, spiServer, localClientPort, localServerPort);
        var session = HostImsUserspaceEspSession.OpenWindowsBearer(winDivertDll,
            localAddress, pcscfAddress, interfaceIndex, proposal, interfaceLuid);
        IHostImsSecurityReservation reservation = new Reservation(session);
        return Task.FromResult(reservation);
    }

    private sealed class Reservation(HostImsUserspaceEspSession session)
        : IHostImsUserspaceSecurityReservation
    {
        private readonly HostImsUserspaceEspSession _session = session;
        private bool _disposed;

        public SecurityProposal Proposal => _session.Proposal;

        public void Activate(SecurityAgreement agreement, byte[] ck, byte[] ik)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _session.Activate(agreement, ck, ik);
        }

        public void Send(SipDatagram datagram)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _session.Send(datagram);
        }

        public SipDatagram Receive(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _session.Receive(cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
    }
}
