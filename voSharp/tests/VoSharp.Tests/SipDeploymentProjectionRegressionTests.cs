using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;

namespace VoSharp.Tests;

// Supply future fields through ordinary JSON, not references to future APIs.
// The old kernel therefore compiles and fails on validation or real UDP
// behavior. All endpoints, credentials and identities below are synthetic.
public sealed partial class SipServerFeatureTests
{
    private static SipGatewayOptions DeploymentProjectionOptions(object projection)
    {
        var options = JsonSerializer.Deserialize<SipGatewayOptions>(JsonSerializer.Serialize(projection))
            ?? throw new InvalidDataException("Synthetic projection was not an object.");
        return options with
        {
            BindAddress = IPAddress.Loopback,
            SipPort = Port(),
            Realm = "lab.local",
            Accounts = new() { ["alice"] = "test-alice", ["bob"] = "test-bob" },
            RequireConfiguredLines = true,
            LineRoutes = new(),
            DefaultOutboundLineId = null,
            AllowCarrierCalls = false,
            MessagesEnabled = true
        };
    }

    private static async Task<SipMessage?> DeploymentProjectionPacket(UdpClient socket, int milliseconds = 250)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        try { return SipMessage.Parse((await socket.ReceiveAsync(timeout.Token)).Buffer); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return null; }
    }

    private static string DeploymentProjectionProof(SipMessage request, string challenge, string user,
        string algorithm, string realm = "lab.local")
    {
        var nonce = Regex.Match(challenge, "nonce=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(nonce);
        const string nc = "00000001";
        const string cnonce = "synthetic-projection-client";
        string Hash(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToHexString(algorithm == "SHA-256" ? SHA256.HashData(bytes) : MD5.HashData(bytes)).ToLowerInvariant();
        }
        var ha1 = Hash($"{user}:{realm}:test-{user}");
        var ha2 = Hash($"{request.Method}:{request.RequestUri}");
        var response = Hash($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
        return $"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{request.RequestUri}\", " +
            $"algorithm={algorithm}, response=\"{response}\", qop=auth, nc={nc}, cnonce=\"{cnonce}\"";
    }

    private static void DeploymentProjectionBranch(UdpClient socket, SipMessage request)
    {
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{End(socket).Port};branch=z9hG4bK{Guid.NewGuid():N};rport");
    }

    private static async Task DeploymentProjectionAuthenticate(UdpClient socket, SipMessage request, string user,
        IPEndPoint endpoint, string algorithm = "MD5")
    {
        DeploymentProjectionBranch(socket, request);
        await Send(socket, request, endpoint);
        var response = await Receive(socket);
        Assert.Contains(response.StatusCode, new[] { 401, 407 });
        var challenge = response.GetHeader(response.StatusCode == 401 ? "WWW-Authenticate" : "Proxy-Authenticate");
        Assert.NotNull(challenge);
        Assert.Contains("realm=\"lab.local\"", challenge);
        Assert.Contains("algorithm=" + algorithm, challenge);
        request.SetHeader(response.StatusCode == 401 ? "Authorization" : "Proxy-Authorization",
            DeploymentProjectionProof(request, challenge, user, algorithm));
        request.SetHeader("CSeq", $"2 {request.Method}");
        DeploymentProjectionBranch(socket, request);
        await Send(socket, request, endpoint);
    }

    private static async Task DeploymentProjectionRegister(UdpClient socket, string user, IPEndPoint endpoint,
        string algorithm = "MD5")
    {
        var request = Request("REGISTER", user, user);
        request.SetHeader("Contact", $"<sip:{user}@127.0.0.1:{End(socket).Port}>");
        request.SetHeader("Expires", "300");
        await DeploymentProjectionAuthenticate(socket, request, user, endpoint, algorithm);
        Assert.Equal(200, (await Receive(socket)).StatusCode);
    }

    [Theory]
    [InlineData("OPTIONS", 200)]
    [InlineData("REGISTER", 401)]
    public async Task DeploymentProjectionTrustedNetworksDropsDeniedSourceBeforeUnauthenticatedReply(string method, int allowedStatus)
    {
        var options = DeploymentProjectionOptions(new { TrustedNetworks = new[] { "127.0.0.1/32" } });
        var controller = new Controller();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync();
        var endpoint = gateway.Status.LocalEndPoint!;
        using var allowed = Socket();
        using var denied = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));

        await Send(allowed, Request(method, "alice", "alice"), endpoint);
        Assert.Equal(allowedStatus, (await Receive(allowed)).StatusCode);
        await Send(denied, Request(method, "alice", "alice"), endpoint);
        Assert.Null(await DeploymentProjectionPacket(denied));
        await Send(allowed, Request(method, "bob", "bob"), endpoint);
        Assert.Equal(allowedStatus, (await Receive(allowed)).StatusCode);
        Assert.Empty(gateway.Status.Registrations);
        Assert.Equal(0, controller.DialCount);
        Assert.Equal(0, controller.SmsCount);
    }

    [Fact]
    public async Task DeploymentProjectionTrustedNetworkRangeAllowsASecondCoveredLoopbackSource()
    {
        var options = DeploymentProjectionOptions(new { TrustedNetworks = new[] { "127.0.0.0/24" } });
        await using var gateway = new SipGateway(options, new Controller());
        await gateway.StartAsync();
        using var covered = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        await DeploymentProjectionRegister(covered, "alice", gateway.Status.LocalEndPoint!);
        Assert.Single(gateway.Status.Registrations);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), gateway.Status.Registrations[0].RemoteEndPoint.Address);
    }

    [Theory]
    [InlineData("OPTIONS")]
    [InlineData("REGISTER")]
    public async Task DeploymentProjectionExplicitEmptyTrustedNetworksDeniesAllRatherThanLegacyAllow(string method)
    {
        var options = DeploymentProjectionOptions(new { TrustedNetworks = Array.Empty<string>() });
        await using var gateway = new SipGateway(options, new Controller());
        await gateway.StartAsync();
        using var phone = Socket();
        await Send(phone, Request(method, "alice", "alice"), gateway.Status.LocalEndPoint!);
        Assert.Null(await DeploymentProjectionPacket(phone));
        Assert.Empty(gateway.Status.Registrations);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2/24")]
    [InlineData("0.0.0.0/0")]
    [InlineData("8.8.8.8/32")]
    [InlineData("224.0.0.0/4")]
    [InlineData("::1/128")]
    [InlineData("fe80::1%7/128")]
    [InlineData("not-a-network")]
    public void DeploymentProjectionTrustedNetworksRejectInvalidPublicOrWrongFamilyCidrs(string network)
    {
        var options = DeploymentProjectionOptions(new { TrustedNetworks = new[] { network } });
        Assert.IsType<ArgumentException>(Record.Exception(options.Validate));
    }

    [Fact]
    public void DeploymentProjectionTrustedNetworksRejectDuplicates()
    {
        var options = DeploymentProjectionOptions(new { TrustedNetworks = new[] { "127.0.0.1/32", "127.0.0.1/32" } });
        Assert.IsType<ArgumentException>(Record.Exception(options.Validate));
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void DeploymentProjectionTrustedNetworksBoundUsesDistinctValidNetworks(int count, bool rejected)
    {
        var networks = Enumerable.Range(1, count).Select(value => $"127.0.0.{value}/32").ToArray();
        var options = DeploymentProjectionOptions(new { TrustedNetworks = networks });
        var failure = Record.Exception(options.Validate);
        if (rejected) Assert.IsType<ArgumentException>(failure);
        else Assert.Null(failure);
    }

    [Theory]
    [InlineData("MD5")]
    [InlineData("SHA-256")]
    public async Task DeploymentProjectionLocalRegistrarChallengesAndVerifiesTheExplicitAlgorithm(string algorithm)
    {
        var options = DeploymentProjectionOptions(new { LocalRegistrarAlgorithm = algorithm });
        await using var gateway = new SipGateway(options, new Controller());
        await gateway.StartAsync();
        using var alice = Socket();
        await DeploymentProjectionRegister(alice, "alice", gateway.Status.LocalEndPoint!, algorithm);
        Assert.Single(gateway.Status.Registrations);
        Assert.Equal("alice", gateway.Status.Registrations[0].Extension);
    }

    [Fact]
    public async Task DeploymentProjectionSha256RegistrarRejectsMd5ProofAndThenAcceptsIndependentSha256Proof()
    {
        var options = DeploymentProjectionOptions(new { LocalRegistrarAlgorithm = "SHA-256" });
        await using var gateway = new SipGateway(options, new Controller());
        await gateway.StartAsync();
        using var alice = Socket();
        var endpoint = gateway.Status.LocalEndPoint!;
        var request = Request("REGISTER", "alice", "alice");
        request.SetHeader("Contact", $"<sip:alice@127.0.0.1:{End(alice).Port}>");
        request.SetHeader("Expires", "300");
        await Send(alice, request, endpoint);
        var first = await Receive(alice);
        Assert.Equal(401, first.StatusCode);
        var challenge = first.GetHeader("WWW-Authenticate")!;
        Assert.Contains("algorithm=SHA-256", challenge);
        request.SetHeader("Authorization", DeploymentProjectionProof(request, challenge, "alice", "MD5"));
        request.SetHeader("CSeq", "2 REGISTER");
        DeploymentProjectionBranch(alice, request);
        await Send(alice, request, endpoint);
        var rejected = await Receive(alice);
        Assert.Equal(401, rejected.StatusCode);
        Assert.Empty(gateway.Status.Registrations);

        request.SetHeader("Authorization", DeploymentProjectionProof(request, rejected.GetHeader("WWW-Authenticate")!, "alice", "SHA-256"));
        request.SetHeader("CSeq", "3 REGISTER");
        DeploymentProjectionBranch(alice, request);
        await Send(alice, request, endpoint);
        Assert.Equal(200, (await Receive(alice)).StatusCode);
        Assert.Single(gateway.Status.Registrations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha-256")]
    [InlineData("SHA-512")]
    [InlineData(null)]
    public void DeploymentProjectionLocalRegistrarRejectsInvalidAlgorithmWithoutFallback(string? algorithm)
    {
        var options = DeploymentProjectionOptions(new { LocalRegistrarAlgorithm = algorithm });
        Assert.IsType<ArgumentException>(Record.Exception(options.Validate));
    }

    [Fact]
    public async Task DeploymentProjectionIdentityDomainControlsMessageIdentityWithoutChangingAuthenticationRealm()
    {
        const string domain = "messages.example.invalid";
        var options = DeploymentProjectionOptions(new { IdentityDomain = domain, LocalRegistrarAlgorithm = "MD5" });
        var controller = new Controller();
        await using var gateway = new SipGateway(options, controller);
        await gateway.StartAsync();
        using var alice = Socket(); using var bob = Socket();
        var endpoint = gateway.Status.LocalEndPoint!;
        await DeploymentProjectionRegister(bob, "bob", endpoint);
        await DeploymentProjectionAuthenticate(alice, Text("bob", "synthetic projected domain"), "alice", endpoint);
        var delivered = await Receive(bob);
        await Send(bob, delivered.CreateResponse(200, "OK"), endpoint);
        Assert.Equal(200, (await Receive(alice)).StatusCode);
        Assert.Contains($"<sip:alice@{domain}>", delivered.GetHeader("From"));
        Assert.Equal($"<sip:bob@{domain}>", delivered.GetHeader("To"));
        Assert.EndsWith("@" + domain, delivered.GetHeader("Call-ID"));
        Assert.Equal("lab.local", options.Realm);
        Assert.Equal(0, controller.DialCount);
        Assert.Equal(0, controller.SmsCount);
    }

    [Fact]
    public async Task DeploymentProjectionIdentityDomainControlsInviteIdentityWithoutChangingAuthenticationRealm()
    {
        const string domain = "voice.example.invalid";
        var options = DeploymentProjectionOptions(new { IdentityDomain = domain, LocalRegistrarAlgorithm = "MD5" });
        var controller = new Controller();
        await using var gateway = new SipGateway(options, controller) { InviteTransactionTimeout = TimeSpan.FromSeconds(1) };
        await gateway.StartAsync();
        using var alice = Socket(); using var bob = Socket();
        var endpoint = gateway.Status.LocalEndPoint!;
        await DeploymentProjectionRegister(bob, "bob", endpoint);
        var invite = Request("INVITE", "spoofed", "bob");
        invite.SetHeader("Content-Type", "application/sdp");
        invite.Body = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 20000 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        await DeploymentProjectionAuthenticate(alice, invite, "alice", endpoint);
        var offered = await Receive(bob);
        var declined = offered.CreateResponse(486, "Synthetic Busy");
        declined.SetHeader("To", offered.GetHeader("To") + ";tag=projection-bob");
        await Send(bob, declined, endpoint);
        Assert.Equal("ACK", (await Receive(bob)).Method);
        Assert.Equal(480, (await Final(alice)).StatusCode);
        Assert.Contains($"<sip:alice@{domain}>", offered.GetHeader("From"));
        Assert.Equal($"<sip:bob@{domain}>", offered.GetHeader("To"));
        Assert.EndsWith("@" + domain, offered.GetHeader("Call-ID"));
        Assert.Equal("lab.local", options.Realm);
        Assert.Equal(0, controller.DialCount);
        Assert.Equal(0, controller.SmsCount);
    }

    [Theory]
    [InlineData("bad\nidentity.example")]
    [InlineData("sip:identity.example")]
    [InlineData("identity.example:5060")]
    [InlineData("bad_name.example")]
    [InlineData("8.8.8.8")]
    [InlineData("0.0.0.0")]
    public void DeploymentProjectionIdentityDomainRejectsInvalidOrPublicLiteralIdentity(string domain)
    {
        var options = DeploymentProjectionOptions(new { IdentityDomain = domain });
        Assert.IsType<ArgumentException>(Record.Exception(options.Validate));
    }
}
