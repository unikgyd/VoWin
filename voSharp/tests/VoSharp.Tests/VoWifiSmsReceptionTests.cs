using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using VoSharp.Kernel;
using VoSharp.Sip;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class VoWifiSmsReceptionTests
{
    // SMS-DELIVER, numeric sender, GSM7 "hello". Not an SMS-SUBMIT masquerading as MT data.
    internal static readonly byte[] Deliver = Convert.FromHexString("040B916831081005F000002090100000000005E8329BFD06");
    internal static SipMessage Incoming(bool compact = false, byte[]? payload = null, string callId = "mt@example.test")
    {
        payload ??= [1, 5, 0, 0, (byte)Deliver.Length, .. Deliver];
        var head = compact
            ? $"v: SIP/2.0/UDP 127.0.0.1:5099;branch=z9hG4bK1;rport\r\nf: <sip:gw@example.test>;tag=peer\r\nt: <sip:ue@example.test>\r\ni: {callId}\r\nc: application/vnd.3gpp.sms\r\nl: {payload.Length}\r\n"
            : $"Via: SIP/2.0/UDP 127.0.0.1:5099;branch=z9hG4bK1;rport\r\nFrom: <sip:gw@example.test>;tag=peer\r\nTo: <sip:ue@example.test>\r\nCall-ID: {callId}\r\nContent-Type: application/vnd.3gpp.sms\r\nContent-Length: {payload.Length}\r\n";
        return SipMessage.Parse([.. Encoding.ASCII.GetBytes("MESSAGE sip:ue@example.test SIP/2.0\r\n" + head +
            "P-Asserted-Identity: <sip:smsc@example.test>\r\nCSeq: 1 MESSAGE\r\n\r\n"), .. payload]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliverReachesKernelAndUsesSeparateRpAckTransaction(bool compact)
    {
        await using var kernel = new VoKernel();
        var received = new TaskCompletionSource<SmsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        kernel.SmsReceived += (_, e) => received.TrySetResult(e.Message);
        var responses = new List<SipMessage>();
        var reports = new List<SipMessage>();
        var incoming = Incoming(compact);
        Task Reply(SipMessage message) { responses.Add(message); return Task.CompletedTask; }
        Task<SipMessage> Report(SipMessage message)
        {
            Assert.Single(responses);
            reports.Add(message);
            return Task.FromResult(message.CreateResponse(200, "OK"));
        }
        await kernel.VoWifi.HandleIncomingSipMessageAsync(incoming, Reply, Report);
        Assert.Equal("hello", (await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).Text);
        Assert.Single(kernel.GetInbox());
        Assert.Null(responses[0].RawBody);
        Assert.Equal("0", responses[0].GetHeader("Content-Length"));
        Assert.NotNull(responses[0].GetHeader("Via"));
        var report = Assert.Single(reports);
        Assert.True(report.IsRequest);
        Assert.Equal("MESSAGE", report.Method);
        Assert.Equal("sip:smsc@example.test", report.RequestUri);
        Assert.Equal("<sip:smsc@example.test>", report.GetHeader("To"));
        Assert.StartsWith("<sip:ue@example.test>", report.GetHeader("From"));
        Assert.Equal(incoming.GetHeader("Call-ID"), report.GetHeader("In-Reply-To"));
        Assert.NotEqual(incoming.GetHeader("Call-ID"), report.GetHeader("Call-ID"));
        Assert.Equal(new byte[] { 2, 5 }, report.RawBody);
        // SIP retransmissions get a response without delivering another SMS/RP transaction.
        await kernel.VoWifi.HandleIncomingSipMessageAsync(incoming, Reply, Report);
        Assert.Equal(2, responses.Count);
        Assert.Single(reports);
        Assert.Single(kernel.GetInbox());
    }

    [Fact]
    public async Task InvalidTpduGetsOneSipResponseAndSeparateRpError()
    {
        using var manager = new VoWifiManager();
        var responses = new List<SipMessage>();
        SipMessage? report = null;
        await manager.HandleIncomingSipMessageAsync(Incoming(payload: [1, 0x42, 0, 0, 4, 0]),
            reply => { responses.Add(reply); return Task.CompletedTask; },
            message => { report = message; return Task.FromResult(message.CreateResponse(202, "Accepted")); });
        Assert.Equal(200, Assert.Single(responses).StatusCode);
        Assert.Null(responses[0].RawBody);
        Assert.Equal(new byte[] { 4, 0x42, 1, 95 }, report!.RawBody);
    }

    [Fact]
    public async Task NetworkRpAckDoesNotCauseAckLoop()
    {
        using var manager = new VoWifiManager();
        int replies = 0;
        await manager.HandleIncomingSipMessageAsync(Incoming(payload: [3, 5]),
            _ => { replies++; return Task.CompletedTask; },
            _ => throw new InvalidOperationException("RP-ACK must not be acknowledged with another RP-ACK"));
        Assert.Equal(1, replies);
    }

    [Theory]
    [InlineData("0D0A")]
    [InlineData("002D")]
    [InlineData("0D0A2D2D626F756E6461727978")]
    public void MultipartPreservesAllBinaryBytes(string hex)
    {
        var payload = Convert.FromHexString(hex);
        var message = Incoming();
        message.SetHeader("Content-Type", "multipart/mixed; boundary=boundary");
        message.RawBody = [.. Encoding.ASCII.GetBytes("--boundary\r\nContent-Type: application/vnd.3gpp.sms\r\nContent-Transfer-Encoding: binary\r\n\r\n"),
            .. payload, .. Encoding.ASCII.GetBytes("\r\n--boundary--\r\n")];
        Assert.Equal(payload, ImsSmsHandler.ExtractSmsPayload(message, out _));
    }

    [Fact]
    public void TransferEncodingAndFoldedMixedHeadersAreHandled()
    {
        var raw = "MESSAGE sip:ue@example.test SIP/2.0\r\nv: first\r\nVia: second\r\nc: application/vnd.3gpp.sms;\r\n\tcharset=binary\r\nl: 6\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n=0D=0Aignored";
        var message = SipMessage.Parse(raw);
        Assert.Equal(2, message.Headers["Via"].Count);
        Assert.Equal("application/vnd.3gpp.sms; charset=binary", message.GetHeader("c"));
        Assert.Equal(new byte[] { 13, 10 }, ImsSmsHandler.ExtractSmsPayload(message, out _));
        Assert.Throws<FormatException>(() => SipMessage.Parse(raw.Replace("l: 6", "l: 999")));
        message.SetHeader("Content-Transfer-Encoding", "base64");
        message.RawBody = Encoding.ASCII.GetBytes("***invalid***");
        Assert.Throws<FormatException>(() => ImsSmsHandler.ExtractSmsPayload(message, out _));
    }

    [Fact]
    public async Task UdpReplyGoesToRequestSourceInsteadOfRegistrationPort()
    {
        using var proxy = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var source = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, ((IPEndPoint)proxy.Client.LocalEndPoint!).Port, 0);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.IncomingRequestReceived += async (_, message) =>
        {
            try { await transport.SendAsync(ImsSmsHandler.BuildSipResponse(message, 200, "test")); completed.TrySetResult(); }
            catch (Exception ex) { completed.TrySetException(ex); }
        };
        var request = Incoming(true);
        request.SetHeader("Via", $"SIP/2.0/UDP {source.Client.LocalEndPoint};branch=z9hG4bK1;rport");
        await source.SendAsync(request.ToBytes(), new IPEndPoint(IPAddress.Loopback, transport.LocalEndPoint!.Port));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reply = SipMessage.Parse((await source.ReceiveAsync(timeout.Token)).Buffer);
        Assert.Equal(200, reply.StatusCode);
        Assert.Contains($"rport={((IPEndPoint)source.Client.LocalEndPoint!).Port}", reply.GetHeader("Via"));
        Assert.Equal(0, proxy.Available);
    }

    [Fact]
    public async Task UserSpaceReplyRetainsTheReceivedLocalAndRemotePorts()
    {
        var input = Channel.CreateUnbounded<SipDatagram>();
        var output = Channel.CreateUnbounded<SipDatagram>();
        var local = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 40000);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 41000);
        using var transport = new SipTransport();
        transport.ConnectDatagrams(local, remote, (packet, _) => { output.Writer.TryWrite(packet); return Task.CompletedTask; },
            ct => input.Reader.ReadAsync(ct).AsTask());
        transport.IncomingRequestReceived += async (_, req) => await transport.SendAsync(req.CreateResponse(200, "OK"));
        var request = Incoming();
        var packet = new SipDatagram(request.ToBytes(), new IPEndPoint(local.Address, 40001), new IPEndPoint(remote.Address, 41001));
        await input.Writer.WriteAsync(packet);
        var reply = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(packet.LocalEndPoint, reply.LocalEndPoint);
        Assert.Equal(packet.RemoteEndPoint, reply.RemoteEndPoint);
    }

    [Fact]
    public async Task TransactionsMatchCseqAndRetransmitLostRequests()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        using var transport = new SipTransport();
        int sends = 0;
        transport.ConnectCustom(bytes =>
        {
            int count = Interlocked.Increment(ref sends);
            var message = SipMessage.Parse(bytes);
            var response = message.CreateResponse(200, "OK");
            if (count == 1) response.SetHeader("CSeq", "999 MESSAGE");
            input.Writer.TryWrite(response.ToBytes());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());
        var final = await transport.SendAndReceiveFinalAsync(Incoming(), timeoutMs: 2500);
        Assert.Equal("1 MESSAGE", final.GetHeader("CSeq"));
        Assert.Equal(2, sends);
    }
}
