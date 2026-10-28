using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Channels;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class WindowsImsGatewayPolicyTests
{
    private sealed class Rig : IDisposable
    {
        public SipTransport Transport { get; }
        public SipRegisterSession Session { get; }
        public Channel<SipDatagram> Sent { get; } = Channel.CreateUnbounded<SipDatagram>();
        private readonly Channel<SipDatagram> _received = Channel.CreateUnbounded<SipDatagram>();
        private readonly IPEndPoint _local = new(IPAddress.Loopback, 15060);
        private readonly IPEndPoint _remote = new(IPAddress.Loopback, 15061);
        public Func<SipDatagram, CancellationToken, Task>? Send { get; set; }

        public Rig(int timeout = 150)
        {
            Transport = new SipTransport(timeout);
            Transport.ConnectDatagrams(_local, _remote, async (packet, ct) =>
            {
                await Sent.Writer.WriteAsync(packet, ct);
                if (Send is { } send) await send(packet, ct);
            }, ct => _received.Reader.ReadAsync(ct).AsTask(), timeoutMs: timeout);
            Session = new(Transport, WindowsImsGatewayRedTests.Profile(Transport));
        }

        public async Task<SipMessage> Request() => SipMessage.Parse(
            (await Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Payload);

        public Task Reply(SipMessage request, int code, Action<SipMessage>? configure = null)
        {
            var response = request.CreateResponse(code, "Synthetic");
            configure?.Invoke(response);
            return _received.Writer.WriteAsync(new(response.ToBytes(), _local, _remote)).AsTask();
        }

        public static Task<(byte[] Res, byte[] Ck, byte[] Ik)> NoAka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("Unexpected AKA.");

        public void Dispose() { Session.Dispose(); Transport.Dispose(); }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(403)]
    public async Task ActualResponseStopsSilenceProof(int code)
    {
        using var rig = new Rig();
        var registration = rig.Session.RegisterAsync(Rig.NoAka);
        await rig.Reply(await rig.Request(), code);
        var failure = await Record.ExceptionAsync(async () => await registration);
        Assert.NotNull(failure);
        Assert.True(rig.Session.HasObservedRegistrationResponse);
        Assert.False(VoWifiManager.CanFailOverImsTransport(VoWifiState.ImsRegistering, failure));
    }

    [Theory]
    [InlineData("call-id")]
    [InlineData("cseq")]
    [InlineData("branch")]
    [InlineData("sent-by")]
    public async Task UncorrelatedResponsesCannotBlockGenuineSilenceRetry(string mismatch)
    {
        using var rig = new Rig();
        var registration = rig.Session.RegisterAsync(Rig.NoAka);
        var request = await rig.Request();
        await rig.Reply(request, 100, response =>
        {
            switch (mismatch)
            {
                case "call-id": response.SetHeader("Call-ID", "foreign-call"); break;
                case "cseq": response.SetHeader("CSeq", "999 REGISTER"); break;
                case "branch": response.SetHeader("Via", response.GetHeader("Via")!.Replace("branch=", "branch=foreign-")); break;
                case "sent-by": response.SetHeader("Via", response.GetHeader("Via")!.Replace("127.0.0.1:15060", "127.0.0.2:15060")); break;
            }
        });
        var failure = await Record.ExceptionAsync(async () => await registration);
        Assert.IsType<SipRegistrationTransportException>(failure);
        Assert.False(rig.Session.HasObservedRegistrationResponse);
    }

    [Fact]
    public async Task AuthenticatedTimeoutCannotRequestAnotherGatewayOrReuseVector()
    {
        using var rig = new Rig();
        var calls = 0;
        byte[] res = [1, 2, 3, 4], ck = new byte[16], ik = new byte[16];
        Array.Fill(ck, (byte)1); Array.Fill(ik, (byte)2);
        var registration = rig.Session.RegisterAsync((_, _, _) =>
        {
            calls++;
            return Task.FromResult((res, ck, ik));
        });
        await rig.Reply(await rig.Request(), 401, response => response.SetHeader("WWW-Authenticate",
            "Digest realm=\"ims.test\", nonce=\"" + Convert.ToBase64String(new byte[32]) + "\", algorithm=AKAv1-MD5"));
        var authenticated = await rig.Request();
        Assert.Contains("response=", authenticated.GetHeader("Authorization"));
        var failure = await Record.ExceptionAsync(async () => await registration);
        Assert.NotNull(failure);
        Assert.False(VoWifiManager.CanFailOverImsTransport(VoWifiState.ImsRegistering, failure));
        Assert.Equal(1, calls);
        Assert.All(res.Concat(ck).Concat(ik), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ActualSenderNetworkFaultHasProofButPreparationFaultDoesNot()
    {
        foreach (var preparing in new[] { false, true })
        {
            using var rig = new Rig();
            if (preparing) rig.Transport.PreparingOutgoingRequest += _ => throw new SocketException((int)SocketError.ConnectionReset);
            else rig.Send = (_, _) => throw new SocketException((int)SocketError.ConnectionReset);
            var failure = await Record.ExceptionAsync(() => rig.Session.RegisterAsync(Rig.NoAka));
            Assert.NotNull(failure);
            Assert.Equal(!preparing, VoWifiManager.CanFailOverImsTransport(VoWifiState.ImsRegistering, failure));
            Assert.Equal(!preparing, rig.Sent.Reader.TryRead(out _));
        }
    }

    [Fact]
    public async Task MatchedResponseWinsAgainstSenderFaultEvenIfObserverFails()
    {
        using var rig = new Rig(1000);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Send = async (_, _) =>
        {
            await observed.Task;
            throw new SocketException((int)SocketError.ConnectionReset);
        };
        var request = ImsRegisterBuilder.BuildInitialRegister(WindowsImsGatewayRedTests.Profile(rig.Transport), "atomic-proof-test", 1);
        var exchange = rig.Transport.SendRegistrationAsync(request, _ =>
        {
            observed.TrySetResult();
            throw new IOException("Synthetic response-observer failure.");
        }, () => false, CancellationToken.None);
        await rig.Reply(await rig.Request(), 100);
        var failure = await Record.ExceptionAsync(async () => await exchange.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsType<SocketException>(failure);
        Assert.False(SipRegistrationTransportException.HasUnmixedProof(failure));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTransportDisposalNeverIssueProof(bool dispose)
    {
        using var rig = new Rig(1000);
        using var cancel = new CancellationTokenSource();
        var registration = rig.Session.RegisterAsync(Rig.NoAka, cancel.Token);
        await rig.Request();
        if (dispose) rig.Transport.Dispose(); else cancel.Cancel();
        var failure = await Record.ExceptionAsync(async () => await registration);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.False(SipRegistrationTransportException.HasUnmixedProof(failure));
    }

    [Fact]
    public async Task LegacyTimeoutApisRemainUnchanged()
    {
        using var rig = new Rig(30);
        var request = ImsRegisterBuilder.BuildInitialRegister(WindowsImsGatewayRedTests.Profile(rig.Transport), "legacy-timeout", 1);
        Assert.Null(await rig.Transport.SendAndReceiveAsync(request));
        await Assert.ThrowsAsync<TimeoutException>(() => rig.Transport.SendAndReceiveFinalAsync(request, timeoutMs: 30));
    }

    [Fact]
    public void MixedErrorsAndLocalSocketConfigurationCannotAuthorizeRetry()
    {
        var proof = new SipRegistrationTransportException(new TimeoutException());
        Assert.True(SipRegistrationTransportException.HasUnmixedProof(new AggregateException(proof, proof)));
        foreach (var fatal in new Exception[] { new IOException(), new OperationCanceledException(),
            new CryptographicException(), new FormatException(), new SocketException((int)SocketError.AddressAlreadyInUse) })
        {
            Assert.False(SipRegistrationTransportException.HasUnmixedProof(new AggregateException(proof, fatal)));
            Assert.Throws<ArgumentException>(() => new SipRegistrationTransportException(fatal));
        }
        Assert.False(VoWifiManager.CanFailOverImsTransport(VoWifiState.ConnectingIkev2, proof));
    }

    private static EpdgResolutionResult Discovery()
    {
        var candidate = new EpdgCandidate(Guid.NewGuid().ToString("N") + ".invalid", "test");
        var address = IPAddress.Parse("203.0.113.10");
        return new(candidate.Host, "ims.test", "subscriber@ims.test", "sip:subscriber@ims.test", [address],
            HomeMcc: "234", HomeMnc: "15", FailureScope: candidate.Host,
            Endpoints: [new(address, candidate, "234", "15")]);
    }

    private static string? RetryKey(VoWifiManager manager)
    {
        var endpoint = (EpdgResolvedEndpoint?)typeof(VoWifiManager).GetField("_retryGatewayEndpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager);
        return endpoint is null ? null : $"{endpoint.Candidate.Host}|{endpoint.Address}|{endpoint.HomeMcc}|{endpoint.HomeMnc}";
    }

    [Fact]
    public async Task ManagerCommitsRetryOnlyAfterCleanupAndScopesTheEndpoint()
    {
        using var manager = new VoWifiManager();
        using var other = new VoWifiManager();
        var discovery = Discovery();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handling = manager.CompleteFailedStartAsync(new SipRegistrationTransportException(new TimeoutException()),
            VoWifiState.ImsRegistering, discovery, () => cleanup.Task);
        Assert.Null(RetryKey(manager));
        Assert.False(handling.IsCompleted);
        cleanup.SetResult();
        try
        {
            await handling;
            Assert.Equal($"{discovery.Fqdn}|{discovery.IpAddresses[0]}|234|15", RetryKey(manager));
            Assert.Null(RetryKey(other));
            Assert.False(manager.AutomaticRecoveryPaused);
        }
        finally { EpdgResolver.ReportConnectionSuccess(discovery, discovery.IpAddresses[0]); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureOrCallerCancellationPreventsNextGateway(bool cancel)
    {
        using var manager = new VoWifiManager();
        using var cancellation = new CancellationTokenSource();
        var handling = manager.CompleteFailedStartAsync(new SipRegistrationTransportException(new TimeoutException()),
            VoWifiState.ImsRegistering, Discovery(), () =>
            {
                if (cancel) { cancellation.Cancel(); return Task.CompletedTask; }
                return Task.FromException(new IOException("Synthetic cleanup still owns resources."));
            }, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handling); else await handling;
        Assert.Null(RetryKey(manager));
        Assert.Equal(!cancel, manager.AutomaticRecoveryPaused);
        Assert.Equal(VoWifiState.Failed, manager.State);
        if (!cancel) Assert.Contains("Cleanup incomplete", manager.LastError);
    }

    [Fact]
    public async Task StopOfUnregisteredSessionDisposesLocallyWithoutSendingRemoval()
    {
        using var rig = new Rig(50);
        await Assert.ThrowsAsync<SipRegistrationTransportException>(() => rig.Session.RegisterAsync(Rig.NoAka));
        await rig.Request();
        using var manager = new VoWifiManager();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(VoWifiManager).GetField("_registerSession", flags)!.SetValue(manager, rig.Session);
        typeof(VoWifiManager).GetField("_sipTransport", flags)!.SetValue(manager, rig.Transport);
        await manager.StopVoWifiAsync();
        Assert.True(rig.Transport.IsDisposed);
        Assert.False(rig.Sent.Reader.TryRead(out _));
        Assert.False(manager.RemoteDeregistered);
    }

    [Fact]
    public void PostAuthenticationRetryCannotSelectAnotherMnc()
    {
        var address = IPAddress.Parse("203.0.113.1");
        var first = new EpdgResolvedEndpoint(address, new("first.invalid", "test"), "999", "12");
        var alternateMnc = first with { HomeMnc = "123" };
        var sameHome = first with { Candidate = new("second.invalid", "test") };
        EpdgResolvedEndpoint[] all = [first, alternateMnc, sameHome];
        var excluded = new HashSet<string> { "first.invalid|203.0.113.1|999|12" };
        Assert.Equal([alternateMnc, sameHome], VoWifiManager.FilterGatewayRetryCandidates(all, excluded, null));
        Assert.Equal([sameHome], VoWifiManager.FilterGatewayRetryCandidates(all, excluded, ("999", "12")));
        Assert.Empty(VoWifiManager.FilterGatewayRetryCandidates([first, alternateMnc], excluded, ("999", "12")));
        Assert.Equal(3, all.Length);
    }

    [Fact]
    public async Task PermanentActual403PausesBeforeSessionIsRemoved()
    {
        using var rig = new Rig();
        using var manager = new VoWifiManager();
        var registration = rig.Session.RegisterAsync(Rig.NoAka);
        await rig.Reply(await rig.Request(), 403);
        var failure = await Record.ExceptionAsync(async () => await registration);
        Assert.NotNull(failure);
        Assert.True(rig.Session.LastFailure!.PauseAutomaticRecovery);
        var field = typeof(VoWifiManager).GetField("_registerSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(manager, rig.Session);
        await manager.CompleteFailedStartAsync(failure, VoWifiState.ImsRegistering, Discovery(), () =>
        {
            field.SetValue(manager, null);
            return Task.CompletedTask;
        });
        Assert.True(manager.AutomaticRecoveryPaused);
        Assert.Null(RetryKey(manager));
    }
}
