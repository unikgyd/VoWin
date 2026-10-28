using VoSharp.Kernel.SipGateway;

namespace VoWin.Tests;

public sealed partial class MtSmsForwardWireGuardTests
{
    [Theory]
    [InlineData(401, false)]
    [InlineData(401, true)]
    [InlineData(407, false)]
    [InlineData(407, true)]
    public async Task PeerAuthenticationRetryRechecksActualInboxAndNeverRevivesChangedSource(int statusCode, bool editBody)
    {
        await using var f = await WireFixture.CreateAsync(peer: true);
        var source = $"peer-wire-guard-{statusCode}-{(editBody ? "edit" : "delete")}";
        var original = await f.QueueAsync(source, "synthetic original peer forwarding body");
        var first = await ReceiveWireAsync(f.Peer!, f.Endpoint);
        Assert.True(first.IsRequest);
        Assert.Equal("MESSAGE", first.Method);
        Assert.Equal(original.Text, first.Body);
        Assert.Null(first.GetHeader("Authorization"));
        Assert.Null(first.GetHeader("Proxy-Authorization"));

        var claim = await f.NextClaimAsync();
        await f.RequireUnknownAsync(claim);
        await f.RequireNoPendingAsync();

        // The first wire attempt was valid. A second real SQLite connection
        // now removes or changes its Inbox source before the peer challenges it.
        await f.MutateAsync(original, editBody);
        var challenge = first.CreateResponse(statusCode,
            statusCode == 401 ? "Unauthorized" : "Proxy Authentication Required");
        Assert.Equal(first.GetHeader("From"), challenge.GetHeader("From"));
        Assert.Equal(first.GetHeader("To"), challenge.GetHeader("To"));
        challenge.SetHeader(statusCode == 401 ? "WWW-Authenticate" : "Proxy-Authenticate",
            $"Digest realm=\"{PeerRealm}\",nonce=\"synthetic-wire-guard-{statusCode}\",algorithm=SHA-256,qop=\"auth\"");
        await SendWireAsync(f.Peer!, challenge, f.Endpoint);

        // Authentication must not send a stale payload using the original
        // claim's cached source, or turn an ambiguous attempt back into Pending.
        await NoWireAsync(f.Peer!);
        await f.NextCompletionAsync(claim, SipSmsForwardState.Unknown);
        await f.RequireUnknownAsync(claim);
        await f.RequireNoPendingAsync();
        f.Gateway.NotifySmsInboxCommitted();
        await NoWireAsync(f.Peer!);
        await f.RequireUnknownAsync(claim);
        await f.RequireNoPendingAsync();

        // A source-specific guard must not disable a healthy new source.
        await f.PositivePeerAsync(source + "-fresh");
    }
}
