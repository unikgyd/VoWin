using System.Text;
using VoSharp.Kernel.SipGateway;

namespace VoSharp.Tests;

public sealed partial class SipServerFeatureTests
{
    // A shared deterministic journal fake verifies gateway wiring. The desktop
    // suite separately closes/reopens the actual SQLite store and races writers.
    private sealed class OperationStore : ISipSmsOperationStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (string Fingerprint, SipSmsOperationOutcome Outcome, string Token)> _entries = new();
        public bool FailReserve, FailComplete;
        public Task<SipSmsOperationReservation> ReserveAsync(string id, string fingerprint, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (FailReserve) throw new IOException("synthetic storage failure");
                if (_entries.TryGetValue(id, out var old)) return Task.FromResult(new SipSmsOperationReservation(false, old.Fingerprint == fingerprint, old.Outcome));
                var token = Guid.NewGuid().ToString("N");
                _entries.Add(id, (fingerprint, SipSmsOperationOutcome.Unknown, token));
                return Task.FromResult(new SipSmsOperationReservation(true, true, SipSmsOperationOutcome.Unknown, token));
            }
        }
        public Task CompleteAsync(string id, string fingerprint, string token, SipSmsOperationOutcome outcome, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (FailComplete) throw new IOException("synthetic completion failure");
                Assert.Equal(fingerprint, _entries[id].Fingerprint);
                Assert.Equal(token, _entries[id].Token);
                _entries[id] = (fingerprint, outcome, token);
            }
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("accepted", 202)]
    [InlineData("submitted_parts_unreported", 202)]
    [InlineData("unknown", 503)]
    [InlineData("failed", 503)]
    public async Task DurableOutcomeSurvivesListenerRecreationWithoutAnotherSubmission(string status, int code)
    {
        var journal = new OperationStore(); var options = Options() with { SmsOperationStore = journal };
        var firstController = new Controller { AggregateStatus = status == "accepted" ? null : status }; var request = Text("+15551234567");
        using var phone = Socket();
        await using (var first = new SipGateway(options, firstController))
        {
            await first.StartAsync();
            await Authenticate(phone, request, "alice", first.Status.LocalEndPoint!);
            Assert.Equal(code, (await Receive(phone)).StatusCode);
        }
        var nextController = new Controller(); var duplicateEvents = 0;
        await using var next = new SipGateway(options, nextController);
        next.SmsSubmitted += (_, _) => Interlocked.Increment(ref duplicateEvents);
        await next.StartAsync();
        request.RequestUri = "sip:+15551234567@other-vpn.invalid";
        request.SetHeader("CSeq", "0001 MESSAGE");
        await Authenticate(phone, request, "alice", next.Status.LocalEndPoint!);
        Assert.Equal(code, (await Receive(phone)).StatusCode);
        Assert.Equal(1, firstController.SmsCount); Assert.Equal(0, nextController.SmsCount);
        Assert.Equal(0, duplicateEvents);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("number")]
    [InlineData("resolved-default-line")]
    public async Task ChangedPaidDestinationAfterRestartDoesNotReuseAdmission(string change)
    {
        var journal = new OperationStore(); var options = Options() with { SmsOperationStore = journal };
        var request = Text("+15551234567"); using var phone = Socket();
        await using (var first = new SipGateway(options, new Controller()))
        {
            await first.StartAsync(); await Authenticate(phone, request, "alice", first.Status.LocalEndPoint!);
            Assert.Equal(202, (await Receive(phone)).StatusCode);
        }
        if (change == "body") request.RawBody = Encoding.UTF8.GetBytes("different body");
        if (change == "number") request.RequestUri = "sip:+15551234568@lab.local";
        if (change == "resolved-default-line")
        {
            options = options with { DefaultOutboundLineId = "modem-b" };
            options.LineRoutes["modem-b"] = new() { AllowOutgoingSms = true };
        }
        var controller = new Controller(); await using var next = new SipGateway(options, controller);
        await next.StartAsync(); await Authenticate(phone, request, "alice", next.Status.LocalEndPoint!);
        Assert.Equal(400, (await Receive(phone)).StatusCode); Assert.Equal(0, controller.SmsCount);
    }

    [Fact]
    public async Task ReservationFailureCannotTouchTransport()
    {
        var controller = new Controller();
        await using var gateway = new SipGateway(Options() with { SmsOperationStore = new OperationStore { FailReserve = true } }, controller);
        await gateway.StartAsync(); using var phone = Socket();
        await Authenticate(phone, Text("+15551234567"), "alice", gateway.Status.LocalEndPoint!);
        Assert.Equal(503, (await Receive(phone)).StatusCode); Assert.Equal(0, controller.SmsCount);
    }

    [Theory]
    [InlineData("completion-write")]
    [InlineData("send-throws")]
    public async Task UncertainAttemptRemainsReservedAfterListenerRestart(string failure)
    {
        var journal = new OperationStore { FailComplete = failure == "completion-write" };
        var options = Options() with { SmsOperationStore = journal };
        var request = Text("+15551234567"); using var phone = Socket();
        var controller = new Controller { BeforeSend = failure == "send-throws" ? _ => throw new IOException("synthetic transport loss") : null };
        await using (var first = new SipGateway(options, controller))
        {
            await first.StartAsync(); await Authenticate(phone, request, "alice", first.Status.LocalEndPoint!);
            var result = await Receive(phone); Assert.Equal(503, result.StatusCode); Assert.Contains("Unknown", result.ReasonPhrase);
        }
        var nextController = new Controller(); await using var next = new SipGateway(options, nextController);
        await next.StartAsync(); await Authenticate(phone, request, "alice", next.Status.LocalEndPoint!);
        var replay = await Receive(phone); Assert.Equal(503, replay.StatusCode); Assert.Contains("Do Not Retry", replay.ReasonPhrase);
        Assert.Equal(1, controller.SmsCount); Assert.Equal(0, nextController.SmsCount);
    }

    [Fact]
    public async Task TwoListenersSharingTheStoreCannotSubmitTheSamePendingOperation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new OperationStore(); var options = Options() with { SmsOperationStore = journal };
        var controller = new Controller { BeforeSend = _ => { entered.TrySetResult(); return release.Task; } };
        var other = new Controller();
        await using var first = new SipGateway(options, controller);
        await using var second = new SipGateway(options with { SipPort = Port() }, other);
        await first.StartAsync(); await second.StartAsync(); using var a = Socket(); using var b = Socket();
        var request = Text("+15551234567");
        await Authenticate(a, request, "alice", first.Status.LocalEndPoint!);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var retry = Text("+15551234567"); retry.SetHeader("Call-ID", request.GetHeader("Call-ID")!);
            await Authenticate(b, retry, "alice", second.Status.LocalEndPoint!);
            Assert.Equal(503, (await Receive(b)).StatusCode); Assert.Equal(0, other.SmsCount);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(202, (await Receive(a)).StatusCode); Assert.Equal(1, controller.SmsCount);
    }
}
