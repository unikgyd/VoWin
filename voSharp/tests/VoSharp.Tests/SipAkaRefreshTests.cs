using System.Threading.Channels;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class SipAkaRefreshTests
{
    [Fact]
    public async Task RenewalAndRemovalAdvertiseFreshParametersWithoutMovingAnUnchallengedBinding()
    {
        using var peer = new Peer((request, count) => count == 1 ? Challenge(request, 1, true) : Accepted(request));
        var offers = new Queue<SecurityProposal>([
            new("hmac-sha-1-96", "aes-cbc", 300, 400, 42000, 40001),
            new("hmac-sha-1-96", "aes-cbc", 500, 600, 43000, 40001)]);
        using var session = new SipRegisterSession(peer.Transport, Profile,
            new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001), (_, _, _) => { },
            createRenewalSecurityProposal: offers.Dequeue);
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___) => Task.FromResult((new byte[8], new byte[16], new byte[16]));
        await session.RegisterAsync(Aka);
        var oldProfile = session.CurrentProfile;
        await session.RegisterAsync(Aka);
        Assert.Equal(oldProfile, session.CurrentProfile);
        Assert.Contains("spi-c=" + SecurityAgreementBuilder.FormatSpi(300), peer.Requests[2].GetHeader("Security-Client"));
        Assert.Contains("port-c=42000", peer.Requests[2].GetHeader("Security-Client"));
        Assert.Equal(peer.Requests[1].GetHeader("Contact"), peer.Requests[2].GetHeader("Contact"));
        Assert.Equal(peer.Requests[1].GetHeader("Authorization"), peer.Requests[2].GetHeader("Authorization"));
        await session.DeregisterAsync();
        Assert.Contains("spi-c=" + SecurityAgreementBuilder.FormatSpi(500), peer.Requests[3].GetHeader("Security-Client"));
        Assert.Contains("port-c=43000", peer.Requests[3].GetHeader("Security-Client"));
        Assert.Empty(offers);
    }

    [Fact]
    public async Task FreshChallengeDoesNotChangeOtherRequestsSecurityVerifyUntilRegistrationIsAccepted()
    {
        SipRegisterSession? session = null;
        var nextServer = SecurityServer.Replace("spi-c=1001", "spi-c=2001", StringComparison.Ordinal);
        using var peer = new Peer((request, count) =>
        {
            if (count == 1) return Challenge(request, 1, true);
            if (count == 3)
            {
                var challenge = Challenge(request, 2, true);
                challenge.SetHeader("Security-Server", nextServer);
                return challenge;
            }
            if (count == 4)
            {
                var ongoing = new SipMessage { IsRequest = true, Method = "BYE" };
                session!.AddSecurityHeaders(ongoing);
                Assert.Equal(SecurityServer, ongoing.GetHeader("Security-Verify"));
                Assert.Equal(nextServer, request.GetHeader("Security-Verify"));
            }
            return Accepted(request);
        });
        using var registered = session = new SipRegisterSession(peer.Transport, Profile,
            new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001), (_, _, _) => { },
            createRenewalSecurityProposal: () => new("hmac-sha-1-96", "aes-cbc", 300, 400, 42000, 40001));
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___) => Task.FromResult((new byte[8], new byte[16], new byte[16]));
        await session.RegisterAsync(Aka);
        await session.RegisterAsync(Aka);
        var current = new SipMessage { IsRequest = true, Method = "BYE" };
        session.AddSecurityHeaders(current);
        Assert.Equal(nextServer, current.GetHeader("Security-Verify"));
    }

    [Fact]
    public async Task BadRenewalReservationIsRejectedBeforeSendingOrConsumingAnotherAkaVector()
    {
        using var peer = new Peer((request, count) => count == 1 ? Challenge(request, 1, true) : Accepted(request));
        using var session = new SipRegisterSession(peer.Transport, Profile,
            new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001), (_, _, _) => { },
            createRenewalSecurityProposal: () => new("hmac-sha-1-96", "aes-cbc", 200, 300, 42000, 40001)); // Cross-role SPI reuse.
        var calls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
        { calls++; return Task.FromResult((new byte[8], new byte[16], new byte[16])); }
        await session.RegisterAsync(Aka);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(Aka));
        Assert.Equal(1, calls);
        Assert.Equal(2, peer.Requests.Count);
        Assert.NotNull(session.LastResult);
    }

    private const string SecurityServer = "ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;spi-c=1001;spi-s=1002;port-c=41000;port-s=41001";
    private static readonly ImsProfile Profile = new("ue@ims.test", "sip:ue@ims.test", "ims.test",
        "123456789012345", "10.0.0.1");
    private static string Nonce(byte value) => Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray());

    private static SipMessage Challenge(SipMessage request, byte nonce, bool security, bool qop = true)
    {
        var response = request.CreateResponse(401, "Unauthorized");
        response.SetHeader("WWW-Authenticate",
            $"Digest realm=\"aka-realm.test\", nonce=\"{Nonce(nonce)}\", opaque=\"carrier,token\", algorithm=AKAv1-MD5" +
            (qop ? ", qop=\"auth\"" : ""));
        if (security) response.SetHeader("Security-Server", SecurityServer);
        return response;
    }

    private static SipMessage Accepted(SipMessage request, int expires = 3600)
    {
        var response = request.CreateResponse(200, "OK");
        if (request.GetHeader("Expires") != "0")
            response.SetHeader("Contact", request.GetHeader("Contact")!
                .Replace("expires=3600", $"expires={expires}", StringComparison.Ordinal));
        return response;
    }

    private sealed class Peer : IDisposable
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        public SipTransport Transport { get; } = new();
        public List<SipMessage> Requests { get; } = [];

        public Peer(Func<SipMessage, int, SipMessage> respond)
        {
            Transport.ConnectCustom(bytes =>
            {
                var request = SipMessage.Parse(bytes);
                Requests.Add(request);
                _responses.Writer.TryWrite(respond(request, Requests.Count).ToBytes());
                return Task.CompletedTask;
            }, ct => _responses.Reader.ReadAsync(ct).AsTask());
        }

        public void Dispose() => Transport.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewalAndDeregistrationCarryLastAkaWithoutKeepingRawKeys(bool security)
    {
        using var peer = new Peer((request, count) => count == 1
            ? Challenge(request, 1, security) : Accepted(request));
        var proposal = security ? new SecurityProposal("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001) : null;
        var diagnostics = new List<string>();
        using var session = new SipRegisterSession(peer.Transport, Profile, proposal,
            security ? (_, _, _) => { } : null, diagnosticLog: diagnostics.Add);
        var res = Enumerable.Repeat((byte)9, 8).ToArray();
        var ck = Enumerable.Repeat((byte)7, 16).ToArray();
        var ik = Enumerable.Repeat((byte)5, 16).ToArray();
        var akaCalls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
        { akaCalls++; return Task.FromResult((res, ck, ik)); }

        await session.RegisterAsync(Aka);
        for (var i = 0; i < 3; i++) await session.RegisterAsync(Aka);
        await session.DeregisterAsync();

        Assert.Equal(1, akaCalls);
        var authenticated = peer.Requests[1];
        var authorization = authenticated.GetHeader("Authorization")!;
        Assert.Contains($"nonce=\"{Nonce(1)}\"", authorization);
        Assert.Contains("realm=\"aka-realm.test\"", authorization);
        Assert.DoesNotContain("response=\"\"", authorization);
        Assert.Contains("opaque=\"carrier,token\"", authorization);
        foreach (var request in peer.Requests.Skip(2))
        {
            Assert.Equal(authorization, request.GetHeader("Authorization"));
            Assert.Equal(authenticated.GetHeader("Call-ID"), request.GetHeader("Call-ID"));
            Assert.Equal(authenticated.GetHeader("From"), request.GetHeader("From"));
            if (security)
            {
                Assert.Equal(SecurityServer, request.GetHeader("Security-Verify"));
                Assert.Contains("integrity-protected=yes", request.GetHeader("Authorization"));
                Assert.Contains(":40001;", request.GetHeader("Contact"));
            }
        }
        Assert.Equal(Enumerable.Range(1, peer.Requests.Count).Select(i => $"{i} REGISTER"),
            peer.Requests.Select(r => r.GetHeader("CSeq")));
        foreach (var key in new[] { res, ck, ik }) Assert.All(key, value => Assert.Equal((byte)0, value));
        Assert.Null(session.LastResult);
        var afterDeregister = ImsRegisterBuilder.BuildInitialRegister(Profile, "new-binding", 1);
        session.AddSecurityHeaders(afterDeregister);
        if (security) Assert.Contains("nonce=\"\"", afterDeregister.GetHeader("Authorization"));
        else Assert.Null(afterDeregister.GetHeader("Authorization"));
        Assert.DoesNotContain(diagnostics, line => line.Contains(Nonce(1), StringComparison.Ordinal) ||
            line.Contains(authorization, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh401ReplacesCachedAuthorizationWhileKeepingExistingSecurity(bool security)
    {
        using var peer = new Peer((request, count) => count switch
        {
            1 => Challenge(request, 1, security),
            3 => Challenge(request, 2, false), // Reauthentication on the existing SAs.
            _ => Accepted(request)
        });
        var proposal = security ? new SecurityProposal("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001) : null;
        var activations = 0;
        using var session = new SipRegisterSession(peer.Transport, Profile, proposal,
            security ? (_, _, _) => activations++ : null);
        var akaCalls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
        { akaCalls++; return Task.FromResult((new byte[8], new byte[16], new byte[16])); }

        await session.RegisterAsync(Aka);
        await session.RegisterAsync(Aka);
        await session.RegisterAsync(Aka);

        Assert.Equal(2, akaCalls);
        Assert.Equal(security ? 1 : 0, activations);
        Assert.Equal(peer.Requests[1].GetHeader("Authorization"), peer.Requests[2].GetHeader("Authorization"));
        Assert.Contains($"nonce=\"{Nonce(2)}\"", peer.Requests[3].GetHeader("Authorization"));
        Assert.Equal(peer.Requests[3].GetHeader("Authorization"), peer.Requests[4].GetHeader("Authorization"));
        Assert.NotEqual(peer.Requests[1].GetHeader("Authorization"), peer.Requests[4].GetHeader("Authorization"));
        if (security) Assert.Equal(SecurityServer, peer.Requests[4].GetHeader("Security-Verify"));
    }

    [Fact]
    public async Task AutomaticRefreshDoesNotRepeatIdentityOnlyAuthorization()
    {
        using var peer = new Peer((request, count) => count == 1 ? Challenge(request, 1, true) :
            count > 2 && request.GetHeader("Authorization")!.Contains("response=\"\"", StringComparison.Ordinal)
                ? request.CreateResponse(500, "Invalid registration state")
                : Accepted(request, count == 2 ? 1 : 3600));
        using var session = new SipRegisterSession(peer.Transport, Profile,
            new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001), (_, _, _) => { });
        var akaCalls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
        { akaCalls++; return Task.FromResult((new byte[8], new byte[16], new byte[16])); }
        var refreshed = new TaskCompletionSource<SipRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RegistrationRefreshed += (_, result) => refreshed.TrySetResult(result);
        await session.RegisterAsync(Aka);
        session.StartRefreshing(Aka);
        var result = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopRefreshingAsync();

        Assert.Equal(3600, result.ExpiresSeconds);
        Assert.Equal(1, akaCalls);
        Assert.Equal(3, peer.Requests.Count);
        Assert.Equal(peer.Requests[1].GetHeader("Authorization"), peer.Requests[2].GetHeader("Authorization"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SuccessfulNextNonceReplacesOnlyNonceInRenewalAndDeregistration(bool security, bool qop)
    {
        using var peer = new Peer((request, count) =>
        {
            if (count == 1) return Challenge(request, 1, security, qop);
            var response = Accepted(request);
            if (count is 2 or 3)
                response.SetHeader("Authentication-Info", $"qop=auth, nextnonce=\"{Nonce((byte)count)}\", ext=\"\"");
            return response;
        });
        var diagnostics = new List<string>();
        using var session = new SipRegisterSession(peer.Transport, Profile,
            security ? new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001) : null,
            security ? (_, _, _) => { } : null, diagnosticLog: diagnostics.Add);
        var akaCalls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
        { akaCalls++; return Task.FromResult((new byte[8], new byte[16], new byte[16])); }

        await session.RegisterAsync(Aka);
        await session.RegisterAsync(Aka);
        await session.RegisterAsync(Aka);
        await session.DeregisterAsync();

        Assert.Equal(1, akaCalls); // nextnonce alone does not consume another USIM authentication.
        var authorization = peer.Requests[1].GetHeader("Authorization")!;
        Assert.Equal(authorization.Replace(Nonce(1), Nonce(2), StringComparison.Ordinal), peer.Requests[2].GetHeader("Authorization"));
        foreach (var request in peer.Requests.Skip(3))
            Assert.Equal(authorization.Replace(Nonce(1), Nonce(3), StringComparison.Ordinal), request.GetHeader("Authorization"));
        Assert.DoesNotContain(diagnostics, line => Enumerable.Range(1, 3).Any(i => line.Contains(Nonce((byte)i), StringComparison.Ordinal)));
        Assert.False(session.RequiresNewRegistrationFlow);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task Fresh401AfterNextNonceReplacesResponseAndStillUsesExistingSecurity()
    {
        using var peer = new Peer((request, count) =>
        {
            if (count == 1) return Challenge(request, 1, true);
            if (count == 3) return Challenge(request, 3, false);
            var response = Accepted(request);
            if (count == 2) response.SetHeader("Authentication-Info", $"nextnonce=\"{Nonce(2)}\"");
            return response;
        });
        var activations = 0;
        using var session = new SipRegisterSession(peer.Transport, Profile,
            new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001), (_, _, _) => activations++);
        var akaCalls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
        { akaCalls++; return Task.FromResult((Enumerable.Repeat((byte)akaCalls, 8).ToArray(), new byte[16], new byte[16])); }
        await session.RegisterAsync(Aka);
        await session.RegisterAsync(Aka);
        await session.RegisterAsync(Aka);
        Assert.Equal(2, akaCalls);
        Assert.Equal(1, activations);
        Assert.Contains(Nonce(2), peer.Requests[2].GetHeader("Authorization"));
        Assert.Contains(Nonce(3), peer.Requests[3].GetHeader("Authorization"));
        Assert.Equal(peer.Requests[3].GetHeader("Authorization"), peer.Requests[4].GetHeader("Authorization"));
        Assert.Equal(SecurityServer, peer.Requests[4].GetHeader("Security-Verify"));
    }

    [Theory]
    [InlineData("nextnonce=\"\"")]
    [InlineData("nextnonce=\"one\", NextNonce=\"two\"")]
    [InlineData("nextnonce=\"unterminated")]
    [InlineData("nextnonce=\"value\", broken")]
    public async Task MalformedNextNonceInvalidatesFlowWithoutPublishingSuccessOrKeepingCredentials(string info)
    {
        using var peer = new Peer((request, count) =>
        {
            if (count == 1) return Challenge(request, 1, false);
            var response = Accepted(request);
            response.SetHeader("Authentication-Info", info);
            return response;
        });
        using var session = new SipRegisterSession(peer.Transport, Profile);
        var res = new byte[8]; Array.Fill(res, (byte)9);
        var ck = new byte[16]; Array.Fill(ck, (byte)8);
        var ik = new byte[16]; Array.Fill(ik, (byte)7);
        await Assert.ThrowsAsync<SipRegistrationRestartRequiredException>(() => session.RegisterAsync(
            (_, _, _) => Task.FromResult((res, ck, ik))));
        Assert.Null(session.LastResult);
        Assert.True(session.RequiresNewRegistrationFlow);
        foreach (var key in new[] { res, ck, ik }) Assert.All(key, value => Assert.Equal(0, value));
        await Assert.ThrowsAsync<SipRegistrationRestartRequiredException>(() => session.RegisterAsync(
            (_, _, _) => throw new InvalidOperationException("An invalid flow must not consume AKA again.")));
        Assert.Equal(2, peer.Requests.Count);
        await session.DeregisterAsync();
        Assert.Equal(2, peer.Requests.Count); // No unregister on a flow already known invalid.
    }

    [Fact]
    public async Task EscapedNextNonceIsSafelyStoredWithoutChangingOtherQuotedFields()
    {
        const string value = "new,nonce\\with\"quote";
        const string escaped = "new,nonce\\\\with\\\"quote";
        using var peer = new Peer((request, count) =>
        {
            if (count == 1) return Challenge(request, 1, false);
            var response = Accepted(request);
            if (count == 2) response.SetHeader("Authentication-Info", $"nextnonce=\"{escaped}\", ext=\"has,a,comma\"");
            return response;
        });
        using var session = new SipRegisterSession(peer.Transport, Profile);
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
            => Task.FromResult((new byte[8], new byte[16], new byte[16]));
        await session.RegisterAsync(Aka);
        await session.RegisterAsync(Aka);
        var header = peer.Requests[2].GetHeader("Authorization")!;
        var parsed = ImsRegisterBuilder.ParseWwwAuthenticate(header)!;
        Assert.Equal(value, parsed.Nonce);
        Assert.Equal("carrier,token", parsed.Opaque);
        Assert.Equal(peer.Requests[1].GetHeader("Authorization")!.Replace(Nonce(1), escaped, StringComparison.Ordinal), header);
    }

    [Fact]
    public async Task NextNonceOnMultipleHeadersIsRejectedRatherThanChoosingOne()
    {
        using var peer = new Peer((request, count) =>
        {
            if (count == 1) return Challenge(request, 1, false);
            var response = Accepted(request);
            response.AddHeader("Authentication-Info", $"nextnonce=\"{Nonce(2)}\"");
            response.AddHeader("Authentication-Info", $"nextnonce=\"{Nonce(3)}\"");
            return response;
        });
        using var session = new SipRegisterSession(peer.Transport, Profile);
        await Assert.ThrowsAsync<SipRegistrationRestartRequiredException>(() => session.RegisterAsync(
            (_, _, _) => Task.FromResult((new byte[8], new byte[16], new byte[16]))));
        Assert.Null(session.LastResult);
        Assert.True(session.RequiresNewRegistrationFlow);
        Assert.Equal(2, peer.Requests.Count);
    }

    [Theory]
    [InlineData("ext=\"\", qop=auth", true)]
    [InlineData("nextnonce=\"a,b\\\"c\"", true)]
    [InlineData("nextnonce=\"first\", NEXTNONCE=\"second\"", false)]
    [InlineData("nextnonce=\"unclosed", false)]
    [InlineData("nextnonce=\"value\"\r\nInjected: value", false)]
    public void AuthenticationInfoParsingHonorsQuotedValuesAndRejectsAmbiguity(string header, bool expected)
        => Assert.Equal(expected, ImsRegisterBuilder.TryParseAuthenticationInfo(header, out _));

    [Theory]
    [InlineData(403, false)]
    [InlineData(408, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(504, false)]
    [InlineData(403, true)]
    [InlineData(408, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    public async Task ReregistrationFailureRequiresNewFlowAtEitherResponseStep(int status, bool rechallenged)
    {
        using var peer = new Peer((request, count) => count switch
        {
            1 => Challenge(request, 1, true),
            2 => Accepted(request),
            3 when rechallenged => Challenge(request, 2, false),
            _ => request.CreateResponse(status, "Test flow failure")
        });
        using var session = new SipRegisterSession(peer.Transport, Profile,
            new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001), (_, _, _) => { });
        var akaCalls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
        { akaCalls++; return Task.FromResult((new byte[8], new byte[16], new byte[16])); }
        await session.RegisterAsync(Aka);
        var error = await Assert.ThrowsAsync<SipRegistrationRestartRequiredException>(() => session.RegisterAsync(Aka));
        Assert.Equal(status, error.StatusCode);
        Assert.True(session.RequiresNewRegistrationFlow);
        Assert.Null(session.LastResult);
        Assert.Equal(rechallenged ? 2 : 1, akaCalls);
        var requests = peer.Requests.Count;
        await Assert.ThrowsAsync<SipRegistrationRestartRequiredException>(() => session.RegisterAsync(Aka));
        await session.DeregisterAsync();
        Assert.Equal(requests, peer.Requests.Count);
    }

    [Fact]
    public async Task AutomaticRefresh500HandsOffImmediatelyInsteadOfRetryingDeadFlow()
    {
        using var peer = new Peer((request, count) => count switch
        {
            1 => Challenge(request, 1, true),
            2 => Accepted(request, 1),
            _ => request.CreateResponse(500, "Test flow lost")
        });
        using var session = new SipRegisterSession(peer.Transport, Profile,
            new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001), (_, _, _) => { });
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
            => Task.FromResult((new byte[8], new byte[16], new byte[16]));
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RegistrationFailed += (_, reason) => failed.TrySetResult(reason);
        await session.RegisterAsync(Aka);
        session.StartRefreshing(Aka);
        var reason = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopRefreshingAsync();
        Assert.Contains("SIP 500", reason);
        Assert.DoesNotContain("retries were exhausted", reason);
        Assert.Equal(3, peer.Requests.Count);
        Assert.True(session.RequiresNewRegistrationFlow);
        Assert.Null(session.LastResult);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(504)]
    public async Task InitialFailureIsNotConfusedWithLossOfAnExistingRegistration(int status)
    {
        using var peer = new Peer((request, count) => count == 1 ? request.CreateResponse(status, "Test initial failure") : Accepted(request));
        var retry = new SipRegistrationRetryGate();
        using var session = new SipRegisterSession(peer.Transport, Profile, retryGate: retry);
        Task<(byte[], byte[], byte[])> UnexpectedAka(byte[] _, byte[] __, CancellationToken ___)
            => throw new InvalidOperationException("No AKA challenge was sent.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        Assert.IsNotType<SipRegistrationRestartRequiredException>(error);
        Assert.False(session.RequiresNewRegistrationFlow);
        if (status == 403)
        {
            Assert.Throws<SipRegistrationRetryBlockedException>(() => retry.ThrowIfBlocked(Profile.PrivateIdentity));
            retry.ReleasePause(Profile.PrivateIdentity); // Explicit user retry, not a new automatic flow.
        }
        await session.RegisterAsync(UnexpectedAka);
        Assert.NotNull(session.LastResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrarBackoffSurvivesNewSipFlowAndBlocksBeforeAnySendOrAka(bool authenticatedStep)
    {
        var clock = new SipRegistrationRetryTests.TestClock();
        var retry = new SipRegistrationRetryGate(clock);
        using var peer = new Peer((request, count) =>
        {
            if (authenticatedStep && count == 1) return Challenge(request, 1, false);
            var rejected = request.CreateResponse(503, "Unavailable");
            rejected.SetHeader("Retry-After", "120 (maintenance);duration=30");
            return rejected;
        });
        using var initial = new SipRegisterSession(peer.Transport, Profile, retryGate: retry, retryScope: "test-card");
        var akaCalls = 0;
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
        { akaCalls++; return Task.FromResult((new byte[8], new byte[16], new byte[16])); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => initial.RegisterAsync(Aka));
        Assert.Equal(120u, initial.LastFailure!.RetryAfterSeconds);
        Assert.Equal(503, initial.LastFailure.StatusCode);
        Assert.False(initial.LastFailure.RequiresNewFlow);
        using var replacementPeer = new Peer((request, _) => Accepted(request));
        using var replacement = new SipRegisterSession(replacementPeer.Transport, Profile, retryGate: retry, retryScope: "test-card");
        await Assert.ThrowsAsync<SipRegistrationRetryBlockedException>(() => replacement.RegisterAsync(Aka));
        Assert.Empty(replacementPeer.Requests);
        Assert.Equal(authenticatedStep ? 1 : 0, akaCalls);
        clock.Advance(TimeSpan.FromSeconds(120));
        await replacement.RegisterAsync(Aka);
        Assert.Single(replacementPeer.Requests);
    }

    [Theory]
    [InlineData("300", false)]
    [InlineData("bogus", true)]
    public async Task RefreshFailurePreservesStructuredFeedbackForItsOwner(string retryAfter, bool invalid)
    {
        using var peer = new Peer((request, count) =>
        {
            if (count == 1) return Challenge(request, 1, true);
            if (count == 2) return Accepted(request);
            var rejected = request.CreateResponse(500, "Unavailable");
            rejected.SetHeader("Retry-After", retryAfter);
            return rejected;
        });
        using var session = new SipRegisterSession(peer.Transport, Profile,
            new("hmac-sha-1-96", "aes-cbc", 100, 200, 40000, 40001), (_, _, _) => { });
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___)
            => Task.FromResult((new byte[8], new byte[16], new byte[16]));
        await session.RegisterAsync(Aka);
        var error = await Assert.ThrowsAsync<SipRegistrationRestartRequiredException>(() => session.RegisterAsync(Aka));
        Assert.Same(session.LastFailure, error.Failure);
        Assert.Equal(invalid, error.Failure!.InvalidRetryAfter);
        Assert.Equal(invalid, error.Failure.PauseAutomaticRecovery);
        Assert.Equal(invalid ? null : 300u, error.Failure.RetryAfterSeconds);
        Assert.Null(session.LastResult);
    }
}
