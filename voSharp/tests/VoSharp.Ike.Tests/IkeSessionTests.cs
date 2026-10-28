using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using VoSharp.Common.Aka;
using Xunit;

namespace VoSharp.Ike.Tests;

public class IkeSessionTests
{
    [Fact]
    public void FinalResponderAuth_RejectsMissingWrongMethodAndWrongMic()
    {
        var expected = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        Assert.Throws<AuthenticationException>(() => IkeSession.VerifyFinalResponderAuth(null, expected));
        Assert.Throws<AuthenticationException>(() => IkeSession.VerifyFinalResponderAuth(
            new IkePayload(IkePayloadType.Authentication, [1, 0, 0, 0, .. expected]), expected));
        Assert.Throws<AuthenticationException>(() => IkeSession.VerifyFinalResponderAuth(
            new IkePayload(IkePayloadType.Authentication, [2, 0, 0, 0, .. new byte[32]]), expected));
        IkeSession.VerifyFinalResponderAuth(
            new IkePayload(IkePayloadType.Authentication, [2, 0, 0, 0, .. expected]), expected);
    }

    [Fact]
    public void HandshakeResponse_RejectsWrongSpiOrMessageId()
    {
        var response = new IkeWire.IkeMessage
        {
            InitiatorSpi = 11,
            ResponderSpi = 22,
            Exchange = IkeExchangeType.IkeAuth,
            Flags = IkeFlags.Response,
            MessageId = 3
        };
        IkeSession.ValidateHandshakeResponse(response, 11, 22, IkeExchangeType.IkeAuth, 3);
        Assert.Throws<IkeFormatException>(() => IkeSession.ValidateHandshakeResponse(
            response, 12, 22, IkeExchangeType.IkeAuth, 3));
        Assert.Throws<IkeFormatException>(() => IkeSession.ValidateHandshakeResponse(
            response, 11, 23, IkeExchangeType.IkeAuth, 3));
        Assert.Throws<IkeFormatException>(() => IkeSession.ValidateHandshakeResponse(
            response, 11, 22, IkeExchangeType.IkeAuth, 4));
    }

    [Fact]
    public void NegotiatedSuite_MustMatchOfferedProposalNumber()
    {
        var offered = new[] { IkeSuite.Preferred };
        var downgraded = IkeSuite.Preferred with { EncryptionBits = 128 };
        IkeSession.ValidateChosenSuite(1, IkeSuite.Preferred, offered);
        Assert.Throws<IkeFormatException>(() => IkeSession.ValidateChosenSuite(1, downgraded, offered));
        Assert.Throws<IkeFormatException>(() => IkeSession.ValidateChosenSuite(2, IkeSuite.Preferred, offered));

        var childOffered = new[] { EspSuite.Preferred };
        var childDowngraded = EspSuite.Preferred with { EncryptionBits = 256 };
        Assert.Throws<IkeFormatException>(() => IkeSession.ValidateChosenSuite(1, childDowngraded, childOffered));
    }

    private sealed class MockAkaProvider : IAkaProvider
    {
        public int Calls;
        public byte[] Res { get; init; } = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
        public byte[] Ck { get; init; } = new byte[16];
        public byte[] Ik { get; init; } = new byte[16];

        public MockAkaProvider()
        {
            Array.Fill(Ck, (byte)0xCC);
            Array.Fill(Ik, (byte)0xEE);
        }

        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
        { Calls++; return Task.FromResult(AkaResult.Succeeded(Res, Ck, Ik)); }
    }

    [Theory]
    [InlineData(false, EapType.Aka, false, "")]
    [InlineData(true, EapType.Aka, false, "")]
    [InlineData(false, EapType.AkaPrime, false, "")]
    [InlineData(true, EapType.AkaPrime, false, "")]
    [InlineData(false, EapType.AkaPrime, true, "")]
    [InlineData(false, EapType.AkaPrime, false, "reserved")]
    [InlineData(false, EapType.AkaPrime, false, "md5")]
    [InlineData(false, EapType.AkaPrime, false, "missing-auth")]
    [InlineData(false, EapType.AkaPrime, false, "dup-auth")]
    [InlineData(false, EapType.AkaPrime, false, "dup-sa")]
    [InlineData(false, EapType.AkaPrime, false, "missing-ts")]
    [InlineData(false, EapType.AkaPrime, false, "missing-cp")]
    [InlineData(false, EapType.AkaPrime, false, "malformed-cp")]
    [InlineData(false, EapType.AkaPrime, false, "unoffered")]
    [InlineData(false, EapType.AkaPrime, false, "dh")]
    [InlineData(false, EapType.AkaPrime, false, "spi-zero")]
    [InlineData(false, EapType.AkaPrime, false, "changed-id")]
    [InlineData(false, EapType.AkaPrime, false, "family-widen")]
    [InlineData(false, EapType.AkaPrime, false, "no-local-scope")]
    [InlineData(false, EapType.AkaPrime, false, "no-peer-scope")]
    [InlineData(false, EapType.AkaPrime, false, "unknown-mode")]
    [InlineData(false, EapType.AkaPrime, false, "compression")]
    [InlineData(false, EapType.AkaPrime, false, "family")]
    public async Task IkeSession_FullHandshakeWithMockEpdg_AuthenticatesAndRejectsBadFinalAuth(bool ipv6, byte method, bool corruptAuth, string failure)
    {
        var ip = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var listener = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        listener.Bind(new IPEndPoint(ip, 0));
        var sim = new MockAkaProvider();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = RunMockEpdgAsync(listener, method, corruptAuth, failure, deadline.Token);
        var result = await IkeSession.EstablishAsync(new(ip, sim, "460001234567890", "460", "00",
            EpdgPort: ((IPEndPoint)listener.LocalEndPoint!).Port,
            Suite: IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 },
            AddressFamilyMode: IkeAddressFamilyMode.Ipv4, EapMethod: method,
            ChildSuite: failure == "md5" ? new EspSuite(12, 128, 1) : null), deadline.Token);
        try
        {
            await server;
            var success = !corruptAuth && failure is "" or "reserved" or "md5";
            Assert.Equal(success, result.Success);
            Assert.True(result.EapSucceeded);
            Assert.Equal(1, sim.Calls);
            if (success)
            {
                Assert.Equal("10.0.0.2", result.AssignedIp);
                Assert.Equal("10.0.0.1", result.PcscfIp);
                Assert.NotNull(result.Controller); Assert.NotNull(result.Transport);
                Assert.NotEmpty(result.OutboundEncKey!); Assert.NotEmpty(result.InboundAuthKey!);
                Assert.Contains(result.OutboundEncKey!, value => value != 0);
                Assert.NotNull(result.ChildPolicy);
            }
            else
            {
                Assert.Null(result.Transport); Assert.Null(result.ChildPolicy);
                if (corruptAuth) Assert.Equal("Responder final AUTH is missing or invalid.", result.ErrorMessage);
                else Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
                Assert.Equal(failure == "family", result.RetryableAddressFamilyFailure);
            }
        }
        finally { result.Controller?.Dispose(); result.Transport?.Dispose(); }
    }

    [Fact]
    public void IkeSessionRequest_InitializesProperties()
    {
        var mockSim = new MockAkaProvider();
        var req = new IkeSessionRequest(
            EpdgIp: IPAddress.Parse("127.0.0.1"),
            AkaProvider: mockSim,
            Imsi: "460001234567890",
            HomeMcc: "460",
            HomeMnc: "00",
            ExpectedIccid: "89860012345678901234",
            Apn: "ims",
            Imei: "860000000000001",
            FallbackPcscf: "10.0.0.1"
        );

        Assert.Equal("460001234567890", req.Imsi);
        Assert.Equal("ims", req.Apn);
        Assert.Equal("10.0.0.1", req.FallbackPcscf);
        Assert.Equal("860000000000001", req.Imei);
    }

    [Fact]
    public void InitialIkeAuthPayloads_MatchReferenceOrderWithoutMobike()
    {
        static IkePayload P(IkePayloadType type) => new(type, Array.Empty<byte>());
        var payloads = IkeSession.BuildInitialAuthPayloads(
            P(IkePayloadType.IdentificationInitiator),
            P(IkePayloadType.IdentificationResponder),
            P(IkePayloadType.Configuration),
            P(IkePayloadType.SecurityAssociation),
            P(IkePayloadType.TrafficSelectorInitiator),
            P(IkePayloadType.TrafficSelectorResponder));

        Assert.Equal(new[]
        {
            IkePayloadType.IdentificationInitiator,
            IkePayloadType.IdentificationResponder,
            IkePayloadType.Configuration,
            IkePayloadType.SecurityAssociation,
            IkePayloadType.TrafficSelectorInitiator,
            IkePayloadType.TrafficSelectorResponder,
            IkePayloadType.Notify,
            IkePayloadType.Notify
        }, payloads.Select(payload => payload.Type));
        Assert.Equal((ushort)16384, BinaryPrimitives.ReadUInt16BigEndian(payloads[6].Body.AsSpan(2, 2)));
        Assert.Equal((ushort)16417, BinaryPrimitives.ReadUInt16BigEndian(payloads[7].Body.AsSpan(2, 2)));
    }

    [Fact]
    public void DeviceIdentity_Uses41101LengthTypeAndTbcd()
    {
        var payload = IkeSession.MakeDeviceIdentityNotify("352127213600296", null, requestedType: 1);

        Assert.Equal((ushort)41101, BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2, 2)));
        Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(4, 2)));
        Assert.Equal((byte)1, payload.Body[6]);
        Assert.Equal(Convert.FromHexString("53127212630092F6"), payload.Body[7..]);
    }

    [Fact]
    public void BackoffTimer_01Aa_IsTenMinutes()
    {
        // TS 24.302 uses Timer 3: unit 101 means one minute, value 10.
        Assert.Equal("10 minutes", IkeSession.DescribeBackoffTimer(Convert.FromHexString("01AA")));
    }

    [Theory]
    [InlineData(IkeAddressFamilyMode.Ipv6, 1, 8, 10, 21)]
    [InlineData(IkeAddressFamilyMode.Ipv4, 1, 1, 3, 20)]
    public void CpAndTrafficSelectors_FollowAddressFamily(
        IkeAddressFamilyMode mode, byte selectorCount, ushort address, ushort dns, ushort pcscf)
    {
        var ts = IkeSession.BuildTrafficSelectors(IkePayloadType.TrafficSelectorInitiator, mode);
        var cp = IkeSession.BuildConfigurationRequest(mode);

        Assert.Equal(selectorCount, ts.Body[0]);
        Assert.Equal(address, BinaryPrimitives.ReadUInt16BigEndian(cp.Body.AsSpan(4, 2)));
        Assert.Equal(dns, BinaryPrimitives.ReadUInt16BigEndian(cp.Body.AsSpan(8, 2)));
        Assert.Equal(pcscf, BinaryPrimitives.ReadUInt16BigEndian(cp.Body.AsSpan(12, 2)));
    }

    [Fact]
    public void ConfigurationReply_ParsesIpv6AddressDnsAndPcscf()
    {
        var assigned = IPAddress.Parse("2001:db8::1234").GetAddressBytes();
        var dns = IPAddress.Parse("2001:4860:4860::8888").GetAddressBytes();
        var pcscf = IPAddress.Parse("2001:db8::5060").GetAddressBytes();
        var body = new byte[4 + 4 + 17 + 4 + 16 + 4 + 16];
        body[0] = 2;
        var offset = 4;
        WriteAttribute(8, assigned.Concat(new byte[] { 64 }).ToArray());
        WriteAttribute(10, dns);
        WriteAttribute(21, pcscf);

        var parsed = IkeSession.ParseConfigurationPayload(body);

        Assert.Equal("2001:db8::1234", parsed.AssignedIp);
        Assert.Equal(new[] { "2001:4860:4860::8888" }, parsed.DnsList);
        Assert.Equal(new[] { "2001:db8::5060" }, parsed.PcscfList);
        return;

        void WriteAttribute(ushort type, byte[] value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset, 2), type);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset + 2, 2), checked((ushort)value.Length));
            value.CopyTo(body, offset + 4);
            offset += 4 + value.Length;
        }
    }

    [Fact]
    public void DualRequestOffersBothFamiliesInCpAndTrafficSelectors()
    {
        var cp = IkeSession.BuildConfigurationRequest(IkeAddressFamilyMode.Dual).Body;
        Assert.Equal(new ushort[] { 1, 3, 20, 8, 10, 21 }, Enumerable.Range(0, 6)
            .Select(index => BinaryPrimitives.ReadUInt16BigEndian(cp.AsSpan(4 + index * 4, 2))));
        Assert.Equal(2, IkeSession.BuildTrafficSelectors(IkePayloadType.TrafficSelectorInitiator, IkeAddressFamilyMode.Dual).Body[0]);
    }

    [Fact]
    public async Task ExplicitBindMismatchIsReturnedAsFailureWithoutStartingAuthentication()
    {
        var result = await IkeSession.EstablishAsync(new(IPAddress.IPv6Loopback, new MockAkaProvider(),
            "999123123456789", "999", "12", LocalAddress: IPAddress.Loopback));
        Assert.False(result.Success); Assert.False(result.EapSucceeded);
        Assert.False(result.RetryableTransportFailure); Assert.False(result.RetryableAddressFamilyFailure);
        Assert.Contains("same IP family", result.ErrorMessage);
    }

    [Fact]
    public async Task UserCancellationIsNeverConvertedToAnotherFamilyAttempt()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IkeSession.EstablishAsync(new(IPAddress.Loopback,
            new MockAkaProvider(), "999123123456789", "999", "12"), cancel.Token));
    }

    private static byte[] Cp(params (ushort Type, string Address)[] attributes)
    {
        var output = new List<byte> { 2, 0, 0, 0 };
        foreach (var item in attributes)
        {
            var value = IPAddress.Parse(item.Address).GetAddressBytes();
            if (item.Type == 8) value = [.. value, 64];
            output.AddRange([(byte)(item.Type >> 8), (byte)item.Type, 0, (byte)value.Length, .. value]);
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DualConfigurationSelectsACompatiblePairRegardlessOfAttributeOrder(bool reversed)
    {
        (ushort, string)[] attributes = [(1, "10.0.0.2"), (8, "2001:db8::2"), (21, "2001:db8::3")];
        if (reversed) Array.Reverse(attributes);
        var parsed = IkeSession.ParseConfigurationPayload(Cp(attributes));
        Assert.Equal("2001:db8::2", parsed.AssignedIp); // Don't lose IPv6 just because IPv4 is also assigned.
        Assert.Equal("2001:db8::3", IkeSession.PickPcscf(parsed.PcscfList, parsed.AssignedIp));
    }

    [Theory]
    [InlineData(IkeAddressFamilyMode.Dual, "2001:db8::2")]
    [InlineData(IkeAddressFamilyMode.Ipv6, "2001:db8::2")]
    [InlineData(IkeAddressFamilyMode.Ipv4, "10.0.0.2")]
    public void PdnChoiceUsesNegotiatedInnerAddressesNotTheOuterSocketFamily(IkeAddressFamilyMode mode, string expected)
    {
        var parsed = IkeSession.ParseConfigurationPayload(Cp((1, "10.0.0.2"), (8, "2001:db8::2"),
            (20, "10.0.0.3"), (21, "2001:db8::3")), mode);
        Assert.Equal(expected, parsed.AssignedIp);
        Assert.True(IkeSession.IsUsablePdn(parsed.AssignedIp, IkeSession.PickPcscf(parsed.PcscfList, parsed.AssignedIp)));
    }

    [Fact]
    public void DualConfigurationCanChooseIpv4WhenOnlyIpv4PcscfWasReturned()
    {
        var parsed = IkeSession.ParseConfigurationPayload(Cp((8, "2001:db8::2"), (1, "10.0.0.2"), (20, "10.0.0.3")));
        Assert.Equal("10.0.0.2", parsed.AssignedIp);
    }

    [Theory]
    [InlineData(SocketError.NetworkUnreachable, true)]
    [InlineData(SocketError.AddressFamilyNotSupported, true)]
    [InlineData(SocketError.ConnectionRefused, true)]
    [InlineData(SocketError.AccessDenied, false)]
    public void OnlyExplicitRouteErrorsAreSafeEndpointFallbacks(SocketError error, bool expected)
    {
        Assert.Equal(expected, IkeSession.IsRouteFailure(new SocketException((int)error)));
        Assert.False(IkeSession.IsRouteFailure(new AuthenticationException("synthetic rejection")));
        Assert.False(IkeSession.IsRouteFailure(new CryptographicException("synthetic authentication fault")));
        Assert.False(IkeSession.IsRouteFailure(new OperationCanceledException()));
    }

    [Theory]
    [InlineData(36)]
    [InlineData(38)]
    public void AddressFamilyFallbackCannotOverrideAuthenticationRefusalOrBackoff(ushort type)
    {
        static IkePayload Notify(ushort value) => new(IkePayloadType.Notify, [0, 0, (byte)(value >> 8), (byte)value]);
        Assert.True(IkeSession.IsAddressFamilyRejection([Notify(type)]));
        Assert.False(IkeSession.IsAddressFamilyRejection([Notify(type), Notify(24)]));
        Assert.False(IkeSession.IsAddressFamilyRejection([Notify(type), Notify(9003)]));
        Assert.False(IkeSession.IsAddressFamilyRejection([Notify(type), Notify(41041)]));
    }

    [Fact]
    public async Task IkeSession_CookieChallenge_RetriesWithCookieAsFirstPayload()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var epdgPort = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cookie = Convert.FromHexString("000072E7ED2ADECBB8E705ED74C3552C91B38F04");

        var serverTask = Task.Run(async () =>
        {
            var buffer = new byte[65535];
            EndPoint source = new IPEndPoint(IPAddress.Any, 0);

            var firstDatagram = await listener.ReceiveFromAsync(buffer, SocketFlags.None, source, testCts.Token);
            var firstRequest = IkeWire.ParseMessage(buffer.AsSpan(0, firstDatagram.ReceivedBytes));
            await listener.SendToAsync(
                BuildInitErrorResponse(firstRequest.InitiatorSpi, 16390, cookie),
                SocketFlags.None,
                firstDatagram.RemoteEndPoint,
                testCts.Token);

            var secondDatagram = await listener.ReceiveFromAsync(buffer, SocketFlags.None, source, testCts.Token);
            var secondRequest = IkeWire.ParseMessage(buffer.AsSpan(0, secondDatagram.ReceivedBytes));

            Assert.Equal(firstRequest.InitiatorSpi, secondRequest.InitiatorSpi);
            Assert.Equal(0u, secondRequest.MessageId);
            Assert.Equal(firstRequest.Payloads.Count + 1, secondRequest.Payloads.Count);
            Assert.Equal(IkePayloadType.Notify, secondRequest.Payloads[0].Type);
            Assert.Equal((ushort)16390, BinaryPrimitives.ReadUInt16BigEndian(secondRequest.Payloads[0].Body.AsSpan(2, 2)));
            Assert.Equal(cookie, secondRequest.Payloads[0].Body[4..]);
            for (var i = 0; i < firstRequest.Payloads.Count; i++)
            {
                Assert.Equal(firstRequest.Payloads[i].Type, secondRequest.Payloads[i + 1].Type);
                Assert.Equal(firstRequest.Payloads[i].Body, secondRequest.Payloads[i + 1].Body);
            }

            await listener.SendToAsync(
                BuildInitErrorResponse(secondRequest.InitiatorSpi, 14, Array.Empty<byte>()),
                SocketFlags.None,
                secondDatagram.RemoteEndPoint,
                testCts.Token);
        }, testCts.Token);

        var result = await IkeSession.EstablishAsync(new IkeSessionRequest(
            EpdgIp: IPAddress.Loopback,
            AkaProvider: new MockAkaProvider(),
            Imsi: "234336571010764",
            HomeMcc: "234",
            HomeMnc: "33",
            EpdgPort: epdgPort), testCts.Token);

        await serverTask;
        Assert.False(result.Success);
        Assert.Contains("NO_PROPOSAL_CHOSEN", result.ErrorMessage);
    }

    private static byte[] BuildInitErrorResponse(ulong initiatorSpi, ushort notifyType, byte[] data)
    {
        var body = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), notifyType);
        data.CopyTo(body, 4);
        var response = new IkeWire.IkeMessage
        {
            InitiatorSpi = initiatorSpi,
            ResponderSpi = 0,
            Exchange = IkeExchangeType.IkeSaInit,
            Flags = IkeFlags.Response,
            MessageId = 0
        };
        response.Payloads.Add(new IkePayload(IkePayloadType.Notify, body));
        return IkeWire.SerializeMessage(response);
    }

    private static async Task RunMockEpdgAsync(Socket socket, byte method, bool corruptAuth, string failure, CancellationToken ct)
    {
        static byte[] Combine(params byte[][] values) => values.SelectMany(value => value).ToArray();
        using var contract = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "eap-aka-v1.json")));
        var vector = contract.RootElement.GetProperty("flows")[method == EapType.Aka ? 1 : 3];
        byte[] Fixture(string name) => Convert.FromHexString(vector.GetProperty(name).GetString()!);
        var buffer = new byte[65535];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
        var peer = received.RemoteEndPoint; var initRequestBytes = buffer[..received.ReceivedBytes];
        var init = IkeWire.ParseMessage(initRequestBytes);
        var suite = IkeSuite.Preferred with { DhGroupId = IkeDhGroupId.Ecp256 };
        using var dh = Ecp256Group.Create();
        var shared = dh.ComputeSharedSecret(init.Payloads.Single(p => p.Type == IkePayloadType.KeyExchange).Body.AsSpan(4));
        var ni = init.Payloads.Single(p => p.Type == IkePayloadType.Nonce).Body;
        var nr = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var keys = IkeCrypto.DeriveIkeKeys(suite, shared, ni, nr, init.InitiatorSpi, 17);
        CryptographicOperations.ZeroMemory(shared);
        IkeWire.IkeMessage Header(uint id, IkeExchangeType exchange, IkeFlags flags) => new()
        { InitiatorSpi = init.InitiatorSpi, ResponderSpi = 17, Version = 0x20, Exchange = exchange, Flags = flags, MessageId = id };
        var initial = Header(0, IkeExchangeType.IkeSaInit, IkeFlags.Response);
        initial.Payloads.Add(new(IkePayloadType.SecurityAssociation, IkeWire.EncodeProposals([new()
        { ProposalNumber = 1, Protocol = IkeProtocolId.Ike, Transforms = suite.ToTransforms() }])));
        initial.Payloads.Add(new(IkePayloadType.KeyExchange, Combine([0, 19, 0, 0], dh.Public)));
        initial.Payloads.Add(new(IkePayloadType.Nonce, nr));
        initial.Payloads.Add(new(IkePayloadType.Notify, [0, 0, 0x40, 0x2e]));
        var initResponseBytes = IkeWire.SerializeMessage(initial);
        await socket.SendToAsync(initResponseBytes, SocketFlags.None, peer, ct);

        async Task<IkeDecryptedMessage> Receive(uint id)
        {
            using var receiver = new IkeEncryptedMessageReceiver(Header(id, IkeExchangeType.IkeAuth, IkeFlags.Initiator),
                suite, keys.SkEi, keys.SkAi, true, TimeSpan.FromSeconds(5));
            while (true)
            {
                var datagram = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
                Assert.Equal(peer, datagram.RemoteEndPoint);
                var result = receiver.Accept(buffer.AsSpan(0, datagram.ReceivedBytes));
                if (result != null) return result;
            }
        }
        async Task Send(uint id, IReadOnlyList<IkePayload> payloads)
        {
            var padded = payloads.Concat([new IkePayload(IkePayloadType.Notify, Combine([0, 0, 0xfd, 0xe8], new byte[1700]))]).ToArray();
            var packets = IkeProtectedMessage.Encrypt(Header(id, IkeExchangeType.IkeAuth, IkeFlags.Response), padded,
                suite, keys.SkEr, keys.SkAr, true, 256);
            Assert.True(packets.Count > 2);
            var corrupt = packets[0].ToArray(); corrupt[^1] ^= 1;
            await socket.SendToAsync(corrupt, SocketFlags.None, peer, ct);
            foreach (var packet in packets.Reverse()) await socket.SendToAsync(packet, SocketFlags.None, peer, ct);
        }
        var first = await Receive(1);
        var idi = first.Payloads.Single(p => p.Type == IkePayloadType.IdentificationInitiator);
        var idr = first.Payloads.Single(p => p.Type == IkePayloadType.IdentificationResponder);
        await Send(1, [idr, new(IkePayloadType.Eap, Fixture("request"))]);
        var response = await Receive(2);
        Assert.Equal(Fixture("response"), response.Payloads.Single(p => p.Type == IkePayloadType.Eap).Body);
        await Send(2, [new(IkePayloadType.Eap, Fixture("notificationRequest"))]);
        response = await Receive(3);
        Assert.Equal(Fixture("notificationResponse"), response.Payloads.Single(p => p.Type == IkePayloadType.Eap).Body);
        await Send(3, [new(IkePayloadType.Eap, Fixture("success"))]);
        var final = await Receive(4);
        var keyPad = IkeCrypto.Prf(suite, Fixture("msk"), Encoding.ASCII.GetBytes("Key Pad for IKEv2"));
        var expectedInitiator = IkeCrypto.Prf(suite, keyPad, Combine(initRequestBytes, nr, IkeCrypto.Prf(suite, keys.SkPi, idi.Body)));
        Assert.Equal(Combine([2, 0, 0, 0], expectedInitiator), final.Payloads.Single(p => p.Type == IkePayloadType.Authentication).Body);
        var responderAuth = IkeCrypto.Prf(suite, keyPad, Combine(initResponseBytes, ni, IkeCrypto.Prf(suite, keys.SkPr, idr.Body)));
        if (corruptAuth) responderAuth[0] ^= 1;
        var child = IkeWire.DecodeProposals(first.Payloads.Single(p => p.Type == IkePayloadType.SecurityAssociation).Body)[0];
        var selected = new IkeProposal { ProposalNumber = child.ProposalNumber, Protocol = IkeProtocolId.Esp, Spi = [1, 2, 3, 4], Transforms = child.Transforms };
        if (failure == "unoffered") selected.Transforms[0] = new(1, 12, 256);
        if (failure == "dh") selected.Transforms.Add(new(4, 14));
        if (failure == "spi-zero") Array.Clear(selected.Spi);
        var finalPayloads = new List<IkePayload> {
            new(IkePayloadType.Authentication, Combine([2, 0, 0, 0], responderAuth)),
            new(IkePayloadType.SecurityAssociation, IkeWire.EncodeProposals([selected])),
            new(IkePayloadType.Configuration, [2, 0, 0, 0, 0, 1, 0, 4, 10, 0, 0, 2, 0, 20, 0, 4, 10, 0, 0, 1]),
            first.Payloads.Single(p => p.Type == IkePayloadType.TrafficSelectorInitiator),
            first.Payloads.Single(p => p.Type == IkePayloadType.TrafficSelectorResponder)};
        if (failure == "reserved") finalPayloads[0].Body.AsSpan(1, 3).Fill(0xff);
        if (failure == "missing-auth") finalPayloads.RemoveAt(0);
        if (failure == "dup-auth") finalPayloads.Add(finalPayloads[0]);
        if (failure == "dup-sa") finalPayloads.Add(finalPayloads[1]);
        if (failure == "missing-ts") finalPayloads.RemoveAll(p => p.Type == IkePayloadType.TrafficSelectorInitiator);
        if (failure == "missing-cp") finalPayloads.RemoveAll(p => p.Type == IkePayloadType.Configuration);
        if (failure == "malformed-cp") { var cp = finalPayloads.Single(p => p.Type == IkePayloadType.Configuration); finalPayloads[finalPayloads.IndexOf(cp)] = cp with { Body = Combine(cp.Body, [0, 3, 0, 4, 1]) }; }
        if (failure == "changed-id") finalPayloads.Add(new(IkePayloadType.IdentificationResponder, [2, 0, 0, 0, (byte)'x']));
        if (failure is "no-local-scope" or "no-peer-scope" or "family-widen")
        {
            var kind = failure == "no-peer-scope" ? IkePayloadType.TrafficSelectorResponder : IkePayloadType.TrafficSelectorInitiator;
            var ts = finalPayloads.Single(p => p.Type == kind);
            finalPayloads[finalPayloads.IndexOf(ts)] = new(kind, IkeTrafficSelectors.Encode(failure == "family-widen" ? [IkeTrafficSelector.Any(true)] :
                [new(IPAddress.Parse("10.0.0.9"), IPAddress.Parse("10.0.0.9"), 17, 0, 65535)]));
        }
        if (failure == "unknown-mode") finalPayloads.Add(new(IkePayloadType.Notify, [0, 0, 0x40, 7]));
        if (failure == "compression") finalPayloads.Add(new(IkePayloadType.Notify, [0, 0, 0x40, 3]));
        if (failure == "family")
        {
            var cp = finalPayloads.Single(p => p.Type == IkePayloadType.Configuration);
            finalPayloads[finalPayloads.IndexOf(cp)] = new(IkePayloadType.Configuration, Combine([2, 0, 0, 0, 0, 1, 0, 4, 10, 0, 0, 2, 0, 21, 0, 16], IPAddress.Parse("2001:db8::1").GetAddressBytes()));
        }
        await Send(4, finalPayloads);
        foreach (var key in new[] { keys.SkD, keys.SkAi, keys.SkAr, keys.SkEi, keys.SkEr, keys.SkPi, keys.SkPr, keyPad, responderAuth, expectedInitiator })
            CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void PickPcscfPrefersTheAssignedAddressFamily()
    {
        // A dual-family CFG_REQUEST may be answered with both P-CSCFs, in either order.
        var both = new[] { "10.20.30.40", "2001:db8::1" };

        Assert.Equal("2001:db8::1", IkeSession.PickPcscf(both, "2001:db8::10"));
        Assert.Equal("10.20.30.40", IkeSession.PickPcscf(both, "10.99.0.5"));
    }

    [Fact]
    public void PickPcscfFallsBackWhenNoFamilyMatches()
    {
        Assert.Equal("10.20.30.40", IkeSession.PickPcscf(new[] { "10.20.30.40" }, "2001:db8::10"));
        Assert.Null(IkeSession.PickPcscf(Array.Empty<string>(), "2001:db8::10"));
        Assert.Equal("2001:db8::1", IkeSession.PickPcscf(new[] { "2001:db8::1" }, null));
    }

    [Fact]
    public void IsUsablePdnRequiresMatchingAddressFamilies()
    {
        Assert.True(IkeSession.IsUsablePdn("2001:db8::10", "2001:db8::1"));
        Assert.True(IkeSession.IsUsablePdn("10.99.0.5", "10.20.30.40"));
        // IKE_AUTH succeeds but no IMS packet can ever be built across families.
        Assert.False(IkeSession.IsUsablePdn("2001:db8::10", "10.20.30.40"));
        Assert.False(IkeSession.IsUsablePdn(null, "10.20.30.40"));
        Assert.False(IkeSession.IsUsablePdn("2001:db8::10", "not-an-ip"));
    }
}
