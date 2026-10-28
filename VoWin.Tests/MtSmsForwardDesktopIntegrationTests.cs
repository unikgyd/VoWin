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

/// <summary>Actual desktop event wiring, real SQLite and synthetic loopback SIP; never a modem or production profile.</summary>
public sealed class MtSmsForwardDesktopIntegrationTests
{
    private const string User = "1001", Password = "synthetic-mt-desktop-password", Realm = "desktop-mt.test", Line = "original-offline-slot";
    private static readonly string Binding = new('a', 64);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task KernelReceiveCapturesBeforeAwaitAndOnlyCommittedOriginalRecipientReachesRealSip(bool knownCard) => OnDispatcher(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-mt-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "preferences.db"); var database = new ObservedDatabase(path);
            var notification = new TaskCompletionSource<SmsMessageModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = 0;
            await using var service = new VoKernelService(database, new(Path.Combine(directory, "telephony.json")),
                Path.Combine(directory, "diagnostic.log"), (message, _) => { notifications++; notification.TrySetResult(message); }, Dispatcher.CurrentDispatcher);
            var options = Options();
            await service.StartSipGatewayAsync(options);
            Assert.Empty(service.Kernel.Pool.Slots);
            var incoming = new SmsMessage(1, SmsStatus.Unread, "+15551234567", "synthetic desktop original MT", DateTime.UtcNow,
                Id: "actual-desktop-event-source");
            Emit(service, new(incoming, Line, knownCard ? Binding : null));
            var captured = await database.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(User, Assert.Single(captured).User);
            Assert.Equal(0, notifications); Assert.Empty(await database.GetAllSmsMessagesAsync());

            // The real receive call is now suspended before the database write.
            // A later gateway/configuration must not become its recipient plan.
            await service.StopSipGatewayAsync();
            await service.StartSipGatewayAsync(options with
            {
                Accounts = new() { [User] = Password, ["1002"] = Password },
                LocalAccountEpochs = new() { [User] = new('e', 32), ["1002"] = new('f', 32) }
            });
            database.AllowCommit.TrySetResult();
            var shown = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await database.Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsNew);
            Assert.Equal(1, notifications);
            var stored = Assert.Single(await database.GetAllSmsMessagesAsync());
            Assert.Equal(shown.Id, stored.Id); Assert.Equal(incoming.Id, stored.SourceMessageId);
            Assert.Equal(knownCard ? Binding : null, stored.SimBinding);
            var queue = (ISipSmsForwardStore)database;
            var item = Assert.Single(await queue.GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None));
            Assert.Equal(User, item.Recipient.User); Assert.Equal(knownCard ? Binding : null, item.Payload.SimBinding);

            using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = service.Kernel.LocalSipGateway!.Status.LocalEndPoint!;
            var buffered = await Register(phone, endpoint);
            var message = buffered ?? await Receive(phone, endpoint);
            Assert.Equal("MESSAGE", message.Method); Assert.Equal(incoming.Text, message.Body);
            Assert.Contains(knownCard ? "sms2-" : "sms-ro-", message.GetHeader("From"));
            Assert.Equal(Line, message.GetHeader("X-VoWin-Line"));
            Assert.Equal((long)SipSmsForwardState.Unknown, await Scalar(path, "SELECT State FROM SipSmsForwards WHERE Id=$id", item.Id));
            Assert.Empty(await queue.GetPendingAsync(DateTimeOffset.UtcNow, 2048, CancellationToken.None));
            await phone.SendAsync(message.CreateResponse(200, "OK").ToBytes(), endpoint);
            var completed = await database.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(item.Id, completed.Id); Assert.Equal(SipSmsForwardState.Accepted, completed.State);
            Assert.Equal((long)SipSmsForwardState.Accepted, await Scalar(path, "SELECT State FROM SipSmsForwards WHERE Id=$id", item.Id));

            Emit(service, new(incoming, Line, knownCard ? Binding : null));
            var duplicate = await database.Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(duplicate.IsNew); Assert.Equal(0, duplicate.QueuedRecipients);
            Assert.Equal(1, notifications); Assert.Equal(1L, await Scalar(path, "SELECT COUNT(*) FROM SipSmsForwards"));
            Assert.Empty(service.Kernel.Pool.Slots); Assert.Null(service.Kernel.ActiveCall);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    });

    private sealed class ObservedDatabase(string path) : PreferenceDatabaseService(path), IPreferenceDatabaseService, ISipSmsForwardStore
    {
        public readonly TaskCompletionSource<IReadOnlyList<SipSmsForwardRecipient>> Captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Channel<IncomingSmsPersistenceResult> Commits = Channel.CreateUnbounded<IncomingSmsPersistenceResult>();
        public readonly TaskCompletionSource<(string Id, SipSmsForwardState State)> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IncomingSmsPersistenceResult> IPreferenceDatabaseService.SaveIncomingSmsWithForwardsAsync(SmsMessageModel message,
            IReadOnlyList<SipSmsForwardRecipient> recipients, CancellationToken ct)
        {
            Captured.TrySetResult(recipients.ToArray());
            await AllowCommit.Task.WaitAsync(ct);
            var result = await base.SaveIncomingSmsWithForwardsAsync(message, recipients, ct);
            Commits.Writer.TryWrite(result); return result;
        }
        async Task ISipSmsForwardStore.CompleteAsync(string id, string token, SipSmsForwardState state, CancellationToken ct)
        {
            await base.CompleteAsync(id, token, state, ct);
            Completed.TrySetResult((id, state));
        }
    }

    private static SipGatewayOptions Options()
    {
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return new() { BindAddress = IPAddress.Loopback, SipPort = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port,
            Realm = Realm, Accounts = new() { [User] = Password }, LocalAccountEpochs = new() { [User] = new('e', 32) },
            MessagesEnabled = true, ForwardIncomingSms = true, RequireConfiguredLines = true,
            ExtensionPolicies = new() { [User] = new() { AllowSms = false } }, // Send permission must not disable reception.
            LineRoutes = new() { [Line] = new() { ForwardIncomingSms = true } } };
    }
    private static void Emit(VoKernelService service, SmsReceivedEventArgs incoming) =>
        ((EventHandler<SmsReceivedEventArgs>)typeof(VoKernel).GetField("SmsReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service.Kernel)!).Invoke(service.Kernel, incoming);

    private static async Task<SipMessage?> Register(UdpClient phone, IPEndPoint gateway)
    {
        var uri = "sip:" + Realm;
        var request = new SipMessage { IsRequest = true, Method = "REGISTER", RequestUri = uri };
        var port = ((IPEndPoint)phone.Client.LocalEndPoint!).Port;
        request.SetHeader("Via", $"SIP/2.0/UDP 127.0.0.1:{port};branch=z9hG4bK{Guid.NewGuid():N};rport");
        request.SetHeader("From", $"<sip:{User}@{Realm}>;tag=synthetic-phone");
        request.SetHeader("To", $"<sip:{User}@{Realm}>"); request.SetHeader("Call-ID", Guid.NewGuid().ToString("N"));
        request.SetHeader("CSeq", "1 REGISTER"); request.SetHeader("Contact", $"<sip:{User}@127.0.0.1:{port}>"); request.SetHeader("Expires", "300");
        await phone.SendAsync(request.ToBytes(), gateway);
        var challenge = await Receive(phone, gateway); Assert.Equal(401, challenge.StatusCode);
        var nonce = Regex.Match(challenge.GetHeader("WWW-Authenticate")!, "nonce=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(nonce);
        const string cnonce = "synthetic-mt-client", nc = "00000001";
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
    private static async Task<long> Scalar(string path, string sql, string? id = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Private }.ToString());
        await connection.OpenAsync(); using var command = new SqliteCommand(sql, connection);
        if (id != null) command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
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
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
