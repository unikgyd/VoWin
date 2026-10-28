using System.Net;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

/// <summary>Pure ownership proofs: never enumerates or changes a real modem, route, or driver.</summary>
public sealed class HostImsBearerIsolationTests
{
    private static readonly IPAddress Local = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress Pcscf = IPAddress.Parse("2001:db8::20");
    private static readonly HostImsEndpointCandidate Endpoint = new(5, Local, Pcscf);
    private static readonly HostImsRouteAdapter[] Adapters =
        [new("SIM A", 72, [Local]), new("SIM B", 73, [Local])];

    private static HostImsRouteObservation Route(int index, IPAddress local, IPAddress remote) =>
        new(true, index, local, 0, checked((ulong)index * 10));

    private static HostImsRouteCheck Verified(int index, ulong? luid = null) =>
        new(Endpoint, true, "fake IMS bearer", index, "Verified") { InterfaceLuid = luid };

    private static HostImsProbeResult Probe(int? index = 73, ulong? luid = 730) =>
        new(HostImsReadiness.HostRoutable, false, null,
            [new HostImsPdnContext(5, "IPV6", "ims", true, [Local], [], [], [Pcscf], ["fake IMS"])
                { HostOwnedAddresses = [Local] }], "fake")
        {
            SimInserted = true, OwnedInterfaceIndex = index, OwnedInterfaceLuid = luid,
            RouteChecks = [Verified(72, 720)]
        };

    [Fact]
    public void DuplicateSourceWithoutBearerIdentityIsAmbiguousNotFirstAdapterWins()
    {
        var calls = 0;
        var result = HostImsRouteVerifier.CheckWithRoutes(Endpoint, Adapters, (index, source, target) =>
        {
            calls++;
            return Route(index, source, target);
        });
        Assert.False(result.IsVerified);
        Assert.Contains("multiple", result.Summary);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ExplicitOwnerSelectsSecondAdapterEvenWhenAddressesAndPcscfMatch()
    {
        var lookups = new List<int>();
        var result = HostImsRouteVerifier.CheckWithRoutes(Endpoint, Adapters, (index, source, target) =>
        {
            lookups.Add(index);
            return Route(index, source, target);
        }, expectedInterfaceIndex: 73, expectedInterfaceLuid: 730);
        Assert.True(result.IsVerified);
        Assert.Equal(73, result.InterfaceIndex);
        Assert.Equal(730ul, result.InterfaceLuid);
        Assert.Equal([73], lookups);
    }

    [Theory]
    [InlineData(72, 730, "2001:db8::10")]
    [InlineData(73, 720, "2001:db8::10")]
    [InlineData(73, 730, "2001:db8::11")]
    public void ForcedRouteMustMatchInterfaceNativeIdentityAndSource(int actualIndex, ulong actualLuid, string source)
    {
        var result = HostImsRouteVerifier.CheckWithRoutes(Endpoint, Adapters,
            (_, _, _) => new(true, actualIndex, IPAddress.Parse(source), 0, actualLuid), 73, 730);
        Assert.False(result.IsVerified);
    }

    [Theory]
    [InlineData(72, 720, "2001:db8::10", false)]
    [InlineData(73, 720, "2001:db8::10", false)]
    [InlineData(73, 730, "2001:db8::11", false)]
    [InlineData(73, 730, "2001:db8::10", true)]
    public void RawInjectionRequiresUnconstrainedBestRouteNotMerelyForcedRoute(
        int globalIndex, ulong globalLuid, string globalSource, bool expected)
    {
        var lookups = new List<int>();
        var result = HostImsRouteVerifier.CheckWithRoutes(Endpoint, Adapters, (index, source, target) =>
        {
            lookups.Add(index);
            return index == 0 ? new(true, globalIndex, IPAddress.Parse(globalSource), 0, globalLuid)
                : Route(index, source, target);
        }, 73, 730, requireGlobalBest: true);
        Assert.Equal(expected, result.IsVerified);
        Assert.Equal([73, 0], lookups);
    }

    [Fact]
    public void MissingOwnedAdapterCannotBorrowAnotherAdaptersIdenticalAddress()
    {
        var result = HostImsRouteVerifier.CheckWithRoutes(Endpoint, [Adapters[0]], Route, 73, 730);
        Assert.False(result.IsVerified);
    }

    [Fact]
    public void RouteLookupFailureIsFailClosed()
    {
        var result = HostImsRouteVerifier.CheckWithRoutes(Endpoint, Adapters,
            (_, _, _) => throw new IOException("fake native route error"), 73, 730);
        Assert.False(result.IsVerified);
    }

    [Fact]
    public void MatchingAddressProofFromDifferentSimCannotAuthorizeRegistration()
    {
        var probe = Probe();
        Assert.Equal([Endpoint], probe.EndpointCandidates);
        Assert.False(probe.IsRouteVerified(Endpoint));
        Assert.False(probe.CanAttemptWindowsIms);
        Assert.False((probe with { RouteChecks = [Verified(73, 720)] }).CanAttemptWindowsIms);
        Assert.True((probe with { RouteChecks = [Verified(73, 730)] }).CanAttemptWindowsIms);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(73, 0ul)]
    [InlineData(null, 730ul)]
    public void InvalidOwnerCannotBecomeVerified(int? index, ulong? luid)
    {
        var probe = Probe(index, luid) with { RouteChecks = [Verified(index ?? 73, luid)] };
        Assert.False(probe.CanAttemptWindowsIms);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticAndManualSelectionRejectDifferentBearerProof(bool manual)
    {
        var bearer = new WindowsHostImsPppBearerInfo(73, Local, [Pcscf]) { InterfaceLuid = 730 };
        var selected = manual ? Endpoint : null;
        Assert.Null(ModemSlot.SelectHostImsEndpoint([Endpoint], selected, bearer, [Verified(72, 720)]));
        Assert.Null(ModemSlot.SelectHostImsEndpoint([Endpoint], selected, bearer, [Verified(73, 720)]));
        Assert.Null(ModemSlot.SelectHostImsEndpoint([Endpoint], selected, bearer));
        Assert.Equal(Endpoint, ModemSlot.SelectHostImsEndpoint([Endpoint], selected, bearer, [Verified(73, 730)]));
        Assert.Null(ModemSlot.SelectHostImsEndpoint([Endpoint], selected,
            bearer with { InterfaceIndex = 0 }, [Verified(0, 730)]));
        Assert.Null(ModemSlot.SelectHostImsEndpoint([Endpoint], selected,
            bearer with { InterfaceLuid = 0 }, [Verified(73, 0)]));
    }

    [Fact]
    public async Task WrongBearerFailsBeforeSocketBackendOrSimAka()
    {
        var backend = new FakeSecurityBackend();
        var akaCalls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] rand, byte[] autn, CancellationToken ct)
        {
            akaCalls++;
            throw new InvalidOperationException("AKA must not be called");
        }
        var profile = new ImsProfile("001010123456789@ims.test", "sip:001010123456789@ims.test",
            "ims.test", "123456789012345", Local.ToString(), 5060);
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsRegistrationClient.RegisterAsync(
            Probe(), Endpoint, profile, Aka, localPort: 0, securityBackend: backend));
        Assert.Equal(0, backend.Reserves);
        Assert.Equal(0, akaCalls);
    }

    [Fact]
    public async Task LegacyBackendMustNotSilentlyDropKnownNativeOwner()
    {
        var backend = new FakeSecurityBackend();
        IHostImsSecurityBackend contract = backend;
        await Assert.ThrowsAsync<NotSupportedException>(() => contract.ReserveOwnedAsync(
            Local, Pcscf, 73, 730, 40666, 40667));
        Assert.Equal(0, backend.Reserves);
    }

    [Fact]
    public void RawRouteVerifierRemembersFirstLuidAndRejectsReusedInterfaceIndex()
    {
        var currentLuid = 730ul;
        var seenOwners = new List<ulong?>();
        var verify = WindowsHostImsWinDivertIo.CreateRouteVerifier(Local, Pcscf, 73, null,
            (_, _, expectedLuid) =>
            {
                seenOwners.Add(expectedLuid);
                return Verified(73, currentLuid);
            });
        Assert.True(verify());
        currentLuid = 731;
        Assert.False(verify());
        currentLuid = 730;
        Assert.True(verify());
        Assert.Equal(new ulong?[] { null, 730, 730 }, seenOwners);
    }

    [Fact]
    public void RawRouteVerifierDoesNotReplaceExplicitOwnerWithFirstObservedInterface()
    {
        var verify = WindowsHostImsWinDivertIo.CreateRouteVerifier(Local, Pcscf, 73, 730,
            (_, _, _) => Verified(73, 731));
        Assert.False(verify());
    }

    [Fact]
    public void ReadyFrameRetainsNativeIdentityAndOldOwnerCompatibility()
    {
        const string old = "VOWIN_IMS_BEARER_READY {\"InterfaceIndex\":73,\"LocalAddress\":\"2001:db8::10\",\"PcscfAddresses\":[\"2001:db8::20\"]}";
        Assert.Null(WindowsHostImsPppBearerLease.ParseReady(old).InterfaceLuid);
        var current = old.Replace("\"InterfaceIndex\":73", "\"InterfaceIndex\":73,\"InterfaceLuid\":730");
        Assert.Equal(730ul, WindowsHostImsPppBearerLease.ParseReady(current).InterfaceLuid);
        Assert.Throws<FormatException>(() => WindowsHostImsPppBearerLease.ParseReady(
            current.Replace("\"InterfaceLuid\":730", "\"InterfaceLuid\":0")));
    }

    private sealed class FakeSecurityBackend : IHostImsSecurityBackend
    {
        internal int Reserves;
        public Task<IHostImsSecurityReservation> ReserveAsync(IPAddress localAddress,
            IPAddress pcscfAddress, int interfaceIndex, int localClientPort,
            int localServerPort, CancellationToken ct = default)
        {
            Reserves++;
            throw new InvalidOperationException("The fake backend must not be reached");
        }
    }
}
