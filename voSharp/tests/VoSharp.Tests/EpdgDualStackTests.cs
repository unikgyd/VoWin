using System.Net;
using System.Net.Sockets;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class EpdgDualStackTests
{
    private static EpdgResolvedEndpoint Endpoint(string address, string host = "epdg.example.test") =>
        new(IPAddress.Parse(address), new(host, "PLMN", "99912"), "999", "12");

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    [InlineData(false, false, 0)]
    public void DirectAutomaticSelectionKeepsOnlyFamiliesWithRoutes(bool ipv4, bool ipv6, int expected)
    {
        var paths = EpdgNetworkPathSelector.Select([Endpoint("2001:db8::20"), Endpoint("192.0.2.20")], null,
            probe: (remote, _) => new(remote.AddressFamily == AddressFamily.InterNetwork ? ipv4 : ipv6));
        Assert.Equal(expected, paths.Endpoints.Length);
        Assert.All(paths.Endpoints, endpoint => Assert.True(endpoint.Address.AddressFamily == AddressFamily.InterNetwork ? ipv4 : ipv6));
    }

    [Fact]
    public void UnknownRoutesRetainBothFamiliesAndDuplicateRealmsOnlyQueryAnAddressOnce()
    {
        var queries = 0;
        var input = new[] { Endpoint("192.0.2.20"), Endpoint("2001:db8::20"), Endpoint("192.0.2.20") with { HomeMnc = "123" } };
        var paths = EpdgNetworkPathSelector.Select(input, null, probe: (_, _) => { queries++; return new(null); });
        Assert.Equal(2, queries); Assert.Equal(input, paths.Endpoints);
        Assert.Contains("待 IKE 验证", paths.Summary);
    }

    [Fact]
    public void ExplicitDirectBindRestrictsTheOuterFamilyWithoutChangingInnerFamilyOrIdentity()
    {
        var local = IPAddress.Parse("192.0.2.100");
        var probes = 0;
        var paths = EpdgNetworkPathSelector.Select([Endpoint("2001:db8::20"), Endpoint("192.0.2.20")], "direct", local,
            (remote, bound) => { Assert.Equal(local, bound); Assert.Equal(AddressFamily.InterNetwork, remote.AddressFamily); probes++; return new(true); });
        Assert.Equal(1, probes); Assert.Equal("12", Assert.Single(paths.Endpoints).HomeMnc);
    }

    [Theory]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("socks5h://proxy.example.test:1080")]
    public void ProxyRemoteFamiliesAreIndependentOfLocalBindAndDirectRoute(string proxy)
    {
        var local = IPAddress.Parse("192.0.2.100");
        var paths = EpdgNetworkPathSelector.Select([Endpoint("2001:db8::20"), Endpoint("192.0.2.20")], proxy, local,
            (_, _) => throw new InvalidOperationException("Never probe direct ePDG routes when a proxy is configured."));
        Assert.Equal(2, paths.Endpoints.Length);
        using var manager = new VoWifiManager();
        var discovery = manager.BuildEpdgDiscoveryOptions(null, proxy, local);
        Assert.Null(discovery.RequiredAddressFamily);
        Assert.Equal(EpdgAddressPreference.System, discovery.AddressPreference);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void WindowsRouteProbeUsesTheNativeBestRouteWithoutSendingPackets(string ip)
    {
        if (!OperatingSystem.IsWindows() || ip == "::1" && !Socket.OSSupportsIPv6) return;
        var destination = IPAddress.Parse(ip);
        var selected = EpdgNetworkPathSelector.QueryWindowsRoute(destination, null);
        Assert.True(selected.Available, selected.Detail);
        Assert.Equal(destination, selected.Source);
        Assert.True(selected.InterfaceIndex > 0);
        var bound = EpdgNetworkPathSelector.QueryWindowsRoute(destination, destination);
        Assert.True(bound.Available, bound.Detail);
        Assert.Equal(destination, bound.Source);
    }

    private sealed class Dns : IEpdgDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(
            host.Contains("mcc204", StringComparison.Ordinal) ? new[] { IPAddress.Parse("192.0.2.40"), IPAddress.Parse("2001:db8::40") } : []);
    }

    [Fact]
    public async Task FirstPlmnWithNoDnsDoesNotPreventSecondPlmnFromProvidingBothFamilies()
    {
        var sim = SimIdentity.FromImsiAndIccid("515661234567890", Guid.NewGuid().ToString(), mncLength: 2, homePlmns: ["51566", "20404"]);
        var result = await EpdgResolver.ResolveAsync(sim, new EpdgDiscoveryOptions(DnsResolver: new Dns()));
        Assert.Contains("mnc004.mcc204", result.Fqdn);
        Assert.Equal(2, result.Endpoints!.Count);
        Assert.Contains(result.IpAddresses, ip => ip.AddressFamily == AddressFamily.InterNetwork);
        Assert.Contains(result.IpAddresses, ip => ip.AddressFamily == AddressFamily.InterNetworkV6);
        Assert.Equal("515", result.HomeMcc); Assert.Equal("66", result.HomeMnc); // Discovery is not authentication identity.
    }

    [Fact]
    public void EapSuccessWithBadResponderAuthenticationIsNotAnAddressFamilyRetry()
    {
        var failed = new VoSharp.Ike.IkeSessionResult(false, null, null, [], 0, 0,
            null, null, null, null, null, null, null, false, "responder AUTH invalid", EapSucceeded: true);
        Assert.False(CSharpIkeBackend.CanRetryInnerFamily(failed));
        Assert.False(CSharpIkeBackend.CanRetryInnerFamily(failed with { RetryableTransportFailure = true }));
        Assert.True(CSharpIkeBackend.CanRetryInnerFamily(failed with { RetryableAddressFamilyFailure = true }));
        Assert.True(CSharpIkeBackend.CanRetryInnerFamily(failed with { Success = true, AssignedIp = "2001:db8::2", PcscfIp = "10.0.0.3" }));
        Assert.False(CSharpIkeBackend.CanRetryInnerFamily(failed with { Success = true, AssignedIp = "2001:db8::2", PcscfIp = "2001:db8::3" }));
    }
}
