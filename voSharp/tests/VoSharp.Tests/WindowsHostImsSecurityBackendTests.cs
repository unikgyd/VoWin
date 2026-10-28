using System.Net;
using System.Runtime.InteropServices;
using VoSharp.Ipsec;
using VoSharp.Sip;
using VoSharp.Telephony;

namespace VoSharp.Tests;

public sealed class WindowsHostImsSecurityBackendTests
{
    private static readonly IPAddress Local = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress Pcscf = IPAddress.Parse("2001:db8::20");
    private static readonly object NativeResolverSync = new();
    private static bool _nativeResolverInstalled;

    [Fact]
    public void OptionalNativeAbiSmokeRejectsInvalidInputWithoutOpeningWfp()
    {
        var nativeDll = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_NATIVE_SMOKE_DLL");
        if (string.IsNullOrWhiteSpace(nativeDll))
            return;
        InstallNativeResolver(nativeDll);
        var native = new HostImsWfpNativeApi();
        nint handle = 0;
        var status = native.Reserve(Local.GetAddressBytes(), Pcscf.GetAddressBytes(),
            0, 40666, 40667, ref handle, out var clientSpi, out var serverSpi);
        Assert.Equal(87u, status);
        Assert.Equal(nint.Zero, handle);
        Assert.Equal(0u, clientSpi);
        Assert.Equal(0u, serverSpi);
    }

    [Fact]
    public async Task OptionalNativeLoopbackReservationAndActivationRoundTrip()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_NATIVE_LOOPBACK") != "1")
            return;
        var nativeDll = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_NATIVE_SMOKE_DLL");
        Assert.False(string.IsNullOrWhiteSpace(nativeDll));
        InstallNativeResolver(nativeDll!);

        using var backend = new WindowsHostImsSecurityBackend();
        using var reservation = await backend.ReserveAsync(
            IPAddress.IPv6Loopback, IPAddress.Parse("::2"), 1, 60000, 60001);
        reservation.Activate(new SecurityAgreement(reservation.Proposal,
            "ipsec-3gpp", 0xABCDEF01u, 0xABCDEF02u, 61000, 61001),
            new byte[16], new byte[16]);
    }

    private static void InstallNativeResolver(string nativeDll)
    {
        lock (NativeResolverSync)
        {
            if (_nativeResolverInstalled) return;
            var loaded = NativeLibrary.Load(nativeDll);
            NativeLibrary.SetDllImportResolver(typeof(WindowsHostImsSecurityBackend).Assembly,
                (name, _, _) => name == "VoWinHostImsWfp" ? loaded : nint.Zero);
            _nativeResolverInstalled = true;
        }
    }

    [Fact]
    public async Task InstallsReceiverOwnedSpisAndErasesTemporaryKeys()
    {
        var native = new FakeNative();
        var backend = new WindowsHostImsSecurityBackend(native, allowNonWindowsForTests: true);
        var reservation = await backend.ReserveAsync(Local, Pcscf, 72, 40666, 40667);
        Assert.Equal(1001u, reservation.Proposal.SpiClient);
        Assert.Equal(1002u, reservation.Proposal.SpiServer);
        Assert.Equal((uint)72, native.InterfaceIndex);
        Assert.Equal((ushort)40666, native.ClientPort);
        Assert.Equal((ushort)40667, native.ServerPort);

        var ck = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var ik = Enumerable.Range(17, 16).Select(i => (byte)i).ToArray();
        var agreement = Agreement(reservation.Proposal);
        reservation.Activate(agreement, ck, ik);
        Assert.Equal(2001u, native.PcscfClientSpi);
        Assert.Equal(2002u, native.PcscfServerSpi);
        Assert.Equal((uint)IpsecAuthAlgorithm.HmacSha1_96, native.AuthAlgorithm);
        Assert.Equal((uint)IpsecCipherAlgorithm.AesCbc128, native.CipherAlgorithm);
        Assert.Equal(new byte[20], native.BorrowedAuthKey);
        Assert.Equal(new byte[16], native.BorrowedCipherKey);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (byte)i), ck);
        Assert.Equal(Enumerable.Range(17, 16).Select(i => (byte)i), ik);
        Assert.Throws<InvalidOperationException>(() => reservation.Activate(agreement, ck, ik));
        reservation.Dispose();
        reservation.Dispose();
        Assert.Equal(1, native.ReleaseCount);
    }

    [Fact]
    public async Task FailedReservationReleasesPartialNativeHandle()
    {
        var native = new FakeNative { ReserveStatus = 87 };
        var backend = new WindowsHostImsSecurityBackend(native, allowNonWindowsForTests: true);
        var error = await Assert.ThrowsAsync<IpsecException>(() =>
            backend.ReserveAsync(Local, Pcscf, 72, 40666, 40667));
        Assert.Equal(87, error.Win32Error);
        Assert.Equal(1, native.ReleaseCount);
    }

    [Fact]
    public async Task RejectsChangedSecuritySelectionBeforeInstallingSa()
    {
        var native = new FakeNative();
        var backend = new WindowsHostImsSecurityBackend(native, allowNonWindowsForTests: true);
        var reservation = await backend.ReserveAsync(Local, Pcscf, 72, 40666, 40667);
        var wrong = Agreement(reservation.Proposal with { PortClient = 40668 });
        Assert.Throws<InvalidOperationException>(() =>
            reservation.Activate(wrong, new byte[16], new byte[16]));
        Assert.Equal(0, native.ActivateCount);
        reservation.Dispose();
    }

    [Fact]
    public async Task NativeActivationFailureCanStillBeDisposed()
    {
        var native = new FakeNative { ActivateStatus = 5 };
        var backend = new WindowsHostImsSecurityBackend(native, allowNonWindowsForTests: true);
        var reservation = await backend.ReserveAsync(Local, Pcscf, 72, 40666, 40667);
        var error = Assert.Throws<IpsecException>(() =>
            reservation.Activate(Agreement(reservation.Proposal), new byte[16], new byte[16]));
        Assert.Equal(5, error.Win32Error);
        reservation.Dispose();
        Assert.Equal(1, native.ReleaseCount);
    }

    [Fact]
    public async Task FailedReleaseKeepsHandleForRetry()
    {
        var native = new FakeNative { FailFirstRelease = true };
        var backend = new WindowsHostImsSecurityBackend(native, allowNonWindowsForTests: true);
        var reservation = await backend.ReserveAsync(Local, Pcscf, 72, 40666, 40667);
        Assert.Throws<IpsecException>(() => reservation.Dispose());
        Assert.Equal(1, backend.PendingCleanupCount);
        backend.RetryPendingCleanup();
        Assert.Equal(0, backend.PendingCleanupCount);
        Assert.Equal(2, native.ReleaseCount);
    }

    [Fact]
    public async Task PartialReservationCleanupFailureIsRetainedForRetry()
    {
        var native = new FakeNative { ReserveStatus = 87, FailFirstRelease = true };
        var backend = new WindowsHostImsSecurityBackend(native, allowNonWindowsForTests: true);
        await Assert.ThrowsAsync<IpsecException>(() =>
            backend.ReserveAsync(Local, Pcscf, 72, 40666, 40667));
        Assert.Equal(1, backend.PendingCleanupCount);
        backend.RetryPendingCleanup();
        Assert.Equal(0, backend.PendingCleanupCount);
        Assert.Equal(2, native.ReleaseCount);
    }

    private static SecurityAgreement Agreement(SecurityProposal proposal) =>
        new(proposal, "ipsec-3gpp", 2001, 2002, 50601, 50602);

    private sealed class FakeNative : IHostImsWfpNativeApi
    {
        public uint ReserveStatus { get; init; }
        public uint ActivateStatus { get; init; }
        public bool FailFirstRelease { get; init; }
        public int ActivateCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public uint InterfaceIndex { get; private set; }
        public ushort ClientPort { get; private set; }
        public ushort ServerPort { get; private set; }
        public uint PcscfClientSpi { get; private set; }
        public uint PcscfServerSpi { get; private set; }
        public uint AuthAlgorithm { get; private set; }
        public uint CipherAlgorithm { get; private set; }
        public byte[]? BorrowedAuthKey { get; private set; }
        public byte[]? BorrowedCipherKey { get; private set; }

        public uint Reserve(byte[] local, byte[] pcscf, uint interfaceIndex,
            ushort clientPort, ushort serverPort, ref nint handle,
            out uint clientSpi, out uint serverSpi)
        {
            Assert.Equal(Local.GetAddressBytes(), local);
            Assert.Equal(Pcscf.GetAddressBytes(), pcscf);
            InterfaceIndex = interfaceIndex;
            ClientPort = clientPort;
            ServerPort = serverPort;
            handle = 42;
            clientSpi = 1001;
            serverSpi = 1002;
            return ReserveStatus;
        }

        public uint Activate(ref nint handle, uint pcscfClientSpi, uint pcscfServerSpi,
            uint authAlgorithm, uint cipherAlgorithm, byte[] authKey,
            uint authKeyLength, byte[]? cipherKey, uint cipherKeyLength)
        {
            ActivateCount++;
            PcscfClientSpi = pcscfClientSpi;
            PcscfServerSpi = pcscfServerSpi;
            AuthAlgorithm = authAlgorithm;
            CipherAlgorithm = cipherAlgorithm;
            BorrowedAuthKey = authKey;
            BorrowedCipherKey = cipherKey;
            Assert.Equal((uint)authKey.Length, authKeyLength);
            Assert.Equal((uint)(cipherKey?.Length ?? 0), cipherKeyLength);
            return ActivateStatus;
        }

        public uint Release(ref nint handle)
        {
            ReleaseCount++;
            if (FailFirstRelease && ReleaseCount == 1) return 5;
            handle = 0;
            return 0;
        }
    }
}
