using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

public sealed class SipGatewayTests
{
    [Fact]
    public void Options_RejectWildcardAndPublicBindingByDefault()
    {
        var accounts = new Dictionary<string, string> { ["1001"] = "secret" };
        Assert.Throws<ArgumentException>(() => new SipGatewayOptions { BindAddress = IPAddress.Any, Accounts = accounts }.Validate());
        Assert.Throws<ArgumentException>(() => new SipGatewayOptions { BindAddress = IPAddress.Parse("8.8.8.8"), Accounts = accounts }.Validate());
        new SipGatewayOptions { BindAddress = IPAddress.Parse("10.66.66.1"), Accounts = accounts }.Validate();
    }

    [Fact]
    public async Task DigestNonceCount_OnlyOneConcurrentReplayIsAccepted()
    {
        var authenticator = new SipDigestAuthenticator("test.local",
            new Dictionary<string, string> { ["1001"] = "secret" });
        var challenge = authenticator.CreateChallenge();
        var nonce = Regex.Match(challenge, "nonce=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        Assert.NotEmpty(nonce);

        SipMessage Request(uint count)
        {
            var request = new SipMessage
            {
                IsRequest = true,
                Method = "REGISTER",
                RequestUri = "sip:test.local"
            };
            var nc = count.ToString("x8");
            var ha1 = SipDigestAuthenticator.Md5Hex("1001:test.local:secret");
            var ha2 = SipDigestAuthenticator.Md5Hex("REGISTER:sip:test.local");
            var digest = SipDigestAuthenticator.Md5Hex($"{ha1}:{nonce}:{nc}:client:auth:{ha2}");
            request.SetHeader("Authorization",
                $"Digest username=\"1001\", realm=\"test.local\", nonce=\"{nonce}\", uri=\"sip:test.local\", response=\"{digest}\", algorithm=MD5, qop=auth, nc={nc}, cnonce=\"client\"");
            return request;
        }

        var replay = Request(1);
        var unsupportedAlgorithm = Request(1);
        unsupportedAlgorithm.SetHeader("Authorization",
            unsupportedAlgorithm.GetHeader("Authorization")!.Replace("algorithm=MD5", "algorithm=MD5-sess"));
        Assert.False(authenticator.Validate(unsupportedAlgorithm, out _));
        var attempts = Enumerable.Range(0, 128)
            .Select(index => Task.Run(() => authenticator.Validate(replay, out _))).ToArray();
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, accepted => accepted);
        Assert.False(authenticator.Validate(replay, out _));
        Assert.True(authenticator.Validate(Request(2), out _));
        Assert.False(authenticator.Validate(Request(1), out _));
    }

    [Fact]
    public void DigestQuotedPairs_AreUnescapedWithoutRegexEscapeSemantics()
    {
        var values = SipDigestAuthenticator.ParseParameters("username=\"a\\qb\", cnonce=\"x\\ny\"");
        Assert.Equal("aqb", values["username"]);
        Assert.Equal("xny", values["cnonce"]);
    }

    [Fact]
    public void InviteDigest_PrefersProxyAuthorizationOverStaleAuthorization()
    {
        var authenticator = new SipDigestAuthenticator("test.local",
            new Dictionary<string, string> { ["1001"] = "secret" });
        var nonce = Regex.Match(authenticator.CreateChallenge(), "nonce=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        var request = new SipMessage
        {
            IsRequest = true,
            Method = "INVITE",
            RequestUri = "sip:123@test.local"
        };
        var ha1 = SipDigestAuthenticator.Md5Hex("1001:test.local:secret");
        var ha2 = SipDigestAuthenticator.Md5Hex("INVITE:sip:123@test.local");
        var digest = SipDigestAuthenticator.Md5Hex($"{ha1}:{nonce}:00000001:client:auth:{ha2}");
        request.SetHeader("Authorization", "Digest username=\"stale\"");
        request.SetHeader("Proxy-Authorization",
            $"Digest username=\"1001\", realm=\"test.local\", nonce=\"{nonce}\", uri=\"sip:123@test.local\", response=\"{digest}\", algorithm=MD5, qop=auth, nc=00000001, cnonce=\"client\"");
        Assert.True(authenticator.Validate(request, out var username));
        Assert.Equal("1001", username);
    }

    [Fact]
    public void Sdp_UsesPacketSourceUnlessExplicitlyTrusted()
    {
        const string body = "v=0\r\nc=IN IP4 203.0.113.55\r\nm=audio 25000 RTP/AVP 111 8 0\r\n";
        var source = IPAddress.Parse("10.66.66.2");
        Assert.True(SipGatewaySdp.TryParseAudioOffer(body, source, false, out var safe));
        Assert.Equal(source, safe!.Address);
        Assert.Equal(8, safe.PayloadType);
        Assert.True(SipGatewaySdp.TryParseAudioOffer(body, source, true, out var trusted));
        Assert.Equal(IPAddress.Parse("203.0.113.55"), trusted!.Address);
    }

    [Fact]
    public void RtpSession_RaisesDecodedPcmForG711()
    {
        using var session = new RtpSession(null);
        short[]? decoded = null;
        session.OnAudioDecoded = samples => decoded = samples;
        var packet = new byte[12 + 160];
        packet[0] = 0x80;
        packet[1] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 160);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 42);
        Array.Fill(packet, (byte)0xd5, 12, 160);
        session.ProcessRtpPacket(packet);
        Assert.NotNull(decoded);
        Assert.Equal(160, decoded!.Length);
    }

    [Fact]
    public void RtpSession_DropsWrongSourceVersionAndUnnegotiatedPayload()
    {
        using var session = new RtpSession(null);
        var allowed = new IPEndPoint(IPAddress.Loopback, 25000);
        session.SetRemoteEndpoint(allowed.Address, allowed.Port, payloadType: 8);
        var decoded = 0;
        session.OnAudioDecoded = _ => decoded++;
        var packet = new byte[172];
        packet[0] = 0x80;
        packet[1] = 8;
        Array.Fill(packet, (byte)0xD5, 12, 160);

        session.ProcessRtpPacket(packet, new IPEndPoint(IPAddress.Loopback, 25001));
        packet[0] = 0x40;
        session.ProcessRtpPacket(packet, allowed);
        packet[0] = 0x80;
        packet[1] = 7;
        session.ProcessRtpPacket(packet, allowed);
        Assert.Equal(0, decoded);

        packet[1] = 8;
        session.ProcessRtpPacket(packet, allowed);
        Assert.Equal(1, decoded);
    }

    [Fact]
    public void Cancel_MustMatchInviteTransactionAndSource()
    {
        var invite = new SipMessage { IsRequest = true, Method = "INVITE", RequestUri = "sip:123@test" };
        invite.SetHeader("Via", "SIP/2.0/UDP 10.0.0.2:5060;branch=z9hG4bK-one");
        invite.SetHeader("From", "<sip:1001@test>;tag=phone");
        invite.SetHeader("To", "<sip:123@test>");
        invite.SetHeader("Call-ID", "call-1@test");
        invite.SetHeader("CSeq", "42 INVITE");
        var cancel = new SipMessage { IsRequest = true, Method = "CANCEL", RequestUri = invite.RequestUri };
        cancel.SetHeader("Via", invite.GetHeader("Via")!);
        cancel.SetHeader("From", invite.GetHeader("From")!);
        cancel.SetHeader("To", invite.GetHeader("To")!);
        cancel.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        cancel.SetHeader("CSeq", "42 CANCEL");
        var source = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 5060);

        Assert.True(SipGateway.MatchesCancelTransaction(invite, source, cancel, source));
        Assert.False(SipGateway.MatchesCancelTransaction(invite, source, cancel,
            new IPEndPoint(source.Address, 5061)));
        cancel.SetHeader("Via", "SIP/2.0/UDP 10.0.0.2:5060;branch=z9hG4bK-other");
        Assert.False(SipGateway.MatchesCancelTransaction(invite, source, cancel, source));
    }

    [Fact]
    public void DialogRequests_MustMatchBothTagsInPhoneDirection()
    {
        const string gatewayFrom = "<sip:gateway@test>;tag=gw";
        const string phoneTo = "<sip:1001@test>;tag=phone";
        var request = new SipMessage { IsRequest = true, Method = "BYE", RequestUri = "sip:gateway@test" };
        request.SetHeader("From", phoneTo);
        request.SetHeader("To", gatewayFrom);
        Assert.True(SipGateway.MatchesDialogTags(gatewayFrom, phoneTo, false, request));
        Assert.False(SipGateway.MatchesDialogTags(gatewayFrom, phoneTo, true, request));
        Assert.True(SipGateway.MatchesDialogTags(phoneTo, gatewayFrom, true, request));
        request.SetHeader("To", "<sip:gateway@test>;tag=wrong");
        Assert.False(SipGateway.MatchesDialogTags(gatewayFrom, phoneTo, false, request));
        request.SetHeader("To", "<sip:gateway@test>");
        Assert.False(SipGateway.MatchesDialogTags(gatewayFrom, phoneTo, false, request));
    }

    [Fact]
    public void CarrierEnd_MustMatchBridgedCallIdNotJustSlot()
    {
        var otherCall = new CallEndedEventArgs("other-call", "123", null, null, null, DateTime.UtcNow, "slot-1");
        var bridgedCall = new CallEndedEventArgs("bridged-call", "123", null, null, null, DateTime.UtcNow, "slot-1");
        var otherSlot = new CallEndedEventArgs("bridged-call", "123", null, null, null, DateTime.UtcNow, "slot-2");

        Assert.False(SipGateway.MatchesCarrierCallEnd("bridged-call", "slot-1", otherCall));
        Assert.False(SipGateway.MatchesCarrierCallEnd("bridged-call", "slot-1", otherSlot));
        Assert.True(SipGateway.MatchesCarrierCallEnd("bridged-call", "slot-1", bridgedCall));
    }

    [Fact]
    public void RtpSession_DeduplicatesTelephoneEventEndPackets()
    {
        using var session = new RtpSession(null);
        var received = new List<char>();
        session.OnDtmfReceived = (digit, _) => received.Add(digit);
        var packet = new byte[16];
        packet[0] = 0x80;
        packet[1] = 101;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 1234);
        packet[12] = 11; // #
        packet[13] = 0x80 | 10;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), 800);
        session.ProcessRtpPacket(packet);
        session.ProcessRtpPacket(packet);
        Assert.Equal(['#'], received);
    }

    [Fact]
    public async Task Registrar_ChallengesThenRegistersDigestAuthenticatedExtension()
    {
        var port = GetFreeUdpPort();
        var options = new SipGatewayOptions
        {
            BindAddress = IPAddress.Loopback,
            SipPort = port,
            Realm = "test.local",
            Accounts = new Dictionary<string, string> { ["1001"] = "correct horse" }
        };
        await using var gateway = new SipGateway(options, new FakeController());
        await gateway.StartAsync();
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var server = new IPEndPoint(IPAddress.Loopback, port);

        var first = BuildRegister(port, 1, "z9hG4bK-first");
        await phone.SendAsync(first.ToBytes(), server);
        var challenge = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(401, challenge.StatusCode);
        var nonce = Regex.Match(challenge.GetHeader("WWW-Authenticate")!, "nonce=\\\"([^\\\"]+)\\\"").Groups[1].Value;

        var malformedCredentials = BuildRegister(port, 2, "z9hG4bK-malformed-credentials");
        malformedCredentials.SetHeader("Authorization", "Digest username=\"bad\\quser\", realm=\"test.local\"");
        await phone.SendAsync(malformedCredentials.ToBytes(), server);
        var rejected = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(401, rejected.StatusCode);

        var authenticated = BuildRegister(port, 2, "z9hG4bK-second");
        var uri = authenticated.RequestUri;
        const string nc = "00000001";
        const string cnonce = "abcdef0123456789";
        var ha1 = SipDigestAuthenticator.Md5Hex("1001:test.local:correct horse");
        var ha2 = SipDigestAuthenticator.Md5Hex($"REGISTER:{uri}");
        var response = SipDigestAuthenticator.Md5Hex($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
        authenticated.SetHeader("Authorization",
            $"Digest username=\"1001\", realm=\"test.local\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\", algorithm=MD5, qop=auth, nc={nc}, cnonce=\"{cnonce}\"");
        await phone.SendAsync(authenticated.ToBytes(), server);
        var ok = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(200, ok.StatusCode);
        var registration = Assert.Single(gateway.Status.Registrations);
        Assert.Equal("1001", registration.Extension);
        Assert.Equal(((IPEndPoint)phone.Client.LocalEndPoint!).Port, registration.RemoteEndPoint.Port);
    }

    [Fact]
    public async Task PhoneInvite_BridgesCarrierAndRejectsWrongDialogBye()
    {
        var port = GetFreeUdpPort();
        var options = new SipGatewayOptions
        {
            BindAddress = IPAddress.Loopback,
            SipPort = port,
            Realm = "test.local",
            Accounts = new Dictionary<string, string> { ["1001"] = "secret" }
        };
        var controller = new DialingController();
        await using var gateway = new SipGateway(options, controller)
        {
            InviteAckTimeout = TimeSpan.FromMilliseconds(1200)
        };
        await gateway.StartAsync();
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var server = new IPEndPoint(IPAddress.Loopback, port);

        SipMessage Invite(int cseq, string branch)
        {
            var invite = new SipMessage
            {
                IsRequest = true,
                Method = "INVITE",
                RequestUri = "sip:123456@test.local",
                Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 25000 RTP/AVP 8\r\n"
            };
            invite.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:5099;branch={branch};rport");
            invite.SetHeader("From", "<sip:1001@test.local>;tag=phone");
            invite.SetHeader("To", "<sip:123456@test.local>");
            invite.SetHeader("Call-ID", "outbound-bridge@test.local");
            invite.SetHeader("CSeq", $"{cseq} INVITE");
            invite.SetHeader("Content-Type", "application/sdp");
            return invite;
        }

        var unauthenticated = Invite(1, "z9hG4bK-first");
        await phone.SendAsync(unauthenticated.ToBytes(), server);
        var challenge = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(407, challenge.StatusCode);
        var nonce = Regex.Match(challenge.GetHeader("Proxy-Authenticate")!, "nonce=\\\"([^\\\"]+)\\\"").Groups[1].Value;

        var authenticated = Invite(2, "z9hG4bK-second");
        var ha1 = SipDigestAuthenticator.Md5Hex("1001:test.local:secret");
        var ha2 = SipDigestAuthenticator.Md5Hex($"INVITE:{authenticated.RequestUri}");
        var digest = SipDigestAuthenticator.Md5Hex($"{ha1}:{nonce}:00000001:client:auth:{ha2}");
        authenticated.SetHeader("Proxy-Authorization",
            $"Digest username=\"1001\", realm=\"test.local\", nonce=\"{nonce}\", uri=\"{authenticated.RequestUri}\", response=\"{digest}\", algorithm=MD5, qop=auth, nc=00000001, cnonce=\"client\"");
        await phone.SendAsync(authenticated.ToBytes(), server);
        SipMessage final;
        do
        {
            final = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        } while (final.StatusCode < 200);
        Assert.Equal(200, final.StatusCode);
        Assert.Equal("123456", controller.DialedNumber);
        Assert.Equal(1, gateway.Status.ActiveDialogs);

        SipMessage Ack(string branch)
        {
            var ack = new SipMessage { IsRequest = true, Method = "ACK", RequestUri = "sip:gateway@test.local" };
            ack.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:5099;branch={branch};rport");
            ack.SetHeader("From", authenticated.GetHeader("From")!);
            ack.SetHeader("To", final.GetHeader("To")!);
            ack.SetHeader("Call-ID", authenticated.GetHeader("Call-ID")!);
            ack.SetHeader("CSeq", "2 ACK");
            return ack;
        }

        var forgedAck = Ack("z9hG4bK-forged-ack");
        forgedAck.SetHeader("To", "<sip:123456@test.local>;tag=wrong");
        await phone.SendAsync(forgedAck.ToBytes(), server);
        var retransmittedFinal = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(200, retransmittedFinal.StatusCode);
        Assert.Equal(final.GetHeader("To"), retransmittedFinal.GetHeader("To"));
        await phone.SendAsync(Ack("z9hG4bK-valid-ack").ToBytes(), server);
        await Task.Delay(1400);
        Assert.Equal(1, gateway.Status.ActiveDialogs);
        Assert.Equal(0, controller.HangupCount);

        SipMessage Bye(int cseq, string branch)
        {
            var bye = new SipMessage { IsRequest = true, Method = "BYE", RequestUri = "sip:gateway@test.local" };
            bye.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:5099;branch={branch};rport");
            bye.SetHeader("From", authenticated.GetHeader("From")!);
            bye.SetHeader("To", final.GetHeader("To")!);
            bye.SetHeader("Call-ID", authenticated.GetHeader("Call-ID")!);
            bye.SetHeader("CSeq", $"{cseq} BYE");
            return bye;
        }

        var forged = Bye(3, "z9hG4bK-forged");
        forged.SetHeader("To", "<sip:123456@test.local>;tag=wrong");
        await phone.SendAsync(forged.ToBytes(), server);
        Assert.Equal(481, SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer).StatusCode);
        Assert.Equal(0, controller.HangupCount);
        Assert.Equal(1, gateway.Status.ActiveDialogs);

        await phone.SendAsync(Bye(4, "z9hG4bK-valid").ToBytes(), server);
        Assert.Equal(200, SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer).StatusCode);
        await controller.HungUp.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, controller.HangupCount);
        Assert.Equal("slot-a", controller.HungUpSlot);
        Assert.Equal(0, gateway.Status.ActiveDialogs);

        // The baseband may report a connected call just after CANCEL. That
        // late carrier leg must still be torn down, not left orphaned.
        controller.DialRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledInvite = Invite(5, "z9hG4bK-cancelled");
        cancelledInvite.SetHeader("Call-ID", "cancelled-bridge@test.local");
        var nextDigest = SipDigestAuthenticator.Md5Hex($"{ha1}:{nonce}:00000002:client:auth:{ha2}");
        cancelledInvite.SetHeader("Proxy-Authorization",
            $"Digest username=\"1001\", realm=\"test.local\", nonce=\"{nonce}\", uri=\"{cancelledInvite.RequestUri}\", response=\"{nextDigest}\", algorithm=MD5, qop=auth, nc=00000002, cnonce=\"client\"");
        await phone.SendAsync(cancelledInvite.ToBytes(), server);
        var provisional = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(100, provisional.StatusCode);
        provisional = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(180, provisional.StatusCode);

        var cancel = new SipMessage { IsRequest = true, Method = "CANCEL", RequestUri = cancelledInvite.RequestUri };
        cancel.SetHeader("Via", cancelledInvite.GetHeader("Via")!);
        cancel.SetHeader("From", cancelledInvite.GetHeader("From")!);
        cancel.SetHeader("To", cancelledInvite.GetHeader("To")!);
        cancel.SetHeader("Call-ID", cancelledInvite.GetHeader("Call-ID")!);
        cancel.SetHeader("CSeq", "5 CANCEL");
        await phone.SendAsync(cancel.ToBytes(), server);
        var cancelOk = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(200, cancelOk.StatusCode);
        Assert.Equal("5 CANCEL", cancelOk.GetHeader("CSeq"));
        controller.DialRelease.TrySetResult();
        var cancelledFinal = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal(487, cancelledFinal.StatusCode);
        await controller.SecondHangup.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, controller.HangupCount);
        Assert.Equal(0, gateway.Status.ActiveDialogs);

        controller.DialRelease = null;
        var unacknowledged = Invite(6, "z9hG4bK-no-ack");
        unacknowledged.SetHeader("Call-ID", "unacknowledged-bridge@test.local");
        var thirdDigest = SipDigestAuthenticator.Md5Hex($"{ha1}:{nonce}:00000003:client:auth:{ha2}");
        unacknowledged.SetHeader("Proxy-Authorization",
            $"Digest username=\"1001\", realm=\"test.local\", nonce=\"{nonce}\", uri=\"{unacknowledged.RequestUri}\", response=\"{thirdDigest}\", algorithm=MD5, qop=auth, nc=00000003, cnonce=\"client\"");
        await phone.SendAsync(unacknowledged.ToBytes(), server);
        SipMessage noAckFinal;
        do
        {
            noAckFinal = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        } while (noAckFinal.StatusCode < 200);
        Assert.Equal(200, noAckFinal.StatusCode);
        await controller.ThirdHangup.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, controller.HangupCount);
        Assert.Equal(0, gateway.Status.ActiveDialogs);
    }

    [Fact]
    public async Task TimedOutFork_CancelsInviteAndClearsLateAcceptedDialog()
    {
        var port = GetFreeUdpPort();
        var options = new SipGatewayOptions
        {
            BindAddress = IPAddress.Loopback,
            SipPort = port,
            Realm = "test.local",
            Accounts = new Dictionary<string, string> { ["1001"] = "secret" }
        };
        var controller = new FakeController();
        await using var gateway = new SipGateway(options, controller)
        {
            InviteTransactionTimeout = TimeSpan.FromMilliseconds(150)
        };
        await gateway.StartAsync();
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var server = new IPEndPoint(IPAddress.Loopback, port);

        var first = BuildRegister(port, 1, "z9hG4bK-timeout-first");
        await phone.SendAsync(first.ToBytes(), server);
        var challenge = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        var nonce = Regex.Match(challenge.GetHeader("WWW-Authenticate")!, "nonce=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        var register = BuildRegister(port, 2, "z9hG4bK-timeout-second");
        var ha1 = SipDigestAuthenticator.Md5Hex("1001:test.local:secret");
        var ha2 = SipDigestAuthenticator.Md5Hex($"REGISTER:{register.RequestUri}");
        var response = SipDigestAuthenticator.Md5Hex($"{ha1}:{nonce}:00000001:abcdef:auth:{ha2}");
        register.SetHeader("Authorization",
            $"Digest username=\"1001\", realm=\"test.local\", nonce=\"{nonce}\", uri=\"{register.RequestUri}\", response=\"{response}\", algorithm=MD5, qop=auth, nc=00000001, cnonce=\"abcdef\"");
        await phone.SendAsync(register.ToBytes(), server);
        Assert.Equal(200, SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer).StatusCode);

        controller.RaiseIncoming(new IncomingCallEventArgs("carrier-1", "12345678", slotId: "slot-1"));
        var invite = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("INVITE", invite.Method);
        var wrongBranch = invite.CreateResponse(200, "OK");
        wrongBranch.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bK-wrong");
        await phone.SendAsync(wrongBranch.ToBytes(), server);
        var cancel = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("CANCEL", cancel.Method);
        Assert.Equal(invite.GetHeader("Call-ID"), cancel.GetHeader("Call-ID"));

        var accepted = invite.CreateResponse(200, "OK");
        accepted.SetHeader("To", invite.GetHeader("To") + ";tag=late-phone");
        accepted.SetHeader("Contact", "<sip:1001@127.0.0.1:25000>");
        accepted.SetHeader("Content-Type", "application/sdp");
        accepted.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 25000 RTP/AVP 8\r\n";
        await phone.SendAsync(accepted.ToBytes(), server);

        var ack = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        var bye = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("ACK", ack.Method);
        Assert.Equal("BYE", bye.Method);
        Assert.Equal(invite.GetHeader("Call-ID"), bye.GetHeader("Call-ID"));
        await phone.SendAsync(bye.CreateResponse(200, "OK").ToBytes(), server);
        Assert.Equal(0, gateway.Status.ActiveDialogs);

        controller.RaiseIncoming(new IncomingCallEventArgs("carrier-2", "12345678", slotId: "slot-1"));
        var secondInvite = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("INVITE", secondInvite.Method);
        var secondCancel = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("CANCEL", secondCancel.Method);
        var terminated = secondInvite.CreateResponse(487, "Request Terminated");
        terminated.SetHeader("To", secondInvite.GetHeader("To") + ";tag=cancelled-phone");
        await phone.SendAsync(terminated.ToBytes(), server);
        var nonSuccessAck = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("ACK", nonSuccessAck.Method);
        Assert.Equal(secondInvite.GetHeader("Via"), nonSuccessAck.GetHeader("Via"));
        Assert.Equal("1 ACK", nonSuccessAck.GetHeader("CSeq"));

        gateway.InviteTransactionTimeout = TimeSpan.FromSeconds(2);
        controller.RaiseIncoming(new IncomingCallEventArgs("carrier-3", "12345678", slotId: "slot-1"));
        var thirdInvite = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        var declined = thirdInvite.CreateResponse(486, "Busy Here");
        declined.SetHeader("To", thirdInvite.GetHeader("To") + ";tag=busy-phone");
        await phone.SendAsync(declined.ToBytes(), server);
        var declineAck = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("ACK", declineAck.Method);
        Assert.Equal(thirdInvite.GetHeader("Via"), declineAck.GetHeader("Via"));
        await phone.SendAsync(declined.ToBytes(), server);
        var repeatedAck = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("ACK", repeatedAck.Method);
        Assert.Equal(thirdInvite.GetHeader("Via"), repeatedAck.GetHeader("Via"));

        controller.RaiseIncoming(new IncomingCallEventArgs("carrier-4", "12345678", slotId: "slot-1"));
        var fourthInvite = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        var acceptedImmediately = fourthInvite.CreateResponse(200, "OK");
        acceptedImmediately.SetHeader("To", fourthInvite.GetHeader("To") + ";tag=accepted-phone");
        acceptedImmediately.SetHeader("Contact", "<sip:1001@127.0.0.1:25000>");
        acceptedImmediately.SetHeader("Content-Type", "application/sdp");
        acceptedImmediately.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 25000 RTP/AVP 8\r\n";
        await phone.SendAsync(acceptedImmediately.ToBytes(), server);
        var immediateAck = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        var cleanupBye = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("ACK", immediateAck.Method);
        Assert.Equal("BYE", cleanupBye.Method);
        await phone.SendAsync(cleanupBye.CreateResponse(200, "OK").ToBytes(), server);

        gateway.InviteTransactionTimeout = TimeSpan.FromMilliseconds(800);
        controller.RaiseIncoming(new IncomingCallEventArgs("carrier-5", "12345678", slotId: "slot-1"));
        var ringingInvite = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        var ringing = ringingInvite.CreateResponse(180, "Ringing");
        ringing.SetHeader("To", ringingInvite.GetHeader("To") + ";tag=ringing-phone");
        await phone.SendAsync(ringing.ToBytes(), server);
        var afterRinging = SipMessage.Parse((await phone.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("CANCEL", afterRinging.Method);
    }

    private static SipMessage BuildRegister(int port, int cseq, string branch)
    {
        var request = new SipMessage { IsRequest = true, Method = "REGISTER", RequestUri = "sip:test.local" };
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:5099;branch={branch};rport");
        request.SetHeader("From", "<sip:1001@test.local>;tag=phone");
        request.SetHeader("To", "<sip:1001@test.local>");
        request.SetHeader("Call-ID", "register-test@test.local");
        request.SetHeader("CSeq", $"{cseq} REGISTER");
        request.SetHeader("Contact", $"<sip:1001@127.0.0.1:{port}>");
        request.SetHeader("Expires", "600");
        return request;
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private sealed class FakeController : ISipGatewayCallController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall;
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public void RaiseIncoming(IncomingCallEventArgs incoming) => IncomingCall?.Invoke(this, incoming);
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task HangupAsync(string? slotId, CancellationToken ct) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }

    private sealed class DialingController : ISipGatewayCallController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public string? DialedNumber { get; private set; }
        public int HangupCount { get; private set; }
        public string? HungUpSlot { get; private set; }
        public TaskCompletionSource HungUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondHangup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdHangup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? DialRelease { get; set; }

        public async Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct)
        {
            DialedNumber = number;
            if (DialRelease != null) await DialRelease.Task;
            var call = new CallInfo("carrier-outbound", number, CallState.Active,
                DateTime.UtcNow, DateTime.UtcNow, null, "Cellular/UAC", null);
            return new SipGatewayCarrierCall(call, "slot-a", new SilentMedia());
        }

        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task HangupAsync(string? slotId, CancellationToken ct)
        {
            HangupCount++;
            HungUpSlot = slotId;
            HungUp.TrySetResult();
            if (HangupCount >= 2) SecondHangup.TrySetResult();
            if (HangupCount >= 3) ThirdHangup.TrySetResult();
            return Task.CompletedTask;
        }

        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }

    private sealed class SilentMedia : ICallPcmMedia
    {
        public event Action<short[]>? RemotePcmReceived { add { } remove { } }
        public void SendExternalPcm(short[] samples) { }
    }
}
