using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using VoSharp.Telephony.Sms;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

/// <summary>
/// Actual desktop receive wiring, real SQLite and loopback SIP. Only the first
/// database entry is paused; the gateway really stops and starts with another
/// country policy. The wrapper delegates every persistence/claim/guard call.
/// There is no modem, current SIM lookup, paid message or production profile.
/// </summary>
public sealed class MtSmsCanonicalReplyDesktopTests
{
    private const string User = "1001", Password = "synthetic-canonical-desktop-password";
    private const string Realm = "canonical-desktop.test", Line = "canonical-offline-line";
    private const string National = "15550000001", OriginalReply = "+8615550000001", LaterReply = "+6315550000001";
    private static readonly string Binding = new('a', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FirstDesktopCapturePrecedesDatabaseWaitAndDoesNotReinterpretAfterGatewayCountryChange(bool knownCard)
    {
        RequireContract(); // Old source produces an explicit assertion RED, not a fixture error.
        return OnDispatcher(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "vowin-canonical-desktop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new PausedDatabase(Path.Combine(directory, "preferences.db"));
            try
            {
                var notifications = Channel.CreateUnbounded<SmsMessageModel>();
                var notificationCount = 0;
                await using var service = new VoKernelService(database, new(Path.Combine(directory, "telephony.json")),
                    Path.Combine(directory, "diagnostic.log"), (message, _) =>
                    {
                        notificationCount++; notifications.Writer.TryWrite(message);
                    }, Dispatcher.CurrentDispatcher);
                var originalOptions = Options("86");
                await service.StartSipGatewayAsync(originalOptions);
                Assert.Empty(service.Kernel.Pool.Slots);
                var original = new SmsMessage(1, SmsStatus.Unread, National, "synthetic original country-capture MT",
                    DateTime.UtcNow, Id: "canonical-desktop-original-source");
                Emit(service, new(original, Line, knownCard ? Binding : null));
                var entered = await Next(database.Captures.Reader);
                Assert.Equal(original.Id, entered.Source);
                Assert.Equal(knownCard ? OriginalReply : "", entered.Reply);
                Assert.Equal(User, Assert.Single(entered.Recipients).User);
                Assert.Equal(0, notificationCount);
                Assert.Empty(await database.GetAllSmsMessagesAsync());

                // Same receiver credentials/epochs, but a genuinely new gateway
                // with a different country policy before the original commits.
                await service.StopSipGatewayAsync();
                await service.StartSipGatewayAsync(originalOptions with { LineRoutes = Routes("63") });
                database.ReleaseFirst.TrySetResult();
                var committed = await Next(database.Commits.Reader);
                Assert.True(committed.IsNew); Assert.Equal(1, committed.QueuedRecipients);
                var shown = await Next(notifications.Reader);
                Assert.Equal(1, notificationCount);
                var stored = Assert.Single(await database.GetAllSmsMessagesAsync());
                Assert.Equal(shown.Id, stored.Id); Assert.Equal(original.Id, stored.SourceMessageId);
                Assert.Equal(National, stored.SenderOrRecipient);
                Assert.Equal(knownCard ? Binding : null, stored.SimBinding);
                Assert.Equal(knownCard ? OriginalReply : "", Snapshot(stored));
                Assert.Equal(knownCard ? OriginalReply : "", await ReplyColumn(database.Path, stored.Id));
                var item = Assert.Single(await ((ISipSmsForwardStore)database).GetPendingAsync(
                    DateTimeOffset.UtcNow, 2048, CancellationToken.None));
                Assert.Equal(knownCard ? OriginalReply : "", Reply(item.Payload));
                Assert.Equal(National, item.Payload.Sender);

                using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var endpoint = service.Kernel.LocalSipGateway!.Status.LocalEndPoint!;
                var buffered = await Register(phone, endpoint);
                var forwarded = buffered ?? await Receive(phone, endpoint);
                RequireForward(forwarded, original.Text, knownCard ? OriginalReply : null);
                await phone.SendAsync(forwarded.CreateResponse(200, "OK").ToBytes(), endpoint);
                var firstCompletion = await Next(database.Completions.Reader);
                Assert.Equal(item.Id, firstCompletion.Id); Assert.Equal(SipSmsForwardState.Accepted, firstCompletion.State);

                // A duplicate is captured under +63, but cannot rewrite the
                // original Inbox/intent or acquire a new recipient plan.
                var beforeDuplicate = await Ledger(database.Path);
                Emit(service, new(original, Line, knownCard ? Binding : null));
                var duplicateCapture = await Next(database.Captures.Reader);
                Assert.Equal(original.Id, duplicateCapture.Source);
                Assert.Equal(knownCard ? LaterReply : "", duplicateCapture.Reply);
                var duplicate = await Next(database.Commits.Reader);
                Assert.False(duplicate.IsNew); Assert.Equal(0, duplicate.QueuedRecipients);
                Assert.Equal(beforeDuplicate, await Ledger(database.Path));
                Assert.Equal(1, notificationCount);
                var unchanged = Assert.Single(await database.GetAllSmsMessagesAsync());
                Assert.Equal(knownCard ? OriginalReply : "", Snapshot(unchanged));
                Assert.Equal(knownCard ? OriginalReply : "", await ReplyColumn(database.Path, unchanged.Id));
                await NoWire(phone);

                // A genuinely new source after reconfiguration must capture
                // +63, proving this is not a blanket refusal or frozen policy.
                var newer = original with { Id = "canonical-desktop-new-source", Text = "synthetic new country-capture MT" };
                Emit(service, new(newer, Line, knownCard ? Binding : null));
                var newCapture = await Next(database.Captures.Reader);
                Assert.Equal(newer.Id, newCapture.Source);
                Assert.Equal(knownCard ? LaterReply : "", newCapture.Reply);
                var newCommit = await Next(database.Commits.Reader);
                Assert.True(newCommit.IsNew); Assert.Equal(1, newCommit.QueuedRecipients);
                _ = await Next(notifications.Reader);
                var second = await Receive(phone, endpoint);
                RequireForward(second, newer.Text, knownCard ? LaterReply : null);
                await phone.SendAsync(second.CreateResponse(200, "OK").ToBytes(), endpoint);
                var secondCompletion = await Next(database.Completions.Reader);
                Assert.NotEqual(firstCompletion.Id, secondCompletion.Id);
                Assert.Equal(SipSmsForwardState.Accepted, secondCompletion.State);
                Assert.Equal(2, notificationCount);
                var all = await database.GetAllSmsMessagesAsync();
                Assert.Equal(2, all.Count);
                var latest = Assert.Single(all, message => message.SourceMessageId == newer.Id);
                Assert.Equal(knownCard ? LaterReply : "", Snapshot(latest));
                Assert.Equal(knownCard ? LaterReply : "", await ReplyColumn(database.Path, latest.Id));
                Assert.Empty(await ((ISipSmsForwardStore)database).GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None));
                Assert.Empty(service.Kernel.Pool.Slots); Assert.Null(service.Kernel.ActiveCall);
            }
            finally
            {
                database.ReleaseFirst.TrySetResult();
                SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true);
            }
        });
    }

    private static void RequireContract()
    {
        Assert.True(typeof(SmsMessageModel).GetProperty("SipReplyNumberSnapshot") is not null,
            "Desktop receive must expose its private first-receive canonical snapshot contract.");
        Assert.True(typeof(SipSmsForwardPayload).GetProperty("ReplyNumber") is not null,
            "Durable worker payload must expose the canonical reply snapshot contract.");
    }

    private static string? Snapshot(SmsMessageModel message) =>
        (string?)typeof(SmsMessageModel).GetProperty("SipReplyNumberSnapshot")!.GetValue(message);
    private static string? Reply(SipSmsForwardPayload payload) =>
        (string?)typeof(SipSmsForwardPayload).GetProperty("ReplyNumber")!.GetValue(payload);

    private static SipGatewayOptions Options(string country)
    {
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return new()
        {
            BindAddress = IPAddress.Loopback, SipPort = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port,
            Realm = Realm, Accounts = new() { [User] = Password }, LocalAccountEpochs = new() { [User] = new('e', 32) },
            MessagesEnabled = true, ForwardIncomingSms = true, RequireConfiguredLines = true,
            // Receive-time number semantics are independent of paid-send grants.
            AllowOutgoingSms = false, ExtensionPolicies = new() { [User] = new() { AllowSms = false } },
            LineRoutes = Routes(country)
        };
    }

    private static Dictionary<string, SipGatewayLineRoute> Routes(string country) => new()
    {
        [Line] = new() { ForwardIncomingSms = true, AllowOutgoingSms = false,
            CountryCode = country, NationalNumberLength = 11, NormalizeE164 = true }
    };

    private static void Emit(VoKernelService service, SmsReceivedEventArgs incoming) =>
        ((EventHandler<SmsReceivedEventArgs>)typeof(VoKernel).GetField("SmsReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service.Kernel)!).Invoke(service.Kernel, incoming);

    private static void RequireForward(SipMessage message, string body, string? canonical)
    {
        Assert.Equal("MESSAGE", message.Method); Assert.Equal(body, message.Body);
        Assert.Equal(Line, message.GetHeader("X-VoWin-Line"));
        var from = message.GetHeader("From");
        Assert.NotNull(from); Assert.Contains("\"" + National + "\"", from);
        if (canonical == null)
        {
            Assert.Contains("<sip:sms-ro-", from); Assert.DoesNotContain("<sip:sms2-", from);
        }
        else
        {
            Assert.Contains("<sip:sms2-", from); Assert.Contains("-" + canonical + "@", from);
        }
    }

    private sealed class PausedDatabase(string path) : PreferenceDatabaseService(path), IPreferenceDatabaseService, ISipSmsForwardStore
    {
        public string Path { get; } = path;
        public readonly TaskCompletionSource ReleaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Channel<(string? Source, string? Reply, IReadOnlyList<SipSmsForwardRecipient> Recipients)> Captures =
            Channel.CreateUnbounded<(string?, string?, IReadOnlyList<SipSmsForwardRecipient>)>();
        public readonly Channel<IncomingSmsPersistenceResult> Commits = Channel.CreateUnbounded<IncomingSmsPersistenceResult>();
        public readonly Channel<(string Id, SipSmsForwardState State)> Completions = Channel.CreateUnbounded<(string, SipSmsForwardState)>();
        private int _entries;

        async Task<IncomingSmsPersistenceResult> IPreferenceDatabaseService.SaveIncomingSmsWithForwardsAsync(SmsMessageModel message,
            IReadOnlyList<SipSmsForwardRecipient> recipients, CancellationToken ct)
        {
            Captures.Writer.TryWrite((message.SourceMessageId, Snapshot(message), recipients.ToArray()));
            if (Interlocked.Increment(ref _entries) == 1) await ReleaseFirst.Task.WaitAsync(ct);
            var result = await base.SaveIncomingSmsWithForwardsAsync(message, recipients, ct);
            Commits.Writer.TryWrite(result); return result;
        }

        async Task ISipSmsForwardStore.CompleteAsync(string id, string token, SipSmsForwardState state, CancellationToken ct)
        {
            await base.CompleteAsync(id, token, state, ct);
            Completions.Writer.TryWrite((id, state));
        }
    }

    private static async Task<T> Next<T>(ChannelReader<T> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task<SipMessage?> Register(UdpClient phone, IPEndPoint gateway)
    {
        var uri = "sip:" + Realm;
        var request = new SipMessage { IsRequest = true, Method = "REGISTER", RequestUri = uri };
        var port = ((IPEndPoint)phone.Client.LocalEndPoint!).Port;
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("From", $"<sip:{User}@{Realm}>;tag=synthetic-canonical-phone");
        request.SetHeader("To", $"<sip:{User}@{Realm}>"); request.SetHeader("Call-ID", Guid.NewGuid().ToString("N"));
        request.SetHeader("CSeq", "1 REGISTER"); request.SetHeader("Contact", $"<sip:{User}@127.0.0.1:{port}>"); request.SetHeader("Expires", "300");
        await phone.SendAsync(request.ToBytes(), gateway);
        var challenge = await Receive(phone, gateway); Assert.Equal(401, challenge.StatusCode);
        var nonce = Regex.Match(challenge.GetHeader("WWW-Authenticate")!, "nonce=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(nonce);
        const string cnonce = "synthetic-canonical-client", nc = "00000001";
        static string Digest(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var response = Digest(Digest(User + ":" + Realm + ":" + Password) + ":" + nonce + ":" + nc + ":" + cnonce + ":auth:" + Digest("REGISTER:" + uri));
        request.SetHeader("Authorization", $"Digest username=\"{User}\", realm=\"{Realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\", algorithm=MD5, qop=auth, nc={nc}, cnonce=\"{cnonce}\"");
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        await phone.SendAsync(request.ToBytes(), gateway);
        SipMessage? buffered = null;
        while (true)
        {
            var next = await Receive(phone, gateway);
            if (next.IsRequest) { Assert.Equal("MESSAGE", next.Method); Assert.Null(buffered); buffered = next; continue; }
            Assert.Equal(200, next.StatusCode); return buffered;
        }
    }

    private static async Task<SipMessage> Receive(UdpClient phone, IPEndPoint expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var datagram = await phone.ReceiveAsync(timeout.Token); Assert.Equal(expected, datagram.RemoteEndPoint);
        return SipMessage.Parse(datagram.Buffer);
    }

    private static async Task NoWire(UdpClient phone)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await phone.ReceiveAsync(timeout.Token));
    }

    private static async Task<string> ReplyColumn(string path, string inbox)
    {
        using var connection = Connection(path); await connection.OpenAsync();
        using var command = new SqliteCommand("SELECT ReplyNumberSnapshot FROM SipSmsForwards WHERE InboxId=$inbox", connection);
        command.Parameters.AddWithValue("$inbox", inbox);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> Ledger(string path)
    {
        using var connection = Connection(path); await connection.OpenAsync();
        using var command = new SqliteCommand("""
            SELECT hex(Id)||'/'||hex(InboxId)||'/'||hex(PayloadFingerprint)||'/'||hex(SenderSnapshot)||'/'||hex(ReplyNumberSnapshot)
                ||'/'||ExpiresAtTicks||'/'||State||'/'||hex(AttemptToken)
            FROM SipSmsForwards ORDER BY Id
            """, connection);
        using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return string.Join("\n", values);
    }

    private static SqliteConnection Connection(string path) => new(new SqliteConnectionStringBuilder
        { DataSource = path, Cache = SqliteCacheMode.Private }.ToString());

    private static async Task OnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
