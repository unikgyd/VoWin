using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using Xunit.Abstractions;

namespace VoSharp.Tests;

/// <summary>Real LAN PBX, synthetic PCM only. No modem, carrier, microphone or speaker.</summary>
public sealed class AsteriskLanLiveTests(ITestOutputHelper output)
{
    private static readonly IPEndPoint Server = new(IPAddress.Parse("10.0.0.2"), 5060);
    // Live opt-in is not authority to guess a PBX realm or reuse a local/phone secret for a trunk role.
    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value :
            throw new InvalidOperationException($"The opt-in Asterisk test requires explicit {name}.");
    private static SipPeerOutboundCredentials OutboundCredentials(string username, string password)
    {
        var credentials = new SipPeerOutboundCredentials(username, password, RequiredEnvironment("VOWIN_ASTERISK_EXPECTED_REALM"))
        {
            AllowedAlgorithms = RequiredEnvironment("VOWIN_ASTERISK_ALLOWED_ALGORITHMS").Split(',', StringSplitOptions.TrimEntries)
        };
        credentials.Validate();
        return credentials;
    }
    private static SipGatewayPeer GatewayPeer()
    {
        var inbound = new SipPeerInboundCredentials(RequiredEnvironment("VOWIN_ASTERISK_PEER_INBOUND_USERNAME"),
            RequiredEnvironment("VOWIN_ASTERISK_PEER_INBOUND_PASSWORD"))
        {
            Algorithm = RequiredEnvironment("VOWIN_ASTERISK_PEER_INBOUND_ALGORITHM")
        };
        inbound.Validate();
        return new(Server, "600", inbound,
            OutboundCredentials(RequiredEnvironment("VOWIN_ASTERISK_PEER_OUTBOUND_USERNAME"),
                RequiredEnvironment("VOWIN_ASTERISK_PEER_OUTBOUND_PASSWORD")));
    }
    private static SipGatewayPeer PhonePeer(string username, string password) => new(Server, "unused",
        new SipPeerInboundCredentials(username, password), OutboundCredentials(username, password));
    private static IPAddress Local
    {
        get
        {
            using var route = new UdpClient(Server.AddressFamily);
            route.Connect(Server); // UDP connect selects a route without sending a packet.
            return ((IPEndPoint)route.Client.LocalEndPoint!).Address;
        }
    }

    [AsteriskLanLiveFact]
    public async Task RealAsteriskRoutesEachVoWinModuleToItsPhoneAndBridgesBothPcmDirections()
    {
        var path = Environment.GetEnvironmentVariable("VOWIN_ASTERISK_CREDENTIALS") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Lab", "asterisk-credentials.json");
        var credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path))!;
        var gatewayPeer = GatewayPeer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        var controller = new LabCarrier();
        var options = new SipGatewayOptions
        {
            BindAddress = Local, SipPort = 5062, Accounts = new(),
            Peer = gatewayPeer,
            RequireConfiguredLines = true,
            LineRoutes = new() { ["com6"] = new(), ["com10"] = new() }
        };
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync(ct);
        foreach (var (line, extension) in new[] { ("com6", "1001"), ("com10", "1002") })
        {
            using var sip = new UdpClient(new IPEndPoint(Local, 0));
            using var rtp = new UdpClient(new IPEndPoint(Local, 0));
            var phone = PhonePeer(extension, credentials[extension]);
            var register = Request(sip, "REGISTER", $"sip:{Server.Address}", extension); register.SetHeader("Expires", "120");
            Assert.Equal(200, (await AuthenticateAsync(sip, register, phone, ct)).StatusCode);
            try
            {
                controller.Ring(line);
                var invite = await ReceiveRequestAsync(sip, "INVITE", ct);
                Assert.Contains($"sip:{extension}@", invite.RequestUri);
                Assert.False(controller.Answered.Task.IsCompleted);
                var ringing = invite.CreateResponse(180, "Ringing");
                ringing.SetHeader("To", invite.GetHeader("To") + ";tag=lab-phone");
                await SendAsync(sip, ringing, ct);
                Assert.True(SipGatewaySdp.TryParseAudioOffer(invite.Body, Server.Address, false, out var remoteMedia));
                var accepted = invite.CreateResponse(200, "OK");
                accepted.SetHeader("To", ringing.GetHeader("To")!);
                accepted.SetHeader("Contact", $"<sip:{extension}@{sip.Client.LocalEndPoint}>");
                accepted.SetHeader("Content-Type", "application/sdp");
                var port = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
                accepted.Body = SipGatewaySdp.BuildOffer(Local, port, port + 1);
                await SendAsync(sip, accepted, ct);
                var ack = await ReceiveRequestAsync(sip, "ACK", ct);
                Assert.Equal(invite.GetHeader("Call-ID"), ack.GetHeader("Call-ID"));
                await controller.Answered.Task.WaitAsync(ct);
                var pcm = Enumerable.Repeat((short)2000, 160).ToArray();
                var encoded = G711Codec.EncodeAlaw(pcm);
                for (ushort sequence = 1; sequence <= 24; sequence++)
                {
                    var packet = new byte[172]; packet[0] = 0x80; packet[1] = 8;
                    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)sequence * 160);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x98765432);
                    encoded.CopyTo(packet, 12);
                    await rtp.SendAsync(packet, new IPEndPoint(remoteMedia!.Address, remoteMedia.Port), ct);
                    await Task.Delay(20, ct);
                }
                var uplink = await controller.Media.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.All(uplink, sample => Assert.InRange(sample, 1800, 2200));
                for (var frame = 0; frame < 12; frame++) { controller.Media.Emit(pcm); await Task.Delay(20, ct); }
                using var receiveLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                receiveLimit.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var packet = await rtp.ReceiveAsync(receiveLimit.Token);
                    if (packet.Buffer.Length >= 172 && packet.Buffer.AsSpan(12, 160).SequenceEqual(encoded)) break;
                }
                var bye = Request(sip, "BYE", Regex.Match(invite.GetHeader("Contact")!, @"<([^>]+)>").Groups[1].Value, extension);
                bye.SetHeader("From", accepted.GetHeader("To")!); bye.SetHeader("To", invite.GetHeader("From")!);
                bye.SetHeader("Call-ID", invite.GetHeader("Call-ID")!); bye.SetHeader("CSeq", "1 BYE");
                await SendAsync(sip, bye, ct);
                Assert.Equal(200, (await FinalAsync(sip, bye.GetHeader("Call-ID")!, ct)).StatusCode);
                while (gateway.Status.ActiveDialogs != 0) await Task.Delay(20, ct);
                Assert.Equal(line, controller.LastHangupLine);
                output.WriteLine($"Real Asterisk routed {line} only to {extension}; carrier pickup deferred until phone answer; both G.711/PCM directions and BYE cleanup verified. Carrier is synthetic: no PSTN call.");
            }
            finally
            {
                var unregister = Request(sip, "REGISTER", $"sip:{Server.Address}", extension);
                unregister.SetHeader("Contact", "*"); unregister.SetHeader("Expires", "0");
                Assert.Equal(200, (await AuthenticateAsync(sip, unregister, phone, ct)).StatusCode);
            }
        }
    }

    private static async Task<SipMessage> ReceiveRequestAsync(UdpClient socket, string method, CancellationToken ct)
    {
        while (true)
        {
            var packet = await socket.ReceiveAsync(ct);
            Assert.Equal(Server, packet.RemoteEndPoint);
            var message = SipMessage.Parse(packet.Buffer);
            if (message.IsRequest && message.Method == "OPTIONS") await SendAsync(socket, message.CreateResponse(200, "OK"), ct);
            if (message.IsRequest && message.Method == method) return message;
        }
    }

    [AsteriskLanLiveFact]
    public async Task RealAsteriskOutboundPrefixesPinTheModuleAndPreserveE164WithoutCallingACarrier()
    {
        var path = Environment.GetEnvironmentVariable("VOWIN_ASTERISK_CREDENTIALS") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Lab", "asterisk-credentials.json");
        var credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path))!;
        var gatewayPeer = GatewayPeer();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = limit.Token;
        var controller = new LabCarrier();
        await using var gateway = new SipGateway(new SipGatewayOptions
        {
            BindAddress = Local, SipPort = 5062, Accounts = new(),
            Peer = gatewayPeer with { AllowOutboundCalls = true },
            RequireConfiguredLines = true,
            LineRoutes = new() { ["com6"] = new(), ["com10"] = new() }
        }, controller);
        await gateway.StartAsync(ct);
        using var sip = new UdpClient(new IPEndPoint(Local, 0)); using var rtp = new UdpClient(new IPEndPoint(Local, 0));
        var phone = PhonePeer("1001", credentials["1001"]);
        var register = Request(sip, "REGISTER", $"sip:{Server.Address}", "1001"); register.SetHeader("Expires", "120");
        Assert.Equal(200, (await AuthenticateAsync(sip, register, phone, ct)).StatusCode);
        try
        {
            foreach (var (prefix, line) in new[] { ("81", "com6"), ("82", "com10") })
            {
                var invite = Request(sip, "INVITE", $"sip:{prefix}123456@{Server.Address}", "1001");
                invite.SetHeader("Content-Type", "application/sdp");
                var port = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
                invite.Body = SipGatewaySdp.BuildOffer(Local, port, port + 1);
                var accepted = await AuthenticateAsync(sip, invite, phone, ct);
                Assert.Equal(200, accepted.StatusCode);
                Assert.Equal(line, controller.LastDialLine); Assert.Equal("+123456", controller.LastDialNumber);
                await SendAsync(sip, InDialog(sip, invite, accepted, "ACK", 2), ct);
                var bye = InDialog(sip, invite, accepted, "BYE", 3); await SendAsync(sip, bye, ct);
                Assert.Equal(200, (await FinalAsync(sip, bye.GetHeader("Call-ID")!, ct)).StatusCode);
                while (gateway.Status.ActiveDialogs != 0) await Task.Delay(20, ct);
                output.WriteLine($"Real Asterisk prefix {prefix} pinned {line} and supplied +E.164; authenticated VoWin dialog ended. Carrier controller synthetic, no real number dialed.");
            }
        }
        finally
        {
            var unregister = Request(sip, "REGISTER", $"sip:{Server.Address}", "1001");
            unregister.SetHeader("Contact", "*"); unregister.SetHeader("Expires", "0");
            Assert.Equal(200, (await AuthenticateAsync(sip, unregister, phone, ct)).StatusCode);
        }
    }

    private sealed class LabMedia : ICallPcmMedia
    {
        public event Action<short[]>? RemotePcmReceived;
        public TaskCompletionSource<short[]> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(short[] pcm) => RemotePcmReceived?.Invoke(pcm);
        public void SendExternalPcm(short[] samples)
        {
            if (samples.Length > 0 && samples.All(sample => sample is >= 1800 and <= 2200)) Received.TrySetResult(samples);
        }
    }

    private sealed class LabCarrier : ISipGatewayCallController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall;
        public event EventHandler<CallEndedEventArgs>? CallEnded;
        public LabMedia Media { get; private set; } = new();
        public TaskCompletionSource Answered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _callId = "", _line = "";
        public string? LastHangupLine;
        public string? LastDialLine, LastDialNumber;
        public void Ring(string line)
        {
            Media = new(); Answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _callId = Guid.NewGuid().ToString("N"); _line = line;
            IncomingCall?.Invoke(this, new(_callId, "15500000000", slotId: line));
        }
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct)
        {
            Assert.Equal(_line, slotId); Answered.TrySetResult();
            return Task.FromResult(new SipGatewayCarrierCall(new(_callId, "15500000000", CallState.Active,
                DateTime.UtcNow, DateTime.UtcNow, null, "Synthetic PCM", null), slotId, Media));
        }
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct)
        {
            LastDialLine = slotId; LastDialNumber = number;
            _callId = Guid.NewGuid().ToString("N"); _line = slotId!; Media = new();
            return Task.FromResult(new SipGatewayCarrierCall(new(_callId, number, CallState.Active,
                DateTime.UtcNow, DateTime.UtcNow, null, "Synthetic PCM", null), slotId, Media));
        }
        public Task HangupAsync(string? slotId, CancellationToken ct)
        {
            LastHangupLine = slotId;
            CallEnded?.Invoke(this, new(_callId, "15500000000", null, "BYE", null, DateTime.UtcNow, slotId));
            return Task.CompletedTask;
        }
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }

    [AsteriskLanLiveFact]
    public async Task RealAsteriskAuthenticatesBothExtensionsAndEchoesG711WithoutCarrierCalls()
    {
        var path = Environment.GetEnvironmentVariable("VOWIN_ASTERISK_CREDENTIALS") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Lab", "asterisk-credentials.json");
        var credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        foreach (var extension in new[] { "1001", "1002" })
        {
            var peer = PhonePeer(extension, credentials[extension]);
            using var sip = new UdpClient(new IPEndPoint(Local, 0));
            using var rtp = new UdpClient(new IPEndPoint(Local, 0));
            var registration = Request(sip, "REGISTER", $"sip:{Server.Address}", extension);
            registration.SetHeader("Expires", "120");
            Assert.Equal(200, (await AuthenticateAsync(sip, registration, peer, ct)).StatusCode);
            output.WriteLine($"Real Asterisk extension {extension}: Digest registration accepted.");
            try
            {
                var rtpPort = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
                var invite = Request(sip, "INVITE", $"sip:700@{Server.Address}", extension);
                invite.SetHeader("Content-Type", "application/sdp");
                invite.Body = SipGatewaySdp.BuildOffer(Local, rtpPort, rtpPort + 1);
                var accepted = await AuthenticateAsync(sip, invite, peer, ct);
                Assert.Equal(200, accepted.StatusCode);
                Assert.True(SipGatewaySdp.TryParseAudioOffer(accepted.Body, Server.Address, false, out var offer));
                await SendAsync(sip, InDialog(sip, invite, accepted, "ACK", 2), ct);
                var audio = G711Codec.EncodeAlaw(Enumerable.Repeat((short)2000, 160).ToArray());
                var receiver = Task.Run(async () =>
                {
                    while (true)
                    {
                        var packet = await rtp.ReceiveAsync(ct);
                        if (packet.RemoteEndPoint.Address.Equals(Server.Address) && packet.Buffer.Length >= 172 &&
                            (packet.Buffer[1] & 0x7f) == 8 && packet.Buffer.AsSpan(12, 160).SequenceEqual(audio)) return;
                    }
                }, ct);
                for (ushort sequence = 1; sequence <= 40; sequence++)
                {
                    var packet = new byte[172]; packet[0] = 0x80; packet[1] = 8;
                    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)sequence * 160);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x12345678);
                    audio.CopyTo(packet, 12);
                    await rtp.SendAsync(packet, new IPEndPoint(offer!.Address, offer.Port), ct);
                    await Task.Delay(20, ct);
                }
                await receiver.WaitAsync(TimeSpan.FromSeconds(5), ct);
                var bye = InDialog(sip, invite, accepted, "BYE", 3);
                await SendAsync(sip, bye, ct);
                Assert.Equal(200, (await FinalAsync(sip, bye.GetHeader("Call-ID")!, ct)).StatusCode);
                output.WriteLine($"Real Asterisk extension {extension}: local Echo answered, exact G.711 PCM round trip verified, BYE accepted. No carrier call.");
            }
            finally
            {
                var unregister = Request(sip, "REGISTER", $"sip:{Server.Address}", extension);
                unregister.SetHeader("Contact", "*");
                unregister.SetHeader("Expires", "0");
                Assert.Equal(200, (await AuthenticateAsync(sip, unregister, peer, ct)).StatusCode);
            }
        }
    }

    private static SipMessage Request(UdpClient socket, string method, string uri, string username)
    {
        var endpoint = (IPEndPoint)socket.Client.LocalEndPoint!;
        var request = new SipMessage { IsRequest = true, Method = method, RequestUri = uri, SipVersion = "SIP/2.0" };
        request.SetHeader("Via", $"SIP/2.0/UDP {endpoint};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("From", $"<sip:{username}@{Server.Address}>;tag={Guid.NewGuid():N}");
        request.SetHeader("To", $"<{uri}>");
        if (method == "REGISTER") request.SetHeader("To", $"<sip:{username}@{Server.Address}>");
        request.SetHeader("Call-ID", Guid.NewGuid().ToString("N") + "@vowin-lan-lab");
        request.SetHeader("CSeq", $"1 {method}");
        request.SetHeader("Contact", $"<sip:{username}@{endpoint}>");
        request.SetHeader("Max-Forwards", "70");
        return request;
    }

    private static async Task<SipMessage> AuthenticateAsync(UdpClient socket, SipMessage request, SipGatewayPeer peer, CancellationToken ct)
    {
        await SendAsync(socket, request, ct);
        var challenged = await FinalAsync(socket, request.GetHeader("Call-ID")!, ct);
        Assert.Equal(401, challenged.StatusCode);
        if (request.Method == "INVITE")
        {
            var ack = SipMessage.Parse(request.ToBytes());
            ack.Method = "ACK"; ack.Body = ""; ack.SetHeader("CSeq", "1 ACK"); ack.SetHeader("To", challenged.GetHeader("To")!);
            await SendAsync(socket, ack, ct);
        }
        var authorization = SipPeerDigest.Create(challenged.GetHeader("WWW-Authenticate")!, request, peer);
        Assert.NotNull(authorization);
        request.SetHeader("Authorization", authorization);
        request.SetHeader("CSeq", $"2 {request.Method}");
        request.SetHeader("Via", $"SIP/2.0/UDP {socket.Client.LocalEndPoint};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await SendAsync(socket, request, ct);
        return await FinalAsync(socket, request.GetHeader("Call-ID")!, ct);
    }

    private static SipMessage InDialog(UdpClient socket, SipMessage invite, SipMessage accepted, string method, int sequence)
    {
        var request = SipMessage.Parse(invite.ToBytes());
        request.Method = method;
        request.RequestUri = Regex.Match(accepted.GetHeader("Contact") ?? "", @"<([^>]+)>").Groups[1].Value;
        Assert.NotEmpty(request.RequestUri);
        request.SetHeader("To", accepted.GetHeader("To")!);
        request.SetHeader("CSeq", $"{sequence} {method}");
        request.SetHeader("Via", $"SIP/2.0/UDP {socket.Client.LocalEndPoint};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.Headers.Remove("Authorization"); request.Headers.Remove("Content-Type"); request.Body = "";
        return request;
    }

    private static Task SendAsync(UdpClient socket, SipMessage message, CancellationToken ct) => socket.SendAsync(message.ToBytes(), Server, ct).AsTask();

    private static async Task<SipMessage> FinalAsync(UdpClient socket, string callId, CancellationToken ct)
    {
        while (true)
        {
            var packet = await socket.ReceiveAsync(ct);
            Assert.Equal(Server, packet.RemoteEndPoint);
            var message = SipMessage.Parse(packet.Buffer);
            if (message.IsRequest && message.Method == "OPTIONS")
                await SendAsync(socket, message.CreateResponse(200, "OK"), ct);
            if (!message.IsRequest && message.StatusCode >= 200 && message.GetHeader("Call-ID") == callId) return message;
        }
    }
}

internal sealed class AsteriskLanLiveFactAttribute : FactAttribute
{
    public AsteriskLanLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_ASTERISK_LAN") != "1")
            Skip = "Explicit private Asterisk LAN integration only; no modem or carrier call.";
    }
}
