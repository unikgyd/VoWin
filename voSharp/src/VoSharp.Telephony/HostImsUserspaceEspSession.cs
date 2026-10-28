using System.Net;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Telephony;

/// <summary>
/// Raw IPv6 ESP packet boundary for the experimental cellular Host IMS path.
/// The implementation must be pinned to the verified IMS bearer/P-CSCF and
/// must not inject decrypted UDP back into the Windows IP stack.
/// </summary>
internal interface IHostImsRawEspIo : IDisposable
{
    void Send(byte[] ipv6EspPacket);
    byte[] Receive(CancellationToken cancellationToken);
}

/// <summary>
/// Reuses the existing C# IMS ESP codec on a Windows-owned cellular bearer.
/// Registration may select this leg only through an explicit security backend;
/// the default Host IMS registration path does not install a raw packet driver.
/// </summary>
internal sealed class HostImsUserspaceEspSession : IDisposable
{
    private readonly ImsIpsecTransport _esp;
    private readonly IHostImsRawEspIo _io;
    private readonly IPAddress _local;
    private readonly IPAddress _pcscf;
    private bool _active;
    private bool _disposed;

    internal HostImsUserspaceEspSession(IPAddress local, IPAddress pcscf,
        IHostImsRawEspIo io, SecurityProposal? proposal = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(pcscf);
        ArgumentNullException.ThrowIfNull(io);
        if (local.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            pcscf.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            local.Equals(pcscf) || local.IsIPv4MappedToIPv6 || pcscf.IsIPv4MappedToIPv6)
            throw new ArgumentException("Host IMS raw ESP requires distinct IPv6 bearer and P-CSCF addresses.");
        _local = local;
        _pcscf = pcscf;
        _io = io;
        _esp = new ImsIpsecTransport(local, pcscf, proposal);
    }

    internal SecurityProposal Proposal => _esp.Proposal;

    internal static HostImsUserspaceEspSession OpenWindowsBearer(
        string winDivertDll, IPAddress local, IPAddress pcscf,
        int interfaceIndex, SecurityProposal? proposal = null, ulong? interfaceLuid = null)
    {
        var io = new WindowsHostImsWinDivertIo(winDivertDll, local, pcscf,
            interfaceIndex, interfaceLuid);
        try { return new HostImsUserspaceEspSession(local, pcscf, io, proposal); }
        catch { io.Dispose(); throw; }
    }

    internal void Activate(SecurityAgreement agreement, byte[] ck, byte[] ik)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Security-Client advertises several supported algorithm pairs with
        // the same locally reserved SPIs and ports. The P-CSCF may choose any
        // advertised pair, including on a later REGISTER re-challenge; only
        // the local SA selectors must remain immutable.
        var selected = agreement.Selected;
        if (selected.SpiClient != Proposal.SpiClient ||
            selected.SpiServer != Proposal.SpiServer ||
            selected.PortClient != Proposal.PortClient ||
            selected.PortServer != Proposal.PortServer ||
            !SecurityAgreementBuilder.DefaultIntegrityAlgorithms.Contains(
                selected.IntegrityAlgorithm, StringComparer.Ordinal) ||
            !SecurityAgreementBuilder.DefaultEncryptionAlgorithms.Contains(
                selected.EncryptionAlgorithm, StringComparer.Ordinal))
            throw new InvalidOperationException("Security-Server selected an unoffered Host IMS ESP SA.");
        _esp.Activate(agreement, ck, ik);
        _active = true;
    }

    internal void Send(SipDatagram datagram)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_active) throw new InvalidOperationException("Host IMS ESP is not active; refusing plaintext fallback.");
        if (!datagram.LocalEndPoint.Address.Equals(_local) ||
            !datagram.RemoteEndPoint.Address.Equals(_pcscf))
            throw new InvalidOperationException("Host IMS SIP endpoint is not the selected bearer/P-CSCF.");
        _io.Send(_esp.Protect(datagram));
    }

    internal SipDatagram Receive(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_active) throw new InvalidOperationException("Host IMS ESP is not active.");
        var packet = _io.Receive(cancellationToken);
        if (packet.Length < 60 || packet[0] >> 4 != 6 || packet[6] != 50)
            throw new FormatException("Host IMS raw input is not an IPv6 ESP packet.");
        var datagram = IpPacketUtils.ParseUdpPacket(_esp.Unprotect(packet));
        if (!datagram.LocalEndPoint.Address.Equals(_local) ||
            !datagram.RemoteEndPoint.Address.Equals(_pcscf))
            throw new FormatException("Decrypted Host IMS SIP endpoint changed.");
        return datagram;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _esp.Dispose(); }
        finally { _io.Dispose(); }
    }
}
