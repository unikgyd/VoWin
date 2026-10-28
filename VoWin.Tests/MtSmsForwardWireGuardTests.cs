using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

// A real file Store, its independent second service and actual loopback UDP.
// The wrapper observes real FULL claim commits; it never invents a valid source
// or uses a future guard API, so these regressions compile on the old runtime.
public sealed partial class MtSmsForwardWireGuardTests
{
    private const string User = "1001", Password = "synthetic-source-guard-password", Realm = "source-guard.test";
    private const string Line = "source-guard-line", PeerRealm = "guard-peer.test";
    private static readonly string Binding = new('a', 64);

    [Theory]
    [InlineData("delete")]
    [InlineData("body")]
    [InlineData("source-id")]
    public async Task DestinationGateWaitRechecksActualInboxAfterFullClaim(string mutation)
    {
        await using var f = await WireFixture.CreateAsync(peer: false);
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await RegisterWireAsync(phone, f.Endpoint);
        var destinations = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(SipGateway)
            .GetField("_messageDestinations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Gateway)!;
        var held = new SemaphoreSlim(0, 1);
        Assert.True(destinations.TryAdd(User + "@" + (IPEndPoint)phone.Client.LocalEndPoint!, held));
        var released = false;
        try
        {
            var original = await f.QueueAsync("first-wire-guard-" + mutation, "synthetic first wire private body");
            var claim = await f.NextClaimAsync();
            await f.RequireUnknownAsync(claim);
            await f.RequireNoPendingAsync();
            await NoWireAsync(phone); // No payload while the destination gate is held.
            await f.MutateAsync(original, mutation);
            held.Release(); released = true;
            await NoWireAsync(phone);
            await f.NextCompletionAsync(claim, SipSmsForwardState.Unknown);
            await f.RequireUnknownAsync(claim);
            await f.RequireNoPendingAsync();
            f.Gateway.NotifySmsInboxCommitted();
            await NoWireAsync(phone);
            await f.RequireUnknownAsync(claim);
            await f.RequireNoPendingAsync();
            await f.PositiveLocalAsync(phone, "fresh-after-gate-veto-" + mutation);
        }
        finally
        {
            if (!released) held.Release();
            // Do not dispose a gate while the failing old worker still owns it.
            await f.Gateway.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            held.Dispose();
        }
    }

    private sealed partial class WireStore(PreferenceDatabaseService database) : ISipSmsForwardStore
    {
        // The future runtime guard adapter can delegate through a separate
        // partial file without changing these old-API RED behavior assertions.
        private readonly PreferenceDatabaseService _database = database;
        public readonly Channel<SipSmsForwardClaim> Claims = Channel.CreateUnbounded<SipSmsForwardClaim>();
        public readonly Channel<(string Id, string Token, SipSmsForwardState State)> Completions = Channel.CreateUnbounded<(string, string, SipSmsForwardState)>();
        public Task<IReadOnlyList<SipSmsForwardItem>> GetPendingAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
            ((ISipSmsForwardStore)_database).GetPendingAsync(now, limit, ct);
        public async Task<SipSmsForwardClaim?> TryClaimAsync(string id, string expectedFingerprint, DateTimeOffset now, CancellationToken ct)
        {
            var claim = await ((ISipSmsForwardStore)_database).TryClaimAsync(id, expectedFingerprint, now, ct);
            if (claim != null) Claims.Writer.TryWrite(claim);
            return claim;
        }
        public async Task CompleteAsync(string id, string token, SipSmsForwardState state, CancellationToken ct)
        {
            await ((ISipSmsForwardStore)_database).CompleteAsync(id, token, state, ct);
            Completions.Writer.TryWrite((id, token, state));
        }
        public Task CancelQueuedAsync(string id, CancellationToken ct) => ((ISipSmsForwardStore)_database).CancelQueuedAsync(id, ct);
    }

    private sealed class WireFixture : IAsyncDisposable
    {
        private readonly string _directory;
        public string Path { get; }
        public PreferenceDatabaseService Database { get; }
        public PreferenceDatabaseService OtherDatabase { get; }
        public SipGateway Gateway { get; private set; } = null!;
        public UdpClient? Peer { get; private set; }
        public IPEndPoint Endpoint => Gateway.Status.LocalEndPoint!;
        private readonly WireStore _store;
        private readonly WireController _controller = new();

        private WireFixture()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vowin-source-wire-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "preferences.db");
            Database = new(Path); OtherDatabase = new(Path); _store = new(Database);
        }

        public static async Task<WireFixture> CreateAsync(bool peer)
        {
            var f = new WireFixture();
            try
            {
                await f.Database.InitializeAsync(); await f.OtherDatabase.InitializeAsync();
                using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
                reservation.Dispose();
                var options = new SipGatewayOptions
                {
                    BindAddress = IPAddress.Loopback, SipPort = port, Realm = Realm,
                    Accounts = peer ? new() : new() { [User] = Password },
                    LocalAccountEpochs = peer ? new() : new() { [User] = new('e', 32) },
                    MessagesEnabled = true, ForwardIncomingSms = true, RequireConfiguredLines = true,
                    ExtensionPolicies = peer ? new() : new() { [User] = new() { AllowSms = false } },
                    LineRoutes = new() { [Line] = new() { ForwardIncomingSms = true } },
                    SmsForwardStore = f._store
                };
                if (peer)
                {
                    f.Peer = new(new IPEndPoint(IPAddress.Loopback, 0));
                    options = options with
                    {
                        PeerCredentialEpoch = new('f', 32),
                        Peer = new((IPEndPoint)f.Peer.Client.LocalEndPoint!, "sms-in",
                            new SipPeerInboundCredentials("peer-in", "synthetic-guard-inbound"),
                            new SipPeerOutboundCredentials("peer-out", "synthetic-guard-outbound", PeerRealm))
                    };
                }
                f.Gateway = new(options, f._controller); await f.Gateway.StartAsync();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        public async Task<SmsMessageModel> QueueAsync(string source, string body)
        {
            var message = new SmsMessageModel { SlotId = Line, SimBinding = Binding, SourceMessageId = source,
                SenderOrRecipient = "+15551234567", Text = body, Timestamp = DateTime.UtcNow, IsOutgoing = false };
            var recipients = Gateway.CaptureSmsForwardRecipients(Line);
            Assert.Single(recipients);
            var saved = await Database.SaveIncomingSmsWithForwardsAsync(message, recipients);
            Assert.True(saved.IsNew); Assert.Equal(1, saved.QueuedRecipients);
            Gateway.NotifySmsInboxCommitted();
            return message;
        }

        public async Task<SipSmsForwardClaim> NextClaimAsync()
        {
            var claim = await _store.Claims.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await RequireUnknownAsync(claim);
            return claim;
        }

        public async Task NextCompletionAsync(SipSmsForwardClaim claim, SipSmsForwardState expected)
        {
            var value = await _store.Completions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(claim.Id, value.Id); Assert.Equal(claim.AttemptToken, value.Token); Assert.Equal(expected, value.State);
        }

        public Task MutateAsync(SmsMessageModel message, bool editBody) => MutateAsync(message, editBody ? "body" : "delete");

        public async Task MutateAsync(SmsMessageModel message, string mutation)
        {
            if (mutation == "delete") { await OtherDatabase.DeleteSmsMessageAsync(message.Id); return; }
            if (mutation == "body")
            {
                message.Text = "synthetic changed local Inbox body";
                await OtherDatabase.SaveSmsMessageAsync(message); return;
            }
            Assert.Equal("source-id", mutation);
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand("UPDATE SmsMessages SET SourceMessageId=$source WHERE Id=$id", connection);
            command.Parameters.AddWithValue("$source", "synthetic-edited-source-metadata"); command.Parameters.AddWithValue("$id", message.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task RequireUnknownAsync(SipSmsForwardClaim claim)
        {
            using var connection = Connection(); await connection.OpenAsync();
            using var command = new SqliteCommand("SELECT State,AttemptToken FROM SipSmsForwards WHERE Id=$id", connection);
            command.Parameters.AddWithValue("$id", claim.Id);
            using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal((long)SipSmsForwardState.Unknown, reader.GetInt64(0)); Assert.Equal(claim.AttemptToken, reader.GetString(1));
            Assert.False(await reader.ReadAsync()); Assert.Equal(0, _controller.Calls);
        }

        public async Task RequireNoPendingAsync() => Assert.Empty(await ((ISipSmsForwardStore)Database)
            .GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None));

        public async Task PositiveLocalAsync(UdpClient phone, string source)
        {
            var message = await QueueAsync(source, "synthetic fresh local positive source");
            var claim = await NextClaimAsync();
            var request = await ReceiveWireAsync(phone, Endpoint);
            Assert.Equal("MESSAGE", request.Method); Assert.Equal(message.Text, request.Body);
            await SendWireAsync(phone, request.CreateResponse(200, "OK"), Endpoint);
            await NextCompletionAsync(claim, SipSmsForwardState.Accepted);
            await RequireNoPendingAsync();
        }

        public async Task PositivePeerAsync(string source)
        {
            var message = await QueueAsync(source, "synthetic fresh peer positive source");
            var request = await ReceiveWireAsync(Peer!, Endpoint); var claim = await NextClaimAsync();
            Assert.Equal("MESSAGE", request.Method); Assert.Equal(message.Text, request.Body);
            await SendWireAsync(Peer!, request.CreateResponse(200, "OK"), Endpoint);
            await NextCompletionAsync(claim, SipSmsForwardState.Accepted);
            await RequireNoPendingAsync();
        }

        private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder { DataSource = Path, Cache = SqliteCacheMode.Private }.ToString());

        public async ValueTask DisposeAsync()
        {
            if (Gateway != null) await Gateway.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Peer?.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task<SipMessage> ReceiveWireAsync(UdpClient phone, IPEndPoint expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var datagram = await phone.ReceiveAsync(timeout.Token); Assert.Equal(expected, datagram.RemoteEndPoint);
            var message = SipMessage.Parse(datagram.Buffer);
            if (message.IsRequest && message.Method == "OPTIONS")
            {
                // The peer's ordinary readiness probe carries no private SMS.
                Assert.Empty(message.Body); Assert.Null(message.GetHeader("Authorization")); Assert.Null(message.GetHeader("Proxy-Authorization"));
                await SendWireAsync(phone, message.CreateResponse(200, "OK"), expected); continue;
            }
            return message;
        }
    }

    private static Task SendWireAsync(UdpClient phone, SipMessage message, IPEndPoint endpoint) => phone.SendAsync(message.ToBytes(), endpoint).AsTask();

    private static async Task NoWireAsync(UdpClient phone, TimeSpan? duration = null)
    {
        using var timeout = new CancellationTokenSource(duration ?? TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await phone.ReceiveAsync(timeout.Token));
    }

    private static async Task RegisterWireAsync(UdpClient phone, IPEndPoint gateway)
    {
        var uri = "sip:" + Realm;
        var request = new SipMessage { IsRequest = true, Method = "REGISTER", RequestUri = uri };
        var port = ((IPEndPoint)phone.Client.LocalEndPoint!).Port;
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("From", $"<sip:{User}@{Realm}>;tag=synthetic-guard-phone");
        request.SetHeader("To", $"<sip:{User}@{Realm}>"); request.SetHeader("Call-ID", Guid.NewGuid().ToString("N"));
        request.SetHeader("CSeq", "1 REGISTER"); request.SetHeader("Contact", $"<sip:{User}@127.0.0.1:{port}>"); request.SetHeader("Expires", "300");
        await SendWireAsync(phone, request, gateway);
        var challenge = await ReceiveWireAsync(phone, gateway); Assert.Equal(401, challenge.StatusCode);
        var nonce = Regex.Match(challenge.GetHeader("WWW-Authenticate")!, "nonce=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(nonce);
        const string cnonce = "synthetic-source-guard", nc = "00000001";
        static string Digest(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var response = Digest(Digest(User + ":" + Realm + ":" + Password) + ":" + nonce + ":" + nc + ":" + cnonce + ":auth:" + Digest("REGISTER:" + uri));
        request.SetHeader("Authorization", $"Digest username=\"{User}\",realm=\"{Realm}\",nonce=\"{nonce}\",uri=\"{uri}\",response=\"{response}\",algorithm=MD5,qop=auth,nc={nc},cnonce=\"{cnonce}\"");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await SendWireAsync(phone, request, gateway); Assert.Equal(200, (await ReceiveWireAsync(phone, gateway)).StatusCode);
    }

    private sealed class WireController : ISipGatewayCallController
    {
        public int Calls;
        public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
        public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
        public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct) { Interlocked.Increment(ref Calls); throw new InvalidOperationException("No carrier is allowed in this test."); }
        public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) { Interlocked.Increment(ref Calls); throw new InvalidOperationException("No carrier is allowed in this test."); }
        public Task HangupAsync(string? slotId, CancellationToken ct) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.CompletedTask;
        public ImsCallManager? GetImsMedia(string? slotId) => null;
    }
}
