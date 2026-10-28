using System.Net;
using System.Net.Sockets;
using VoSharp.Kernel.SipGateway;

namespace VoWin.Tests;

public sealed partial class MtSmsForwardWireGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachPayloadChunkRechecksTheActualInboxAndPartialAcceptanceStaysUnknown(bool editBody)
    {
        await using var f = await WireFixture.CreateAsync(peer: false);
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await RegisterWireAsync(phone, f.Endpoint);
        var source = "split-source-guard-" + (editBody ? "edit" : "delete");
        var original = await f.QueueAsync(source, new string('x', 400));
        var first = await ReceiveWireAsync(phone, f.Endpoint);
        Assert.Equal("MESSAGE", first.Method); Assert.StartsWith("[1/2] ", first.Body);
        var claim = await f.NextClaimAsync(); await f.RequireUnknownAsync(claim);
        await f.MutateAsync(original, editBody);
        // A real 200 for the first chunk cannot grant authority for the second
        // chunk after its actual saved source was deleted or edited.
        await SendWireAsync(phone, first.CreateResponse(200, "OK"), f.Endpoint);
        await NoWireAsync(phone);
        await f.NextCompletionAsync(claim, SipSmsForwardState.Unknown);
        await f.RequireUnknownAsync(claim); await f.RequireNoPendingAsync();
        f.Gateway.NotifySmsInboxCommitted(); await NoWireAsync(phone);
        await f.RequireUnknownAsync(claim); await f.RequireNoPendingAsync();
        await f.PositiveLocalAsync(phone, source + "-fresh");
    }
}
