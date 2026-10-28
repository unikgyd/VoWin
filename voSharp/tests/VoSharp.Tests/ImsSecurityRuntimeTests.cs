using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class ImsSecurityRuntimeTests
{
    private static readonly IPAddress Local = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Remote = IPAddress.Parse("10.0.0.2");
    private static byte[] Key => Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static SecurityProposal Proposal(string encryption) => new("hmac-sha-1-96", encryption, 1000, 2000, 40000, 40001);
    private static SecurityAgreement Agreement(SecurityProposal proposal) => new(proposal, "verify", 1001, 1002, 41000, 41001);

    // Independent Python fixtures, fixed IV 000102...0f. Reserved SPI 200 was
    // migrated to 2000 with Node HMAC after independently verifying the old MAC;
    // ciphertext and UDP checksums are unchanged (see nested IMS verification).
    [Theory]
    [InlineData("null", "000007d000000001a0289c41000b000061626301020303111267c55af292e2f6f7cc9c87")]
    [InlineData("aes-cbc", "000007d000000001000102030405060708090a0b0c0d0e0f17c808e55fd58344d0b0d130fb78dfceb10421a4c66613e0e3cca50f")]
    public void ProtectedInboundUdpUsesServerSaAndRejectsTamperReplay(string encryption, string fixture)
    {
        using var security = new ImsIpsecTransport(Local, Remote, Proposal(encryption));
        security.Activate(Agreement(security.Proposal), Key, Key);
        var carrierIp = IpPacketUtils.BuildIpv4UdpPacket(Remote, Local, 41000, 40001, []);
        var packet = IpPacketUtils.ReplaceIpv4Payload(carrierIp, 50, Convert.FromHexString(fixture));
        var corrupt = packet.ToArray();
        corrupt[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => security.Unprotect(corrupt));
        var decoded = IpPacketUtils.ParseIpv4UdpPacket(security.Unprotect(packet));
        Assert.Equal("abc"u8.ToArray(), decoded.Payload);
        Assert.Equal(new IPEndPoint(Local, 40001), decoded.LocalEndPoint);
        Assert.Equal(new IPEndPoint(Remote, 41000), decoded.RemoteEndPoint);
        Assert.Throws<FormatException>(() => security.Unprotect(packet));
        var plain = IpPacketUtils.BuildIpv4UdpPacket(Remote, Local, 41000, 40001, [1]);
        Assert.Throws<FormatException>(() => security.Unprotect(plain));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("aes-cbc")]
    [InlineData("des-ede3-cbc")]
    public void OutboundSaUsesPeerSpiAndSeparateSequenceSpaces(string encryption)
    {
        using var security = new ImsIpsecTransport(Local, Remote, Proposal(encryption));
        security.Activate(Agreement(security.Proposal), Key, Key);
        var client = security.Protect(new SipDatagram("request"u8.ToArray(), new(Local, 40000), new(Remote, 41001)));
        var server = security.Protect(new SipDatagram("response"u8.ToArray(), new(Local, 40001), new(Remote, 41000)));
        Assert.Equal(50, client[9]);
        Assert.Equal(1002u, BinaryPrimitives.ReadUInt32BigEndian(client.AsSpan(20)));
        Assert.Equal(1001u, BinaryPrimitives.ReadUInt32BigEndian(server.AsSpan(20)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(client.AsSpan(24)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(server.AsSpan(24)));
        // Reverse the roles to verify both protected UDP legs can be received.
        var peerProposal = new SecurityProposal("hmac-sha-1-96", encryption, 1001, 1002, 41000, 41001);
        using var peer = new ImsIpsecTransport(Remote, Local, peerProposal);
        peer.Activate(new SecurityAgreement(peerProposal, "verify", 1000, 2000, 40000, 40001), Key, Key);
        Assert.Equal("request"u8.ToArray(), IpPacketUtils.ParseIpv4UdpPacket(peer.Unprotect(client)).Payload);
        Assert.Equal("response"u8.ToArray(), IpPacketUtils.ParseIpv4UdpPacket(peer.Unprotect(server)).Payload);
    }

    [Fact]
    public void Ipv6EspRoundTripsBothDirectionsAndRejectsReplay()
    {
        var local = IPAddress.Parse("2001:db8:100::10");
        var remote = IPAddress.Parse("2001:db8:100::20");
        var proposal = Proposal("aes-cbc");
        using var ue = new ImsIpsecTransport(local, remote, proposal);
        ue.Activate(Agreement(proposal), Key, Key);
        var peerProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            1001, 1002, 41000, 41001);
        using var pcscf = new ImsIpsecTransport(remote, local, peerProposal);
        pcscf.Activate(new SecurityAgreement(peerProposal, "verify", 1000, 2000,
            40000, 40001), Key, Key);

        var outgoing = ue.Protect(new SipDatagram("REGISTER"u8.ToArray(),
            new(local, 40000), new(remote, 41001)));
        Assert.Equal(6, outgoing[0] >> 4);
        Assert.Equal(50, outgoing[6]);
        Assert.Equal(1002u, BinaryPrimitives.ReadUInt32BigEndian(outgoing.AsSpan(40)));
        Assert.Equal("REGISTER"u8.ToArray(),
            IpPacketUtils.ParseUdpPacket(pcscf.Unprotect(outgoing)).Payload);
        Assert.Throws<FormatException>(() => pcscf.Unprotect(outgoing));

        var incoming = pcscf.Protect(new SipDatagram("200 OK"u8.ToArray(),
            new(remote, 41000), new(local, 40001)));
        Assert.Equal(50, incoming[6]);
        Assert.Equal(2000u, BinaryPrimitives.ReadUInt32BigEndian(incoming.AsSpan(40)));
        Assert.Equal("200 OK"u8.ToArray(),
            IpPacketUtils.ParseUdpPacket(ue.Unprotect(incoming)).Payload);
        Assert.Throws<FormatException>(() => ue.Unprotect(incoming));
    }

    [Fact]
    public void Ipv6InboundEspMatchesIndependentAesCbcHmacSha1Fixture()
    {
        // Independently generated with Python cryptography AES-CBC and stdlib
        // hmac, using the RFC 3602 ESP layout and TS 33.203 Annex I keys.
        // It is not produced by ImsIpsecTransport or IpPacketUtils.
        const string packetHex =
            "600000000044324020010db8010000000000000000000020" +
            "20010db8010000000000000000000010" +
            "000007d000000001000102030405060708090a0b0c0d0e0f" +
            "0a1c6abfa9100dc9c352b5e058dbb4fc" +
            "ca29caa71dc356bd8fd0d6029c4f1540" +
            "83d19cc53106db2774fe5123";
        var local = IPAddress.Parse("2001:db8:100::10");
        var remote = IPAddress.Parse("2001:db8:100::20");
        using var ue = new ImsIpsecTransport(local, remote, Proposal("aes-cbc"));
        ue.Activate(Agreement(ue.Proposal), Key, Key);

        var packet = Convert.FromHexString(packetHex);
        Assert.Equal(50, packet[6]);
        Assert.DoesNotContain("IMS-IPv6-vector"u8.ToArray(), packet);
        var clear = ue.Unprotect(packet);
        Assert.Equal(0x9069, BinaryPrimitives.ReadUInt16BigEndian(clear.AsSpan(46, 2)));
        var datagram = IpPacketUtils.ParseUdpPacket(clear);
        Assert.Equal("IMS-IPv6-vector"u8.ToArray(), datagram.Payload);
        Assert.Equal(new IPEndPoint(local, 40001), datagram.LocalEndPoint);
        Assert.Equal(new IPEndPoint(remote, 41000), datagram.RemoteEndPoint);
        Assert.Throws<FormatException>(() => ue.Unprotect(packet));
    }

    [Theory]
    [InlineData("hmac-sha-1-96", "aes-cbc")]
    [InlineData("hmac-sha-1-96", "des-ede3-cbc")]
    [InlineData("hmac-sha-1-96", "null")]
    [InlineData("hmac-md5-96", "aes-cbc")]
    [InlineData("hmac-md5-96", "des-ede3-cbc")]
    [InlineData("hmac-md5-96", "null")]
    public void Ipv6OutboundEspMatchesExternalOpenSslProgramWhenRequested(
        string integrity, string encryption)
    {
        // Opt in with an absolute path to a native OpenSSL executable. This
        // checks bytes produced by the C# ESP implementation, not a fixture
        // generated by another copy of our codec.
        var openssl = Environment.GetEnvironmentVariable("VOWIN_TEST_OPENSSL_EXE");
        if (string.IsNullOrWhiteSpace(openssl)) return;
        Assert.True(Path.IsPathFullyQualified(openssl) && File.Exists(openssl),
            "VOWIN_TEST_OPENSSL_EXE must name an existing absolute executable.");

        var local = IPAddress.Parse("2001:db8:100::10");
        var remote = IPAddress.Parse("2001:db8:100::20");
        var proposal = Proposal(encryption) with { IntegrityAlgorithm = integrity };
        using var ue = new ImsIpsecTransport(local, remote, proposal);
        ue.Activate(Agreement(ue.Proposal), Key, Key);
        var payload = "OPENSSL-ESP-CROSS-CHECK"u8.ToArray();
        var packet = ue.Protect(new SipDatagram(payload, new(local, 40000), new(remote, 41001)));
        Assert.Equal(50, packet[6]);
        var esp = packet.AsSpan(40);
        Assert.Equal(1002u, BinaryPrimitives.ReadUInt32BigEndian(esp));
        var ivLength = encryption switch { "null" => 0, "des-ede3-cbc" => 8, _ => 16 };
        var iv = esp.Slice(8, ivLength).ToArray();
        var cipher = esp.Slice(8 + ivLength, esp.Length - 8 - ivLength - 12).ToArray();
        var (integrityKey, encryptionKey) = SecurityAgreementBuilder.ExpandKeys(
            Key, Key, integrity, encryption);
        try
        {
            var hash = integrity == "hmac-md5-96" ? "-md5" : "-sha1";
            var hmac = RunOpenSsl(openssl, ["dgst", hash, "-mac", "HMAC", "-macopt",
                "hexkey:" + Convert.ToHexString(integrityKey), "-binary"],
                esp[..^12].ToArray());
            Assert.Equal(esp[^12..].ToArray(), hmac.AsSpan(0, 12).ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(integrityKey);
        }

        byte[] clear;
        try
        {
            clear = encryption == "null" ? cipher : RunOpenSsl(openssl,
                ["enc", "-d", encryption == "aes-cbc" ? "-aes-128-cbc" : "-des-ede3-cbc",
                    "-nopad", "-K", Convert.ToHexString(encryptionKey), "-iv", Convert.ToHexString(iv)],
                cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
        }
        var expectedUdp = IpPacketUtils.BuildUdpPacket(local, remote, 40000, 41001, payload).AsSpan(40);
        Assert.Equal(expectedUdp.ToArray(), clear.AsSpan(0, expectedUdp.Length).ToArray());
        Assert.Equal(17, clear[^1]);
        Assert.Equal(clear.Length - expectedUdp.Length - 2, clear[^2]);
        for (var i = 0; i < clear[^2]; i++) Assert.Equal(i + 1, clear[expectedUdp.Length + i]);
    }

    private static byte[] RunOpenSsl(string executable, string[] arguments, byte[] input)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL did not start.");
        process.StandardInput.BaseStream.Write(input);
        process.StandardInput.Close();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"OpenSSL exited {process.ExitCode}: {error}");
        return output.ToArray();
    }

    [Fact]
    public void ProtectedEspRejectsUnnegotiatedRemoteUdpPort()
    {
        using var ue = new ImsIpsecTransport(Local, Remote, Proposal("aes-cbc"));
        ue.Activate(Agreement(ue.Proposal), Key, Key);
        Assert.Throws<InvalidOperationException>(() => ue.Protect(new SipDatagram(
            "request"u8.ToArray(), new(Local, 40000), new(Remote, 41002))));

        // Build an authenticated ESP packet from a peer whose server port is
        // different from the one the UE negotiated. Correct SPI/key and UE
        // destination port alone must not make the packet acceptable.
        var peerProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
            1001, 1002, 41000, 41002);
        using var peer = new ImsIpsecTransport(Remote, Local, peerProposal);
        peer.Activate(new SecurityAgreement(peerProposal, "verify", 1000, 2000,
            40000, 40001), Key, Key);
        var wrongPortEsp = peer.Protect(new SipDatagram(
            "response"u8.ToArray(), new(Remote, 41002), new(Local, 40000)));
        Assert.Throws<FormatException>(() => ue.Unprotect(wrongPortEsp));

        // Rejecting a valid-MAC packet for the wrong selector must not consume
        // sequence 1 from the inbound SA's anti-replay window.
        var expectedPeerProposal = peerProposal with { PortServer = 41001 };
        using var expectedPeer = new ImsIpsecTransport(Remote, Local, expectedPeerProposal);
        expectedPeer.Activate(new SecurityAgreement(expectedPeerProposal, "verify",
            1000, 2000, 40000, 40001), Key, Key);
        var validEsp = expectedPeer.Protect(new SipDatagram(
            "accepted"u8.ToArray(), new(Remote, 41001), new(Local, 40000)));
        Assert.Equal("accepted"u8.ToArray(), IpPacketUtils.ParseUdpPacket(
            ue.Unprotect(validEsp)).Payload);
    }

    [Fact]
    public async Task RegistrationActivatesSecurityBeforeAuthenticatedRequestAndKeepsAllRoutes()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var proposal = Proposal("aes-cbc");
        const string securityHeader = "ipsec-3gpp;alg=hmac-sha-1-96;ealg=aes-cbc;prot=esp;mod=trans;spi-c=1001;spi-s=1002;port-c=41000;port-s=41001";
        bool activated = false;
        int count = 0;
        var profile = new ImsProfile("ue@example.test", "sip:ue@example.test", "example.test", "123456789012345", "10.0.0.1");
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            var request = SipMessage.Parse(bytes);
            var response = request.CreateResponse(++count == 1 ? 401 : 200, count == 1 ? "Unauthorized" : "OK");
            if (count == 1)
            {
                Assert.NotNull(request.GetHeader("Security-Client"));
                Assert.Equal("sec-agree", request.GetHeader("Require"));
                Assert.Equal("sec-agree", request.GetHeader("Proxy-Require"));
                var identityAuthorization = request.GetHeader("Authorization");
                Assert.NotNull(identityAuthorization);
                Assert.Contains("algorithm=AKAv1-MD5", identityAuthorization, StringComparison.Ordinal);
                Assert.Contains("integrity-protected=no", identityAuthorization, StringComparison.Ordinal);
                response.SetHeader("WWW-Authenticate", $"Digest realm=\"example.test\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
                response.SetHeader("Security-Server", securityHeader);
            }
            else
            {
                Assert.True(activated);
                Assert.Equal(securityHeader, request.GetHeader("Security-Verify"));
                Assert.Contains(":40001;", request.GetHeader("Via"));
                Assert.Contains(":40001;", request.GetHeader("Contact"));
                Assert.Contains("integrity-protected=yes", request.GetHeader("Authorization"));
                response.AddHeader("Contact", "<sip:another@10.0.0.3>;expires=0");
                response.AddHeader("Contact", request.GetHeader("Contact")!);
                response.AddHeader("Service-Route", "<sip:first.example.test;lr>");
                response.AddHeader("Service-Route", "<sip:second.example.test;lr>");
            }
            input.Writer.TryWrite(response.ToBytes());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());
        using var session = new SipRegisterSession(transport, profile, proposal,
            (agreement, ck, ik) => { activated = true; Assert.Equal(proposal.SpiServer, agreement.Selected.SpiServer); Assert.Equal(Key, ck); });
        var result = await session.RegisterAsync((_, _, _) => Task.FromResult((new byte[8], Key, Key)));
        Assert.True(result.SmsCapabilityConfirmed);
        Assert.Contains("first.example.test", result.ServiceRoute);
        Assert.Contains("second.example.test", result.ServiceRoute);
        var rpAck = new SipMessage { IsRequest = true, Method = "MESSAGE" };
        session.AddSecurityHeaders(rpAck);
        Assert.Equal("sec-agree", rpAck.GetHeader("Require"));
        Assert.Equal("sec-agree", rpAck.GetHeader("Proxy-Require"));
        Assert.Equal(securityHeader, rpAck.GetHeader("Security-Verify"));
    }

    [Fact]
    public async Task RegistrationRefreshesWithNewCseqBeforeGrantedExpiry()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var cseqs = new List<string>();
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            var request = SipMessage.Parse(bytes);
            lock (cseqs) cseqs.Add(request.GetHeader("CSeq")!);
            var response = request.CreateResponse(200, "OK");
            response.SetHeader("Contact", request.GetHeader("Contact")!.Replace("expires=3600", "expires=1"));
            input.Writer.TryWrite(response.ToBytes());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());
        using var session = new SipRegisterSession(transport, new("ue@example.test", "sip:ue@example.test", "example.test", "123456789012345", "10.0.0.1"));
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___) => throw new InvalidOperationException("No challenge expected");
        var refreshed = new TaskCompletionSource<SipRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RegistrationRefreshed += (_, result) => refreshed.TrySetResult(result);
        await session.RegisterAsync(Aka);
        session.StartRefreshing(Aka);
        var result = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopRefreshingAsync();
        Assert.True(result.SmsCapabilityConfirmed);
        Assert.Equal(1, result.ExpiresSeconds);
        Assert.Equal(new[] { "1 REGISTER", "2 REGISTER" }, cseqs);
    }

    [Fact]
    public async Task RegistrationRefreshRetriesTransientTransportFailureBeforeGrantedExpiry()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var requestCount = 0;
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            var request = SipMessage.Parse(bytes);
            var current = Interlocked.Increment(ref requestCount);
            // A local send failure can be retried on this flow. SIP 500 cannot:
            // TS 24.229 requires initial-registration recovery for that response.
            if (current == 2) throw new IOException("Temporary local send failure");
            var response = request.CreateResponse(200, "OK");
            if (response.StatusCode == 200)
                response.SetHeader("Contact", request.GetHeader("Contact")!.Replace("expires=3600", "expires=1"));
            input.Writer.TryWrite(response.ToBytes());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());

        using var session = new SipRegisterSession(transport,
            new("ue@example.test", "sip:ue@example.test", "example.test", "123456789012345", "10.0.0.1"));
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No challenge expected");
        var refreshed = new TaskCompletionSource<SipRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RegistrationRefreshed += (_, result) => refreshed.TrySetResult(result);

        await session.RegisterAsync(Aka);
        session.StartRefreshing(Aka);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopRefreshingAsync();

        Assert.Equal(3, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task LongGrantedRegistrationDoesNotFailRefreshSchedulerImmediately()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        var requestCount = 0;
        using var transport = new SipTransport();
        transport.ConnectCustom(bytes =>
        {
            Interlocked.Increment(ref requestCount);
            var request = SipMessage.Parse(bytes);
            var response = request.CreateResponse(200, "OK");
            response.SetHeader("Contact", request.GetHeader("Contact")!
                .Replace("expires=3600", $"expires={int.MaxValue}", StringComparison.Ordinal));
            input.Writer.TryWrite(response.ToBytes());
            return Task.CompletedTask;
        }, ct => input.Reader.ReadAsync(ct).AsTask());
        using var session = new SipRegisterSession(transport,
            new("ue@example.test", "sip:ue@example.test", "example.test", "123456789012345", "10.0.0.1"));
        Task<(byte[], byte[], byte[])> Aka(byte[] _, byte[] __, CancellationToken ___) =>
            throw new InvalidOperationException("No challenge expected");
        var failureCount = 0;
        session.RegistrationFailed += (_, _) => Interlocked.Increment(ref failureCount);

        var result = await session.RegisterAsync(Aka);
        Assert.Equal(int.MaxValue, result.ExpiresSeconds);
        session.StartRefreshing(Aka);
        await Task.Delay(200);
        Assert.NotNull(session.LastResult);
        Assert.Equal(0, Volatile.Read(ref failureCount));
        Assert.Equal(1, Volatile.Read(ref requestCount));
        await session.StopRefreshingAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
}
