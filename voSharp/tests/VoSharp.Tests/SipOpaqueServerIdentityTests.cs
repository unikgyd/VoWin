using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

[Collection("SIP loopback sockets")]
public sealed class SipOpaqueServerIdentityTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static UdpClient Socket() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static IPEndPoint End(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;
    private static SipGateway Gateway(Controller controller)
    {
        using var probe = Socket();
        return new(new() { BindAddress = IPAddress.Loopback, SipPort = End(probe).Port,
            Accounts = new() { ["local"] = "offline-only-secret" } }, controller);
    }
    private static SipMessage Request(string method, string callId = "OpaqueCall", string branch = "z9hG4bKOpaqueBranch")
    {
        var request = new SipMessage { IsRequest = true, Method = method, RequestUri = "sip:123@vowin.local" };
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:5060;branch={branch}");
        request.SetHeader("Call-ID", callId); request.SetHeader("CSeq", $"1 {method}");
        request.SetHeader("From", "<sip:local@vowin.local>;tag=phone");
        request.SetHeader("To", "<sip:123@vowin.local>;tag=gateway");
        return request;
    }
    private static async Task<SipMessage> Exchange(UdpClient client, SipGateway gateway, SipMessage request)
    {
        await client.SendAsync(request.ToBytes(), gateway.Status.LocalEndPoint!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return SipMessage.Parse((await client.ReceiveAsync(timeout.Token)).Buffer);
    }
    private static SipMessage Different(SipMessage original, bool branch)
    {
        var changed = SipMessage.Parse(original.ToBytes());
        changed.SetHeader(branch ? "Via" : "Call-ID", original.GetHeader(branch ? "Via" : "Call-ID")!.Replace("Opaque", "opaque", StringComparison.Ordinal));
        return changed;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CachedResponseDoesNotAliasCaseDistinctOpaqueTransactions(bool branch)
    {
        using var client = Socket(); await using var gateway = Gateway(new()); await gateway.StartAsync();
        var original = Request("OPTIONS"); Assert.Equal(200, (await Exchange(client, gateway, original)).StatusCode);
        var distinct = Different(original, branch);
        var response = await Exchange(client, gateway, distinct);
        Assert.Equal(distinct.GetHeader("Call-ID"), response.GetHeader("Call-ID"));
        Assert.Equal(distinct.GetHeader("Via"), response.GetHeader("Via"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InFlightTransactionDoesNotSuppressCaseDistinctOpaqueTransaction(bool branch)
    {
        using var client = Socket(); await using var gateway = Gateway(new()); await gateway.StartAsync();
        var original = Request("OPTIONS");
        var key = (string)typeof(SipGateway).GetMethod("ServerTransactionKey", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [original, End(client)])!;
        var active = (ConcurrentDictionary<string, byte>)typeof(SipGateway).GetField("_serverProcessing", Hidden)!.GetValue(gateway)!;
        Assert.True(active.TryAdd(key, 0));
        try
        {
            var distinct = Different(original, branch);
            var response = await Exchange(client, gateway, distinct);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal(distinct.GetHeader("Call-ID"), response.GetHeader("Call-ID"));
            Assert.Equal(distinct.GetHeader("Via"), response.GetHeader("Via"));
        }
        finally { active.TryRemove(key, out _); }
    }

    [Theory]
    [InlineData("ACK")] [InlineData("BYE")]
    public async Task DialogLookupDoesNotAcceptCaseMutatedCallId(string method)
    {
        using var client = Socket(); var controller = new Controller();
        await using var gateway = Gateway(controller); await gateway.StartAsync();
        var original = Request("INVITE");
        var dialogType = typeof(SipGateway).GetNestedType("Dialog", BindingFlags.NonPublic)!;
        var dialog = Activator.CreateInstance(dialogType, nonPublic: true)!;
        void Set(string name, object value) => dialogType.GetProperty(name)!.SetValue(dialog, value);
        Set("LocalCallId", "OpaqueCall"); Set("Extension", "local"); Set("PhoneEndPoint", End(client));
        Set("Media", new RtpSession(null, localAddress: IPAddress.Loopback)); Set("Cancellation", new CancellationTokenSource());
        Set("CarrierCallId", "carrier"); Set("From", original.GetHeader("From")!); Set("To", original.GetHeader("To")!);
        Set("OriginalInvite", original); dialogType.GetField("InviteFinalSent")!.SetValue(dialog, 1);
        var dialogs = (IDictionary)typeof(SipGateway).GetField("_dialogs", Hidden)!.GetValue(gateway)!;
        dialogs.Add("OpaqueCall", dialog);
        try
        {
            var mutated = Request(method, "opaqueCall");
            if (method == "ACK")
            {
                var acked = (TaskCompletionSource)dialogType.GetProperty("InviteAcknowledged")!.GetValue(dialog)!;
                var handleAck = typeof(SipGateway).GetMethod("HandleAck", Hidden)!;
                handleAck.Invoke(gateway, [mutated, End(client)]);
                Assert.False(acked.Task.IsCompleted);
                handleAck.Invoke(gateway, [Request("ACK"), End(client)]);
                Assert.True(acked.Task.IsCompleted);
            }
            else
            {
                Assert.Equal(481, (await Exchange(client, gateway, mutated)).StatusCode);
                Assert.Equal(0, controller.Hangups);
                Assert.True(dialogs.Contains("OpaqueCall"));
                Assert.Equal(200, (await Exchange(client, gateway, Request("BYE"))).StatusCode);
            }
        }
        finally
        {
            // This fixture owns only an injected dialog, not a real phone/carrier leg.
            // Remove it explicitly rather than make gateway disposal await a synthetic BYE.
            dialogs.Remove("OpaqueCall");
            ((IDisposable)dialog).Dispose();
        }
    }

    private sealed class Controller : ISipGatewayCallController
    {
        public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public int Hangups;
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task HangupAsync(string? slotId, CancellationToken ct) { Interlocked.Increment(ref Hangups); return Task.CompletedTask; }
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
