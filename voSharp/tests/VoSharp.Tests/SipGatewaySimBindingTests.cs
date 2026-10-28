using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;

namespace VoSharp.Tests;

/// <summary>Only synthetic SIM tokens, memory admission and loopback SIP. No device transport exists.</summary>
[Collection("SIP loopback sockets")]
public sealed class SipGatewaySimBindingTests
{
    private const string Realm = "sim-binding.test", User = "alice", Password = "synthetic-alice-password";
    private const string LineA = "synthetic-line-a", LineB = "synthetic-line-b", Number = "+15551234567";
    private static readonly string BindingA = new('a', 64), BindingB = new('b', 64);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(4);
    private static UdpClient Socket() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static IPEndPoint Endpoint(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;
    private static async Task<SipMessage> Receive(UdpClient socket) =>
        SipMessage.Parse((await socket.ReceiveAsync().WaitAsync(Deadline)).Buffer);
    private static Task Send(UdpClient socket, SipMessage request, IPEndPoint remote) => socket.SendAsync(request.ToBytes(), remote).AsTask();
    private static SipGatewayOptions Options(Journal? journal = null)
    {
        using var reservation = Socket();
        return new() { BindAddress = IPAddress.Loopback, SipPort = Endpoint(reservation).Port, Realm = Realm,
            Accounts = new() { [User] = Password }, MessagesEnabled = true, ForwardIncomingSms = true,
            AllowOutgoingSms = true, RequireConfiguredLines = true, DefaultOutboundLineId = LineB,
            SmsOperationStore = journal ?? new Journal(), LineRoutes = new()
            {
                [LineA] = new() { AllowOutgoingSms = true, ForwardIncomingSms = true, SmsTarget = User },
                [LineB] = new() { AllowOutgoingSms = true, ForwardIncomingSms = true, SmsTarget = User },
            } };
    }
    private static string BoundAlias(string line, string binding, string number = Number) =>
        "sms2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line.ToLowerInvariant())))[..12].ToLowerInvariant() + "-" +
        Convert.ToBase64String(Convert.FromHexString(binding)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "-" + number;
    private static SipMessage Request(string method, string target)
    {
        var request = new SipMessage { IsRequest = true, Method = method, RequestUri = $"sip:{target}@{Realm}" };
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:15060;branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("From", $"<sip:{User}@{Realm}>;tag=synthetic-source");
        request.SetHeader("To", $"<sip:{target}@{Realm}>");
        request.SetHeader("Call-ID", "synthetic-" + Guid.NewGuid().ToString("N"));
        request.SetHeader("CSeq", $"1 {method}");
        if (method == "MESSAGE")
        {
            request.SetHeader("Content-Type", "text/plain;charset=UTF-8");
            request.RawBody = Encoding.UTF8.GetBytes("synthetic SIM-bound reply");
        }
        return request;
    }
    private static async Task<SipMessage> Authenticated(UdpClient phone, SipMessage request, IPEndPoint remote)
    {
        request.Headers.Remove("Authorization"); request.Headers.Remove("Proxy-Authorization");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{Endpoint(phone).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await Send(phone, request, remote);
        var challenge = await Receive(phone);
        Assert.Contains(challenge.StatusCode, new[] { 401, 407 });
        var auth = SipPeerDigest.Create(challenge.GetHeader(challenge.StatusCode == 401 ? "WWW-Authenticate" : "Proxy-Authenticate")!, request,
            new(remote, User, new SipPeerInboundCredentials(User, Password),
                new SipPeerOutboundCredentials(User, Password, Realm) { AllowedAlgorithms = ["MD5"] }));
        Assert.NotNull(auth);
        request.SetHeader(challenge.StatusCode == 401 ? "Authorization" : "Proxy-Authorization", auth);
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{Endpoint(phone).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await Send(phone, request, remote);
        return await Receive(phone);
    }
    private static async Task Register(UdpClient phone, IPEndPoint remote)
    {
        var register = Request("REGISTER", User);
        register.SetHeader("Contact", $"<sip:{User}@127.0.0.1:{Endpoint(phone).Port}>");
        register.SetHeader("Expires", "300");
        Assert.Equal(200, (await Authenticated(phone, register, remote)).StatusCode);
    }
    private static void NoFallback(Controller controller)
    {
        Assert.Equal(0, controller.UnboundCalls);
        Assert.Equal(0, controller.UnboundIdempotentCalls);
    }

    [Fact]
    public async Task OriginalSimReplyUsesOnlyBoundTransportAndStablePaidOperation()
    {
        var controller = new Controller(); controller.SetBinding(LineA, BindingA); controller.SetBinding(LineB, BindingB);
        var journal = new Journal();
        await using var gateway = new SipGateway(Options(journal), controller); await gateway.StartAsync();
        using var phone = Socket(); var request = Request("MESSAGE", BoundAlias(LineA, BindingA));
        var callId = request.GetHeader("Call-ID");
        Assert.Equal(202, (await Authenticated(phone, request, gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal((Number, LineA, BindingA), controller.LastBound);
        Assert.Equal(1, controller.BoundCalls); Assert.Equal(1, controller.ActualSubmissions); NoFallback(controller);
        // A new authentication branch and another network-domain spelling do not make a new paid operation.
        request.RequestUri = $"sip:{BoundAlias(LineA, BindingA)}@other-vpn.invalid";
        Assert.Equal(202, (await Authenticated(phone, request, gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal(callId, request.GetHeader("Call-ID")); Assert.Equal("1 MESSAGE", request.GetHeader("CSeq"));
        Assert.Equal(SipGateway.SmsOperationId(Realm, User, callId!, 1), Assert.Single(journal.Operations));
        Assert.Equal(1, controller.BoundCalls); NoFallback(controller);
    }

    [Fact]
    public async Task OrdinaryNumberAlsoCapturesAndUsesTheSelectedSimBinding()
    {
        var controller = new Controller(); controller.SetBinding(LineB, BindingB);
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync(); using var phone = Socket();
        Assert.Equal(202, (await Authenticated(phone, Request("MESSAGE", Number), gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal((Number, LineB, BindingB), controller.LastBound);
        Assert.Equal(1, controller.BoundCalls); NoFallback(controller);
    }

    [Fact]
    public async Task BoundTransportCannotBorrowLegacyIdempotentAdmissionWhenTheStoreIsMissing()
    {
        var controller = new Controller(); controller.SetBinding(LineA, BindingA);
        await using var gateway = new SipGateway(Options() with { SmsOperationStore = null }, controller);
        await gateway.StartAsync(); using var phone = Socket();
        Assert.Equal(503, (await Authenticated(phone, Request("MESSAGE", BoundAlias(LineA, BindingA)), gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal(0, controller.BoundCalls); Assert.Equal(0, controller.ActualSubmissions); NoFallback(controller);
    }

    [Fact]
    public async Task OrdinaryNumberWithoutTheSelectedSimBindingCannotUseUnboundTransport()
    {
        var controller = new Controller(); controller.SetBinding(LineB, null);
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync(); using var phone = Socket();
        Assert.Equal(403, (await Authenticated(phone, Request("MESSAGE", Number), gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal(0, controller.BoundCalls); Assert.Equal(0, controller.ActualSubmissions); NoFallback(controller);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("missing")]
    public async Task OriginalAliasRejectsChangedOrUnknownCurrentSimBeforeAnyTransport(string state)
    {
        var controller = new Controller(); controller.SetBinding(LineA, state == "replacement" ? BindingB : null);
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync(); using var phone = Socket();
        Assert.Equal(403, (await Authenticated(phone, Request("MESSAGE", BoundAlias(LineA, BindingA)), gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal(0, controller.BoundCalls); Assert.Equal(0, controller.ActualSubmissions); NoFallback(controller);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("read-only")]
    public async Task UnboundHistoricalAndReadOnlyAliasesNeverDowngradeToDefaultLine(string kind)
    {
        var controller = new Controller(); controller.SetBinding(LineA, BindingA); controller.SetBinding(LineB, BindingB);
        var target = kind == "legacy" ? SipGateway.SmsReplyUser(LineA, Number) : "sms-ro-" + new string('c', 64);
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync(); using var phone = Socket();
        Assert.Equal(403, (await Authenticated(phone, Request("MESSAGE", target), gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal(0, controller.BoundCalls); NoFallback(controller);
    }

    [Fact]
    public async Task SameSimOnAnotherLineCannotReplaceTheOriginalLineOrOverrideItsHeader()
    {
        var controller = new Controller(); controller.SetBinding(LineA, BindingA); controller.SetBinding(LineB, BindingA);
        var options = Options(); options.LineRoutes.Remove(LineA);
        await using (var missingOriginal = new SipGateway(options, controller))
        {
            await missingOriginal.StartAsync(); using var phone = Socket();
            Assert.Equal(403, (await Authenticated(phone, Request("MESSAGE", BoundAlias(LineA, BindingA)), missingOriginal.Status.LocalEndPoint!)).StatusCode);
        }
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync(); using var other = Socket();
        var conflict = Request("MESSAGE", BoundAlias(LineA, BindingA)); conflict.SetHeader("X-VoWin-Line", LineB);
        Assert.Equal(403, (await Authenticated(other, conflict, gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal(0, controller.BoundCalls); NoFallback(controller);
    }

    [Fact]
    public async Task IncomingCachedOldSessionBindingIsNotRewrittenFromTheReplacementSim()
    {
        var controller = new Controller(); controller.SetBinding(LineA, BindingB);
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync(); using var phone = Socket();
        await Register(phone, gateway.Status.LocalEndPoint!);
        controller.Incoming(Number, LineA, BindingA);
        var forwarded = await Receive(phone); Assert.Equal("MESSAGE", forwarded.Method);
        Assert.Contains("sip:" + BoundAlias(LineA, BindingA) + "@", forwarded.GetHeader("From"));
        Assert.DoesNotContain(BindingB, forwarded.GetHeader("From")); Assert.Equal(0, controller.BindingQueries);
        await Send(phone, forwarded.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        Assert.Equal(403, (await Authenticated(phone, Request("MESSAGE", BoundAlias(LineA, BindingA)), gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal(0, controller.BoundCalls); NoFallback(controller);
    }

    [Theory]
    [InlineData("+15551234567", false)]
    [InlineData("12345", true)]
    [InlineData("SYNTHETIC-BANK", true)]
    public async Task IncomingWithoutTrustedOriginalSimOrFullPhoneNumberOnlyExposesReadOnlyIdentity(string sender, bool hasBinding)
    {
        var controller = new Controller(); controller.SetBinding(LineA, BindingB);
        await using var gateway = new SipGateway(Options(), controller); await gateway.StartAsync(); using var phone = Socket();
        await Register(phone, gateway.Status.LocalEndPoint!);
        controller.Incoming(sender, LineA, hasBinding ? BindingA : null);
        var forwarded = await Receive(phone); Assert.Equal("MESSAGE", forwarded.Method);
        var identity = Regex.Match(forwarded.GetHeader("From") ?? "", @"<sip:([^@>]+)@").Groups[1].Value;
        Assert.StartsWith("sms-ro-", identity); Assert.Equal(0, controller.BindingQueries);
        await Send(phone, forwarded.CreateResponse(200, "OK"), gateway.Status.LocalEndPoint!);
        Assert.Equal(403, (await Authenticated(phone, Request("MESSAGE", identity), gateway.Status.LocalEndPoint!)).StatusCode);
        Assert.Equal(0, controller.BoundCalls); NoFallback(controller);
    }

    [Fact]
    public async Task SimChangesInsideBoundSendRemainUnknownAcrossRestartWithoutUnboundRetry()
    {
        var journal = new Journal(); var options = Options(journal);
        var controller = new Controller(); controller.SetBinding(LineA, BindingA);
        controller.BeforeBoundSend = () => controller.SetBinding(LineA, BindingB);
        using var phone = Socket(); var request = Request("MESSAGE", BoundAlias(LineA, BindingA));
        var callId = request.GetHeader("Call-ID");
        await using (var first = new SipGateway(options, controller))
        {
            await first.StartAsync();
            var result = await Authenticated(phone, request, first.Status.LocalEndPoint!);
            Assert.Equal(503, result.StatusCode); Assert.Contains("Unknown", result.ReasonPhrase);
            Assert.Equal(SipGateway.SmsOperationId(Realm, User, callId!, 1), Assert.Single(journal.Operations));
            Assert.Equal(1, controller.BoundCalls); Assert.Equal(0, controller.ActualSubmissions); NoFallback(controller);
        }
        var replacement = new Controller(); replacement.SetBinding(LineA, BindingA);
        await using var next = new SipGateway(options, replacement); await next.StartAsync();
        var replay = await Authenticated(phone, request, next.Status.LocalEndPoint!);
        Assert.Equal(503, replay.StatusCode); Assert.Contains("Do Not Retry", replay.ReasonPhrase);
        Assert.Equal(callId, request.GetHeader("Call-ID")); Assert.Equal("1 MESSAGE", request.GetHeader("CSeq"));
        Assert.Equal(0, replacement.BoundCalls); Assert.Equal(0, replacement.ActualSubmissions); NoFallback(replacement);
    }

    private sealed class Journal : ISipSmsOperationStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (string Fingerprint, SipSmsOperationOutcome Outcome, string Token)> _entries = new();
        public string[] Operations { get { lock (_gate) return _entries.Keys.ToArray(); } }
        public Task<SipSmsOperationReservation> ReserveAsync(string id, string fingerprint, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_entries.TryGetValue(id, out var old)) return Task.FromResult(new SipSmsOperationReservation(false, old.Fingerprint == fingerprint, old.Outcome));
                var token = Guid.NewGuid().ToString("N"); _entries.Add(id, (fingerprint, SipSmsOperationOutcome.Unknown, token));
                return Task.FromResult(new SipSmsOperationReservation(true, true, SipSmsOperationOutcome.Unknown, token));
            }
        }
        public Task CompleteAsync(string id, string fingerprint, string token, SipSmsOperationOutcome outcome, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Assert.Equal(fingerprint, _entries[id].Fingerprint); Assert.Equal(token, _entries[id].Token);
                _entries[id] = (fingerprint, outcome, token);
            }
            return Task.CompletedTask;
        }
    }
    private sealed class Controller : ISipGatewayCallController, ISipGatewayIdempotentMessageController, ISipGatewaySimBoundMessageController
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string?> _bindings = new(StringComparer.OrdinalIgnoreCase);
        private (string Number, string Line, string Binding) _lastBound;
        private int _bound, _unbound, _unboundIdempotent, _submitted, _queries;
        public int BoundCalls => Volatile.Read(ref _bound);
        public int UnboundCalls => Volatile.Read(ref _unbound);
        public int UnboundIdempotentCalls => Volatile.Read(ref _unboundIdempotent);
        public int ActualSubmissions => Volatile.Read(ref _submitted);
        public int BindingQueries => Volatile.Read(ref _queries);
        public (string Number, string Line, string Binding) LastBound { get { lock (_gate) return _lastBound; } }
        public Action? BeforeBoundSend;
        public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public event EventHandler<SmsReceivedEventArgs>? SmsReceived;
        public void SetBinding(string line, string? binding) { lock (_gate) _bindings[line] = binding; }
        public string? GetSmsSimBinding(string slotId)
        {
            Interlocked.Increment(ref _queries); lock (_gate) return _bindings.GetValueOrDefault(slotId);
        }
        public void Incoming(string sender, string line, string? binding) => SmsReceived?.Invoke(this,
            new(new SmsMessage(1, SmsStatus.Unread, sender, "synthetic incoming SMS", DateTime.UtcNow, Id: Guid.NewGuid().ToString("N")), line, binding));
        private static SmsSubmitResult Accepted(string number, string text) => new(number, text, SmsEncoding.Ucs2, null,
            1, 1, 1, true, "accepted", [], DateTime.UtcNow);
        public Task<SmsSubmitResult> SendSmsAsync(string number, string text, string? slotId, CancellationToken ct)
        { Interlocked.Increment(ref _unbound); return Task.FromResult(Accepted(number, text)); }
        public Task<SmsSubmitResult> SendSmsOnceAsync(string operationId, string number, string text, string? slotId, CancellationToken ct)
        { Interlocked.Increment(ref _unboundIdempotent); return Task.FromResult(Accepted(number, text)); }
        public Task<SmsSubmitResult> SendSmsForSimAsync(string number, string text, string slotId, string expectedBinding, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _bound);
            BeforeBoundSend?.Invoke();
            lock (_gate)
            {
                if (_bindings.GetValueOrDefault(slotId) != expectedBinding)
                    throw new IOException("Synthetic SIM changed inside the bound-send boundary; no transport used.");
                _lastBound = (number, slotId, expectedBinding);
                Interlocked.Increment(ref _submitted);
            }
            return Task.FromResult(Accepted(number, text));
        }
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct) => throw new NotSupportedException("No carrier backend exists.");
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) => throw new NotSupportedException("No carrier backend exists.");
        public Task HangupAsync(string? slotId, CancellationToken ct) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
