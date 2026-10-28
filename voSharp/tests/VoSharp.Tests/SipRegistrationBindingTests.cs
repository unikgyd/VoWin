using System.Net;
using System.Net.Sockets;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class SipRegistrationBindingTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("no-expiry")]
    [InlineData("wrong-transport")]
    public async Task RegisterDoesNotClaimSuccessWithoutOwnGrantedContact(string responseKind)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var request = SipMessage.Parse(packet.Buffer);
            var response = request.CreateResponse(200, "OK");
            var requested = request.GetHeader("Contact")!;
            var returned = responseKind switch
            {
                "foreign" => $"\"{requested}\" <sip:foreign@127.0.0.1:5060>;expires=3600",
                "no-expiry" => requested.Replace(";expires=3600", "", StringComparison.Ordinal),
                "wrong-transport" => requested.Replace(";transport=udp>", ";transport=tcp>", StringComparison.OrdinalIgnoreCase),
                _ => null
            };
            if (returned is not null) response.SetHeader("Contact", returned);
            if (responseKind == "missing") response.SetHeader("Expires", "3600");
            await pcscf.SendAsync(response.ToBytes(), packet.RemoteEndPoint);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task RegisterAcceptsOwnContactWhenRegistrarOmitsDefaultUdpTransportParameter()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var request = SipMessage.Parse(packet.Buffer);
            var response = request.CreateResponse(200, "OK");
            response.SetHeader("Contact", request.GetHeader("Contact")!
                .Replace(";transport=udp>", ">", StringComparison.OrdinalIgnoreCase));
            await pcscf.SendAsync(response.ToBytes(), packet.RemoteEndPoint);
        });

        var result = await session.RegisterAsync(UnexpectedAka);
        await server;
        Assert.NotEmpty(result.ContactUri);
        Assert.Equal(3600, result.ExpiresSeconds);
        Assert.NotNull(session.LastResult);
    }

    [Fact]
    public async Task ShortGrantedLifetime_RefreshesWithSameBindingAndUpdatedExpiry()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var refreshed = new TaskCompletionSource<SipRegistrationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.RegistrationRefreshed += (_, result) => refreshed.TrySetResult(result);
        var server = Task.Run(async () =>
        {
            var first = await ReceiveAsync(pcscf);
            var firstResponse = first.Request.CreateResponse(200, "OK");
            firstResponse.SetHeader("Contact", first.Request.GetHeader("Contact")!
                .Replace(";expires=3600", ";expires=1", StringComparison.Ordinal));
            await pcscf.SendAsync(firstResponse.ToBytes(), first.Remote);

            var renewal = await ReceiveAsync(pcscf);
            Assert.Equal(first.Request.GetHeader("Call-ID"), renewal.Request.GetHeader("Call-ID"));
            Assert.True(Cseq(renewal.Request) > Cseq(first.Request));
            var renewalResponse = renewal.Request.CreateResponse(200, "OK");
            renewalResponse.SetHeader("Contact", renewal.Request.GetHeader("Contact")!);
            await pcscf.SendAsync(renewalResponse.ToBytes(), renewal.Remote);
        });

        var initial = await session.RegisterAsync(UnexpectedAka);
        Assert.Equal(1, initial.ExpiresSeconds);
        session.StartRefreshing(UnexpectedAka);
        var refreshedResult = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3600, refreshedResult.ExpiresSeconds);
        Assert.Equal(refreshedResult, session.LastResult);
        await session.StopRefreshingAsync();
        await server;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeregistrationTreatsEchoedZeroExpiryAsRemovedButRejectsActiveBinding(bool zeroExpiry)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var initial = await ReceiveAsync(pcscf);
            var accepted = initial.Request.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", initial.Request.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), initial.Remote);

            var deregister = await ReceiveAsync(pcscf);
            Assert.EndsWith(";expires=0", deregister.Request.GetHeader("Contact"));
            var removed = deregister.Request.CreateResponse(200, "OK");
            var echoed = deregister.Request.GetHeader("Contact")!;
            removed.SetHeader("Contact", zeroExpiry ? echoed :
                echoed.Replace(";expires=0", ";expires=3600", StringComparison.Ordinal));
            await pcscf.SendAsync(removed.ToBytes(), deregister.Remote);
        });

        await session.RegisterAsync(UnexpectedAka);
        if (zeroExpiry)
        {
            await session.DeregisterAsync();
            Assert.Null(session.LastResult);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeregisterAsync());
            Assert.NotNull(session.LastResult);
        }
        await server;
    }

    [Theory]
    [InlineData("Security-Server")]
    [InlineData("Require")]
    [InlineData("Proxy-Require")]
    public async Task RegisterDoesNotAcceptUnprotected200ThatRequiresSecurity(string header)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            var response = packet.Request.CreateResponse(200, "OK");
            response.SetHeader("Contact", packet.Request.GetHeader("Contact")!);
            response.SetHeader(header, header == "Security-Server" ? "ipsec-3gpp" : "timer, sec-agree");
            await pcscf.SendAsync(response.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Contains("no IMS security association", error.Message);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task RegisterRejectsSecAgreeChallengeWithoutSecurityServer()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            var response = packet.Request.CreateResponse(401, "Unauthorized");
            response.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            response.SetHeader("Require", "sec-agree");
            await pcscf.SendAsync(response.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Contains("omitted Security-Server", error.Message);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task RegisterClearsRandAndAutnWhenCardAkaThrows()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            var response = packet.Request.CreateResponse(401, "Unauthorized");
            response.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(response.ToBytes(), packet.Remote);
        });
        byte[]? capturedRand = null;
        byte[]? capturedAutn = null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(
            (rand, autn, _) =>
            {
                capturedRand = rand;
                capturedAutn = autn;
                throw new InvalidOperationException("simulated card failure");
            }));
        await server;
        Assert.Contains("simulated card failure", error.Message);
        Assert.NotNull(capturedRand);
        Assert.NotNull(capturedAutn);
        Assert.All(capturedRand, value => Assert.Equal((byte)0, value));
        Assert.All(capturedAutn, value => Assert.Equal((byte)0, value));
        Assert.Null(session.LastResult);
    }

    [Theory]
    [InlineData("hmac-sha-2-256", "1.000")]
    [InlineData("hmac-sha-1-96", "NaN")]
    [InlineData("hmac-sha-1-96", "1.1")]
    public async Task UnsupportedSecurityServerIsRejectedBeforeCardAka(string integrity, string quality)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 40000, 40001);
        using var session = new SipRegisterSession(transport, Profile(transport), proposal, (_, _, _) => { });
        var akaCalls = 0;
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            var response = packet.Request.CreateResponse(401, "Unauthorized");
            response.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            response.SetHeader("Security-Server",
                $"ipsec-3gpp;q={quality};alg={integrity};ealg=aes-cbc;prot=esp;mod=trans;" +
                "spi-c=2001;spi-s=2002;port-c=41000;port-s=41001");
            await pcscf.SendAsync(response.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync((_, _, _) =>
        {
            akaCalls++;
            return Task.FromResult((Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>()));
        }));
        await server;
        Assert.Contains("offered no IMS security mechanism", error.Message);
        Assert.Equal(0, akaCalls);
        Assert.Null(session.LastResult);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    public async Task RegisterRejectsSecurityDowngradeWhenOfferedIpsecButPeerOmitsAgreement(int statusCode)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 40000, 40001);
        var activated = false;
        using var session = new SipRegisterSession(transport, Profile(transport), proposal,
            (_, _, _) => activated = true);
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            Assert.NotNull(packet.Request.GetHeader("Security-Client"));
            var response = packet.Request.CreateResponse(statusCode,
                statusCode == 200 ? "OK" : "Unauthorized");
            if (statusCode == 200)
                response.SetHeader("Contact", packet.Request.GetHeader("Contact")!);
            else
                response.SetHeader("WWW-Authenticate",
                    $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(response.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Contains(statusCode == 200 ? "no IMS security association" : "omitted Security-Server", error.Message);
        Assert.False(activated);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public void RegisterRequiresSecurityProposalAndActivationCallbackTogether()
    {
        using var transport = new SipTransport();
        var profile = new ImsProfile("ue@ims.test", "sip:ue@ims.test", "ims.test",
            "123456789012345", "127.0.0.1");
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 40000, 40001);
        Assert.Throws<ArgumentException>(() => new SipRegisterSession(transport, profile, proposal));
        Assert.Throws<ArgumentException>(() => new SipRegisterSession(transport, profile,
            activateSecurity: (_, _, _) => { }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterRetriesOne423WithHigherLifetimeAndStableIdentity(bool afterAkaChallenge)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var first = await ReceiveAsync(pcscf);
            if (afterAkaChallenge)
            {
                var unauthorized = first.Request.CreateResponse(401, "Unauthorized");
                unauthorized.SetHeader("WWW-Authenticate",
                    $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
                await pcscf.SendAsync(unauthorized.ToBytes(), first.Remote);
            }
            var rejected = afterAkaChallenge ? await ReceiveAsync(pcscf) : first;
            var tooBrief = rejected.Request.CreateResponse(423, "Interval Too Brief");
            tooBrief.SetHeader("Min-Expires", "7200");
            await pcscf.SendAsync(tooBrief.ToBytes(), rejected.Remote);

            var retried = await ReceiveAsync(pcscf);
            var accepted = retried.Request.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", retried.Request.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), retried.Remote);
            return (first.Request, rejected.Request, retried.Request);
        });

        var akaCalls = 0;
        var result = await session.RegisterAsync((_, _, _) =>
        {
            Interlocked.Increment(ref akaCalls);
            return Task.FromResult((new byte[8], new byte[16], new byte[16]));
        });
        var (first, rejected, retried) = await server;
        Assert.Equal(afterAkaChallenge ? 1 : 0, akaCalls);
        Assert.EndsWith(";expires=3600", first.GetHeader("Contact"));
        Assert.EndsWith(";expires=7200", retried.GetHeader("Contact"));
        Assert.Equal(first.GetHeader("Call-ID"), retried.GetHeader("Call-ID"));
        Assert.Equal(first.GetHeader("From"), retried.GetHeader("From"));
        Assert.True(Cseq(retried) > Cseq(rejected));
        Assert.Equal(7200, result.ExpiresSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("3600")]
    public async Task RegisterRejects423WithoutUsableMinExpires(string? minExpires)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var packet = await ReceiveAsync(pcscf);
            var tooBrief = packet.Request.CreateResponse(423, "Interval Too Brief");
            if (minExpires is not null) tooBrief.SetHeader("Min-Expires", minExpires);
            await pcscf.SendAsync(tooBrief.ToBytes(), packet.Remote);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        await server;
        Assert.Contains("Min-Expires", error.Message);
        Assert.Null(session.LastResult);
    }

    [Fact]
    public async Task RegisterStopsAfterOne423NegotiationRetry()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback, pcscfPort, 0);
        using var session = new SipRegisterSession(transport, Profile(transport));
        var server = Task.Run(async () =>
        {
            var first = await ReceiveAsync(pcscf);
            var firstRejection = first.Request.CreateResponse(423, "Interval Too Brief");
            firstRejection.SetHeader("Min-Expires", "7200");
            await pcscf.SendAsync(firstRejection.ToBytes(), first.Remote);
            var second = await ReceiveAsync(pcscf);
            var secondRejection = second.Request.CreateResponse(423, "Interval Too Brief");
            secondRejection.SetHeader("Min-Expires", "10800");
            await pcscf.SendAsync(secondRejection.ToBytes(), second.Remote);
            return (first.Request, second.Request);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RegisterAsync(UnexpectedAka));
        var (firstRequest, secondRequest) = await server;
        Assert.Contains("Min-Expires=10800", error.Message);
        Assert.EndsWith(";expires=7200", secondRequest.GetHeader("Contact"));
        Assert.True(Cseq(secondRequest) > Cseq(firstRequest));
        Assert.Null(session.LastResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeregistrationRechallengeRenegotiatesSecurityBeforeCardAka(bool incompatible)
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new SipTransport();
        transport.Connect(IPAddress.Loopback, IPAddress.Loopback,
            ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port, 0);
        var proposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc", 1001, 1002, 40000, 40001);
        var activated = new List<SecurityAgreement>();
        using var session = new SipRegisterSession(transport, Profile(transport), proposal,
            (agreement, _, _) => activated.Add(agreement));
        const string initialSecurity = "ipsec-3gpp;q=1;alg=hmac-sha-1-96;ealg=aes-cbc;" +
            "prot=esp;mod=trans;spi-c=2001;spi-s=2002;port-c=41000;port-s=41001";
        var nextSecurity = $"ipsec-3gpp;q=1;alg={(incompatible ? "hmac-sha-2-256" : "hmac-md5-96")};" +
            "ealg=aes-cbc;prot=esp;mod=trans;spi-c=3001;spi-s=3002;port-c=41000;port-s=41001";
        var challenge = $"Digest realm=\"ims.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", " +
            "algorithm=AKAv1-MD5, qop=\"auth\"";
        var server = Task.Run(async () =>
        {
            var initial = await ReceiveAsync(pcscf);
            var initial401 = initial.Request.CreateResponse(401, "Unauthorized");
            initial401.SetHeader("WWW-Authenticate", challenge);
            initial401.SetHeader("Security-Server", initialSecurity);
            await pcscf.SendAsync(initial401.ToBytes(), initial.Remote);

            var authenticated = await ReceiveAsync(pcscf);
            Assert.Equal(initialSecurity, authenticated.Request.GetHeader("Security-Verify"));
            var accepted = authenticated.Request.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", authenticated.Request.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), authenticated.Remote);

            var deregister = await ReceiveAsync(pcscf);
            Assert.Equal("0", deregister.Request.GetHeader("Expires"));
            var reauthenticated = deregister.Request.CreateResponse(401, "Unauthorized");
            reauthenticated.SetHeader("WWW-Authenticate", challenge);
            reauthenticated.SetHeader("Security-Server", nextSecurity);
            await pcscf.SendAsync(reauthenticated.ToBytes(), deregister.Remote);
            if (incompatible) return;

            var retried = await ReceiveAsync(pcscf);
            Assert.Equal("0", retried.Request.GetHeader("Expires"));
            Assert.Equal(nextSecurity, retried.Request.GetHeader("Security-Verify"));
            await pcscf.SendAsync(retried.Request.CreateResponse(200, "OK").ToBytes(), retried.Remote);
        });

        var akaCalls = 0;
        Task<(byte[] Res, byte[] Ck, byte[] Ik)> Aka(byte[] _, byte[] __, CancellationToken ___)
        {
            akaCalls++;
            return Task.FromResult((new byte[8], new byte[16], new byte[16]));
        }
        await session.RegisterAsync(Aka);
        if (incompatible)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeregisterAsync(Aka));
            Assert.Contains("no installable IMS security mechanism", error.Message);
            Assert.Equal(1, akaCalls);
            Assert.Single(activated);
            Assert.NotNull(session.LastResult);
        }
        else
        {
            await session.DeregisterAsync(Aka);
            Assert.Equal(2, akaCalls);
            Assert.Equal(2, activated.Count);
            Assert.Equal("hmac-md5-96", activated[1].Selected.IntegrityAlgorithm);
            Assert.Null(session.LastResult);
        }
        await server;
    }

    private static async Task<(SipMessage Request, IPEndPoint Remote)> ReceiveAsync(UdpClient pcscf)
    {
        var packet = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return (SipMessage.Parse(packet.Buffer), packet.RemoteEndPoint);
    }

    private static uint Cseq(SipMessage request) =>
        uint.Parse(request.GetHeader("CSeq")!.Split(' ')[0]);

    private static ImsProfile Profile(SipTransport transport) =>
        new("001010123456789@ims.test", "sip:+15551234567@ims.test",
            "ims.test", "123456789012345", IPAddress.Loopback.ToString(),
            transport.LocalEndPoint!.Port);

    private static Task<(byte[] Res, byte[] Ck, byte[] Ik)> UnexpectedAka(
        byte[] _, byte[] __, CancellationToken ___) =>
        throw new InvalidOperationException("No AKA challenge expected.");
}
