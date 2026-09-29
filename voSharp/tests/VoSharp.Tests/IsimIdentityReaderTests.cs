using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using VoSharp.Common.Aka;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sip;
using VoSharp.Sim;
using VoSharp.Telephony;

namespace VoSharp.Tests;

public sealed class IsimIdentityReaderTests
{
    [Fact]
    public async Task ReadsProvisionedImpiDomainAndDefaultImpuAndClosesChannel()
    {
        var card = new FakeIsimSession(channelId: 4);
        var result = await new IsimIdentityReader(card).ReadAsync();

        Assert.Equal(IsimIdentityReadiness.Available, result.Readiness);
        Assert.Equal("001010123456789@ims.mnc001.mcc001.3gppnetwork.org", result.Identity!.PrivateIdentity);
        Assert.Equal("ims.mnc001.mcc001.3gppnetwork.org", result.Identity.HomeDomain);
        Assert.Equal("sip:+15551234567@ims.mnc001.mcc001.3gppnetwork.org", result.Identity.DefaultPublicIdentity);
        Assert.Contains(card.Commands, command => command.Contains("40A40004026F02", StringComparison.Ordinal));
        Assert.Contains(card.Commands, command => command.Contains("40B20104", StringComparison.Ordinal));
        Assert.Contains("AT+CCHC=4", card.Commands);
    }

    [Fact]
    public async Task NoSimDoesNotOpenApplicationOrInventIdentity()
    {
        var card = new FakeIsimSession(channelId: 1) { HasSim = false };
        var result = await new IsimIdentityReader(card).ReadAsync();

        Assert.Equal(IsimIdentityReadiness.SimUnavailable, result.Readiness);
        Assert.Null(result.Identity);
        Assert.Equal(["AT+CPIN?"], card.Commands);
    }

    [Fact]
    public async Task MalformedMandatoryIdentityFailsClosedAndClosesChannel()
    {
        var card = new FakeIsimSession(channelId: 1) { CorruptImpu = true };
        var result = await new IsimIdentityReader(card).ReadAsync();

        Assert.Equal(IsimIdentityReadiness.ReadFailed, result.Readiness);
        Assert.Null(result.Identity);
        Assert.Contains("AT+CCHC=1", card.Commands);
    }

    [Fact]
    public async Task DiscoversFullIsimAidWhenPrefixCannotOpenTheCard()
    {
        var card = new FakeIsimSession(channelId: 1) { RequireFullAid = true };
        var result = await new IsimIdentityReader(card).ReadAsync();

        Assert.Equal(IsimIdentityReadiness.Available, result.Readiness);
        Assert.Contains("AT+CCHO=\"A0000000871004A1B2\"", card.Commands);
        Assert.DoesNotContain("AT+CCHO=\"A0000000871004\"", card.Commands);
    }

    [Fact]
    public async Task DistinguishesAbsentIsimFromUnreadableIsim()
    {
        var usimOnly = new FakeIsimSession(channelId: 1) { UsimOnly = true };
        var absent = await new IsimIdentityReader(usimOnly).ReadAsync();
        Assert.Equal(IsimIdentityReadiness.ApplicationAbsent, absent.Readiness);

        var unreadable = new FakeIsimSession(channelId: 1) { IsimOpenFails = true };
        var failure = await new IsimIdentityReader(unreadable).ReadAsync();
        Assert.Equal(IsimIdentityReadiness.ApplicationUnavailable, failure.Readiness);

        var malformed = new FakeIsimSession(channelId: 1) { IsimOpenFails = true, BadDirectory = true };
        var badDirectory = await new IsimIdentityReader(malformed).ReadAsync();
        Assert.Equal(IsimIdentityReadiness.ApplicationUnavailable, badDirectory.Readiness);
    }

    [Fact]
    public void UsimOnlyIdentityRequiresAuthoritativeNonConflictingHomePlmn()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150999999999", "89860012345678901234", mncLength: 2);
        var profile = HostImsRegistrationClient.BuildUsimFallbackProfile(sim,
            "123456789012345", "127.0.0.1");
        Assert.Equal("234150999999999@ims.mnc015.mcc234.3gppnetwork.org", profile.PrivateIdentity);
        Assert.Equal("sip:234150999999999@ims.mnc015.mcc234.3gppnetwork.org", profile.PublicIdentity);
        Assert.Throws<InvalidOperationException>(() => HostImsRegistrationClient.BuildUsimFallbackProfile(
            sim with { IsHomePlmnAuthoritative = false }, "123456789012345", "127.0.0.1"));
        Assert.Throws<InvalidOperationException>(() => HostImsRegistrationClient.BuildUsimFallbackProfile(
            sim with { HasPlmnConflict = true }, "123456789012345", "127.0.0.1"));
        Assert.Throws<InvalidOperationException>(() => HostImsRegistrationClient.BuildUsimFallbackProfile(
            sim with { Mnc = "160" }, "123456789012345", "127.0.0.1"));
        Assert.Throws<InvalidOperationException>(() => HostImsRegistrationClient.BuildUsimFallbackProfile(
            sim with { Iccid = "" }, "123456789012345", "127.0.0.1"));
    }

    [Fact]
    public void RejectsInvalidIdentityTlvAndAcceptsExtendedLength()
    {
        Assert.Null(IsimIdentityReader.DecodeIdentityTlv([0x80, 0x05, 0x41]));
        Assert.Null(IsimIdentityReader.DecodeIdentityTlv([0x81, 0x01, 0x41]));
        Assert.Null(IsimIdentityReader.DecodeIdentityTlv([0x80, 0x02, 0xC3, 0x28]));
        Assert.Equal("abc", IsimIdentityReader.DecodeIdentityTlv([0x80, 0x81, 0x03, 0x61, 0x62, 0x63, 0xFF]));
    }

    [Fact]
    public async Task RunsImsAkaOnSelectedIsimLogicalChannel()
    {
        var card = new FakeIsimSession(channelId: 4);
        var result = await new IsimIdentityReader(card).AuthenticateAsync(
            AkaChallenge.Create(new byte[16], new byte[16]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(8, result.Res!.Length);
        Assert.Equal(16, result.Ck!.Length);
        Assert.Equal(16, result.Ik!.Length);
        Assert.Contains(card.Commands, command => command.Contains("40880081", StringComparison.Ordinal));
        Assert.Contains("AT+CCHC=4", card.Commands);
        result.Clear();
    }

    [Fact]
    public void ClearingAkaSynchronizationFailureAlsoWipesAuts()
    {
        var auts = Enumerable.Repeat((byte)0xA5, 14).ToArray();
        var result = AkaResult.SyncFailed(auts);
        result.Clear();
        Assert.All(auts, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task IsimAkaSynchronizationFailureIsNotMisreportedAsSuccess()
    {
        var card = new FakeIsimSession(channelId: 1) { SynchronizationFailure = true };
        var result = await new IsimIdentityReader(card).AuthenticateAsync(
            AkaChallenge.Create(new byte[16], new byte[16]));

        Assert.False(result.Success);
        Assert.True(result.SynchronizationFailure);
        Assert.Equal(14, result.Auts!.Length);
        result.Clear();
    }

    [Fact]
    public async Task HostImsRegistrationUsesCardImpuAndIsimAkaThrough401Challenge()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = new HostImsProbeResult(HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        };
        probe = WithVerifiedRoute(probe);
        var card = new FakeIsimSession(channelId: 4);
        var serverTask = Task.Run(async () =>
        {
            var first = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var initial = SipMessage.Parse(first.Buffer);
            var challenge = initial.CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.mnc001.mcc001.3gppnetwork.org\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(challenge.ToBytes(), first.RemoteEndPoint);

            var second = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var authenticated = SipMessage.Parse(second.Buffer);
            var accepted = authenticated.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", authenticated.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), second.RemoteEndPoint);
            return (initial, authenticated);
        });

        await using var client = await HostImsRegistrationClient.RegisterWithIsimAsync(
            card, _ => Task.FromResult("123456789012345"), probe,
            Assert.Single(probe.EndpointCandidates), localPort: 0, pcscfPort: pcscfPort);
        var (initial, authenticated) = await serverTask;
        Assert.Contains("sip:+15551234567@ims.mnc001.mcc001.3gppnetwork.org", initial.GetHeader("From"));
        Assert.Contains("001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
            authenticated.GetHeader("Authorization"));
        Assert.Contains(card.Commands, command => command.Contains("40880081", StringComparison.Ordinal));
        Assert.True(client.Result.ExpiresSeconds > 0);
        Assert.True(client.IsRegistered);
        Assert.Equal("AT ISIM", client.CardPath);
        Assert.NotNull(client.CurrentResult);
    }

    [Fact]
    public async Task HostImsRegistrationPrefersQmiIsimAndDoesNotRetryAkaViaAt()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(HostImsReadiness.HostRoutable,
            true, "QMI", [context], "candidate") { SimInserted = true });
        var card = new FakeIsimSession(channelId: 4);
        var sim = SimIdentity.FromImsiAndIccid("001010123456789", "89860012345678901234", mncLength: 2);
        var identity = new IsimIdentity("001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
            "ims.mnc001.mcc001.3gppnetwork.org", "sip:+15551234567@ims.mnc001.mcc001.3gppnetwork.org");
        var qmiReads = 0;
        var qmiAka = 0;
        var serverTask = Task.Run(async () =>
        {
            var first = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var challenge = SipMessage.Parse(first.Buffer).CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.mnc001.mcc001.3gppnetwork.org\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(challenge.ToBytes(), first.RemoteEndPoint);
            var second = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var authenticated = SipMessage.Parse(second.Buffer);
            var accepted = authenticated.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", authenticated.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), second.RemoteEndPoint);
        });

        await using var client = await HostImsRegistrationClient.RegisterWithCardAsync(
            card, _ => Task.FromResult("123456789012345"), sim, probe,
            Assert.Single(probe.EndpointCandidates), localPort: 0, pcscfPort: pcscfPort,
            tryReadQmiIsim: _ =>
            {
                qmiReads++;
                return Task.FromResult<IsimIdentityProbeResult?>(
                    new(IsimIdentityReadiness.Available, identity, "QMI UIM ISIM"));
            },
            authenticateQmiIsim: (_, _) =>
            {
                qmiAka++;
                return Task.FromResult(AkaResult.Succeeded(new byte[8], new byte[16], new byte[16]));
            });
        await serverTask;
        Assert.Equal(2, qmiReads);
        Assert.Equal(1, qmiAka);
        Assert.Equal("QMI ISIM", client.CardPath);
        Assert.DoesNotContain(card.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal));
        Assert.DoesNotContain(card.Commands, command => command.StartsWith("AT+CCHO=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QmiAkaFailureDoesNotReplayChallengeThroughAt()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(HostImsReadiness.HostRoutable,
            true, "QMI", [context], "candidate") { SimInserted = true });
        var card = new FakeIsimSession(channelId: 4);
        var sim = SimIdentity.FromImsiAndIccid("001010123456789", "89860012345678901234", mncLength: 2);
        var identity = new IsimIdentity("001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
            "ims.mnc001.mcc001.3gppnetwork.org", "sip:+15551234567@ims.mnc001.mcc001.3gppnetwork.org");
        var qmiAka = 0;
        var serverTask = Task.Run(async () =>
        {
            var first = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var challenge = SipMessage.Parse(first.Buffer).CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.mnc001.mcc001.3gppnetwork.org\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(challenge.ToBytes(), first.RemoteEndPoint);
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostImsRegistrationClient.RegisterWithCardAsync(card,
                _ => Task.FromResult("123456789012345"), sim, probe,
                Assert.Single(probe.EndpointCandidates), localPort: 0, pcscfPort: pcscfPort,
                tryReadQmiIsim: _ => Task.FromResult<IsimIdentityProbeResult?>(
                    new(IsimIdentityReadiness.Available, identity, "QMI UIM ISIM")),
                authenticateQmiIsim: (_, _) =>
                {
                    qmiAka++;
                    return Task.FromResult(AkaResult.Failed("simulated QMI card rejection"));
                }));
        await serverTask;
        Assert.Contains("simulated QMI card rejection", failure.Message);
        Assert.Equal(1, qmiAka);
        Assert.DoesNotContain(card.Commands, command => command.StartsWith("AT+CCHO=", StringComparison.Ordinal));
        Assert.DoesNotContain(card.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostImsRegistrationUsesUsimAkaOnlyWhenIsimIsAbsent()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = new HostImsProbeResult(HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        };
        probe = WithVerifiedRoute(probe);
        var card = new FakeIsimSession(channelId: 1) { UsimOnly = true };
        var sim = SimIdentity.FromImsiAndIccid("234150999999999", "89860012345678901234", mncLength: 2);
        var qmiUsimAttempts = 0;
        var serverTask = Task.Run(async () =>
        {
            var first = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var initial = SipMessage.Parse(first.Buffer);
            var challenge = initial.CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.mnc015.mcc234.3gppnetwork.org\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(challenge.ToBytes(), first.RemoteEndPoint);

            var second = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var authenticated = SipMessage.Parse(second.Buffer);
            var accepted = authenticated.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", authenticated.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), second.RemoteEndPoint);
            return (initial, authenticated);
        });

        await using var client = await HostImsRegistrationClient.RegisterWithCardAsync(
            card, _ => Task.FromResult("123456789012345"), sim, probe,
            Assert.Single(probe.EndpointCandidates), localPort: 0, pcscfPort: pcscfPort,
            tryQmiUsim: _ =>
            {
                qmiUsimAttempts++;
                return Task.FromResult(false);
            },
            authenticateQmiUsim: (_, _) => throw new InvalidOperationException("QMI AKA must not run"));
        var (initial, authenticated) = await serverTask;
        Assert.Equal(1, qmiUsimAttempts);
        Assert.Contains("sip:234150999999999@ims.mnc015.mcc234.3gppnetwork.org", initial.GetHeader("From"));
        Assert.Contains("234150999999999@ims.mnc015.mcc234.3gppnetwork.org",
            authenticated.GetHeader("Authorization"));
        Assert.Contains(card.Commands, command => command.Contains("01880081", StringComparison.Ordinal));
        Assert.True(client.IsRegistered);
        Assert.Equal("AT USIM", client.CardPath);
    }

    [Fact]
    public async Task HostImsUsimFallbackPrefersQmiAkaAfterPreflight()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(HostImsReadiness.HostRoutable,
            true, "QMI", [context], "candidate") { SimInserted = true });
        var card = new FakeIsimSession(channelId: 1) { UsimOnly = true };
        var sim = SimIdentity.FromImsiAndIccid("234150999999999", "89860012345678901234", mncLength: 2);
        var qmiAka = 0;
        var serverTask = Task.Run(async () =>
        {
            var first = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var challenge = SipMessage.Parse(first.Buffer).CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.mnc015.mcc234.3gppnetwork.org\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(challenge.ToBytes(), first.RemoteEndPoint);
            var second = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var authenticated = SipMessage.Parse(second.Buffer);
            var accepted = authenticated.CreateResponse(200, "OK");
            accepted.SetHeader("Contact", authenticated.GetHeader("Contact")!);
            await pcscf.SendAsync(accepted.ToBytes(), second.RemoteEndPoint);
        });

        await using var client = await HostImsRegistrationClient.RegisterWithCardAsync(
            card, _ => Task.FromResult("123456789012345"), sim, probe,
            Assert.Single(probe.EndpointCandidates), localPort: 0, pcscfPort: pcscfPort,
            tryQmiUsim: _ => Task.FromResult(true),
            authenticateQmiUsim: (_, _) =>
            {
                qmiAka++;
                return Task.FromResult(AkaResult.Succeeded(new byte[8], new byte[16], new byte[16]));
            });
        await serverTask;
        Assert.True(client.IsRegistered);
        Assert.Equal("QMI USIM", client.CardPath);
        Assert.Equal(1, qmiAka);
        Assert.DoesNotContain(card.Commands, command => command.Contains("01880081", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsimFallbackRejectsWrongSelectedCardBeforeSendingSip()
    {
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = new HostImsProbeResult(HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        };
        probe = WithVerifiedRoute(probe);
        var card = new FakeIsimSession(channelId: 1) { UsimOnly = true };
        var staleSim = SimIdentity.FromImsiAndIccid("234150999999999", "89860012345678909999", mncLength: 2);
        var qmiAttempts = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostImsRegistrationClient.RegisterWithCardAsync(card,
                _ => Task.FromResult("123456789012345"), staleSim, probe,
                Assert.Single(probe.EndpointCandidates), localPort: 0,
                tryReadQmiIsim: _ =>
                {
                    qmiAttempts++;
                    return Task.FromResult<IsimIdentityProbeResult?>(null);
                },
                authenticateQmiIsim: (_, _) => throw new InvalidOperationException("QMI AKA must not run")));
        Assert.Equal(1, qmiAttempts);
        Assert.Contains("EF.ICCID", failure.Message);
        Assert.DoesNotContain(card.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsimRegistrationRejectsQmiAtCardMismatchBeforeSendingSip()
    {
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(HostImsReadiness.HostRoutable,
            true, "MBIM", [context], "candidate") { SimInserted = true });
        var card = new FakeIsimSession(channelId: 1);
        var staleSim = SimIdentity.FromImsiAndIccid("234150999999999", "89860012345678909999", mncLength: 2);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostImsRegistrationClient.RegisterWithCardAsync(card,
                _ => Task.FromResult("123456789012345"), staleSim, probe,
                Assert.Single(probe.EndpointCandidates), localPort: 0));
        Assert.Contains("EF.ICCID", failure.Message);
        Assert.DoesNotContain(card.Commands, command => command.Contains("01880081", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsimRegistrationRejectsCardSwitchBeforeAka()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = WithVerifiedRoute(new HostImsProbeResult(HostImsReadiness.HostRoutable,
            true, "MBIM", [context], "candidate") { SimInserted = true });
        var card = new FakeIsimSession(channelId: 1) { ChangeIccidAfterFirstRead = true };
        var sim = SimIdentity.FromImsiAndIccid("234150999999999", "89860012345678901234", mncLength: 2);
        var serverTask = Task.Run(async () =>
        {
            var first = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var challenge = SipMessage.Parse(first.Buffer).CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.mnc001.mcc001.3gppnetwork.org\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(challenge.ToBytes(), first.RemoteEndPoint);
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostImsRegistrationClient.RegisterWithCardAsync(card,
                _ => Task.FromResult("123456789012345"), sim, probe,
                Assert.Single(probe.EndpointCandidates), localPort: 0, pcscfPort: pcscfPort));
        await serverTask;
        Assert.Contains("EF.ICCID", failure.Message);
        Assert.DoesNotContain(card.Commands, command => command.Contains("01880081", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostImsRegistrationStopsBeforeAkaIfIsimIdentityChanges()
    {
        using var pcscf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var pcscfPort = ((IPEndPoint)pcscf.Client.LocalEndPoint!).Port;
        var context = new HostImsPdnContext(2, "IP", "ims", true,
            [IPAddress.Loopback], [], [], [IPAddress.Loopback], ["test cellular interface"])
        {
            HostOwnedAddresses = [IPAddress.Loopback]
        };
        var probe = new HostImsProbeResult(HostImsReadiness.HostRoutable, true, "MBIM", [context], "candidate")
        {
            SimInserted = true
        };
        probe = WithVerifiedRoute(probe);
        var card = new FakeIsimSession(channelId: 1) { ChangeIdentityAfterFirstRead = true };
        var serverTask = Task.Run(async () =>
        {
            var first = await pcscf.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var challenge = SipMessage.Parse(first.Buffer).CreateResponse(401, "Unauthorized");
            challenge.SetHeader("WWW-Authenticate",
                $"Digest realm=\"ims.mnc001.mcc001.3gppnetwork.org\", nonce=\"{Convert.ToBase64String(new byte[32])}\", algorithm=AKAv1-MD5, qop=\"auth\"");
            await pcscf.SendAsync(challenge.ToBytes(), first.RemoteEndPoint);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostImsRegistrationClient.RegisterWithIsimAsync(card,
                _ => Task.FromResult("123456789012345"), probe,
                Assert.Single(probe.EndpointCandidates), localPort: 0, pcscfPort: pcscfPort));
        await serverTask;
        Assert.Contains("identity changed", error.Message);
        Assert.DoesNotContain(card.Commands, command => command.Contains("01880081", StringComparison.Ordinal));
    }

    private static HostImsProbeResult WithVerifiedRoute(HostImsProbeResult probe) => probe with
    {
        RouteChecks = probe.EndpointCandidates.Select(endpoint =>
            new HostImsRouteCheck(endpoint, true, "test cellular interface",
                OperatingSystem.IsWindows() ? GetLoopbackIndex(endpoint.LocalAddress.AddressFamily) : 1,
                "Verified")).ToArray()
    };

    private static int GetLoopbackIndex(AddressFamily family)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .First(item => item.NetworkInterfaceType == NetworkInterfaceType.Loopback &&
                item.GetIPProperties().UnicastAddresses.Any(address =>
                    address.Address.AddressFamily == family && IPAddress.IsLoopback(address.Address)));
        var properties = nic.GetIPProperties();
        return family == AddressFamily.InterNetwork
            ? properties.GetIPv4Properties().Index
            : properties.GetIPv6Properties().Index;
    }

    private sealed class FakeIsimSession(int channelId) : IAtSession
    {
        private byte _selectedFile;
        private int _impuReads;
        private int _iccidReads;
        public bool IsOpen => true;
        public bool HasSim { get; set; } = true;
        public bool CorruptImpu { get; set; }
        public bool RequireFullAid { get; set; }
        public bool SynchronizationFailure { get; set; }
        public bool ChangeIdentityAfterFirstRead { get; set; }
        public bool ChangeIccidAfterFirstRead { get; set; }
        public bool UsimOnly { get; set; }
        public bool IsimOpenFails { get; set; }
        public bool BadDirectory { get; set; }
        public List<string> Commands { get; } = [];

        public Task<AtResponse> ExecuteCommandAsync(string command, int timeoutMs = 2000, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (command == "AT+CPIN?")
                return Task.FromResult(HasSim
                    ? Reply("+CPIN: READY")
                    : new AtResponse(false, [], "+CME ERROR: 10"));
            if (command == "AT+CUAD")
                return Task.FromResult(BadDirectory
                    ? Reply("+CUAD: \"4F07FFFFFFFFFFFFFF\"")
                    : UsimOnly
                    ? Reply("+CUAD: \"4F07A0000000871002\"")
                    : RequireFullAid
                        ? Reply("+CUAD: \"4F09A0000000871004A1B25000\"")
                        : new AtResponse(false, [], "ERROR"));
            if (command.StartsWith("AT+CCHO=", StringComparison.Ordinal))
            {
                if ((UsimOnly || IsimOpenFails) && command.Contains("A0000000871004", StringComparison.Ordinal))
                    return Task.FromResult(new AtResponse(false, [], "ERROR"));
                if (RequireFullAid && command != "AT+CCHO=\"A0000000871004A1B2\"")
                    return Task.FromResult(new AtResponse(false, [], "ERROR"));
                return Task.FromResult(Reply($"+CCHO: {channelId}"));
            }
            if (command.StartsWith("AT+CCHC=", StringComparison.Ordinal))
                return Task.FromResult(Reply("OK"));
            if (command.StartsWith("AT+CSIM=", StringComparison.Ordinal))
            {
                var csimQuote = command.IndexOf('"');
                var csimApdu = Convert.FromHexString(command[(csimQuote + 1)..^1]);
                byte[] result;
                if (csimApdu.AsSpan().SequenceEqual(Convert.FromHexString("00B000000A")))
                {
                    result = [0x98, 0x68, 0x00, 0x21, 0x43, 0x65, 0x87, 0x09, 0x21, 0x43, 0x90, 0x00];
                    if (ChangeIccidAfterFirstRead && _iccidReads++ > 0) result[9] = 0x99;
                }
                else result = [0x90, 0x00];
                var csimHex = Convert.ToHexString(result);
                return Task.FromResult(Reply($"+CSIM: {csimHex.Length},\"{csimHex}\""));
            }
            if (!command.StartsWith($"AT+CGLA={channelId},", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected AT command: {command}");

            var firstQuote = command.IndexOf('"');
            var apdu = Convert.FromHexString(command[(firstQuote + 1)..^1]);
            byte[] response;
            if (apdu[1] == 0xA4)
            {
                _selectedFile = apdu[^1];
                response = [0x90, 0x00];
            }
            else if (apdu[1] is 0xB0 or 0xB2)
            {
                var value = _selectedFile switch
                {
                    0x02 => "001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
                    0x03 => "ims.mnc001.mcc001.3gppnetwork.org",
                    0x04 => ChangeIdentityAfterFirstRead && _impuReads > 0
                        ? "sip:+19995550000@ims.mnc001.mcc001.3gppnetwork.org"
                        : "sip:+15551234567@ims.mnc001.mcc001.3gppnetwork.org",
                    _ => throw new InvalidOperationException("Wrong EF selected")
                };
                var data = Encoding.UTF8.GetBytes(value);
                var tlv = new byte[] { 0x80, checked((byte)data.Length) }.Concat(data).ToArray();
                if (CorruptImpu && _selectedFile == 0x04) tlv = [0x80, 0x05, 0x41];
                response = apdu[^1] == 0x00
                    ? [0x6C, checked((byte)tlv.Length)]
                    : [.. tlv, 0x90, 0x00];
                if (_selectedFile == 0x04 && apdu[^1] != 0x00) _impuReads++;
            }
            else if (apdu[1] == 0x88)
            {
                response = SynchronizationFailure
                    ? [0xDC, 0x0E, .. Enumerable.Repeat((byte)0x44, 14), 0x90, 0x00]
                    : [0xDB, 0x2B, 0x08, .. Enumerable.Repeat((byte)0x11, 8),
                        0x10, .. Enumerable.Repeat((byte)0x22, 16),
                        0x10, .. Enumerable.Repeat((byte)0x33, 16), 0x90, 0x00];
            }
            else throw new InvalidOperationException($"Unexpected APDU: {Convert.ToHexString(apdu)}");

            var hex = Convert.ToHexString(response);
            return Task.FromResult(Reply($"+CGLA: {channelId},{hex.Length},\"{hex}\""));
        }

        public Task<AtResponse> ExecutePromptCommandAsync(string initialCommand, string payload,
            int promptTimeoutMs = 3000, int completionTimeoutMs = 15000, CancellationToken ct = default) =>
            throw new NotSupportedException();

        private static AtResponse Reply(string line) => new(true, [line], RawOutput: line);
    }
}
