using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VoSharp.Ipsec;
using VoSharp.Modem;
using VoSharp.Sip;

namespace VoSharp.Telephony;

/// <summary>
/// Experimental Windows manual-SA adapter. It is intentionally not installed
/// by the product: bearer lifetime, remote-port selectors, rekey and crash
/// recovery must be verified before a carrier REGISTER uses this backend.
/// </summary>
public sealed class WindowsHostImsSecurityBackend : IHostImsSecurityBackend, IDisposable
{
    private readonly IHostImsWfpNativeApi _native;
    private readonly bool _allowNonWindowsForTests;
    private readonly object _pendingSync = new();
    private readonly List<nint> _pendingCleanup = [];

    public WindowsHostImsSecurityBackend() : this(new HostImsWfpNativeApi(), false) { }

    internal WindowsHostImsSecurityBackend(IHostImsWfpNativeApi native,
        bool allowNonWindowsForTests = false)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _allowNonWindowsForTests = allowNonWindowsForTests;
    }

    public Task<IHostImsSecurityReservation> ReserveAsync(
        IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex,
        int localClientPort, int localServerPort, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(pcscfAddress);
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !_allowNonWindowsForTests)
            throw new PlatformNotSupportedException("Windows WFP is required for Host IMS IPsec.");
        if (localAddress.AddressFamily != AddressFamily.InterNetworkV6 ||
            pcscfAddress.AddressFamily != AddressFamily.InterNetworkV6 ||
            localAddress.IsIPv4MappedToIPv6 || pcscfAddress.IsIPv4MappedToIPv6 ||
            localAddress.Equals(pcscfAddress))
            throw new ArgumentException("Host IMS WFP requires distinct IPv6 bearer and P-CSCF addresses.");
        if (interfaceIndex <= 0 ||
            !SecurityAgreementBuilder.IsValidProtectedPort(localClientPort) ||
            !SecurityAgreementBuilder.IsValidProtectedPort(localServerPort) ||
            localClientPort == localServerPort)
            throw new ArgumentException("Host IMS WFP requires a verified interface and two distinct protected ports.");
        RetryPendingCleanup();

        nint handle = 0;
        uint clientSpi = 0, serverSpi = 0;
        uint status;
        try
        {
            status = _native.Reserve(localAddress.GetAddressBytes(), pcscfAddress.GetAddressBytes(),
                checked((uint)interfaceIndex), checked((ushort)localClientPort),
                checked((ushort)localServerPort), ref handle, out clientSpi, out serverSpi);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new IpsecException("The experimental VoWinHostImsWfp native DLL is unavailable or incompatible.", inner: ex);
        }
        if (status != 0 || handle == 0 || clientSpi < 256 || serverSpi < 256 || clientSpi == serverSpi)
        {
            var failure = status == 0 ? 13u : status; // ERROR_INVALID_DATA.
            var cleanup = handle == 0 ? 0 : _native.Release(ref handle);
            if (cleanup != 0 || handle != 0)
            {
                if (handle != 0) TrackPendingCleanup(handle);
                throw new IpsecException("Host IMS WFP reservation failed and native cleanup is incomplete.",
                    unchecked((int)(cleanup != 0 ? cleanup : failure)));
            }
            throw new IpsecException("Host IMS WFP could not reserve two inbound SPIs.", unchecked((int)failure));
        }
        IHostImsSecurityReservation reservation = new Reservation(this, _native, handle,
            localAddress, pcscfAddress,
            new SecurityProposal("hmac-sha-1-96", "aes-cbc", clientSpi, serverSpi,
                localClientPort, localServerPort));
        return Task.FromResult(reservation);
    }

    public async Task<IHostImsSecurityReservation> ReserveOwnedAsync(
        IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex, ulong? interfaceLuid,
        int localClientPort, int localServerPort, CancellationToken ct = default)
    {
        if (interfaceLuid is null)
            return await ReserveAsync(localAddress, pcscfAddress, interfaceIndex,
                localClientPort, localServerPort, ct).ConfigureAwait(false);
        var endpoint = new HostImsEndpointCandidate(5, localAddress, pcscfAddress);
        void VerifyOwner()
        {
            if (!HostImsRouteVerifier.Check([endpoint], interfaceIndex, interfaceLuid)[0].IsVerified)
                throw new InvalidOperationException("The Windows WFP route does not belong to the IMS bearer owner.");
        }
        VerifyOwner();
        var reservation = await ReserveAsync(localAddress, pcscfAddress, interfaceIndex,
            localClientPort, localServerPort, ct).ConfigureAwait(false);
        try { VerifyOwner(); return reservation; }
        catch { reservation.Dispose(); throw; }
    }

    private void TrackPendingCleanup(nint handle)
    {
        lock (_pendingSync) _pendingCleanup.Add(handle);
    }

    /// <summary>Retries native cleanup that failed during reservation or session disposal.</summary>
    public void RetryPendingCleanup()
    {
        lock (_pendingSync)
        {
            uint firstFailure = 0;
            for (var i = _pendingCleanup.Count - 1; i >= 0; --i)
            {
                var handle = _pendingCleanup[i];
                var status = _native.Release(ref handle);
                if (status == 0 && handle == 0)
                    _pendingCleanup.RemoveAt(i);
                else
                {
                    _pendingCleanup[i] = handle;
                    if (firstFailure == 0) firstFailure = status == 0 ? 1u : status;
                }
            }
            if (firstFailure != 0)
                throw new IpsecException("Windows WFP still has pending Host IMS SA/filter cleanup.",
                    unchecked((int)firstFailure));
        }
    }

    public void Dispose() => RetryPendingCleanup();

    internal int PendingCleanupCount
    {
        get { lock (_pendingSync) return _pendingCleanup.Count; }
    }

    private sealed class Reservation : IHostImsSecurityReservation
    {
        private readonly WindowsHostImsSecurityBackend _owner;
        private readonly object _sync = new();
        private readonly IHostImsWfpNativeApi _native;
        private readonly IPAddress _localAddress;
        private readonly IPAddress _pcscfAddress;
        private nint _handle;
        private bool _activated;

        internal Reservation(WindowsHostImsSecurityBackend owner,
            IHostImsWfpNativeApi native, nint handle,
            IPAddress localAddress, IPAddress pcscfAddress, SecurityProposal proposal)
        {
            _owner = owner;
            _native = native;
            _handle = handle;
            _localAddress = localAddress;
            _pcscfAddress = pcscfAddress;
            Proposal = proposal;
        }

        public SecurityProposal Proposal { get; }

        public void Activate(SecurityAgreement agreement, byte[] ck, byte[] ik)
        {
            ArgumentNullException.ThrowIfNull(agreement);
            ArgumentNullException.ThrowIfNull(ck);
            ArgumentNullException.ThrowIfNull(ik);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_handle == 0, this);
                if (_activated)
                    throw new InvalidOperationException("Host IMS WFP rekey requires a new SA reservation.");
                if (agreement.Selected.SpiClient != Proposal.SpiClient ||
                    agreement.Selected.SpiServer != Proposal.SpiServer ||
                    agreement.Selected.PortClient != Proposal.PortClient ||
                    agreement.Selected.PortServer != Proposal.PortServer)
                    throw new InvalidOperationException("The IMS Security-Server selection does not match the reserved UE SPI/ports.");

                using var plan = HostImsSaPlan.Create(_localAddress, _pcscfAddress,
                    agreement, ck, ik);
                var inbound = plan.ClientPair.Inbound;
                var authKey = inbound.AuthKey;
                var cipherKey = inbound.CipherKey;
                var status = _native.Activate(ref _handle,
                    agreement.PcscfClientSpi, agreement.PcscfServerSpi,
                    (uint)inbound.Auth, (uint)inbound.Cipher,
                    authKey, checked((uint)authKey.Length),
                    cipherKey.Length == 0 ? null : cipherKey,
                    checked((uint)cipherKey.Length));
                if (status != 0)
                    throw new IpsecException("Windows WFP failed to activate both IMS ESP SA pairs.",
                        unchecked((int)status));
                if (_handle == 0)
                    throw new IpsecException("Windows WFP reported activation but released the reservation.");
                _activated = true;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_handle == 0) return;
                var status = _native.Release(ref _handle);
                if (status != 0 || _handle != 0)
                {
                    if (_handle != 0)
                    {
                        _owner.TrackPendingCleanup(_handle);
                        _handle = 0;
                    }
                    throw new IpsecException("Windows WFP could not remove every Host IMS SA/filter.",
                        unchecked((int)status));
                }
            }
        }
    }
}

internal interface IHostImsWfpNativeApi
{
    uint Reserve(byte[] local, byte[] pcscf, uint interfaceIndex,
        ushort clientPort, ushort serverPort, ref nint handle,
        out uint clientSpi, out uint serverSpi);
    uint Activate(ref nint handle, uint pcscfClientSpi, uint pcscfServerSpi,
        uint authAlgorithm, uint cipherAlgorithm, byte[] authKey, uint authKeyLength,
        byte[]? cipherKey, uint cipherKeyLength);
    uint Release(ref nint handle);
}

internal sealed class HostImsWfpNativeApi : IHostImsWfpNativeApi
{
    private const string Library = "VoWinHostImsWfp";

    [DllImport(Library, EntryPoint = "VoWinImsReserveV6", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint ReserveNative(byte[] local, byte[] pcscf,
        uint interfaceIndex, ushort clientPort, ushort serverPort,
        ref nint handle, out uint clientSpi, out uint serverSpi);

    [DllImport(Library, EntryPoint = "VoWinImsActivateV6", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint ActivateNative(ref nint handle,
        uint pcscfClientSpi, uint pcscfServerSpi, uint authAlgorithm,
        uint cipherAlgorithm, byte[] authKey, uint authKeyLength,
        byte[]? cipherKey, uint cipherKeyLength);

    [DllImport(Library, EntryPoint = "VoWinImsRelease", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint ReleaseNative(ref nint handle);

    public uint Reserve(byte[] local, byte[] pcscf, uint interfaceIndex,
        ushort clientPort, ushort serverPort, ref nint handle,
        out uint clientSpi, out uint serverSpi) =>
        ReserveNative(local, pcscf, interfaceIndex, clientPort, serverPort,
            ref handle, out clientSpi, out serverSpi);

    public uint Activate(ref nint handle, uint pcscfClientSpi, uint pcscfServerSpi,
        uint authAlgorithm, uint cipherAlgorithm, byte[] authKey, uint authKeyLength,
        byte[]? cipherKey, uint cipherKeyLength) =>
        ActivateNative(ref handle, pcscfClientSpi, pcscfServerSpi, authAlgorithm,
            cipherAlgorithm, authKey, authKeyLength, cipherKey, cipherKeyLength);

    public uint Release(ref nint handle) => ReleaseNative(ref handle);
}
