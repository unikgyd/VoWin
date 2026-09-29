using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VoSharp.Modem;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class HostImsPdnTests
{
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

        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, endpoint, profile, UnexpectedAka, localPort: 0, pcscfPort: pcscfPort);
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
            var registered = register.CreateResponse(200, "OK");
            registered.SetHeader("Contact", register.GetHeader("Contact")!);
            await pcscf.SendAsync(registered.ToBytes(), registration.RemoteEndPoint);

            var call = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var invite = SipMessage.Parse(call.Buffer);
            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=network");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = $"v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {mediaPort} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            accepted.SetHeader("Content-Length", System.Text.Encoding.UTF8.GetByteCount(accepted.Body).ToString());
            await pcscf.SendAsync(accepted.ToBytes(), call.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            return (invite, ack, call.RemoteEndPoint);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:+15550001111@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");

        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, endpoint, profile, UnexpectedAka, localPort: 0, pcscfPort: pcscfPort);
        using var calls = new ImsCallManager { ExternalMediaBridgeEnabled = true };
        var connected = await calls.DialAsync("+15551234567", client);
        var (sent, ack, source) = await serverTask;
        Assert.Equal(CallState.Active, connected.State);
        Assert.True(calls.IsHostImsCall);
        Assert.Equal("INVITE", sent.Method);
        Assert.Equal("ACK", ack.Method);
        Assert.Equal(local, source.Address);
        Assert.Contains("sip:+15550001111@ims.test", sent.GetHeader("From"));
        Assert.Contains("c=IN IP4 127.0.0.1", sent.Body);
        Assert.Null(sent.GetHeader("P-Access-Network-Info"));
        Assert.Contains("sip:001010123456789@127.0.0.1", sent.GetHeader("Contact"));

        calls.SendExternalPcm(new short[160]);
        var packet = await media.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(local, packet.RemoteEndPoint.Address);
        Assert.Equal(8, packet.Buffer[1] & 0x7f);
        await calls.HangupAsync();
        var bye = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
        Assert.Equal("BYE", bye.Method);
        Assert.False(calls.IsHostImsCall);

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
        incoming.SetHeader("From", "<sip:+15551234567@ims.test>;tag=remote");
        incoming.SetHeader("To", "<sip:+15550001111@ims.test>");
        incoming.SetHeader("Call-ID", "host-incoming@test");
        incoming.SetHeader("CSeq", "1 INVITE");
        incoming.SetHeader("Contact", "<sip:caller@127.0.0.1>");
        var replies = new List<SipMessage>();
        await calls.HandleIncomingInviteAsync(incoming, client, reply =>
        {
            replies.Add(reply);
            return Task.CompletedTask;
        });
        Assert.Equal([100, 180], replies.Select(reply => reply.StatusCode));
        Assert.False(incomingIsVoWifi);
        Assert.True(calls.IsHostImsCall);
        var answered = await calls.AnswerAsync();
        Assert.Equal(CallState.Active, answered.State);
        Assert.Equal(200, replies[^1].StatusCode);
        Assert.Contains("c=IN IP4 127.0.0.1", replies[^1].Body);
        calls.SendExternalPcm(new short[160]);
        var packet = await media.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(local, packet.RemoteEndPoint.Address);
        Assert.Equal(8, packet.Buffer[1] & 0x7f);
        await calls.HangupAsync();

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

    [Fact]
    public async Task HostImsHangupDuringInvite_CancelsAndAcknowledgesLateSuccessWithoutRevivingCall()
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

            var accepted = invite.CreateResponse(200, "OK");
            accepted.SetHeader("To", $"{invite.GetHeader("To")};tag=late");
            accepted.SetHeader("Contact", "<sip:network@127.0.0.1>");
            accepted.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
            accepted.SetHeader("Content-Type", "application/sdp");
            await pcscf.SendAsync(accepted.ToBytes(), call.RemoteEndPoint);
            var ack = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            var bye = SipMessage.Parse((await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            return (invite, cancel, ack, bye);
        });
        var profile = new ImsProfile("001010123456789@ims.test", "sip:+15550001111@ims.test",
            "ims.test", "123456789012345", "192.0.2.50");
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No AKA challenge expected.");
        await using var client = await HostImsRegistrationClient.RegisterAsync(
            probe, Assert.Single(probe.EndpointCandidates), profile, UnexpectedAka,
            localPort: 0, pcscfPort: pcscfPort);
        using var calls = new ImsCallManager();
        var ringingState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedCount = 0;
        calls.CallStateChanged += (_, args) =>
        {
            if (args.NewState == CallState.Ringing) ringingState.TrySetResult();
        };
        calls.CallConnected += (_, _) => Interlocked.Increment(ref connectedCount);

        var dialTask = calls.DialAsync("+15551234567", client);
        await ringingState.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await calls.HangupAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dialTask);
        var (inviteSent, cancelSent, ackSent, byeSent) = await serverTask;
        Assert.Equal("CANCEL", cancelSent.Method);
        Assert.Equal(inviteSent.RequestUri, cancelSent.RequestUri);
        Assert.Equal(inviteSent.GetHeader("Via"), cancelSent.GetHeader("Via"));
        Assert.Equal(inviteSent.GetHeader("To"), cancelSent.GetHeader("To"));
        Assert.Equal(inviteSent.GetHeader("From"), cancelSent.GetHeader("From"));
        Assert.Equal(inviteSent.GetHeader("Route"), cancelSent.GetHeader("Route"));
        Assert.Equal("ACK", ackSent.Method);
        Assert.Equal("BYE", byeSent.Method);
        Assert.Equal(0, connectedCount);
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
            Assert.Contains($":{ueClientPort};", authenticated.GetHeader("Via"));
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
        Assert.Equal(protectedServerPort, response.RemoteEndPoint.Port);
        Assert.Equal(200, SipMessage.Parse(response.Buffer).StatusCode);
        using var oldPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, initialLocalPort));
        transport.Dispose();
        using var releasedServerPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, protectedServerPort));
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
