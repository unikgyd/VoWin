using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

public sealed partial class SipServerFeatureTests
{
    private static SipGatewayOptions ExceptionalOptions() => new()
    {
        BindAddress = IPAddress.Loopback, SipPort = 5062, Realm = "cleanup.test",
        Accounts = new(StringComparer.OrdinalIgnoreCase) { ["alice"] = "synthetic-alice", ["bob"] = "synthetic-bob" }
    };

    private static void DisposedGatewaySocket(SipGateway gateway)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Dispose();
        typeof(SipGateway).GetField("_udp", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, socket);
    }

    private static SipMessage ExceptionalInvite(string target)
    {
        var request = new SipMessage { IsRequest = true, Method = "INVITE", RequestUri = $"sip:{target}@cleanup.test",
            Body = SipGatewaySdp.BuildOffer(IPAddress.Loopback, 19000, 19001, offerBoth: true) };
        request.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:19002;branch=z9hG4bKsynthetic-cleanup;rport");
        request.SetHeader("From", "<sip:alice@cleanup.test>;tag=synthetic-from");
        request.SetHeader("To", $"<sip:{target}@cleanup.test>");
        request.SetHeader("Call-ID", "synthetic-cleanup-call");
        request.SetHeader("CSeq", "1 INVITE");
        return request;
    }

    private static Task InvokeExceptionalHandler(SipGateway gateway, string name, params object?[] arguments) =>
        (Task)typeof(SipGateway).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gateway, arguments)!;

    [Fact]
    public async Task CarrierInviteInitialResponseFailureRemovesOwnedDialogWithoutDialing()
    {
        var options = ExceptionalOptions(); var controller = new Controller();
        await using var gateway = new SipGateway(options, controller);
        DisposedGatewaySocket(gateway);
        var invite = ExceptionalInvite("+15551234567");
        var authenticator = (SipDigestAuthenticator)typeof(SipGateway).GetField("_authenticator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(gateway)!;
        var credentials = new SipGatewayPeer(new(IPAddress.Loopback, 5060), "unused",
            new SipPeerInboundCredentials("unused", "synthetic-unused"),
            new SipPeerOutboundCredentials("alice", "synthetic-alice", options.Realm) { AllowedAlgorithms = ["MD5"] });
        invite.SetHeader("Proxy-Authorization", SipPeerDigest.Create(authenticator.CreateChallenge(), invite, credentials)!);
        await Assert.ThrowsAnyAsync<Exception>(() => InvokeExceptionalHandler(gateway, "HandlePhoneInviteAsync",
            invite, new IPEndPoint(IPAddress.Loopback, 19002), CancellationToken.None));
        Assert.Equal(0, gateway.Status.ActiveDialogs);
        Assert.Equal(0, controller.DialCount);
    }

    [Fact]
    public async Task ExtensionFailureResponseExceptionStillRemovesOwnedDialog()
    {
        await using var gateway = new SipGateway(ExceptionalOptions(), new Controller());
        DisposedGatewaySocket(gateway);
        var registrations = typeof(SipGateway).GetField("_registrations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
        var binding = new SipGatewayRegistration("bob", "<sip:bob@127.0.0.1:19003>", new(IPAddress.Loopback, 19003),
            DateTimeOffset.UtcNow.AddMinutes(1), "synthetic-only");
        Assert.True((bool)registrations.GetType().GetMethod("TryAdd")!.Invoke(registrations, ["synthetic-bob", binding])!);
        await Assert.ThrowsAnyAsync<Exception>(() => InvokeExceptionalHandler(gateway, "HandleExtensionInviteAsync",
            ExceptionalInvite("bob"), new IPEndPoint(IPAddress.Loopback, 19002), "alice", "bob", CancellationToken.None));
        Assert.Equal(0, gateway.Status.ActiveDialogs);
    }

    [Fact]
    public async Task LosingBranchAckExceptionStillDisposesUnregisteredDialogMediaAndCancellation()
    {
        await using var gateway = new SipGateway(ExceptionalOptions(), new Controller());
        DisposedGatewaySocket(gateway);
        var dialogType = typeof(SipGateway).GetNestedType("Dialog", BindingFlags.NonPublic)!;
        var dialog = Activator.CreateInstance(dialogType)!;
        using var media = new RtpSession(null, localAddress: IPAddress.Loopback);
        using var cancellation = new CancellationTokenSource();
        void Set(string property, object value) => dialogType.GetProperty(property)!.SetValue(dialog, value);
        Set("LocalCallId", "synthetic-loser-call"); Set("Extension", "bob");
        Set("PhoneEndPoint", new IPEndPoint(IPAddress.Loopback, 19003)); Set("Media", media); Set("Cancellation", cancellation);
        Set("From", "<sip:gateway@cleanup.test>;tag=synthetic-from"); Set("To", "<sip:bob@cleanup.test>;tag=synthetic-to");
        var task = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(dialogType).Invoke(null, [dialog])!;
        var branches = Array.CreateInstance(task.GetType(), 1); branches.SetValue(task, 0);
        try
        {
            await InvokeExceptionalHandler(gateway, "CleanupLosingBranchesAsync", branches);
            Assert.True((bool)dialogType.GetProperty("IsDisposed")!.GetValue(dialog)!);
            Assert.True((bool)typeof(RtpSession).GetField("_isDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(media)!);
            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
            Assert.Equal(0, gateway.Status.ActiveDialogs); // The loser was never promoted to this map.
        }
        finally { ((IDisposable)dialog).Dispose(); }
    }
}
