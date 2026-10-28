using System.Net;
using VoSharp.Kernel.SipGateway;

namespace VoSharp.Tests;

public sealed class SipSmsRecipientAuthorityTests
{
    private static SipGatewayOptions Options() => new()
    {
        Accounts = new(StringComparer.OrdinalIgnoreCase) { ["desk"] = "private-local-password" },
        LocalAccountEpochs = new(StringComparer.OrdinalIgnoreCase) { ["desk"] = new('a', 32) },
        PeerCredentialEpoch = new('b', 32),
        Peer = new(new IPEndPoint(IPAddress.Loopback, 5099), "all",
            new SipPeerInboundCredentials("in-user", "private-in-password"),
            new SipPeerOutboundCredentials("out-user", "private-out-password", "pbx.local"))
    };

    [Fact]
    public void LocalIncarnationIsStableButDifferentEpochAndCredentialNeverMatch()
    {
        var options = Options(); var original = SipSmsRecipientAuthority.Local(options, "desk");
        Assert.Equal(original, SipSmsRecipientAuthority.Local(options.DeepClone(), "DESK"));
        Assert.NotEqual(original, SipSmsRecipientAuthority.Local(options with { LocalAccountEpochs = new() { ["desk"] = new('c', 32) } }, "desk"));
        Assert.NotEqual(original, SipSmsRecipientAuthority.Local(options with { Accounts = new() { ["desk"] = "changed" } }, "desk"));
        Assert.NotEqual(original, SipSmsRecipientAuthority.Local(options with { Realm = "changed.local" }, "desk"));
        Assert.Equal(64, original!.Length); Assert.DoesNotContain("password", original);
    }

    [Fact]
    public void MissingOrMalformedIncarnationCannotAuthorizeQueuedContent()
    {
        var options = Options();
        Assert.Null(SipSmsRecipientAuthority.Local(options, "not-an-account"));
        Assert.Null(SipSmsRecipientAuthority.Local(options with { LocalAccountEpochs = new() }, "desk"));
        Assert.Null(SipSmsRecipientAuthority.Peer(options with { PeerCredentialEpoch = null }, "all"));
        Assert.Null(SipSmsRecipientAuthority.Peer(options with { PeerCredentialEpoch = new('A', 32) }, "all"));
    }

    [Fact]
    public void PeerAuthorityBindsBothPasswordsEndpointTargetRealmAndAlgorithms()
    {
        var options = Options(); var peer = options.Peer!; var original = SipSmsRecipientAuthority.Peer(options, "all");
        Assert.Equal(original, SipSmsRecipientAuthority.Peer(options.DeepClone(), "all"));
        Assert.NotEqual(original, SipSmsRecipientAuthority.Peer(options, "different"));
        Assert.NotEqual(original, SipSmsRecipientAuthority.Peer(options with { PeerCredentialEpoch = new('c', 32) }, "all"));
        SipGatewayPeer[] changed = [
            peer with { EndPoint = new(IPAddress.Loopback, 5100) },
            peer with { InboundAuth = peer.InboundAuth! with { Password = "changed" } },
            peer with { InboundAuth = peer.InboundAuth! with { Algorithm = "MD5" } },
            peer with { OutboundAuth = peer.OutboundAuth! with { Password = "changed" } },
            peer with { OutboundAuth = peer.OutboundAuth! with { ExpectedRealm = "different.local" } },
            peer with { OutboundAuth = peer.OutboundAuth! with { AllowedAlgorithms = ["SHA-256", "MD5"] } }
        ];
        foreach (var mutation in changed) Assert.NotEqual(original, SipSmsRecipientAuthority.Peer(options with { Peer = mutation }, "all"));
    }

    [Fact]
    public void UnpersistedRuntimeGetsAnUnrelatedIncarnationOnEachConstruction()
    {
        var raw = Options() with { LocalAccountEpochs = new(), PeerCredentialEpoch = null };
        var a = SipSmsRecipientAuthority.WithRuntimeEpochs(raw);
        var b = SipSmsRecipientAuthority.WithRuntimeEpochs(raw);
        Assert.NotEqual(SipSmsRecipientAuthority.Local(a, "desk"), SipSmsRecipientAuthority.Local(b, "desk"));
        Assert.NotEqual(SipSmsRecipientAuthority.Peer(a, "all"), SipSmsRecipientAuthority.Peer(b, "all"));
        Assert.Empty(raw.LocalAccountEpochs); Assert.Null(raw.PeerCredentialEpoch);
        Assert.Equal(SipSmsRecipientAuthority.Local(a, "desk"), SipSmsRecipientAuthority.Local(SipSmsRecipientAuthority.WithRuntimeEpochs(a), "desk"));
    }
}
