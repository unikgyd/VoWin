using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class HostImsPdnTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:192.0.2.1")]
    public void InvalidEndpointCannotPassProductionRouteValidation(string value)
    {
        var invalid = IPAddress.Parse(value);
        var local = IPAddress.Parse(invalid.AddressFamily == AddressFamily.InterNetwork ? "192.0.2.1" : "2001:db8::1");
        Assert.False(HostImsPdnParser.IsUsableEndpointAddress(invalid));
        Assert.False(Assert.Single(HostImsRouteVerifier.Check([new(5, local, invalid)])).IsVerified);
        Assert.False(Assert.Single(HostImsRouteVerifier.Check([new(5, invalid, local)])).IsVerified);
    }

    [Fact]
    public void DuplicateDiagnosticEndpointCandidatesAreCollapsed()
    {
        var local = IPAddress.Parse("192.0.2.1"); var remote = IPAddress.Parse("198.51.100.1");
        var context = new HostImsPdnContext(5, "IP", "ims", true, [local], [], [], [remote, remote], ["owned"])
            { HostOwnedAddresses = [local, local] };
        Assert.Equal(remote, Assert.Single(HostImsPdnParser.GetEndpointCandidates([context])).PcscfAddress);
    }
    [Fact]
    public void ParsesImsProfileActivationAndPcscfFromCgcontrdp()
    {
        var profiles = HostImsPdnParser.ParseConfigured([
            "+CGDCONT: 1,\"IPV4V6\",\"internet\",\"0.0.0.0\",0,0,0,0",
            "+CGDCONT: 2,\"IPV4V6\",\"ims\",\"0.0.0.0\",0,0,0,0"
        ]);
        var active = HostImsPdnParser.ParseActivation(["+CGACT: 1,1", "+CGACT: 2,1"]);
        var runtime = HostImsPdnParser.ParseRuntime([
            "+CGCONTRDP: 2,5,\"ims\",\"10.20.30.40.255.255.255.248\",\"10.20.30.41\",\"10.0.0.1\",\"10.0.0.2\",\"10.99.0.10\",\"10.99.0.11\",0"
        ]);

        var ims = Assert.Single(profiles, profile => HostImsPdnParser.IsImsApn(profile.Apn));
        Assert.Equal(2, ims.ContextId);
        Assert.True(active[2]);
        Assert.False(HostImsPdnParser.IsContextActive(2,
            new Dictionary<int, bool> { [2] = false }, hasRuntime: true));
        Assert.True(HostImsPdnParser.IsContextActive(2,
            new Dictionary<int, bool>(), hasRuntime: true));
        var live = Assert.Single(runtime);
        Assert.Equal(IPAddress.Parse("10.20.30.40"), Assert.Single(live.LocalAddresses));
        Assert.Equal([IPAddress.Parse("10.99.0.10"), IPAddress.Parse("10.99.0.11")], live.PcscfServers);
    }

    [Fact]
    public void CgcontrdpWithEmptyDnsStillParsesIpv6PcscfFields()
    {
        var runtime = HostImsPdnParser.ParseRuntime([
            "+CGCONTRDP: 5,6,ims,32.1.13.184.0.1.0.2.0.0.0.0.0.0.0.1," +
            "254.128.0.0.0.0.0.0.0.0.0.0.0.0.0.2,,," +
            "32.1.13.184.0.0.0.0.0.0.0.0.0.0.0.10," +
            "32.1.13.184.0.0.0.0.0.0.0.0.0.0.0.11"
        ]);
        var ims = Assert.Single(runtime);
        Assert.Empty(ims.DnsServers);
        Assert.Equal(IPAddress.Parse("2001:db8:1:2::1"), Assert.Single(ims.LocalAddresses));
        Assert.Equal([IPAddress.Parse("2001:db8::a"), IPAddress.Parse("2001:db8::b")],
            ims.PcscfServers);
    }

    [Fact]
    public void ControlledPppInterfaceMayOwnAnAddressInModemImsPrefix()
    {
        var modem = IPAddress.Parse("2001:db8:1:2::1");
        var ppp = IPAddress.Parse("2001:db8:1:2:1234:5678:9abc:def0");
        var bindings = new Dictionary<IPAddress, string[]> { [ppp] = [HostImsPdnParser.ImsPppEntryName] };
        Assert.True(HostImsPdnParser.LooksLikeCellularAdapter(
            HostImsPdnParser.ImsPppEntryName, "WAN Miniport (IPv6)", NetworkInterfaceType.Ppp));
        Assert.False(HostImsPdnParser.LooksLikeCellularAdapter(
            "Unrelated VPN", "WAN Miniport (IPv6)", NetworkInterfaceType.Ppp));
        Assert.True(HostImsPdnParser.LooksLikeCellularAdapter("VoWin IMS " + new string('a', 16),
            "WAN Miniport (IPv6)", NetworkInterfaceType.Ppp));
        Assert.False(HostImsPdnParser.LooksLikeCellularAdapter("VoWin IMS arbitrary-vpn",
            "WAN Miniport (IPv6)", NetworkInterfaceType.Ppp));
        Assert.Equal([ppp], HostImsPdnParser.ResolveHostOwnedAddresses(
            [modem], bindings, [new HostImsPppAddress(ppp, 64)]));
        Assert.Empty(HostImsPdnParser.ResolveHostOwnedAddresses(
            [IPAddress.Parse("2001:db8:1:3::1")], bindings, [new HostImsPppAddress(ppp, 64)]));
        Assert.Empty(HostImsPdnParser.ResolveHostOwnedAddresses(
            [modem], bindings, [new HostImsPppAddress(ppp, 48)]));
    }

    [Fact]
    public void ParsesQuectelImsAndUsbNetworkModes()
    {
        Assert.True(HostImsPdnParser.ParseQuectelImsEnabled(["+QCFG: \"ims\",1,1"]));
        Assert.False(HostImsPdnParser.ParseQuectelImsEnabled(["+QCFG: \"ims\",2,1"]));
        Assert.Null(HostImsPdnParser.ParseQuectelImsEnabled(["+QCFG: \"ims\",0,1"]));
        Assert.Equal("MBIM", HostImsPdnParser.ParseQuectelUsbNetworkMode(["+QCFG: \"usbnet\",2"]));
        Assert.Equal("RNDIS", HostImsPdnParser.ParseQuectelUsbNetworkMode(["+QCFG: \"usbnet\",3"]));
        Assert.True(HostImsPdnParser.ParseQuectelSimInserted(["+QSIMSTAT: 1,1"]));
        Assert.False(HostImsPdnParser.ParseQuectelSimInserted(["+QSIMSTAT: 0,0"]));
        Assert.Null(HostImsPdnParser.ParseQuectelSimInserted(["ERROR"]));
    }

    [Fact]
    public void InactiveImsContextDoesNotRequireDynamicRuntimeQueryToClassify()
    {
        var configured = HostImsPdnParser.ParseConfigured(["+CGDCONT: 2,\"IPV4V6\",\"ims\""])
            .Where(context => HostImsPdnParser.IsImsApn(context.Apn)).ToArray();
        Assert.False(HostImsPdnParser.IsRuntimeQueryFailureCritical(configured,
            new Dictionary<int, bool> { [2] = false }));
        Assert.True(HostImsPdnParser.IsRuntimeQueryFailureCritical(configured,
            new Dictionary<int, bool> { [2] = true }));
        Assert.True(HostImsPdnParser.IsRuntimeQueryFailureCritical(configured,
            new Dictionary<int, bool>()));
        Assert.True(HostImsPdnParser.IsRuntimeQueryFailureCritical([], new Dictionary<int, bool>()));
    }

    [Fact]
    public void ProbeWithoutSimReportsHardwarePreflightEvenWhenPdnQueriesFail()
    {
        Assert.Equal(HostImsReadiness.SimUnavailable,
            HostImsPdnParser.Classify(simInserted: false, queryFailed: true, contexts: []));
        Assert.Equal(HostImsReadiness.ProbeFailed,
            HostImsPdnParser.Classify(simInserted: null, queryFailed: true, contexts: []));
        Assert.Equal(HostImsReadiness.NotConfigured,
            HostImsPdnParser.Classify(simInserted: true, queryFailed: false, contexts: []));
    }

    [Fact]
    public void HostRoutableRequiresOneActiveContextWithAddressInterfaceAndPcscf()
    {
        var address = IPAddress.Parse("10.20.30.40");
        var pcscf = IPAddress.Parse("10.99.0.10");
        var inactiveMapped = new HostImsPdnContext(2, "IPV4V6", "ims", false,
            [address], [], [], [pcscf], ["Cellular IMS"])
        {
            HostOwnedAddresses = [address]
        };
        var activeUnmapped = new HostImsPdnContext(3, "IPV4V6", "ims", true,
            [IPAddress.Parse("10.20.30.41")], [], [], [pcscf], []);
        Assert.Equal(HostImsReadiness.ModemInternalOnly,
            HostImsPdnParser.Classify(true, false, [inactiveMapped, activeUnmapped]));
        Assert.Equal(HostImsReadiness.HostRoutable,
            HostImsPdnParser.Classify(true, false, [inactiveMapped with { IsActive = true }]));
        var mixedFamily = inactiveMapped with
        {
            IsActive = true,
            PcscfServers = [IPAddress.Parse("2001:db8::1")]
        };
        Assert.Equal(HostImsReadiness.ModemInternalOnly,
            HostImsPdnParser.Classify(true, false, [mixedFamily]));
        var ready = WithVerifiedRoute(new HostImsProbeResult(HostImsReadiness.HostRoutable, true, "MBIM",
            [inactiveMapped with { IsActive = true }], "candidate"));
        Assert.True(ready.CanAttemptWindowsIms);
        Assert.False((ready with { RouteChecks = null }).CanAttemptWindowsIms);
        var candidate = Assert.Single(ready.EndpointCandidates);
        Assert.Equal(2, candidate.ContextId);
        Assert.Equal(address, candidate.LocalAddress);
        Assert.Equal(pcscf, candidate.PcscfAddress);

        var blocked = ready with
        {
            RouteChecks = [new HostImsRouteCheck(candidate, false, "Cellular IMS", 3, "Wrong route")]
        };
        Assert.False(blocked.CanAttemptWindowsIms);
        Assert.False(blocked.IsRouteVerified(candidate));
        Assert.True((blocked with
        {
            RouteChecks = [new HostImsRouteCheck(candidate, true, "Cellular IMS", 3, "Verified")]
        }).CanAttemptWindowsIms);
    }

    [Fact]
    public void WindowsRouteVerifierChecksSourceAndInterfaceWithoutSendingTraffic()
    {
        if (!OperatingSystem.IsWindows()) return;
        var loopback = NetworkInterface.GetAllNetworkInterfaces()
            .First(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Loopback &&
                          nic.GetIPProperties().UnicastAddresses.Any(address =>
                              address.Address.Equals(IPAddress.Loopback)));
        var index = loopback.GetIPProperties().GetIPv4Properties().Index;
        var route = HostImsRouteVerifier.WindowsRoute.TryGetBestRoute(index,
            IPAddress.Loopback, IPAddress.Loopback);

        Assert.True(route.Success, $"GetBestRoute2 failed: {route.Error}");
        Assert.Equal(index, route.InterfaceIndex);
        Assert.Equal(IPAddress.Loopback, route.SourceAddress);

        if (loopback.GetIPProperties().UnicastAddresses.Any(address =>
                address.Address.Equals(IPAddress.IPv6Loopback)))
        {
            var index6 = loopback.GetIPProperties().GetIPv6Properties().Index;
            var route6 = HostImsRouteVerifier.WindowsRoute.TryGetBestRoute(index6,
                IPAddress.IPv6Loopback, IPAddress.IPv6Loopback);
            Assert.True(route6.Success, $"IPv6 GetBestRoute2 failed: {route6.Error}");
            Assert.Equal(index6, route6.InterfaceIndex);
            Assert.Equal(IPAddress.IPv6Loopback, route6.SourceAddress);
        }

        var unowned = Assert.Single(HostImsRouteVerifier.Check([
            new HostImsEndpointCandidate(2, IPAddress.Parse("192.0.2.1"), IPAddress.Parse("198.51.100.2"))
        ]));
        Assert.False(unowned.IsVerified);
    }

    [Theory]
    [InlineData("ims", true)]
    [InlineData("ims.mnc001.mcc001.gprs", true)]
    [InlineData("carrier.ims", true)]
    [InlineData("internet", false)]
    [InlineData("permission", false)]
    public void IdentifiesImsApnWithoutSubstringFalsePositives(string apn, bool expected) =>
        Assert.Equal(expected, HostImsPdnParser.IsImsApn(apn));

    [Fact]
    public void SipTransport_CanBindExplicitHostImsBearerAddress()
    {
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, remotePort: 5060, localPort: 0);
        Assert.Equal(IPAddress.Loopback, transport.LocalEndPoint!.Address);
        Assert.True(transport.LocalEndPoint.Port > 0);
    }

    [Fact]
    public void SipTransport_PinsOnlySelectedWindowsSocketInterface()
    {
        if (!OperatingSystem.IsWindows()) return;
        var index = GetLoopbackIndex(AddressFamily.InterNetwork);
        using var transport = new SipTransport();
        transport.ConnectOnInterface(IPAddress.Loopback, IPAddress.Loopback,
            index, remotePort: 5060, localPort: 0);
        Assert.Equal(index, transport.BoundInterfaceIndex);
        Assert.Equal(IPAddress.Loopback, transport.LocalEndPoint!.Address);

        var index6 = GetLoopbackIndex(AddressFamily.InterNetworkV6);
        using var ipv6 = new SipTransport();
        ipv6.ConnectOnInterface(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback,
            index6, remotePort: 5060, localPort: 0);
        Assert.Equal(index6, ipv6.BoundInterfaceIndex);
        Assert.Equal(IPAddress.IPv6Loopback, ipv6.LocalEndPoint!.Address);
    }

    [Fact]
    public void RtpAndRtcp_RequireExplicitBearerAddressForInterfacePinning()
    {
        Assert.Throws<ArgumentException>(() =>
            new RtpSession(null, outgoingInterfaceIndex: 1));
        Assert.Throws<ArgumentException>(() =>
            new RtpSession(null, localAddress: IPAddress.Any, outgoingInterfaceIndex: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RtpSession(null, localAddress: IPAddress.Loopback, outgoingInterfaceIndex: 0));
    }

    [Fact]
    public void RtpAndRtcp_PinSelectedWindowsInterface()
    {
        if (!OperatingSystem.IsWindows()) return;

        var ipv4Index = GetLoopbackIndex(AddressFamily.InterNetwork);
        using var ipv4 = new RtpSession(null, localAddress: IPAddress.Loopback,
            outgoingInterfaceIndex: ipv4Index);
        Assert.Equal(ipv4Index, ipv4.BoundInterfaceIndex);
        Assert.Equal(IPAddress.Loopback, ipv4.LocalAddress);
        Assert.True(ipv4.LocalPort > 0);
        Assert.True(ipv4.LocalRtcpPort > 0);

        var ipv6Index = GetLoopbackIndex(AddressFamily.InterNetworkV6);
        using var ipv6 = new RtpSession(null, localAddress: IPAddress.IPv6Loopback,
            outgoingInterfaceIndex: ipv6Index);
        Assert.Equal(ipv6Index, ipv6.BoundInterfaceIndex);
        Assert.Equal(IPAddress.IPv6Loopback, ipv6.LocalAddress);
        Assert.True(ipv6.LocalPort > 0);
        Assert.True(ipv6.LocalRtcpPort > 0);
    }

    [Fact]
    public async Task RtcpReport_UsesAdvertisedNonAdjacentPortOnPinnedBearer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var rtpPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rtcpPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var rtpPort = ((IPEndPoint)rtpPeer.Client.LocalEndPoint!).Port;
        var rtcpPort = ((IPEndPoint)rtcpPeer.Client.LocalEndPoint!).Port;
        using var session = new RtpSession(null, localAddress: IPAddress.Loopback,
            outgoingInterfaceIndex: GetLoopbackIndex(AddressFamily.InterNetwork));
        session.SetRemoteEndpoint(IPAddress.Loopback, rtpPort, payloadType: 8,
            remoteRtcpPort: rtcpPort);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, rtcpPort), session.RemoteRtcpEndPoint);
        session.ProcessRtpPacket([0x80, 0x08, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0xd5]);

        var report = await rtcpPeer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(IPAddress.Loopback, report.RemoteEndPoint.Address);
        Assert.Equal(201, report.Buffer[1]);
        Assert.Throws<ArgumentException>(() => session.SetRemoteEndpoint(
            IPAddress.IPv6Loopback, rtpPort, remoteRtcpPort: rtcpPort));
    }

    [Fact]
    public void NoSimHardwarePreflight_RecognizesDownQuectelWindowsAdapterWithoutClaimingImsRoute()
    {
        Assert.True(HostImsPdnParser.LooksLikeCellularAdapter("手机网络",
            "Quectel Wireless Ethernet Adapter", NetworkInterfaceType.Ethernet));
        Assert.False(HostImsPdnParser.LooksLikeCellularAdapter("Wi-Fi",
            "Intel Wireless Adapter", NetworkInterfaceType.Wireless80211));
        Assert.False(HostImsPdnParser.LooksLikeCellularAdapter("VPN",
            "Generic tunnel adapter", NetworkInterfaceType.Tunnel));
        Assert.True(HostImsPdnParser.LooksLikeCellularAdapter("Mobile Broadband",
            "Windows WWAN adapter", NetworkInterfaceType.Wwanpp2));
        Assert.True(HostImsPdnParser.IsHostImsDataInterface("手机网络",
            "Quectel Wireless Ethernet Adapter", NetworkInterfaceType.Ethernet, OperationalStatus.Up));
        Assert.False(HostImsPdnParser.IsHostImsDataInterface("手机网络",
            "Quectel Wireless Ethernet Adapter", NetworkInterfaceType.Ethernet, OperationalStatus.Down));
        Assert.False(HostImsPdnParser.IsHostImsDataInterface("Wi-Fi",
            "Intel Wireless Adapter", NetworkInterfaceType.Wireless80211, OperationalStatus.Up));
        Assert.False(HostImsPdnParser.IsHostImsDataInterface("Cellular VPN",
            "Cellular tunnel adapter", NetworkInterfaceType.Tunnel, OperationalStatus.Up));
        var probe = new HostImsProbeResult(HostImsReadiness.SimUnavailable, null, "RmNet/QMI", [], "No SIM")
        {
            SimInserted = false,
            WindowsCellularAdapters = [new HostImsWindowsAdapter("手机网络", "Quectel Wireless Ethernet Adapter", false)]
        };
        Assert.False(probe.CanAttemptWindowsIms);
        Assert.False(Assert.Single(probe.WindowsCellularAdapters).IsConnected);
    }

    [Fact]
    public async Task HostImsRegistrar_BindsSelectedBearerAddressAndRegistersWithoutExtraIpsec()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var local = IPAddress.Loopback;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [local], [], [], [local], ["test IMS interface"])
        {
            HostOwnedAddresses = [local]
        };
        var probe = new HostImsProbeResult(HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        };
        probe = WithVerifiedRoute(probe);
        var endpoint = Assert.Single(probe.EndpointCandidates);
        var serverTask = Task.Run(async () =>
        {
            var datagram = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var request = SipMessage.Parse(datagram.Buffer);
            var response = request.CreateResponse(200, "OK");
            response.SetHeader("Contact", request.GetHeader("Contact")!);
            await pcscf.SendAsync(response.ToBytes(), datagram.RemoteEndPoint);
            return (request, datagram.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:001010123456789@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");

        var diagnostics = new List<string>();
        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, endpoint, profile, UnexpectedAka, localPort: 0, pcscfPort: pcscfPort,
            diagnosticLog: diagnostics.Add);
        Assert.Contains(diagnostics, message => message.Contains("status=200", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, message =>
            message.Contains(profile.PrivateIdentity, StringComparison.Ordinal) ||
            message.Contains(profile.Imei, StringComparison.Ordinal));
        var (sent, source) = await serverTask;
        Assert.Equal("REGISTER", sent.Method);
        Assert.Equal(local, source.Address);
        Assert.Contains($"{local}:{source.Port}", sent.GetHeader("Via"));
        Assert.Equal(local, client.Transport.LocalEndPoint!.Address);
        Assert.Equal(endpoint, client.Endpoint);
        Assert.True(client.Result.ExpiresSeconds > 0);
        var deregistration = Task.Run(async () =>
        {
            var datagram = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var request = SipMessage.Parse(datagram.Buffer);
            var response = request.CreateResponse(200, "OK");
            await pcscf.SendAsync(response.ToBytes(), datagram.RemoteEndPoint);
            return request;
        });
        Assert.True(await client.DeregisterAsync());
        var removed = await deregistration;
        Assert.Equal("0", removed.GetHeader("Expires"));
        Assert.False(client.IsRegistered);
        client.Invalidate();
        Assert.False(client.IsRegistered);
        Assert.Null(client.CurrentResult);

        var noSim = probe with { Readiness = HostImsReadiness.SimUnavailable, SimInserted = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsRegistrationClient.RegisterAsync(
            noSim, endpoint, profile, UnexpectedAka, localPort: 0, pcscfPort: pcscfPort));
        var inconsistentProbe = probe with { SimInserted = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsRegistrationClient.RegisterAsync(
            inconsistentProbe, endpoint, profile, UnexpectedAka, localPort: 0, pcscfPort: pcscfPort));
        var wrongRoute = probe with
        {
            RouteChecks = [new HostImsRouteCheck(endpoint, false, "test IMS interface", 3, "Route mismatch")]
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsRegistrationClient.RegisterAsync(
            wrongRoute, endpoint, profile, UnexpectedAka, localPort: 0, pcscfPort: pcscfPort));
        var missingIndex = probe with
        {
            RouteChecks = [new HostImsRouteCheck(endpoint, true, "test IMS interface", null, "Verified")]
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsRegistrationClient.RegisterAsync(
            missingIndex, endpoint, profile, UnexpectedAka, localPort: 0, pcscfPort: pcscfPort));
    }

    [Fact]
    public async Task HostImsSms_UsesRegisteredProtectedPathAndDoesNotClaimDelivery()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var local = IPAddress.Loopback;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [local], [], [], [local], ["test IMS interface"])
        {
            HostOwnedAddresses = [local]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        });
        var serverTask = Task.Run(async () =>
        {
            var registration = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var register = SipMessage.Parse(registration.Buffer);
            Assert.DoesNotContain("+g.3gpp.smsip", register.GetHeader("Contact"));
            var registered = register.CreateResponse(200, "OK");
            registered.SetHeader("Contact", register.GetHeader("Contact")!);
            registered.SetHeader("P-Associated-URI", "<tel:+15550001111>, <sip:user@ims.test>");
            registered.SetHeader("Service-Route", "<sip:scscf.ims.test;lr>");
            await pcscf.SendAsync(registered.ToBytes(), registration.RemoteEndPoint);

            var sent = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var message = SipMessage.Parse(sent.Buffer);
            Assert.Equal("MESSAGE", message.Method);
            Assert.Equal("tel:+15551230000", message.RequestUri);
            Assert.Equal("<tel:+15550001111>", message.GetHeader("P-Preferred-Identity"));
            Assert.Equal("<sip:scscf.ims.test;lr>", message.GetHeader("Route"));
            Assert.Equal(ImsSmsHandler.SmsContentType, message.GetHeader("Content-Type"));
            Assert.Equal("binary", message.GetHeader("Content-Transfer-Encoding"));
            var rpdu = ImsSmsHandler.ParseRpdu(message.RawBody!);
            Assert.Equal(0, rpdu.MessageType);
            Assert.NotNull(rpdu.Tpdu);
            Assert.Equal(rpdu.Reference, rpdu.Tpdu![1]);
            await pcscf.SendAsync(message.CreateResponse(202, "Accepted").ToBytes(), sent.RemoteEndPoint);

            var report = new SipMessage
            {
                IsRequest = true, Method = "MESSAGE", RequestUri = message.GetHeader("From")!,
                RawBody = [0x03, rpdu.Reference]
            };
            report.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bKsms-report;rport");
            report.SetHeader("From", "<sip:ipsmgw@ims.test>;tag=gw");
            report.SetHeader("To", message.GetHeader("From")!);
            report.SetHeader("Call-ID", "network-sms-report");
            report.SetHeader("CSeq", "1 MESSAGE");
            report.SetHeader("In-Reply-To", message.GetHeader("Call-ID")!);
            report.SetHeader("Content-Type", ImsSmsHandler.SmsContentType);
            report.SetHeader("Content-Length", "2");
            await pcscf.SendAsync(report.ToBytes(), sent.RemoteEndPoint);
            var acknowledged = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal(200, acknowledged.StatusCode);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:user@ims.test",
            "ims.test", "123456789012345", "127.0.0.1", AdvertiseSmsOverIms: false);
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");
        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, UnexpectedAka,
            localPort: 0, pcscfPort: pcscfPort);
        client.Transport.IncomingRequestReceived += (_, request) =>
            _ = Task.Run(() => client.HandleIncomingSmsReportAsync(request,
                response => client.Transport.SendAsync(response)));
        var result = await client.SendSmsOverImsAsync("+15550002222", "VoWin host SMS test",
            "+15551230000", requestStatusReport: false);
        await serverTask;
        Assert.True(result.AllPartsAccepted);
        Assert.Equal("rp_ack_delivery_unconfirmed", result.SubmissionStatus);
        Assert.Equal(202, Assert.Single(result.PartResults).SipCode);
    }

    [Fact]
    public async Task HostImsSms_OptInReceiverAcknowledgesMtOnceAndReportsRpAck()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var local = IPAddress.Loopback;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [local], [], [], [local], ["test IMS interface"])
        {
            HostOwnedAddresses = [local]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        });
        var sendMt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            var registration = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var register = SipMessage.Parse(registration.Buffer);
            Assert.Contains("+g.3gpp.smsip", register.GetHeader("Contact"));
            var registered = register.CreateResponse(200, "OK");
            registered.SetHeader("Contact", register.GetHeader("Contact")!);
            registered.SetHeader("P-Associated-URI", "<tel:+15550001111>");
            await pcscf.SendAsync(registered.ToBytes(), registration.RemoteEndPoint);
            await sendMt.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var deliver = VoWifiSmsReceptionTests.Deliver;
            var incoming = new SipMessage
            {
                IsRequest = true, Method = "MESSAGE", RequestUri = "sip:ue@ims.test",
                RawBody = [1, 5, 0, 0, (byte)deliver.Length, .. deliver]
            };
            incoming.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{pcscfPort};branch=z9hG4bKmt;rport");
            incoming.SetHeader("From", "<sip:smsc@ims.test>;tag=network");
            incoming.SetHeader("To", "<sip:ue@ims.test>");
            incoming.SetHeader("P-Asserted-Identity", "<sip:smsc@ims.test>");
            incoming.SetHeader("Call-ID", "host-mt@ims.test");
            incoming.SetHeader("CSeq", "1 MESSAGE");
            incoming.SetHeader("Content-Type", ImsSmsHandler.SmsContentType);
            incoming.SetHeader("Content-Length", incoming.RawBody.Length.ToString());
            await pcscf.SendAsync(incoming.ToBytes(), registration.RemoteEndPoint);

            var accepted = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal(200, accepted.StatusCode);
            var report = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal("MESSAGE", report.Method);
            Assert.Equal("sip:smsc@ims.test", report.RequestUri);
            Assert.Equal(incoming.GetHeader("Call-ID"), report.GetHeader("In-Reply-To"));
            Assert.Equal(new byte[] { 2, 5 }, report.RawBody);
            await pcscf.SendAsync(report.CreateResponse(202, "Accepted").ToBytes(),
                registration.RemoteEndPoint);

            await pcscf.SendAsync(incoming.ToBytes(), registration.RemoteEndPoint);
            var duplicate = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal(200, duplicate.StatusCode);
            await Task.Delay(100);
            Assert.Equal(0, pcscf.Available);

            incoming.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{pcscfPort};branch=z9hG4bKmt-retry;rport");
            await pcscf.SendAsync(incoming.ToBytes(), registration.RemoteEndPoint);
            var retryAccepted = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal(200, retryAccepted.StatusCode);
            var retryReport = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal(new byte[] { 2, 5 }, retryReport.RawBody);
            await pcscf.SendAsync(retryReport.CreateResponse(200, "OK").ToBytes(),
                registration.RemoteEndPoint);

            incoming.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{pcscfPort};branch=z9hG4bKmt-conflict;rport");
            incoming.RawBody = [1, 5, 0, 0, 1, 0];
            incoming.SetHeader("Content-Length", incoming.RawBody.Length.ToString());
            await pcscf.SendAsync(incoming.ToBytes(), registration.RemoteEndPoint);
            var conflict = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal(488, conflict.StatusCode);

            incoming.SetHeader("Call-ID", "host-mt-invalid@ims.test");
            incoming.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{pcscfPort};branch=z9hG4bKmt-invalid;rport");
            incoming.SetHeader("CSeq", "2 MESSAGE");
            incoming.RawBody = [1, 6, 0, 0, 1, 0];
            incoming.SetHeader("Content-Length", incoming.RawBody.Length.ToString());
            await pcscf.SendAsync(incoming.ToBytes(), registration.RemoteEndPoint);
            var invalidAccepted = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal(200, invalidAccepted.StatusCode);
            var errorReport = SipMessage.Parse((await pcscf.ReceiveAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal(new byte[] { 4, 6, 1, 95 }, errorReport.RawBody);
            await pcscf.SendAsync(errorReport.CreateResponse(200, "OK").ToBytes(),
                registration.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:ue@ims.test",
            "ims.test", "123456789012345", "127.0.0.1", AdvertiseSmsOverIms: false);
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");
        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, UnexpectedAka,
            localPort: 0, pcscfPort: pcscfPort, enableSmsReceiver: true);
        var received = new TaskCompletionSource<IncomingSms>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCount = 0;
        var reportCodes = Channel.CreateUnbounded<int>();
        client.SmsDeliveryReportAccepted += code => reportCodes.Writer.TryWrite(code);
        client.IncomingSmsReceived += sms =>
        {
            Interlocked.Increment(ref receivedCount);
            received.TrySetResult(sms);
        };
        client.Transport.IncomingRequestReceived += (_, request) =>
            _ = Task.Run(() => client.HandleIncomingSmsReportAsync(request,
                response => client.Transport.SendAsync(response)));
        sendMt.SetResult();
        Assert.Equal("hello", (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Text);
        await serverTask;
        Assert.Equal(1, receivedCount);
        var codes = new[]
        {
            await reportCodes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            await reportCodes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            await reportCodes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
        };
        Assert.Equal(new[] { 202, 200, 200 }, codes);
    }

    [Fact]
    public async Task OutgoingEarlyDialog_AcceptsUpdateAfterProvisionalSdp()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var media = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var mediaPort = ((IPEndPoint)media.Client.LocalEndPoint!).Port;
        var local = IPAddress.Loopback;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [local], [], [], [local], ["test IMS interface"])
        {
            HostOwnedAddresses = [local]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        });
        var inviteReceived = new TaskCompletionSource<SipMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sdp = $"v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {mediaPort} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        var serverTask = Task.Run(async () =>
        {
            var registration = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var register = SipMessage.Parse(registration.Buffer);
            var registered = register.CreateResponse(200, "OK");
            registered.SetHeader("Contact", register.GetHeader("Contact")!);
            await pcscf.SendAsync(registered.ToBytes(), registration.RemoteEndPoint);

            var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(packet.Buffer);
            var progress = invite.CreateResponse(183, "Session Progress");
            progress.SetHeader("To", $"{invite.GetHeader("To")};tag=early-peer");
            progress.SetHeader("Contact", "<sip:peer@127.0.0.1>");
            progress.Body = sdp;
            progress.SetHeader("Content-Type", "application/sdp");
            await pcscf.SendAsync(progress.ToBytes(), packet.RemoteEndPoint);
            inviteReceived.TrySetResult(invite);

            await finishCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", progress.GetHeader("To")!);
            accepted.SetHeader("Contact", "<sip:peer@127.0.0.1>");
            // A final response may omit SDP when the early answer carried it.
            await pcscf.SendAsync(accepted.ToBytes(), packet.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            Assert.Equal("ACK", ack.Method);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:+15550001111@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");
        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, UnexpectedAka,
            localPort: 0, pcscfPort: pcscfPort);
        client.AttachMediaRouteProvisioner((_, _) => Task.CompletedTask);
        using var calls = new ImsCallManager();
        var dial = calls.DialAsync("+15551234567", client);
        var initial = await inviteReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        var update = new SipMessage { IsRequest = true, Method = "UPDATE", RequestUri = initial.RequestUri };
        update.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bKearly-update");
        update.SetHeader("From", $"{initial.GetHeader("To")};tag=early-peer");
        update.SetHeader("To", initial.GetHeader("From")!);
        update.SetHeader("Call-ID", initial.GetHeader("Call-ID")!);
        update.SetHeader("CSeq", "2 UPDATE");
        update.Body = sdp + "a=curr:qos local sendrecv\r\n";
        var replies = new List<SipMessage>();
        await calls.HandleIncomingUpdateAsync(update, reply =>
        {
            replies.Add(reply);
            return Task.CompletedTask;
        });
        Assert.Equal(200, Assert.Single(replies).StatusCode);
        Assert.Contains("m=audio", replies[0].Body);
        finishCall.TrySetResult();
        Assert.Equal(CallState.Active, (await dial.WaitAsync(TimeSpan.FromSeconds(5))).State);
        await serverTask;
        await calls.HangupAsync();
    }

    [Fact]
    public async Task HostImsCall_UsesRegisteredIdentityAndBearerForInviteAndRtp()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var media = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var mediaPort = ((IPEndPoint)media.Client.LocalEndPoint!).Port;
        var local = IPAddress.Loopback;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [local], [], [], [local], ["test IMS interface"])
        {
            HostOwnedAddresses = [local]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        });
        var endpoint = Assert.Single(probe.EndpointCandidates);
        var serverTask = Task.Run(async () =>
        {
            var registration = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var register = SipMessage.Parse(registration.Buffer);
            Assert.DoesNotContain("+g.3gpp.smsip", register.GetHeader("Contact"));
            Assert.DoesNotContain("+g.3gpp.smsip", register.GetHeader("Accept-Contact"));
            Assert.DoesNotContain("MESSAGE", register.GetHeader("Allow"));
            var registered = register.CreateResponse(200, "OK");
            registered.SetHeader("Contact", register.GetHeader("Contact")!);
            registered.SetHeader("Service-Route", "<sip:scscf.ims.test;lr>");
            await pcscf.SendAsync(registered.ToBytes(), registration.RemoteEndPoint);

            var call = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(call.Buffer);
            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=network");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = $"v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=test\r\nc=IN IP4 198.51.100.1\r\nt=0 0\r\nm=video 40000 RTP/AVP 8\r\nc=IN IP4 198.51.100.2\r\na=rtpmap:8 H264/90000\r\na=rtcp:40001 IN IP4 198.51.100.3\r\nm=audio 0 RTP/AVP 0\r\nm=audio {mediaPort} RTP/AVP 8\r\nc=IN IP4 127.0.0.1\r\na=rtpmap:8 PCMA/8000\r\na=rtcp:{mediaPort + 1} IN IP4 127.0.0.2\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            accepted.SetHeader("Content-Length", System.Text.Encoding.UTF8.GetByteCount(accepted.Body).ToString());
            await pcscf.SendAsync(accepted.ToBytes(), call.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            return (invite, ack, call.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:+15550001111@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        string? cellularPani = "3GPP-E-UTRAN-FDD;utran-cell-id-3gpp=1112233C476B4321";
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");

        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, endpoint, profile, UnexpectedAka, localPort: 0, pcscfPort: pcscfPort,
            readCellularPani: _ => Task.FromResult<string?>(cellularPani));
        Assert.False(client.Profile.AdvertiseSmsOverIms);
        Assert.Equal(cellularPani, client.Profile.PAccessNetworkInfo);
        // A fake probe cannot authorize RTP over an arbitrary Windows route.
        // Without a PPP owner, the live IMS interface/source must be verified.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EnsureMediaRouteAsync(IPAddress.Parse("127.0.0.2")));
        cellularPani = "3GPP-E-UTRAN-TDD;utran-cell-id-3gpp=11122000176B4321";
        var mediaRouteRequests = new List<IPAddress>();
        var rejectMediaRoute = false;
        client.AttachMediaRouteProvisioner((destination, _) =>
        {
            mediaRouteRequests.Add(destination);
            if (rejectMediaRoute) throw new InvalidOperationException("Synthetic IMS media route failure.");
            return Task.CompletedTask;
        });
        var mediaRoutesCleared = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.AttachMediaRouteClearer(_ =>
        {
            mediaRoutesCleared.TrySetResult(true);
            return Task.CompletedTask;
        });
        await using var kernel = new VoKernel();
        var slot = new ModemSlot("host-media", "COM999");
        Assert.True(kernel.Pool.TryAddSlot(slot));
        var calls = slot.Calls;
        calls.ExternalMediaBridgeEnabled = true;
        var endedCalls = new List<CallEndedEventArgs>();
        calls.CallEnded += (_, e) => endedCalls.Add(e);
        var connected = await calls.DialAsync("+15551234567", client);
        var (sent, ack, source) = await serverTask;
        Assert.Equal(CallState.Active, connected.State);
        Assert.True(calls.IsHostImsCall);
        Assert.Equal("INVITE", sent.Method);
        Assert.Equal("<sip:scscf.ims.test;lr>", sent.GetHeader("Route"));
        Assert.DoesNotContain("MESSAGE", sent.GetHeader("Allow"));
        Assert.Equal("ACK", ack.Method);
        Assert.Null(ack.GetHeader("Route"));
        Assert.Equal(local, source.Address);
        Assert.Equal([IPAddress.Loopback, IPAddress.Parse("127.0.0.2")], mediaRouteRequests);
        Assert.Contains("sip:+15550001111@ims.test", sent.GetHeader("From"));
        Assert.Contains("c=IN IP4 127.0.0.1", sent.Body);
        Assert.Equal(cellularPani, sent.GetHeader("P-Access-Network-Info"));
        Assert.Equal(cellularPani, client.Profile.PAccessNetworkInfo);
        Assert.Contains("sip:001010123456789@127.0.0.1", sent.GetHeader("Contact"));

        var microphonePcm = Enumerable.Range(0, 160)
            .Select(index => (short)(index % 2 == 0 ? 2000 : -2000)).ToArray();
        Assert.False(kernel.TrySendPresentedImsPcm(connected.CallId, slot.Id, microphonePcm));
        calls.ExternalMediaBridgeEnabled = false;
        Assert.False(kernel.TrySendPresentedImsPcm("another-call", slot.Id, microphonePcm));
        Assert.False(kernel.TrySendPresentedImsPcm(connected.CallId, "another-slot", microphonePcm));
        Assert.True(kernel.TrySendPresentedImsPcm(connected.CallId, slot.Id, microphonePcm));
        UdpReceiveResult packet;
        do
        {
            packet = await media.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        } while (packet.Buffer.AsSpan(12).ToArray().All(value => value == 0xD5));
        Assert.Equal(local, packet.RemoteEndPoint.Address);
        Assert.Equal(8, packet.Buffer[1] & 0x7f);
        Assert.Contains(packet.Buffer.AsSpan(12).ToArray(), value => value != 0xD5);
        await calls.HangupAsync();
        await mediaRoutesCleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bye = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
        Assert.Equal("BYE", bye.Method);
        Assert.Null(bye.GetHeader("Route"));
        Assert.False(calls.IsHostImsCall);
        Assert.False(kernel.TrySendPresentedImsPcm(connected.CallId, slot.Id, microphonePcm));

        rejectMediaRoute = true;
        cellularPani = null;
        var missingMediaRouteAnswer = Task.Run(async () =>
        {
            var attempt = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(attempt.Buffer);
            Assert.Null(invite.GetHeader("P-Access-Network-Info"));
            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=route-missing");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {mediaPort} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            await pcscf.SendAsync(accepted.ToBytes(), attempt.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            var cleanup = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            return (ack, cleanup);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.DialAsync("+15551234569", client));
        var (missingRouteAck, missingRouteBye) = await missingMediaRouteAnswer;
        Assert.Equal("ACK", missingRouteAck.Method);
        Assert.Equal("BYE", missingRouteBye.Method);
        Assert.Equal(CallState.Idle, calls.State);
        Assert.Equal(3, mediaRouteRequests.Count);
        Assert.Null(client.Profile.PAccessNetworkInfo);
        Assert.Equal(2, endedCalls.Count);
        Assert.Null(kernel.ActiveCall);
        rejectMediaRoute = false;

        var unsupportedAnswer = Task.Run(async () =>
        {
            var attempt = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(attempt.Buffer);
            Assert.DoesNotContain("AMR-WB", invite.Body);
            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=unsupported");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 104\r\na=rtpmap:104 AMR-WB/16000\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            await pcscf.SendAsync(accepted.ToBytes(), attempt.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            var cleanup = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            return (ack, cleanup);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.DialAsync("+15551234568", client));
        var (unsupportedAck, cleanupBye) = await unsupportedAnswer;
        Assert.Equal("ACK", unsupportedAck.Method);
        Assert.Equal("BYE", cleanupBye.Method);
        Assert.Equal(CallState.Idle, calls.State);
        Assert.Null(calls.ActiveCall);
        Assert.Equal(3, endedCalls.Count);
        Assert.Null(kernel.ActiveCall);

        var unofferedAmrAnswer = Task.Run(async () =>
        {
            var attempt = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(attempt.Buffer);
            Assert.Contains("a=rtpmap:102 AMR/8000", invite.Body);
            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=unoffered-amr");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {mediaPort} RTP/AVP 103\r\na=rtpmap:103 AMR/8000\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            await pcscf.SendAsync(accepted.ToBytes(), attempt.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            var cleanup = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            return (ack, cleanup);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.DialAsync("+15551234570", client));
        var (unofferedAck, unofferedBye) = await unofferedAmrAnswer;
        Assert.Equal("ACK", unofferedAck.Method);
        Assert.Equal("BYE", unofferedBye.Method);
        Assert.Equal(3, mediaRouteRequests.Count);
        Assert.Equal(CallState.Idle, calls.State);

        var offeredAmrAnswer = Task.Run(async () =>
        {
            var attempt = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(attempt.Buffer);
            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=offered-amr");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {mediaPort} RTP/AVP 102\r\na=rtpmap:102 AMR/8000\r\na=fmtp:102 mode-set=2\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            await pcscf.SendAsync(accepted.ToBytes(), attempt.RemoteEndPoint);
            return SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
        });
        var amrCall = await calls.DialAsync("+15551234571", client);
        Assert.Equal(CallState.Active, amrCall.State);
        Assert.Equal("AMR", amrCall.Codec);
        Assert.Equal("ACK", (await offeredAmrAnswer).Method);
        Assert.Equal(4, mediaRouteRequests.Count);
        await calls.HangupAsync();
        Assert.Equal("BYE", SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer).Method);
    }

    [Fact]
    public async Task HostImsIncomingInvite_AnswersWithBearerBoundRtp()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var media = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var mediaPort = ((IPEndPoint)media.Client.LocalEndPoint!).Port;
        var local = IPAddress.Loopback;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [local], [], [], [local], ["test IMS interface"])
        {
            HostOwnedAddresses = [local]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        });
        var registrationTask = Task.Run(async () =>
        {
            var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var request = SipMessage.Parse(packet.Buffer);
            var response = request.CreateResponse(200, "OK");
            response.SetHeader("Contact", request.GetHeader("Contact")!);
            await pcscf.SendAsync(response.ToBytes(), packet.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:+15550001111@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");

        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, UnexpectedAka,
            localPort: 0, pcscfPort: pcscfPort);
        await registrationTask;
        client.AttachMediaRouteProvisioner((destination, _) =>
        {
            Assert.Equal(IPAddress.Loopback, destination);
            return Task.CompletedTask;
        });
        using var calls = new ImsCallManager { ExternalMediaBridgeEnabled = true };
        bool? incomingIsVoWifi = null;
        calls.IncomingCall += (_, args) => incomingIsVoWifi = args.IsVoWifi;
        var incoming = new SipMessage
        {
            IsRequest = true,
            Method = "INVITE",
            RequestUri = "sip:+15550001111@ims.test",
            Body = $"v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {mediaPort} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n"
        };
        incoming.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bKhostincoming");
        incoming.AddHeader("Via", "SIP/2.0/UDP proxy.ims.test;branch=z9hG4bKupstream");
        incoming.SetHeader("From", "<sip:+15551234567@ims.test>;tag=remote");
        incoming.SetHeader("To", "<sip:+15550001111@ims.test>");
        incoming.SetHeader("Call-ID", "host-incoming@test");
        incoming.SetHeader("CSeq", "1 INVITE");
        incoming.SetHeader("Contact", "<sip:caller@127.0.0.1>");
        incoming.AddHeader("Record-Route", "<sip:edge.ims.test;lr>");
        incoming.AddHeader("Record-Route", "<sip:core.ims.test;lr>");
        var replies = new List<SipMessage>();
        await calls.HandleIncomingInviteAsync(incoming, client, reply =>
        {
            replies.Add(reply);
            return Task.CompletedTask;
        });
        Assert.Equal([100, 180], replies.Select(reply => reply.StatusCode));
        Assert.All(replies, reply => Assert.Equal(incoming.Headers["Via"], reply.Headers["Via"]));
        Assert.Equal(incoming.Headers["Record-Route"], replies[^1].Headers["Record-Route"]);
        Assert.DoesNotContain("MESSAGE", replies[^1].GetHeader("Allow"));
        Assert.False(incomingIsVoWifi);
        Assert.True(calls.IsHostImsCall);
        var answered = await calls.AnswerAsync();
        Assert.Equal(CallState.Ringing, answered.State);
        Assert.Equal(200, replies[^1].StatusCode);
        var ack = new SipMessage { IsRequest = true, Method = "ACK" };
        ack.SetHeader("From", incoming.GetHeader("From")!);
        ack.SetHeader("To", replies[^1].GetHeader("To")!);
        ack.SetHeader("Call-ID", incoming.GetHeader("Call-ID")!);
        ack.SetHeader("CSeq", "1 ACK");
        calls.HandleIncomingAck(ack);
        Assert.Equal(CallState.Active, calls.State);
        Assert.Equal(incoming.Headers["Via"], replies[^1].Headers["Via"]);
        Assert.Equal(incoming.Headers["Record-Route"], replies[^1].Headers["Record-Route"]);
        Assert.DoesNotContain("MESSAGE", replies[^1].GetHeader("Allow"));
        Assert.Contains("c=IN IP4 127.0.0.1", replies[^1].Body);
        calls.SendExternalPcm(new short[160]);
        var packet = await media.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(local, packet.RemoteEndPoint.Address);
        Assert.Equal(8, packet.Buffer[1] & 0x7f);
        await calls.HangupAsync();
        var incomingBye = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
        Assert.Equal("BYE", incomingBye.Method);
        Assert.Equal("<sip:edge.ims.test;lr>, <sip:core.ims.test;lr>", incomingBye.GetHeader("Route"));

        // Exercise the carrier-style precondition exchange on the Host IMS
        // client, not only on the VoWiFi call-manager fixture.
        incoming.SetHeader("Call-ID", "host-qos@test");
        incoming.SetHeader("Supported", "precondition,100rel");
        incoming.Body = $"v=0\r\no=- 2 2 IN IP4 127.0.0.1\r\ns=test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {mediaPort} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n" +
            "a=curr:qos local none\r\na=curr:qos remote none\r\n" +
            "a=des:qos mandatory local sendrecv\r\na=des:qos optional remote sendrecv\r\n";
        replies.Clear();
        await calls.HandleIncomingInviteAsync(incoming, client, reply =>
        {
            replies.Add(reply);
            return Task.CompletedTask;
        });
        Assert.Equal([100, 183], replies.Select(reply => reply.StatusCode));
        Assert.True(calls.IsHostImsCall);
        Assert.Contains("precondition", replies[^1].GetHeader("Supported"));
        var prack = new SipMessage { IsRequest = true, Method = "PRACK" };
        prack.SetHeader("From", incoming.GetHeader("From")!);
        prack.SetHeader("To", replies[^1].GetHeader("To")!);
        prack.SetHeader("Call-ID", incoming.GetHeader("Call-ID")!);
        prack.SetHeader("CSeq", "2 PRACK");
        prack.SetHeader("RAck", "1 1 INVITE");
        await calls.HandleIncomingPrackAsync(prack, reply =>
        {
            replies.Add(reply);
            return Task.CompletedTask;
        });
        Assert.Equal(CallState.Incoming, calls.State);
        var update = new SipMessage { IsRequest = true, Method = "UPDATE" };
        update.SetHeader("From", incoming.GetHeader("From")!);
        update.SetHeader("To", replies[1].GetHeader("To")!);
        update.SetHeader("Call-ID", incoming.GetHeader("Call-ID")!);
        update.SetHeader("CSeq", "3 UPDATE");
        update.Body = incoming.Body.Replace("a=curr:qos local none", "a=curr:qos local sendrecv");
        await calls.HandleIncomingUpdateAsync(update, reply =>
        {
            replies.Add(reply);
            return Task.CompletedTask;
        });
        Assert.Equal(180, replies[^1].StatusCode);
        Assert.Equal(CallState.Ringing, calls.State);
        await calls.AnswerAsync();
        Assert.Equal(200, replies[^1].StatusCode);
        Assert.Empty(replies[^1].Body);
        ack.SetHeader("To", replies[^1].GetHeader("To")!);
        ack.SetHeader("Call-ID", incoming.GetHeader("Call-ID")!);
        calls.HandleIncomingAck(ack);
        Assert.Equal(CallState.Active, calls.State);
        await calls.HangupAsync();
        Assert.Equal("BYE", SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer).Method);

        incoming.SetHeader("Call-ID", "host-unsupported@test");
        incoming.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 104\r\na=rtpmap:104 AMR-WB/16000\r\n";
        replies.Clear();
        await calls.HandleIncomingInviteAsync(incoming, client, reply =>
        {
            replies.Add(reply);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.AnswerAsync());
        Assert.Equal(488, replies[^1].StatusCode);
        Assert.Equal(CallState.Idle, calls.State);
        Assert.False(calls.IsHostImsCall);
    }

    [Fact]
    public async Task HostImsRejectedInvite_ReleasesCallAndMediaState()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var local = IPAddress.Loopback;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [local], [], [], [local], ["test IMS interface"])
        {
            HostOwnedAddresses = [local]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        });
        var serverTask = Task.Run(async () =>
        {
            var registration = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var register = SipMessage.Parse(registration.Buffer);
            var registered = register.CreateResponse(200, "OK");
            registered.SetHeader("Contact", register.GetHeader("Contact")!);
            await pcscf.SendAsync(registered.ToBytes(), registration.RemoteEndPoint);
            var call = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(call.Buffer);
            var rejected = invite.CreateResponse(486, "Busy Here");
            await pcscf.SendAsync(rejected.ToBytes(), call.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:+15550001111@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");
        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, UnexpectedAka,
            localPort: 0, pcscfPort: pcscfPort);
        using var calls = new ImsCallManager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.DialAsync("+15551234567", client));
        await serverTask;
        Assert.Equal(CallState.Idle, calls.State);
        Assert.Null(calls.ActiveCall);
        Assert.False(calls.IsHostImsCall);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostImsHangupDuringInvite_CancelsAndAcknowledgesLateSuccessWithoutRevivingCall(bool cancelDialToken, bool terminated)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var local = IPAddress.Loopback;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [local], [], [], [local], ["test IMS interface"])
        {
            HostOwnedAddresses = [local]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        });
        var releaseLateAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            var registration = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var register = SipMessage.Parse(registration.Buffer);
            var registered = register.CreateResponse(200, "OK");
            registered.SetHeader("Contact", register.GetHeader("Contact")!);
            registered.SetHeader("Service-Route", "<sip:127.0.0.1;lr>");
            await pcscf.SendAsync(registered.ToBytes(), registration.RemoteEndPoint);

            var call = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(call.Buffer);
            var ringing = invite.CreateResponse(180, "Ringing");
            ringing.SetHeader("To", $"{invite.GetHeader("To")};tag=late");
            await pcscf.SendAsync(ringing.ToBytes(), call.RemoteEndPoint);

            var cancelPacket = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = SipMessage.Parse(cancelPacket.Buffer);
            var cancelled = cancel.CreateResponse(200, "OK");
            await pcscf.SendAsync(cancelled.ToBytes(), cancelPacket.RemoteEndPoint);
            await releaseLateAnswer.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var accepted = invite.CreateResponse(terminated ? 487 : 200, terminated ? "Request Terminated" : "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=late");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            await pcscf.SendAsync(accepted.ToBytes(), call.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            if (terminated)
            {
                Assert.Equal(invite.GetHeader("Via"), ack.GetHeader("Via"));
                await pcscf.SendAsync(accepted.ToBytes(), call.RemoteEndPoint);
                var repeatedAck = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
                Assert.Equal("ACK", repeatedAck.Method);
                return (invite, cancel, ack, (SipMessage?)null);
            }
            var bye = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            return (invite, cancel, ack, (SipMessage?)bye);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:+15550001111@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");
        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, UnexpectedAka,
            localPort: 0, pcscfPort: pcscfPort);
        await using var kernel = new VoKernel();
        var slot = new ModemSlot("cancel-test", "TEST-NO-MODEM");
        Assert.True(kernel.Pool.TryAddSlot(slot));
        var calls = slot.Calls;
        var ringingState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedCount = 0;
        var endedCount = 0;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        calls.CallEnded += (_, _) => { Interlocked.Increment(ref endedCount); ended.TrySetResult(); };
        calls.CallStateChanged += (_, args) =>
        {
            if (args.NewState == CallState.Ringing) ringingState.TrySetResult();
        };
        calls.CallConnected += (_, _) => Interlocked.Increment(ref connectedCount);

        using var cancellation = new CancellationTokenSource();
        var dialTask = calls.DialAsync("+15551234567", client, cancellation.Token);
        await ringingState.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelDialToken) cancellation.Cancel();
        else await calls.HangupAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(CallState.Idle, calls.State);
        Assert.Null(calls.ActiveCall);
        Assert.Null(kernel.ActiveCall);
        Assert.Null(kernel.ActiveCallSlotId);
        Assert.Null(calls.GetMediaConfiguration());
        releaseLateAnswer.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dialTask);
        var (inviteSent, cancelSent, ackSent, byeSent) = await serverTask;
        Assert.Equal("CANCEL", cancelSent.Method);
        Assert.Equal(inviteSent.RequestUri, cancelSent.RequestUri);
        Assert.Equal(inviteSent.GetHeader("Via"), cancelSent.GetHeader("Via"));
        Assert.Equal(inviteSent.GetHeader("To"), cancelSent.GetHeader("To"));
        Assert.Equal(inviteSent.GetHeader("From"), cancelSent.GetHeader("From"));
        Assert.Equal(inviteSent.GetHeader("Route"), cancelSent.GetHeader("Route"));
        Assert.Equal("ACK", ackSent.Method);
        if (terminated) Assert.Null(byeSent);
        else Assert.Equal("BYE", byeSent!.Method);
        Assert.Equal(0, connectedCount);
        Assert.Equal(1, endedCount);
        Assert.Equal(CallState.Idle, calls.State);
        Assert.Null(calls.ActiveCall);

        var secondInviteReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendLateFinal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var noProvisional = Task.Run(async () =>
        {
            var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(packet.Buffer);
            secondInviteReceived.TrySetResult();
            await sendLateFinal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=late-without-1xx");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            await pcscf.SendAsync(accepted.ToBytes(), packet.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            var bye = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            return (ack, bye);
        });
        var secondDial = calls.DialAsync("+15551234568", client);
        await secondInviteReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await calls.HangupAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.DialAsync("+15551234569", client));
        sendLateFinal.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondDial);
        var (secondAck, secondBye) = await noProvisional;
        Assert.Equal("ACK", secondAck.Method); // No provisional response: never send CANCEL.
        Assert.Equal("BYE", secondBye.Method);
        Assert.Equal(CallState.Idle, calls.State);
    }

    [Fact]
    public async Task HostImsRegistrar_RejectsCarrierIpsecChallengeWithoutSecurityDataPlane()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test IMS interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate"));
        var serverTask = Task.Run(async () =>
        {
            var datagram = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var request = SipMessage.Parse(datagram.Buffer);
            var response = request.CreateResponse(401, "Unauthorized");
            response.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            response.SetHeader("Security-Server",
                "ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;spi-c=1001;spi-s=1002;port-c=5062;port-s=5063");
            await pcscf.SendAsync(response.ToBytes(), datagram.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:001010123456789@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        var akaCalls = 0;
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> Aka(byte[] _, byte[] __, CancellationToken ___)
        {
            Interlocked.Increment(ref akaCalls);
            return Task.FromResult((new byte[8], new byte[16], new byte[16]));
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, Aka, localPort: 0, pcscfPort: pcscfPort));
        await serverTask;
        Assert.Contains("requires IMS IPsec", error.Message);
        Assert.Equal(0, akaCalls);
    }

    [Fact]
    public async Task HostImsRegistrar_UsesReservedSpisAndProtectedPortsWithInjectedBackend()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var unprotectedPcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var protectedPcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfProtectedPort = ((IPEndPoint)protectedPcscf.Client.LocalEndPoint!).Port;
        var backend = new TestHostImsSecurityBackend();
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test IMS interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate"));
        var server = Task.Run(async () =>
        {
            var initial = await unprotectedPcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var first = SipMessage.Parse(initial.Buffer);
            Assert.True(backend.Reserved);
            var ueClientPort = backend.Proposal.PortClient;
            var ueServerPort = backend.Proposal.PortServer;
            Assert.Contains("spi-c=0000001001", first.GetHeader("Security-Client"));
            Assert.Contains("spi-s=0000001002", first.GetHeader("Security-Client"));
            var challenge = first.CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            challenge.SetHeader("Security-Server",
                $"ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;" +
                $"spi-c=2001;spi-s=2002;port-c=50701;port-s={pcscfProtectedPort}");
            await unprotectedPcscf.SendAsync(challenge.ToBytes(), initial.RemoteEndPoint);

            var protectedPacket = await protectedPcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var authenticated = SipMessage.Parse(protectedPacket.Buffer);
            Assert.True(backend.Activated);
            Assert.Equal(ueClientPort, protectedPacket.RemoteEndPoint.Port);
            Assert.Contains($":{ueServerPort};", authenticated.GetHeader("Via"));
            Assert.Contains($":{ueServerPort};", authenticated.GetHeader("Contact"));
            Assert.NotNull(authenticated.GetHeader("Security-Verify"));
            var accepted = authenticated.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", authenticated.GetHeader("Contact")!);
            await protectedPcscf.SendAsync(accepted.ToBytes(), protectedPacket.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:001010123456789@ims.test",
            "ims.test", "123456789012345", IPAddress.Loopback.ToString());
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> Aka(byte[] _, byte[] __, CancellationToken ___) =>
            Task.FromResult((new byte[8], new byte[16], new byte[16]));

        var client = await HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, Aka,
            localPort: 0,
            pcscfPort: ((IPEndPoint)unprotectedPcscf.Client.LocalEndPoint!).Port,
            securityBackend: backend);
        await server;
        Assert.True(client.IsRegistered);
        Assert.Equal(backend.Proposal.PortClient, client.Transport.LocalEndPoint!.Port);
        Assert.Equal(backend.Proposal.PortServer, client.Transport.ProtectedServerEndPoint!.Port);
        await client.DisposeAsync();
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task HostImsRegistrar_UsesUserspaceEspBackendWithoutPlainUdpFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var unprotectedPcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var media = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var mediaPort = ((IPEndPoint)media.Client.LocalEndPoint!).Port;
        using var backend = new TestHostImsUserspaceBackend();
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test IMS interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate"));
        var server = Task.Run(async () =>
        {
            var initial = await unprotectedPcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var first = SipMessage.Parse(initial.Buffer);
            Assert.True(backend.Reserved);
            Assert.Contains("spi-c=0000001001", first.GetHeader("Security-Client"));
            Assert.Contains("spi-s=0000001002", first.GetHeader("Security-Client"));
            var challenge = first.CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            challenge.SetHeader("Security-Server",
                "ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;" +
                "spi-c=2001;spi-s=2002;port-c=50701;port-s=50702");
            await unprotectedPcscf.SendAsync(challenge.ToBytes(), initial.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:001010123456789@ims.test",
            "ims.test", "123456789012345", IPAddress.Loopback.ToString());
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> Aka(byte[] _, byte[] __, CancellationToken ___) =>
            Task.FromResult((new byte[8], new byte[16], new byte[16]));

        var registration = HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, Aka,
            localPort: 0,
            pcscfPort: ((IPEndPoint)unprotectedPcscf.Client.LocalEndPoint!).Port,
            securityBackend: backend);
        await server;
        var protectedPacket = await backend.ReadSentAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var authenticated = SipMessage.Parse(protectedPacket.Payload);
        Assert.True(backend.Activated);
        Assert.Equal(backend.Proposal.PortClient, protectedPacket.LocalEndPoint.Port);
        Assert.Equal(50702, protectedPacket.RemoteEndPoint.Port);
        Assert.Contains($":{backend.Proposal.PortServer};", authenticated.GetHeader("Via"));
        Assert.Contains($":{backend.Proposal.PortServer};", authenticated.GetHeader("Contact"));
        Assert.NotNull(authenticated.GetHeader("Security-Verify"));
        var accepted = authenticated.CreateResponse(200, "OK");
        accepted.SetHeader("Contact", authenticated.GetHeader("Contact")!);
        // A corrupt or unauthenticated ESP packet must not stop the receive
        // pump before the protected final REGISTER response arrives.
        backend.DeliverError(new FormatException("Invalid authenticated ESP packet"));
        backend.Deliver(new SipDatagram(accepted.ToBytes(), protectedPacket.LocalEndPoint,
            protectedPacket.RemoteEndPoint));

        var client = await registration.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(client.IsRegistered);
        Assert.Equal(backend.Proposal.PortClient, client.Transport.LocalEndPoint!.Port);
        Assert.Equal(backend.Proposal.PortServer, client.Transport.ProtectedServerEndPoint!.Port);

        // A new Security-Server may propose a different peer port. Reject it
        // before replacing the active ESP keys, so the existing binding stays
        // usable until an explicit transport handover is implemented.
        var rechallengeTask = client.Registration.RegisterAsync(Aka);
        var refreshPacket = await backend.ReadSentAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var refresh = SipMessage.Parse(refreshPacket.Payload);
        Assert.Equal("REGISTER", refresh.Method);
        var changedPort = refresh.CreateResponse(401, "Unauthorized");
        changedPort.SetHeader("WWW-Authenticate",
            $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
        changedPort.SetHeader("Security-Server",
            "ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;" +
            "spi-c=3001;spi-s=3002;port-c=50701;port-s=50703");
        backend.Deliver(new SipDatagram(changedPort.ToBytes(), refreshPacket.LocalEndPoint,
            refreshPacket.RemoteEndPoint));
        var rekeyError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rechallengeTask.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("transport handover", rekeyError.Message);
        Assert.Equal(1, backend.ActivationCount);
        Assert.True(client.IsRegistered);

        var validRechallengeTask = client.Registration.RegisterAsync(Aka);
        var nextInitialPacket = await backend.ReadSentAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var nextInitial = SipMessage.Parse(nextInitialPacket.Payload);
        var nextChallenge = nextInitial.CreateResponse(401, "Unauthorized");
        nextChallenge.SetHeader("WWW-Authenticate",
            $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
        nextChallenge.SetHeader("Security-Server",
            "ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;" +
            "spi-c=3001;spi-s=3002;port-c=50701;port-s=50702");
        backend.Deliver(new SipDatagram(nextChallenge.ToBytes(), nextInitialPacket.LocalEndPoint,
            nextInitialPacket.RemoteEndPoint));
        var nextAuthenticatedPacket = await backend.ReadSentAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var nextAuthenticated = SipMessage.Parse(nextAuthenticatedPacket.Payload);
        Assert.Equal("REGISTER", nextAuthenticated.Method);
        Assert.NotNull(nextAuthenticated.GetHeader("Security-Verify"));
        var nextAccepted = nextAuthenticated.CreateResponse(200, "OK");
        nextAccepted.SetHeader("Contact", nextAuthenticated.GetHeader("Contact")!);
        backend.Deliver(new SipDatagram(nextAccepted.ToBytes(), nextAuthenticatedPacket.LocalEndPoint,
            nextAuthenticatedPacket.RemoteEndPoint));
        await validRechallengeTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, backend.ActivationCount);
        Assert.True(client.IsRegistered);

        client.AttachMediaRouteProvisioner((destination, _) =>
        {
            Assert.Equal(IPAddress.Loopback, destination);
            return Task.CompletedTask;
        });
        using var calls = new ImsCallManager();
        var downlinkPcm = new TaskCompletionSource<short[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls.RemotePcmReceived += samples => downlinkPcm.TrySetResult(samples);
        var inviteTask = calls.DialAsync("+15551234567", client);
        var invitePacket = await backend.ReadSentAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var invite = SipMessage.Parse(invitePacket.Payload);
        Assert.Equal("INVITE", invite.Method);
        Assert.Equal(backend.Proposal.PortClient, invitePacket.LocalEndPoint.Port);
        Assert.Equal(50702, invitePacket.RemoteEndPoint.Port);
        Assert.Contains($":{backend.Proposal.PortServer};branch=", invite.GetHeader("Via"));
        Assert.Contains($":{backend.Proposal.PortServer}", invite.GetHeader("Contact"));
        var offeredRtpPort = int.Parse(System.Text.RegularExpressions.Regex.Match(
            invite.Body, @"(?m)^m=audio\s+(\d+)").Groups[1].Value);
        var acceptedCall = invite.CreateResponse(200, "OK");
        acceptedCall.SetHeader("To", $"{invite.GetHeader("To")};tag=peer");
        acceptedCall.SetHeader("Contact", "<sip:peer@127.0.0.1>");
        acceptedCall.Body = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {mediaPort} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        acceptedCall.SetHeader("Content-Type", "application/sdp");
        backend.Deliver(new SipDatagram(acceptedCall.ToBytes(),
            invitePacket.LocalEndPoint, invitePacket.RemoteEndPoint));
        Assert.Equal(CallState.Active, (await inviteTask.WaitAsync(TimeSpan.FromSeconds(3))).State);
        Assert.Equal("ACK", SipMessage.Parse((await backend.ReadSentAsync()
            .WaitAsync(TimeSpan.FromSeconds(3))).Payload).Method);

        var microphonePcm = Enumerable.Range(0, 160)
            .Select(index => (short)(index % 2 == 0 ? 1800 : -1800)).ToArray();
        calls.SendExternalPcm(microphonePcm);
        UdpReceiveResult uplink;
        do
        {
            uplink = await media.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        } while (uplink.Buffer.AsSpan(12).ToArray().All(value => value == 0xD5));
        Assert.Equal(8, uplink.Buffer[1] & 0x7f);
        Assert.Equal(IPAddress.Loopback, uplink.RemoteEndPoint.Address);

        var carrierRtp = new byte[172];
        carrierRtp[0] = 0x80;
        carrierRtp[1] = 8;
        Array.Fill(carrierRtp, (byte)0xD5, 12, 160);
        await media.SendAsync(carrierRtp, new IPEndPoint(IPAddress.Loopback, offeredRtpPort));
        Assert.Equal(160, (await downlinkPcm.Task.WaitAsync(TimeSpan.FromSeconds(3))).Length);

        await calls.HangupAsync();
        Assert.Equal("BYE", SipMessage.Parse((await backend.ReadSentAsync()
            .WaitAsync(TimeSpan.FromSeconds(3))).Payload).Method);

        // An incoming proxy request arrives from its protected client port,
        // but 3GPP UDP responses must use the UE protected client port and
        // the P-CSCF protected server port, not the received reverse pair.
        var incomingReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Transport.IncomingRequestReceived += (_, request) =>
        {
            if (request.Method == "PRACK")
            {
                _ = Task.Run(() => client.Transport.SendAsync(request.CreateResponse(200, "OK")));
                return;
            }
            if (request.Method != "INVITE") return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.Transport.SendAsync(request.CreateResponse(100, "Trying"));
                    var progress = request.CreateResponse(183, "Session Progress");
                    progress.SetHeader("To", $"{request.GetHeader("To")};tag=host");
                    progress.SetHeader("Require", "100rel");
                    progress.SetHeader("RSeq", "1");
                    await client.Transport.SendAsync(progress);
                    incomingReply.TrySetResult(true);
                }
                catch (Exception ex) { incomingReply.TrySetException(ex); }
            });
        };
        var carrierInvite = new SipMessage
        {
            IsRequest = true, Method = "INVITE", RequestUri = "sip:subscriber@ims.test"
        };
        carrierInvite.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bKprotected-incoming");
        carrierInvite.SetHeader("From", "<sip:caller@ims.test>;tag=peer");
        carrierInvite.SetHeader("To", "<sip:subscriber@ims.test>");
        carrierInvite.SetHeader("Call-ID", "protected-incoming@ims.test");
        carrierInvite.SetHeader("CSeq", "1 INVITE");
        backend.Deliver(new SipDatagram(carrierInvite.ToBytes(),
            new(IPAddress.Loopback, backend.Proposal.PortServer),
            new(IPAddress.Loopback, 50701)));
        await incomingReply.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var tryingPacket = await backend.ReadSentAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(backend.Proposal.PortClient, tryingPacket.LocalEndPoint.Port);
        Assert.Equal(50702, tryingPacket.RemoteEndPoint.Port);
        Assert.Equal(100, SipMessage.Parse(tryingPacket.Payload).StatusCode);

        var progressPacket = await backend.ReadSentAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var progressResponse = SipMessage.Parse(progressPacket.Payload);
        Assert.Equal(backend.Proposal.PortClient, progressPacket.LocalEndPoint.Port);
        Assert.Equal(50702, progressPacket.RemoteEndPoint.Port);
        Assert.Equal(183, progressResponse.StatusCode);
        Assert.Equal("100rel", progressResponse.GetHeader("Require"));
        Assert.Equal("1", progressResponse.GetHeader("RSeq"));

        var prack = new SipMessage { IsRequest = true, Method = "PRACK", RequestUri = carrierInvite.RequestUri };
        prack.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bKprotected-prack");
        prack.SetHeader("From", carrierInvite.GetHeader("From")!);
        prack.SetHeader("To", progressResponse.GetHeader("To")!);
        prack.SetHeader("Call-ID", carrierInvite.GetHeader("Call-ID")!);
        prack.SetHeader("CSeq", "2 PRACK");
        prack.SetHeader("RAck", "1 1 INVITE");
        backend.Deliver(new SipDatagram(prack.ToBytes(),
            new(IPAddress.Loopback, backend.Proposal.PortServer),
            new(IPAddress.Loopback, 50701)));
        SipDatagram prackReply;
        do
        {
            prackReply = await backend.ReadSentAsync().WaitAsync(TimeSpan.FromSeconds(3));
        } while (SipMessage.Parse(prackReply.Payload).GetHeader("CSeq") != "2 PRACK");
        Assert.Equal(backend.Proposal.PortClient, prackReply.LocalEndPoint.Port);
        Assert.Equal(50702, prackReply.RemoteEndPoint.Port);
        Assert.Equal(200, SipMessage.Parse(prackReply.Payload).StatusCode);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await unprotectedPcscf.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(150)));
        await client.DisposeAsync();
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task HostImsRegistrar_ReleasesRejectedSecurityReservation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test IMS interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(
            HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate"));
        var backend = new TestHostImsSecurityBackend(returnMismatchedPorts: true);
        var profile = new ImsProfile("001010123456789@ims.test", "sip:001010123456789@ims.test",
            "ims.test", "123456789012345", IPAddress.Loopback.ToString());
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA request expected.");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, UnexpectedAka,
            localPort: 0,
            pcscfPort: ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port,
            securityBackend: backend));
        Assert.Contains("invalid protected ports", error.Message);
        Assert.True(backend.Reserved);
        Assert.True(backend.Disposed);
        Assert.False(backend.Activated);
    }

    private sealed class TestHostImsSecurityBackend(bool returnMismatchedPorts = false)
        : IHostImsSecurityBackend, IHostImsSecurityReservation
    {
        public SecurityProposal Proposal { get; private set; } =
            new("hmac-sha-1-96", "aes-cbc", 1001, 1002, 0, 0);
        public bool Reserved { get; private set; }
        public bool Activated { get; private set; }
        public bool Disposed { get; private set; }

        public Task<IHostImsSecurityReservation> ReserveAsync(
            IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex,
            int localClientPort, int localServerPort,
            CancellationToken ct = default)
        {
            Assert.Equal(IPAddress.Loopback, localAddress);
            Assert.Equal(IPAddress.Loopback, pcscfAddress);
            Assert.True(interfaceIndex > 0);
            Assert.True(localClientPort > 0);
            Assert.True(localServerPort > 0);
            Assert.NotEqual(localClientPort, localServerPort);
            Proposal = Proposal with
            {
                PortClient = returnMismatchedPorts ? localServerPort : localClientPort,
                PortServer = returnMismatchedPorts ? localClientPort : localServerPort
            };
            Reserved = true;
            return Task.FromResult<IHostImsSecurityReservation>(this);
        }

        public void Activate(SecurityAgreement agreement, byte[] ck, byte[] ik)
        {
            Assert.True(Reserved);
            Assert.False(Disposed);
            Assert.Equal(2002u, agreement.PcscfServerSpi);
            Assert.Equal(16, ck.Length);
            Assert.Equal(16, ik.Length);
            Activated = true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class TestHostImsUserspaceBackend
        : IHostImsSecurityBackend, IHostImsUserspaceSecurityReservation
    {
        private readonly Channel<object> _incoming = Channel.CreateUnbounded<object>();
        private readonly Channel<SipDatagram> _outgoing = Channel.CreateUnbounded<SipDatagram>();

        public SecurityProposal Proposal { get; private set; } =
            new("hmac-sha-1-96", "aes-cbc", 1001, 1002, 0, 0);
        public bool Reserved { get; private set; }
        public bool Activated { get; private set; }
        public int ActivationCount { get; private set; }
        public bool Disposed { get; private set; }
        public Task<SipDatagram> ReadSentAsync() => _outgoing.Reader.ReadAsync().AsTask();

        public Task<IHostImsSecurityReservation> ReserveAsync(
            IPAddress localAddress, IPAddress pcscfAddress, int interfaceIndex,
            int localClientPort, int localServerPort, CancellationToken ct = default)
        {
            Assert.Equal(IPAddress.Loopback, localAddress);
            Assert.Equal(IPAddress.Loopback, pcscfAddress);
            Assert.True(interfaceIndex > 0);
            Assert.NotEqual(localClientPort, localServerPort);
            Proposal = Proposal with { PortClient = localClientPort, PortServer = localServerPort };
            Reserved = true;
            return Task.FromResult<IHostImsSecurityReservation>(this);
        }

        public void Activate(SecurityAgreement agreement, byte[] ck, byte[] ik)
        {
            Assert.True(Reserved);
            Assert.Equal(ActivationCount == 0 ? 2002u : 3002u, agreement.PcscfServerSpi);
            Assert.Equal(16, ck.Length);
            Assert.Equal(16, ik.Length);
            Activated = true;
            ActivationCount++;
        }

        public void Send(SipDatagram datagram)
        {
            Assert.True(Activated);
            Assert.False(Disposed);
            Assert.True(_outgoing.Writer.TryWrite(datagram));
        }

        public SipDatagram Receive(CancellationToken cancellationToken) =>
            _incoming.Reader.ReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult() switch
            {
                SipDatagram datagram => datagram,
                Exception error => throw error,
                _ => throw new InvalidOperationException("Unexpected test packet")
            };

        public void Deliver(SipDatagram datagram) => Assert.True(_incoming.Writer.TryWrite(datagram));

        public void DeliverError(Exception error) => Assert.True(_incoming.Writer.TryWrite(error));

        public void Dispose()
        {
            Disposed = true;
            _incoming.Writer.TryComplete();
            _outgoing.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task HostPinnedSipTransportIgnoresMatchingResponseFromAnotherIp()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sameIpDifferentPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var otherPeer = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport(timeoutMs: 3000);
        transport.ConnectOnInterface(IPAddress.Loopback, IPAddress.Loopback,
            GetLoopbackIndex(AddressFamily.InterNetwork), pcscfPort, localPort: 0);
        var profile = new ImsProfile("001010123456789@ims.test", "sip:001010123456789@ims.test",
            "ims.test", "123456789012345", IPAddress.Loopback.ToString(),
            transport.LocalEndPoint!.Port);
        using var session = new SipRegisterSession(transport, profile);
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No challenge expected.");

        var registration = session.RegisterAsync(UnexpectedAka);
        var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var request = SipMessage.Parse(packet.Buffer);
        var accepted = request.CreateResponse(200, "OK");
        accepted.SetHeader("Contact", request.GetHeader("Contact")!);
        await otherPeer.SendAsync(accepted.ToBytes(), transport.LocalEndPoint!);
        await Task.Delay(150);
        var acceptedForgedResponse = registration.IsCompleted;
        await sameIpDifferentPort.SendAsync(accepted.ToBytes(), packet.RemoteEndPoint);
        var result = await registration.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(acceptedForgedResponse);
        Assert.Equal(3600, result.ExpiresSeconds);
    }

    [Fact]
    public async Task HostPinnedSipTransportRebindsProtectedPortsWithoutChangingInterfaceOrAddresses()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var initialPcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var protectedPcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var reservedLocal = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var reservedServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var protectedLocalPort = ((IPEndPoint)reservedLocal.Client.LocalEndPoint!).Port;
        var protectedServerPort = ((IPEndPoint)reservedServer.Client.LocalEndPoint!).Port;
        reservedLocal.Dispose();
        reservedServer.Dispose();
        var initialPcscfPort = ((IPEndPoint)initialPcscf.Client.LocalEndPoint!).Port;
        var protectedPcscfPort = ((IPEndPoint)protectedPcscf.Client.LocalEndPoint!).Port;
        var interfaceIndex = GetLoopbackIndex(AddressFamily.InterNetwork);
        using var transport = new SipTransport();
        transport.ConnectOnInterface(IPAddress.Loopback, IPAddress.Loopback,
            interfaceIndex, initialPcscfPort, localPort: 0);
        var initialLocalPort = transport.LocalEndPoint!.Port;

        var first = SendTestRequestAsync(transport, initialLocalPort, "initial");
        Assert.Throws<InvalidOperationException>(() =>
            transport.ActivateProtectedPortsOnInterface(protectedLocalPort, protectedServerPort, protectedPcscfPort));
        var firstPacket = await initialPcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await initialPcscf.SendAsync(SipMessage.Parse(firstPacket.Buffer).CreateResponse(200, "OK").ToBytes(),
            firstPacket.RemoteEndPoint);
        Assert.Equal(200, (await first).StatusCode);

        transport.ActivateProtectedPortsOnInterface(protectedLocalPort, protectedServerPort, protectedPcscfPort);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, protectedLocalPort), transport.LocalEndPoint);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, protectedPcscfPort), transport.RemoteEndPoint);
        Assert.Equal(interfaceIndex, transport.BoundInterfaceIndex);

        var second = SendTestRequestAsync(transport, protectedLocalPort, "protected");
        var secondPacket = await protectedPcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(protectedLocalPort, secondPacket.RemoteEndPoint.Port);
        await protectedPcscf.SendAsync(SipMessage.Parse(secondPacket.Buffer).CreateResponse(200, "OK").ToBytes(),
            secondPacket.RemoteEndPoint);
        Assert.Equal(200, (await second).StatusCode);

        var incoming = new TaskCompletionSource<SipMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.IncomingRequestReceived += (_, request) => incoming.TrySetResult(request);
        var invite = new SipMessage { IsRequest = true, Method = "OPTIONS", RequestUri = "sip:ue@ims.test" };
        invite.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{protectedPcscfPort};branch=z9hG4bKprotected-incoming;rport");
        invite.SetHeader("From", "<sip:pcscf@ims.test>;tag=peer");
        invite.SetHeader("To", "<sip:ue@ims.test>");
        invite.SetHeader("Call-ID", "protected-incoming@ims.test");
        invite.SetHeader("CSeq", "1 OPTIONS");
        await protectedPcscf.SendAsync(invite.ToBytes(),
            new IPEndPoint(IPAddress.Loopback, protectedServerPort));
        await transport.SendAsync((await incoming.Task.WaitAsync(TimeSpan.FromSeconds(3))).CreateResponse(200, "OK"));
        var response = await protectedPcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(protectedLocalPort, response.RemoteEndPoint.Port);
        Assert.Equal(200, SipMessage.Parse(response.Buffer).StatusCode);
        using var oldPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, initialLocalPort));
        transport.Dispose();
        using var releasedServerPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, protectedServerPort));
    }

    [Fact]
    public async Task HostPinnedSipTransportSwitchesToUserspaceWithoutPlainUdpFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var initialPcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var initialPcscfPort = ((IPEndPoint)initialPcscf.Client.LocalEndPoint!).Port;
        var interfaceIndex = GetLoopbackIndex(AddressFamily.InterNetwork);
        using var transport = new SipTransport();
        transport.ConnectOnInterface(IPAddress.Loopback, IPAddress.Loopback,
            interfaceIndex, initialPcscfPort, localPort: 0);
        var initialLocalPort = transport.LocalEndPoint!.Port;
        var first = SendTestRequestAsync(transport, initialLocalPort, "before-userspace");
        var initialRequest = await initialPcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await initialPcscf.SendAsync(
            SipMessage.Parse(initialRequest.Buffer).CreateResponse(200, "OK").ToBytes(),
            initialRequest.RemoteEndPoint);
        Assert.Equal(200, (await first).StatusCode);

        var ports = transport.ReserveProtectedPortsOnInterface();
        var channel = Channel.CreateUnbounded<SipDatagram>();
        var sent = new TaskCompletionSource<SipDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ActivateUserspaceProtectedPortsOnInterface(
            ports.ClientPort, ports.ServerPort, initialPcscfPort,
            (datagram, token) =>
            {
                token.ThrowIfCancellationRequested();
                sent.TrySetResult(datagram);
                var response = SipMessage.Parse(datagram.Payload).CreateResponse(200, "OK");
                channel.Writer.TryWrite(new SipDatagram(response.ToBytes(),
                    datagram.LocalEndPoint, datagram.RemoteEndPoint));
                return Task.CompletedTask;
            },
            token => channel.Reader.ReadAsync(token).AsTask());
        using var reclaimedInitial = new UdpClient(new IPEndPoint(IPAddress.Loopback, initialLocalPort));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, ports.ClientPort), transport.LocalEndPoint);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, ports.ServerPort), transport.ProtectedServerEndPoint);
        Assert.Equal(interfaceIndex, transport.BoundInterfaceIndex);

        var protectedRequest = SendTestRequestAsync(transport, ports.ClientPort, "userspace");
        var raw = await sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ports.ClientPort, raw.LocalEndPoint.Port);
        Assert.Equal(initialPcscfPort, raw.RemoteEndPoint.Port);
        Assert.Equal(200, (await protectedRequest.WaitAsync(TimeSpan.FromSeconds(3))).StatusCode);
        Assert.Throws<InvalidOperationException>(() => transport.ActivateUserspaceProtectedPortsOnInterface(
            ports.ClientPort, ports.ServerPort, initialPcscfPort,
            (_, _) => Task.CompletedTask,
            token => channel.Reader.ReadAsync(token).AsTask()));
    }

    private static Task<SipMessage> SendTestRequestAsync(SipTransport transport, int localPort, string id)
    {
        var profile = new ImsProfile("001010123456789@ims.test", "sip:001010123456789@ims.test",
            "ims.test", "123456789012345", IPAddress.Loopback.ToString(), localPort);
        var request = ImsRegisterBuilder.BuildInitialRegister(profile, id + "@ims.test", 1);
        return transport.SendAndReceiveFinalAsync(request, timeoutMs: 3000);
    }

    private static HostImsProbeResult WithVerifiedRoute(HostImsProbeResult probe) => probe with
    {
        RouteChecks = probe.EndpointCandidates.Select(endpoint =>
            new HostImsRouteCheck(endpoint, true, "test IMS interface",
                OperatingSystem.IsWindows() ? GetLoopbackIndex(endpoint.LocalAddress.AddressFamily) : 1,
                "Verified")).ToArray()
    };

    private static int GetLoopbackIndex(AddressFamily family)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .First(item => item.NetworkInterfaceType == NetworkInterfaceType.Loopback &&
                item.GetIPProperties().UnicastAddresses.Any(address =>
                    address.Address.AddressFamily == family && IPAddress.IsLoopback(address.Address)));
        var properties = nic.GetIPProperties();
        return family == AddressFamily.InterNetwork
            ? properties.GetIPv4Properties().Index
            : properties.GetIPv6Properties().Index;
    }
}
