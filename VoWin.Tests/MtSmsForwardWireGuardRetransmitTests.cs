using System.Net;
using System.Net.Sockets;
using VoSharp.Kernel.SipGateway;

namespace VoWin.Tests;

public sealed partial class MtSmsForwardWireGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UdpTimeoutRetransmissionRechecksActualInboxAndNeverRevivesChangedSource(bool editBody)
    {
        await using var f = await WireFixture.CreateAsync(peer: false);
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await RegisterWireAsync(phone, f.Endpoint);
        var source = $"local-retransmit-wire-guard-{(editBody ? "edit" : "delete")}";
        var original = await f.QueueAsync(source, "synthetic original retransmission forwarding body");
        var first = await ReceiveWireAsync(phone, f.Endpoint);
        Assert.True(first.IsRequest);
        Assert.Equal("MESSAGE", first.Method);
        Assert.Equal(original.Text, first.Body);
        Assert.Null(first.GetHeader("Authorization"));
        Assert.Null(first.GetHeader("Proxy-Authorization"));

        var claim = await f.NextClaimAsync();
        await f.RequireUnknownAsync(claim);
        await f.RequireNoPendingAsync();
        await f.MutateAsync(original, editBody);

        // Deliberately leave the first MESSAGE unanswered. The real transport's
        // timeout must revalidate the source before its first UDP retransmit.
        await NoWireAsync(phone, TimeSpan.FromMilliseconds(1500));
        await f.NextCompletionAsync(claim, SipSmsForwardState.Unknown);
        await f.RequireUnknownAsync(claim);
        await f.RequireNoPendingAsync();
        f.Gateway.NotifySmsInboxCommitted();
        await NoWireAsync(phone);
        await f.RequireUnknownAsync(claim);
        await f.RequireNoPendingAsync();

        // Prevent a globally disabled sender from satisfying the negative test.
        await f.PositiveLocalAsync(phone, source + "-fresh");
    }
}
