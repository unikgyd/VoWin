using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;

namespace VoSharp.Tests;

public sealed class SipPeerCredentialPolicyTests
{
    private const string InboundPassword = "independent-inbound-secret";
    private const string OutboundPassword = "independent-outbound-secret";

    private static SipGatewayPeer Peer(string realm = "asterisk", IReadOnlyList<string>? algorithms = null) => new(
        new IPEndPoint(IPAddress.Parse("127.0.0.2"), 5060), "600",
        new SipPeerInboundCredentials("pbx-in", InboundPassword),
        new SipPeerOutboundCredentials("gateway-out", OutboundPassword, realm)
        { AllowedAlgorithms = algorithms ?? ["SHA-256"] });

    private static SipGatewayOptions Options(SipGatewayPeer? peer = null) => new()
    {
        BindAddress = IPAddress.Loopback, SipPort = 5062, Peer = peer ?? Peer(),
        Accounts = new() { ["1001"] = "local-only-secret" }
    };

    private static SipMessage Request()
    {
        var request = new SipMessage { IsRequest = true, Method = "MESSAGE", RequestUri = "sip:600@127.0.0.2:5060" };
        request.SetHeader("Call-ID", "offline-peer-only");
        request.SetHeader("CSeq", "1 MESSAGE");
        request.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:5062;branch=z9hG4bK-offline");
        request.SetHeader("From", "<sip:gateway@vowin.local>;tag=offline");
        request.SetHeader("To", "<sip:600@asterisk>");
        return request;
    }

    [Fact]
    public void TypedPeerSupportsZeroOrIndependentLocalAccountsAndLegacyFailsClosed()
    {
        var options = Options();
        options.Validate();
        (options with { Accounts = [] }).Validate();
        (options with { Accounts = new() { ["pbx-in"] = "different-local-secret", ["1001"] = "another-local-secret" } }).Validate();
        Assert.Throws<ArgumentException>(() => (options with { Peer = null, Accounts = [] }).Validate());
        var legacy = new SipGatewayPeer(options.Peer!.EndPoint, "600", "pbx-in", InboundPassword);
        Assert.Throws<ArgumentException>(() => (options with { Peer = legacy }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { Peer = legacy with { OutboundAuth = Peer().OutboundAuth } }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { Peer = legacy with { InboundAuth = Peer().InboundAuth, OutboundAuth = Peer().OutboundAuth } }).Validate());
        Assert.Null(SipPeerDigest.Create("Digest realm=\"asterisk\",nonce=n,algorithm=SHA-256,qop=auth", Request(), legacy));
        Assert.Equal("SHA-256", options.Peer.InboundAuth!.Algorithm);
        Assert.Equal(new[] { "SHA-256" }, new SipPeerOutboundCredentials("u", "p", "realm").AllowedAlgorithms);
    }

    [Theory]
    [InlineData("empty-inbound-password")]
    [InlineData("oversized-inbound-password")]
    [InlineData("invalid-inbound-user")]
    [InlineData("invalid-inbound-algorithm")]
    [InlineData("empty-outbound-password")]
    [InlineData("invalid-outbound-user")]
    [InlineData("empty-realm")]
    [InlineData("whitespace-realm")]
    [InlineData("wildcard-realm")]
    [InlineData("unsafe-realm")]
    [InlineData("empty-algorithms")]
    [InlineData("null-algorithms")]
    [InlineData("duplicate-algorithms")]
    [InlineData("unsupported-algorithm")]
    [InlineData("noncanonical-algorithm")]
    [InlineData("public-endpoint")]
    [InlineData("self-endpoint")]
    [InlineData("wrong-family")]
    [InlineData("legacy-secret-on-typed-peer")]
    public void InvalidOrUnpinnedPeerCredentialsCannotEnable(string mutation)
    {
        var peer = Peer();
        peer = mutation switch
        {
            "empty-inbound-password" => peer with { InboundAuth = peer.InboundAuth! with { Password = "" } },
            "oversized-inbound-password" => peer with { InboundAuth = peer.InboundAuth! with { Password = new string('界', 43) } },
            "invalid-inbound-user" => peer with { InboundAuth = peer.InboundAuth! with { Username = "bad:user" } },
            "invalid-inbound-algorithm" => peer with { InboundAuth = peer.InboundAuth! with { Algorithm = "SHA-512-256" } },
            "empty-outbound-password" => peer with { OutboundAuth = peer.OutboundAuth! with { Password = "" } },
            "invalid-outbound-user" => peer with { OutboundAuth = peer.OutboundAuth! with { Username = "bad\r\nuser" } },
            "empty-realm" => peer with { OutboundAuth = peer.OutboundAuth! with { ExpectedRealm = "" } },
            "whitespace-realm" => peer with { OutboundAuth = peer.OutboundAuth! with { ExpectedRealm = " " } },
            "wildcard-realm" => peer with { OutboundAuth = peer.OutboundAuth! with { ExpectedRealm = "*" } },
            "unsafe-realm" => peer with { OutboundAuth = peer.OutboundAuth! with { ExpectedRealm = "bad\r\nrealm" } },
            "empty-algorithms" => peer with { OutboundAuth = peer.OutboundAuth! with { AllowedAlgorithms = [] } },
            "null-algorithms" => peer with { OutboundAuth = peer.OutboundAuth! with { AllowedAlgorithms = null! } },
            "duplicate-algorithms" => peer with { OutboundAuth = peer.OutboundAuth! with { AllowedAlgorithms = ["MD5", "MD5"] } },
            "unsupported-algorithm" => peer with { OutboundAuth = peer.OutboundAuth! with { AllowedAlgorithms = ["SHA-512-256"] } },
            "noncanonical-algorithm" => peer with { OutboundAuth = peer.OutboundAuth! with { AllowedAlgorithms = ["md5"] } },
            "public-endpoint" => peer with { EndPoint = new(IPAddress.Parse("8.8.8.8"), 5060) },
            "self-endpoint" => peer with { EndPoint = new(IPAddress.Loopback, 5062) },
            "wrong-family" => peer with { EndPoint = new(IPAddress.IPv6Loopback, 5060) },
            "legacy-secret-on-typed-peer" => peer with { Password = "legacy-secret" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.Throws<ArgumentException>(() => Options(peer).Validate());
    }

    [Fact]
    public void DeepCloneSeparatesCredentialsAlgorithmsAddressesAndRoutes()
    {
        string[] algorithms = ["SHA-256", "MD5"];
        var source = Options(Peer(algorithms: algorithms)) with
        {
            Accounts = new() { ["1001"] = "local-secret" },
            ExtensionPolicies = new() { ["1001"] = new() },
            LineRoutes = new() { ["line-a"] = new() { IncomingTarget = "600", LocalIncomingTarget = "1001", SmsTarget = "sms-in", LocalSmsTarget = "1001" } },
            DialRules = [new() { Prefix = "81", LineId = "line-a" }]
        };
        source.Validate();
        var copy = source.DeepClone();
        algorithms[0] = "MD5";
        source.Peer!.EndPoint.Port = 5099;
        source.Accounts.Clear(); source.ExtensionPolicies.Clear(); source.LineRoutes.Clear(); source.DialRules.Clear();
        copy.Validate();
        Assert.Equal("SHA-256", copy.Peer!.OutboundAuth!.AllowedAlgorithms[0]);
        Assert.Equal(5060, copy.Peer.EndPoint.Port);
        Assert.Single(copy.Accounts); Assert.Single(copy.ExtensionPolicies); Assert.Single(copy.LineRoutes); Assert.Single(copy.DialRules);
        Assert.NotSame(source.Peer.InboundAuth, copy.Peer.InboundAuth);
        Assert.NotSame(source.Peer.OutboundAuth, copy.Peer.OutboundAuth);

        var scoped = Options() with { BindAddress = IPAddress.Parse("fe80::1%5"), Peer = Peer() with { EndPoint = new(IPAddress.Parse("fe80::2%6"), 5060) } };
        var scopedCopy = scoped.DeepClone();
        scoped.BindAddress.ScopeId = 10; scoped.Peer!.EndPoint.Address.ScopeId = 11;
        Assert.Equal(5, scopedCopy.BindAddress.ScopeId); Assert.Equal(6, scopedCopy.Peer!.EndPoint.Address.ScopeId);
    }

    [Fact]
    public void PublicJsonAndDiagnosticsNeverContainAnyAuthenticationPassword()
    {
        var options = Options();
        var jsonOptions = new JsonSerializerOptions();
        // IPAddress/IPEndPoint are not built-in System.Text.Json value types;
        // represent public network metadata as strings for this projection.
        jsonOptions.Converters.Add(new AddressConverter()); jsonOptions.Converters.Add(new EndpointConverter());
        var json = JsonSerializer.Serialize(options, jsonOptions);
        foreach (var secret in new[] { InboundPassword, OutboundPassword, "local-only-secret" })
        {
            Assert.DoesNotContain(secret, json);
            Assert.DoesNotContain(secret, options.ToString());
            Assert.DoesNotContain(secret, options.Peer!.ToString());
            Assert.DoesNotContain(secret, options.Peer.InboundAuth!.ToString());
            Assert.DoesNotContain(secret, options.Peer.OutboundAuth!.ToString());
        }
        Assert.DoesNotContain("\"Password\"", json); Assert.DoesNotContain("\"Accounts\"", json);
    }

    [Theory]
    [InlineData("SHA-256", true)]
    [InlineData("SHA-256", false)]
    [InlineData("MD5", true)]
    [InlineData("MD5", false)]
    public void OutboundProofUsesOnlyIndependentPinnedCredential(string algorithm, bool qop)
    {
        var request = Request();
        var challenge = $"Digest realm=\"asterisk\",nonce=\"offline\",algorithm={algorithm},opaque=\"a,b\"" + (qop ? ",qop=\"auth-int, auth\"" : "");
        var proof = SipPeerDigest.Create(challenge, request, Peer(algorithms: [algorithm]));
        Assert.NotNull(proof);
        Assert.True(SipPeerDigest.TryParseParameters(proof![7..], out var values));
        string Hash(string text) => Convert.ToHexString(algorithm == "MD5" ? MD5.HashData(Encoding.UTF8.GetBytes(text)) : SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var ha1 = Hash($"gateway-out:asterisk:{OutboundPassword}");
        var ha2 = Hash($"MESSAGE:{request.RequestUri}");
        var expected = qop ? Hash($"{ha1}:offline:00000001:{values["cnonce"]}:auth:{ha2}") : Hash($"{ha1}:offline:{ha2}");
        Assert.Equal(expected, values["response"]); Assert.Equal("gateway-out", values["username"]); Assert.Equal("a,b", values["opaque"]);
        Assert.Equal(qop, values.ContainsKey("cnonce"));
        if (qop) { Assert.Equal(32, values["cnonce"].Length); Assert.Equal("00000001", values["nc"]); }
        Assert.DoesNotContain(InboundPassword, proof); Assert.DoesNotContain(OutboundPassword, proof);
        request.SetHeader("Authorization", proof);
        Assert.Null(SipPeerDigest.Create(challenge, request, Peer(algorithms: [algorithm])));
    }

    [Fact]
    public void UnknownRealmOrImplicitMd5CannotLearnOrDowngradeCredentialPolicy()
    {
        var peer = Peer(); var request = Request();
        foreach (var challenge in new[] {
            "Digest realm=\"vowin.local\",nonce=n,algorithm=SHA-256,qop=auth",
            "Digest realm=\"ASTERISK\",nonce=n,algorithm=SHA-256,qop=auth",
            "Digest realm=\"asterisk\",nonce=n,algorithm=MD5,qop=auth",
            "Digest realm=\"asterisk\",nonce=n" })
            Assert.Null(SipPeerDigest.Create(challenge, request, peer));
        Assert.Equal("asterisk", peer.OutboundAuth!.ExpectedRealm);
        Assert.NotNull(SipPeerDigest.Create("Digest realm=\"asterisk\",nonce=n", request, Peer(algorithms: ["MD5"])));
    }

    [Theory]
    [InlineData(401, "WWW-Authenticate")]
    [InlineData(407, "Proxy-Authenticate")]
    public void OnlyOneUnambiguousChallengeCanProduceAuthorization(int status, string name)
    {
        var request = Request(); var response = request.CreateResponse(status, "Authentication");
        const string challenge = "Digest realm=\"asterisk\",nonce=n,algorithm=SHA-256,qop=auth";
        response.AddHeader(name, challenge);
        Assert.NotNull(SipPeerDigest.CreatePreferred(response, request, Peer()));
        response.AddHeader(name, challenge);
        Assert.Null(SipPeerDigest.CreatePreferred(response, request, Peer()));
        response.Headers.Remove(name); response.AddHeader(name, challenge);
        response.AddHeader(status == 401 ? "Proxy-Authenticate" : "WWW-Authenticate", challenge);
        Assert.Null(SipPeerDigest.CreatePreferred(response, request, Peer()));
    }

    [Fact]
    public void SharedDigestSyntaxContractDoesNotGrantCredentialAuthority()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "sip-peer-digest-v1.json")));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        foreach (var test in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            var supported = SipPeerDigest.TryParseChallenge(test.GetProperty("challenge").GetString()!, out var parsed);
            Assert.Equal(test.GetProperty("supported").GetBoolean(), supported);
            if (!supported) continue;
            Assert.Equal(test.GetProperty("realm").GetString(), parsed.Realm); Assert.Equal(test.GetProperty("nonce").GetString(), parsed.Nonce);
            Assert.Equal(test.GetProperty("algorithm").GetString(), parsed.Algorithm); Assert.Equal(test.GetProperty("qop").GetBoolean(), parsed.UseQop);
            Assert.Equal(test.TryGetProperty("opaque", out var opaque) ? opaque.GetString() : null, parsed.Opaque);
        }
        Assert.False(SipPeerDigest.TryParseParameters("realm=\"\",REALM=\"a\",nonce=n", out _));
        Assert.False(SipPeerDigest.TryParseParameters("realm=a junk,nonce=n", out _));
        Assert.False(SipPeerDigest.TryParseParameters("realm=\"a\",nonce=\"n\r\nInjected:x\"", out _));
        Assert.False(SipPeerDigest.TryParseParameters(string.Join(',', Enumerable.Range(0, 33).Select(i => $"field{i}=x")), out _));
        Assert.False(SipPeerDigest.TryParseParameters("realm=\"" + new string('界', 1400) + "\",nonce=n", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IndependentLocalRouteFiltersAreValidated(bool calls)
    {
        var options = Options() with { LineRoutes = new() { ["line"] = calls ? new() { LocalIncomingTarget = "bad\r\ntarget" } : new() { LocalSmsTarget = "sip:1001@foreign" } } };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    private sealed class AddressConverter : JsonConverter<IPAddress>
    {
        public override IPAddress? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
    private sealed class EndpointConverter : JsonConverter<IPEndPoint>
    {
        public override IPEndPoint? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
