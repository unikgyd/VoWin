using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using VoSharp.Common.Events;
using VoSharp.Sip;
using VoSharp.Sim;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class VoWifiFlowHardeningTests
{
    [Fact]
    public void RtcpReceiverReport_TracksLossAndRemoteSource()
    {
        using var audio = new WindowsAudioDevice(8000);
        using var session = new RtpSession(audio);
        const uint remoteSsrc = 0x12345678;

        session.ProcessRtpPacket(CreateHeader(100, 1000, remoteSsrc));
        session.ProcessRtpPacket(CreateHeader(101, 1160, remoteSsrc));
        session.ProcessRtpPacket(CreateHeader(103, 1480, remoteSsrc));

        var report = Assert.IsType<byte[]>(session.CreateReceiverReport());
        Assert.Equal(32, report.Length);
        Assert.Equal(0x81, report[0]);
        Assert.Equal(201, report[1]);
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(2, 2)));
        Assert.Equal(remoteSsrc, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(8, 4)));
        Assert.Equal(64, report[12]); // one lost packet out of four expected
        Assert.Equal(1, (report[13] << 16) | (report[14] << 8) | report[15]);
        Assert.Equal((uint)103, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(16, 4)));
    }

    [Fact]
    public async Task SameDialogReInvite_IsAcceptedInsteadOfRejectedAsBusy()
    {
        var manager = new ImsCallManager(new AsyncEventBus());
        var voWifi = new VoWifiManager();
        var replies = new List<SipMessage>();
        Task Reply(SipMessage message)
        {
            replies.Add(message);
            return Task.CompletedTask;
        }

        var initial = CreateInvite("same-dialog", 1);
        await manager.HandleIncomingInviteAsync(initial, voWifi, Reply);
        await manager.AnswerAsync();
        var dialogTo = replies.Single(response => response.StatusCode == 200).GetHeader("To");
        replies.Clear();

        var refresh = CreateInvite("same-dialog", 2);
        refresh.SetHeader("To", dialogTo!);
        refresh.SetHeader("Session-Expires", "1800;refresher=uac");
        refresh.Body = string.Empty;
        refresh.SetHeader("Content-Length", "0");
        await manager.HandleIncomingInviteAsync(refresh, voWifi, Reply);

        Assert.Collection(replies,
            response => Assert.Equal(100, response.StatusCode),
            response =>
            {
                Assert.Equal(200, response.StatusCode);
                Assert.Equal("1800;refresher=uac", response.GetHeader("Session-Expires"));
            });
        Assert.Equal(CallState.Active, manager.State);

        replies.Clear();
        refresh.SetHeader("Via", "SIP/2.0/UDP 192.0.2.10:5060;branch=z9hG4bKstale");
        await manager.HandleIncomingInviteAsync(refresh, voWifi, Reply);
        Assert.Equal(500, Assert.Single(replies).StatusCode);
        manager.Dispose();
    }

    [Fact]
    public async Task EpdgDiscovery_UsesAospPriorityAddressPreferenceAndTemporaryExclusion()
    {
        var sim = SimIdentity.FromImsiAndIccid(
            "999123123456789", "8900000000000000001", mncLength: 3,
            homePlmns: ["999123", "00101"]);
        var options = new EpdgDiscoveryOptions(
            StaticAddresses: ["192.0.2.10", "2001:db8::10"],
            RegisteredPlmn: "310260",
            CellularLocationDomains: ["cell.example.test"],
            IsRoaming: true,
            IsEmergency: true,
            VisitedMcc: "310",
            AddressPreference: EpdgAddressPreference.Ipv6Preferred,
            DnsTimeout: TimeSpan.FromMilliseconds(10),
            PcoAddresses: ["198.51.100.20"],
            MethodPriority: [EpdgDiscoveryMethod.Pco, EpdgDiscoveryMethod.Static, EpdgDiscoveryMethod.Plmn]);

        var candidates = EpdgResolver.BuildCandidates(sim, options);
        Assert.Equal("sos.epdg.epc.mcc310.visited-country.pub.3gppnetwork.org", candidates[0].Host);
        Assert.Equal("VISITED_COUNTRY", candidates[1].Method);
        Assert.Equal("PCO", candidates[2].Method);
        Assert.Equal("STATIC", candidates[3].Method);
        Assert.Contains(candidates, candidate => candidate.Host == "sos.epdg.epc.mnc260.mcc310.pub.3gppnetwork.org");
        Assert.Equal(
            "tac-lb34.tac-hb12.tac.sos.epdg.epc.mnc260.mcc310.pub.3gppnetwork.org",
            EpdgResolver.BuildLteTaiDomain("310", "260", 0x1234, emergency: true));
        Assert.Equal(
            "tac-lb56.tac-mb34.tac-hb12.5gstac.epdg.epc.mnc260.mcc310.pub.3gppnetwork.org",
            EpdgResolver.BuildNrTaiDomain("310", "260", 0x123456));
        Assert.Equal(IPAddress.Parse("192.0.2.44"),
            EpdgResolver.ParsePcoAddress([0x13, 0x00, 0x62, 192, 0, 2, 44]));

        EpdgResolver.ReportConnectionSuccess();
        try
        {
            var resolutionOptions = options with
            {
                IsRoaming = false,
                PcoAddresses = ["192.0.2.10"],
                StaticAddresses = ["2001:db8::10"],
                MethodPriority = [EpdgDiscoveryMethod.Pco, EpdgDiscoveryMethod.Static]
            };
            var first = await EpdgResolver.ResolveAsync(sim, resolutionOptions);
            Assert.Equal(IPAddress.Parse("2001:db8::10"), first.IpAddresses[0]);
            Assert.Equal("ims.mnc123.mcc999.3gppnetwork.org", first.ImsDomain);
            Assert.Equal("STATIC", first.SelectionMethod);

            EpdgResolver.ReportConnectionFailure(IPAddress.Parse("2001:db8::10"));
            var second = await EpdgResolver.ResolveAsync(sim, resolutionOptions);
            Assert.DoesNotContain(IPAddress.Parse("2001:db8::10"), second.IpAddresses);
            Assert.Contains(IPAddress.Parse("192.0.2.10"), second.IpAddresses);
        }
        finally
        {
            EpdgResolver.ReportConnectionSuccess();
        }
    }

    [Fact]
    public async Task SipClientTransaction_RetransmitsIdenticalBytesAndPreparesOnce()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var sent = new ConcurrentQueue<byte[]>();
        var retransmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new SipTransport();
        var prepareCount = 0;
        transport.PreparingOutgoingRequest += message =>
        {
            Interlocked.Increment(ref prepareCount);
            message.SetHeader("X-Prepared", Guid.NewGuid().ToString("N"));
        };
        transport.ConnectCustom(bytes =>
        {
            sent.Enqueue(bytes.ToArray());
            if (sent.Count >= 2) retransmitted.TrySetResult();
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());

        var request = CreateRequest("MESSAGE", "client-retransmit", 1, "z9hG4bKclient");
        var operation = transport.SendAndReceiveFinalAsync(request, timeoutMs: 3000);
        await retransmitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var snapshots = sent.ToArray();
        Assert.True(snapshots.Length >= 2);
        Assert.Equal(snapshots[0], snapshots[1]);
        Assert.Equal(1, prepareCount);

        await input.Writer.WriteAsync(SipMessage.Parse(snapshots[0]).CreateResponse(200, "OK").ToBytes());
        Assert.Equal(200, (await operation).StatusCode);
    }

    [Fact]
    public async Task SipClientTransaction_IgnoresResponseWithWrongViaBranch()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var sent = Channel.CreateUnbounded<byte[]>();
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            sent.Writer.TryWrite(bytes);
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());

        var request = CreateRequest("MESSAGE", "branch-isolation", 1, "z9hG4bKexpected");
        var pending = transport.SendAndReceiveFinalAsync(request, timeoutMs: 2000);
        await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var wrong = request.CreateResponse(200, "OK");
        wrong.SetHeader("Via", "SIP/2.0/UDP 10.0.0.2:41000;branch=z9hG4bKother;rport");
        await input.Writer.WriteAsync(wrong.ToBytes());
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        await input.Writer.WriteAsync(request.CreateResponse(200, "OK").ToBytes());
        Assert.Equal(200, (await pending).StatusCode);
    }

    [Fact]
    public async Task SipClientTransaction_IgnoresResponseWithWrongViaSentBy()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var sent = Channel.CreateUnbounded<byte[]>();
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            sent.Writer.TryWrite(bytes);
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());

        var request = CreateRequest("MESSAGE", "sent-by-isolation", 1, "z9hG4bKexpected");
        var pending = transport.SendAndReceiveFinalAsync(request, timeoutMs: 2000);
        await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var wrong = request.CreateResponse(200, "OK");
        wrong.SetHeader("Via", "SIP/2.0/UDP 10.0.0.99:41000;branch=z9hG4bKexpected;rport");
        await input.Writer.WriteAsync(wrong.ToBytes());
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        var correct = request.CreateResponse(200, "OK");
        correct.SetHeader("Via", request.GetHeader("Via") + ";received=10.0.0.2;rport=41000");
        await input.Writer.WriteAsync(correct.ToBytes());
        Assert.Equal(200, (await pending).StatusCode);
    }

    [Fact]
    public void SipTransport_RejectsSecondConnectionAndConnectionAfterDispose()
    {
        var transport = new SipTransport();
        transport.ConnectCustom(_ => Task.CompletedTask, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<byte>();
        });
        Assert.Throws<InvalidOperationException>(() => transport.ConnectCustom(
            _ => Task.CompletedTask, _ => Task.FromResult(Array.Empty<byte>())));
        transport.Dispose();
        Assert.Throws<ObjectDisposedException>(() => transport.ConnectCustom(
            _ => Task.CompletedTask, _ => Task.FromResult(Array.Empty<byte>())));
    }

    [Fact]
    public async Task SipServerTransaction_ReplaysCachedResponseForDuplicateRequest()
    {
        var input = Channel.CreateUnbounded<SipDatagram>();
        var output = Channel.CreateUnbounded<SipDatagram>();
        var local = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 40000);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 41000);
        using var transport = new SipTransport();
        var deliveries = 0;
        transport.ConnectDatagrams(local, remote,
            (packet, _) => { output.Writer.TryWrite(packet); return Task.CompletedTask; },
            ct => input.Reader.ReadAsync(ct).AsTask());
        transport.IncomingRequestReceived += async (_, request) =>
        {
            Interlocked.Increment(ref deliveries);
            await transport.SendAsync(request.CreateResponse(200, "OK"));
        };

        var request = CreateRequest("OPTIONS", "server-retransmit", 1, "z9hG4bKserver");
        var packet = new SipDatagram(request.ToBytes(), local, remote);
        await input.Writer.WriteAsync(packet);
        var first = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await input.Writer.WriteAsync(packet);
        var second = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, deliveries);
        Assert.Equal(first.Payload, second.Payload);
        Assert.Equal(first.LocalEndPoint, second.LocalEndPoint);
        Assert.Equal(first.RemoteEndPoint, second.RemoteEndPoint);
    }

    [Fact]
    public async Task SipInviteFinalResponse_RetransmitsUntilDialogAckArrives()
    {
        var input = Channel.CreateUnbounded<SipDatagram>();
        var output = Channel.CreateUnbounded<SipDatagram>();
        var local = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 40000);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 41000);
        using var transport = new SipTransport();
        transport.ConnectDatagrams(local, remote,
            (packet, _) => { output.Writer.TryWrite(packet); return Task.CompletedTask; },
            ct => input.Reader.ReadAsync(ct).AsTask());
        transport.IncomingRequestReceived += async (_, request) =>
        {
            if (!request.Method.Equals("INVITE", StringComparison.OrdinalIgnoreCase)) return;
            var response = request.CreateResponse(200, "OK");
            response.SetHeader("To", "<sip:user@example.test>;tag=local");
            await transport.SendAsync(response);
        };

        var invite = CreateRequest("INVITE", "invite-final", 7, "z9hG4bKinvite");
        await input.Writer.WriteAsync(new SipDatagram(invite.ToBytes(), local, remote));
        var first = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var retransmission = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(first.Payload, retransmission.Payload);

        var final = SipMessage.Parse(first.Payload);
        var ack = CreateRequest("ACK", "invite-final", 7, "z9hG4bKack");
        ack.SetHeader("From", final.GetHeader("From")!);
        ack.SetHeader("To", final.GetHeader("To")!);
        await input.Writer.WriteAsync(new SipDatagram(ack.ToBytes(), local, remote));
        await Task.Delay(1100);
        Assert.False(output.Reader.TryRead(out _));
    }

    [Fact]
    public async Task SipInviteClient_ReplaysAckWhenFinalResponseIsRetransmitted()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var sent = Channel.CreateUnbounded<byte[]>();
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            sent.Writer.TryWrite(bytes.ToArray());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());

        var invite = CreateRequest("INVITE", "client-ack", 9, "z9hG4bKclientinvite");
        var pending = transport.SendAndReceiveFinalAsync(invite, timeoutMs: 2000);
        await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var final = invite.CreateResponse(200, "OK");
        final.SetHeader("To", "<sip:user@example.test>;tag=remote-dialog");
        await input.Writer.WriteAsync(final.ToBytes());
        await pending;

        var ack = CreateRequest("ACK", "client-ack", 9, "z9hG4bKclientack");
        ack.SetHeader("From", final.GetHeader("From")!);
        ack.SetHeader("To", final.GetHeader("To")!);
        await transport.SendAsync(ack);
        var firstAck = await ReadAckAsync();

        await input.Writer.WriteAsync(final.ToBytes());
        var replayedAck = await ReadAckAsync();
        Assert.Equal(firstAck, replayedAck);

        async Task<byte[]> ReadAckAsync()
        {
            while (true)
            {
                var bytes = await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                var request = SipMessage.Parse(bytes);
                if (request.Method == "ACK") return bytes;
                // A timer may already have queued an INVITE retransmission
                // before the final response reached the receive pump.
                Assert.Equal("INVITE", request.Method);
            }
        }
    }

    [Fact]
    public async Task IncomingOffer_PreservesDynamicAmrPayloadAndOctetAlignment()
    {
        var manager = new ImsCallManager();
        var voWifi = new VoWifiManager();
        var replies = new List<SipMessage>();
        var invite = CreateInvite("dynamic-amr", 1);
        invite.Body = "v=0\r\no=- 1 1 IN IP4 192.0.2.10\r\ns=test\r\n" +
                      "c=IN IP4 192.0.2.10\r\nt=0 0\r\n" +
                      "m=audio 4000 RTP/AVP 96\r\n" +
                      "a=rtpmap:96 AMR/8000/1\r\na=fmtp:96 octet-align=1; mode-set=7\r\n";
        invite.SetHeader("Content-Length", invite.Body.Length.ToString());

        await manager.HandleIncomingInviteAsync(invite, voWifi, response =>
        {
            replies.Add(response);
            return Task.CompletedTask;
        });
        await manager.AnswerAsync();

        var answer = replies.Single(response => response.StatusCode == 200);
        Assert.Contains("m=audio ", answer.Body);
        Assert.Contains("RTP/AVP 96", answer.Body);
        Assert.Contains("a=fmtp:96", answer.Body);
        Assert.Contains("octet-align=1", answer.Body);
        manager.Dispose();
    }

    private static byte[] CreateHeader(ushort sequence, uint timestamp, uint ssrc)
    {
        var packet = new byte[12];
        packet[0] = 0x80;
        packet[1] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), ssrc);
        return packet;
    }

    private static SipMessage CreateInvite(string callId, int cseq)
    {
        const string sdp = "v=0\r\no=- 1 1 IN IP4 192.0.2.10\r\ns=test\r\nc=IN IP4 192.0.2.10\r\nt=0 0\r\nm=audio 4000 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        var invite = new SipMessage
        {
            IsRequest = true,
            Method = "INVITE",
            RequestUri = "sip:user@example.test",
            SipVersion = "SIP/2.0",
            Body = sdp
        };
        invite.SetHeader("Via", "SIP/2.0/UDP 192.0.2.10:5060;branch=z9hG4bKtest");
        invite.SetHeader("From", "<sip:caller@example.test>;tag=remote");
        invite.SetHeader("To", "<sip:user@example.test>");
        invite.SetHeader("Call-ID", callId);
        invite.SetHeader("CSeq", $"{cseq} INVITE");
        invite.SetHeader("Contact", "<sip:caller@192.0.2.10:5060>");
        invite.SetHeader("Content-Type", "application/sdp");
        invite.SetHeader("Content-Length", sdp.Length.ToString());
        return invite;
    }

    private static SipMessage CreateRequest(string method, string callId, int cseq, string branch)
    {
        var request = new SipMessage
        {
            IsRequest = true,
            Method = method,
            RequestUri = "sip:user@example.test",
            SipVersion = "SIP/2.0"
        };
        request.SetHeader("Via", $"SIP/2.0/UDP 10.0.0.2:41000;branch={branch};rport");
        request.SetHeader("From", "<sip:caller@example.test>;tag=remote");
        request.SetHeader("To", "<sip:user@example.test>");
        request.SetHeader("Call-ID", callId);
        request.SetHeader("CSeq", $"{cseq} {method}");
        request.SetHeader("Content-Length", "0");
        return request;
    }
}
