using System.Diagnostics;
using System.Net;
using System.Reflection;
using VoSharp.Modem;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class HostImsLiveStatusTests
{
    private static SipRegistrationResult Grant(DateTime at, int seconds = 60) =>
        new("<sip:synthetic@127.0.0.1:5060>", seconds, null, null, at);

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(60, -1, 0, false)]
    [InlineData(60, 60, 0, false)]
    [InlineData(60, 1, 60, false)]
    [InlineData(60, 1, 59, true)]
    [InlineData(60, 59, 1, true)]
    public void GrantedLifetimeRequiresBothWallAndMonotonicClocks(int seconds, int wallAge,
        int monotonicAge, bool expected)
    {
        var now = DateTime.UtcNow;
        var start = Stopwatch.GetTimestamp();
        Assert.Equal(expected, SipRegisterSession.IsGrantCurrent(Grant(now.AddSeconds(-wallAge), seconds),
            start, now, start + monotonicAge * Stopwatch.Frequency));
    }

    [Fact]
    public void InvalidClockAnchorsCannotProduceLiveStatus()
    {
        var now = DateTime.UtcNow;
        Assert.False(SipRegisterSession.IsGrantCurrent(Grant(now), 2, now, 1));
        Assert.False(SipRegisterSession.IsGrantCurrent(Grant(DateTime.SpecifyKind(now, DateTimeKind.Unspecified)), 1, now, 2));
        Assert.True(SipRegisterSession.IsGrantCurrent(Grant(now, int.MaxValue), 1, now, 2));
    }

    [Fact]
    public void ClosedTransportInvalidatesLiveGrantWithoutErasingItsHistoricalResult()
    {
        using var transport = new SipTransport();
        using var session = Session(transport);
        var grant = Grant(DateTime.UtcNow);
        typeof(SipRegisterSession).GetProperty(nameof(SipRegisterSession.LastResult))!.SetValue(session, grant);
        Assert.Same(grant, session.CurrentResult);
        transport.Dispose();
        Assert.True(transport.IsDisposed);
        Assert.Null(session.CurrentResult);
        Assert.Same(grant, session.LastResult);
    }

    [Fact]
    public async Task HostClientDoesNotExposeExpiredOrClosedRegistrationAsOnline()
    {
        var transport = new SipTransport();
        var session = Session(transport);
        var grant = Grant(DateTime.UtcNow);
        typeof(SipRegisterSession).GetProperty(nameof(SipRegisterSession.LastResult))!.SetValue(session, grant);
        Func<byte[], byte[], CancellationToken, Task<(byte[] Res, byte[] Ck, byte[] Ik)>> noAka =
            (_, _, _) => throw new InvalidOperationException("No SIM access expected");
        await using var client = (HostImsRegistrationClient)typeof(HostImsRegistrationClient)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([new HostImsEndpointCandidate(1, IPAddress.Loopback, IPAddress.Parse("127.0.0.2")),
                transport, session, grant, 1, noAka, null, null]);
        Assert.True(client.IsRegistered);
        typeof(SipRegisterSession).GetProperty(nameof(SipRegisterSession.LastResult))!.SetValue(session,
            Grant(DateTime.UtcNow.AddMinutes(-2)));
        Assert.False(client.IsRegistered);
        typeof(SipRegisterSession).GetProperty(nameof(SipRegisterSession.LastResult))!.SetValue(session, Grant(DateTime.UtcNow));
        Assert.True(client.IsRegistered);
        transport.Dispose();
        Assert.False(client.IsRegistered);
    }

    private static SipRegisterSession Session(SipTransport transport) => new(transport,
        new ImsProfile("001010000000001@ims.test", "sip:synthetic@ims.test", "ims.test",
            "123456789012345", "127.0.0.1", AdvertiseSmsOverIms: false));
}
