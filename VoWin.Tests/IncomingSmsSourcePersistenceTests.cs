using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel;
using VoSharp.Telephony.Sms;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

/// <summary>Only disposable SQLite files and synthetic events; never opens a modem or SIP listener.</summary>
public sealed class IncomingSmsSourcePersistenceTests
{
    private static readonly string BindingA = new('a', 64), BindingB = new('b', 64);

    private static SmsMessageModel Message(string? binding = null, string? source = "source-a", string? slot = "slot-a",
        string? pdu = null) => new()
    {
        SlotId = slot, SimBinding = binding, SourceMessageId = source, RawPdu = pdu,
        SenderOrRecipient = "+15550001111", Text = "synthetic private inbox text",
        Timestamp = DateTime.UtcNow, DeliveryState = SmsDeliveryState.Received
    };

    [Fact]
    public async Task SourceSnapshotSurvivesAllReadPathsAndIsNotSerializedByThePublicModel()
    {
        await WithDatabase(async (database, path) =>
        {
            var item = Message(BindingA);
            Assert.True(await database.SaveIncomingSmsIfNewAsync(item));
            var reopened = new PreferenceDatabaseService(path);
            var all = Assert.Single(await reopened.GetAllSmsMessagesAsync());
            var byNumber = Assert.Single(await reopened.GetMessagesByNumberAsync(item.SenderOrRecipient));
            var queried = Assert.Single((await reopened.QueryIncomingSmsAsync(new(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1)))).Messages);
            foreach (var stored in new[] { all, byNumber, queried })
            {
                Assert.Equal(item.Id, stored.Id); Assert.Equal(BindingA, stored.SimBinding);
                Assert.Equal("source-a", stored.SourceMessageId); Assert.Equal("slot-a", stored.SlotId);
                var json = JsonSerializer.Serialize(stored);
                Assert.DoesNotContain("SimBinding", json); Assert.DoesNotContain("SourceMessageId", json);
                Assert.DoesNotContain(BindingA, json); Assert.DoesNotContain("source-a", json);
            }
        });
    }

    [Fact]
    public async Task IndependentInstancesAtomicallyAdmitOnlyOneScopedSource()
    {
        await WithDatabase(async (_, path) =>
        {
            var stores = Enumerable.Range(0, 8).Select(_ => new PreferenceDatabaseService(path)).ToArray();
            foreach (var store in stores) await store.InitializeAsync();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = stores.Select(store => Task.Run(async () =>
            {
                await start.Task;
                return await store.SaveIncomingSmsIfNewAsync(Message(BindingA));
            })).ToArray();
            start.SetResult();
            Assert.Single(await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(15)), inserted => inserted);
            var stored = Assert.Single(await stores[0].GetAllSmsMessagesAsync());
            Assert.Equal(BindingA, stored.SimBinding); Assert.Equal("source-a", stored.SourceMessageId);
        });
    }

    [Fact]
    public async Task SourceAndPduDedupNeverCrossSlotOrKnownAndUnknownSimScopes()
    {
        await WithDatabase(async (database, _) =>
        {
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(BindingA, pdu: "same-pdu")));
            Assert.False(await database.SaveIncomingSmsIfNewAsync(Message(BindingA, source: "another-source", pdu: "same-pdu")));
            Assert.False(await database.SaveIncomingSmsIfNewAsync(Message(BindingA, pdu: "different-pdu")));
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(BindingB, pdu: "same-pdu")));
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(BindingA, slot: "slot-b", pdu: "same-pdu")));
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(null, pdu: "same-pdu")));
            Assert.False(await database.SaveIncomingSmsIfNewAsync(Message(null, pdu: "same-pdu")));
            Assert.Equal(4, (await database.GetAllSmsMessagesAsync()).Count);
        });
    }

    [Fact]
    public async Task DifferentNonemptySourceIdsWithoutPduAreNotCollapsedByContentOrTime()
    {
        await WithDatabase(async (database, _) =>
        {
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(BindingA)));
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(BindingA, "source-b")));
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(BindingA, "SOURCE-A")));
            Assert.Equal(3, (await database.GetAllSmsMessagesAsync()).Count);
        });
    }

    [Fact]
    public async Task LegacyContentFallbackRemainsScopedAndNeverInfersACard()
    {
        await WithDatabase(async (database, _) =>
        {
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(null, source: null)));
            Assert.False(await database.SaveIncomingSmsIfNewAsync(Message(null, source: null)));
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(BindingA, source: null)));
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(null, source: null, slot: "slot-b")));
            Assert.True(await database.SaveIncomingSmsIfNewAsync(Message(null, source: null, slot: null)));
            Assert.Equal(4, (await database.GetAllSmsMessagesAsync()).Count);
        });
    }

    [Fact]
    public async Task OrdinaryStatusUpsertCannotEraseOrRetargetTheOriginalSource()
    {
        await WithDatabase(async (database, _) =>
        {
            var original = Message(BindingA, pdu: "original-pdu");
            Assert.True(await database.SaveIncomingSmsIfNewAsync(original));
            await database.SaveSmsMessageAsync(new() { Id = original.Id, SenderOrRecipient = original.SenderOrRecipient,
                Text = original.Text, Timestamp = original.Timestamp, DeliveryStatus = "updated status" });
            var stored = Assert.Single(await database.GetAllSmsMessagesAsync());
            Assert.Equal(BindingA, stored.SimBinding); Assert.Equal("source-a", stored.SourceMessageId);
            Assert.Equal("slot-a", stored.SlotId); Assert.Equal("original-pdu", stored.RawPdu);
            var attemptedRetarget = Message(BindingB, "other-source", "other-slot"); attemptedRetarget.Id = original.Id;
            await database.SaveSmsMessageAsync(attemptedRetarget);
            stored = Assert.Single(await database.GetAllSmsMessagesAsync());
            Assert.Equal(BindingA, stored.SimBinding); Assert.Equal("source-a", stored.SourceMessageId);
            Assert.Equal("slot-a", stored.SlotId);
        });
    }

    [Fact]
    public async Task OldSchemaMigrationLeavesHistoricalRowsUnboundAndDoesNotBackfillOnUpsert()
    {
        await WithDatabase(async (_, path) =>
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                using var command = new SqliteCommand("""
                    CREATE TABLE SmsMessages(Id TEXT PRIMARY KEY,RemoteNumber TEXT NOT NULL,Text TEXT NOT NULL,Timestamp TEXT NOT NULL,
                        IsOutgoing INTEGER NOT NULL,DeliveryState INTEGER NOT NULL,DeliveryStatus TEXT,MessageReference INTEGER,SlotId TEXT,RawPdu TEXT);
                    INSERT INTO SmsMessages VALUES('old-row','legacy-peer','old inbox',$time,0,0,NULL,NULL,'legacy-slot',NULL);
                    """, connection);
                command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("o"));
                await command.ExecuteNonQueryAsync();
            }
            var migrated = new PreferenceDatabaseService(path); await migrated.InitializeAsync();
            var old = Assert.Single(await migrated.GetAllSmsMessagesAsync());
            Assert.Null(old.SimBinding); Assert.Null(old.SourceMessageId);
            old.SimBinding = BindingB; old.SourceMessageId = "not-a-historical-proof";
            await migrated.SaveSmsMessageAsync(old);
            var reloaded = Assert.Single(await new PreferenceDatabaseService(path).GetAllSmsMessagesAsync());
            Assert.Null(reloaded.SimBinding); Assert.Null(reloaded.SourceMessageId);
            Assert.True(await migrated.SaveIncomingSmsIfNewAsync(Message(BindingA)));
        }, initialize: false);
    }

    [Theory]
    [InlineData("bad-binding", "source-a")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "source-a")]
    [InlineData(null, "unsafe\nsource")]
    public async Task InvalidSourceMetadataFailsWithoutWritingARow(string? binding, string source)
    {
        await WithDatabase(async (database, _) =>
        {
            await Assert.ThrowsAsync<ArgumentException>(() => database.SaveIncomingSmsIfNewAsync(Message(binding, source)));
            await Assert.ThrowsAsync<ArgumentException>(() => database.SaveSmsMessageAsync(Message(binding, source)));
            Assert.Empty(await database.GetAllSmsMessagesAsync());
        });
    }

    [Fact]
    public Task SyntheticKernelEventPersistsItsCapturedBindingAndNotifiesOnlyAfterCommit() => OnDispatcherAsync(async () =>
    {
        await WithDatabase(async (database, path) =>
        {
            var notification = new TaskCompletionSource<SmsMessageModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = 0;
            await using var service = new VoKernelService(database, new(Path.Combine(Path.GetDirectoryName(path)!, "telephony.json")),
                Path.Combine(Path.GetDirectoryName(path)!, "test.log"), (item, _) =>
                {
                    notifications++; notification.TrySetResult(item);
                }, Dispatcher.CurrentDispatcher);
            var incoming = new SmsMessage(1, SmsStatus.Unread, "synthetic-sender", "synthetic event text", DateTime.UtcNow,
                Id: "captured-event-id");
            // There is deliberately no current SIM/slot to query. The event's
            // earlier verified session snapshot is the only card evidence.
            var handler = (EventHandler<SmsReceivedEventArgs>)typeof(VoKernel).GetField("SmsReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service.Kernel)!;
            handler.Invoke(service.Kernel, new(incoming, "removed-slot", BindingA));
            var shown = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var saved = Assert.Single(await database.GetAllSmsMessagesAsync());
            Assert.Equal(BindingA, saved.SimBinding); Assert.Equal("captured-event-id", saved.SourceMessageId);
            Assert.Equal(saved.Id, shown.Id); Assert.Equal(1, notifications);
            var add = typeof(VoKernelService).GetMethod("AddIncomingSmsWithSourceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)add.Invoke(service, [incoming, "removed-slot", BindingA])!;
            Assert.Equal(1, notifications);
            // Unknown-source observations remain distinct and unbound; they
            // are not promoted to the previously observed card's authority.
            await (Task)add.Invoke(service, [incoming, "removed-slot", null])!;
            Assert.Equal(2, notifications); Assert.Equal(2, (await database.GetAllSmsMessagesAsync()).Count);
            Assert.Single(await database.GetAllSmsMessagesAsync(), item => item.SimBinding is null);
            Assert.Equal(2, Assert.Single(service.Conversations).UnreadCount);
        });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TwoSlotsWithIdenticalSenderTimeAndTextRemainVisibleAndNotifyIndependently(bool knownBindings) => OnDispatcherAsync(async () =>
    {
        await WithDatabase(async (database, path) =>
        {
            var notifications = new List<SmsMessageModel>();
            var persistedEvents = new List<SmsMessageModel>();
            await using var service = new VoKernelService(database, new(Path.Combine(Path.GetDirectoryName(path)!, "telephony.json")),
                Path.Combine(Path.GetDirectoryName(path)!, "test.log"), (item, _) => notifications.Add(item), Dispatcher.CurrentDispatcher);
            service.IncomingSmsReceived += item => persistedEvents.Add(item);
            var timestamp = DateTime.UtcNow;
            var first = new SmsMessage(1, SmsStatus.Unread, "same-synthetic-sender", "same synthetic text", timestamp,
                Id: "independent-source-a");
            var second = first with { Id = "independent-source-b" };
            var add = typeof(VoKernelService).GetMethod("AddIncomingSmsWithSourceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)add.Invoke(service, [first, "slot-a", knownBindings ? BindingA : null])!;
            await (Task)add.Invoke(service, [second, "slot-b", knownBindings ? BindingB : null])!;
            var saved = await database.GetAllSmsMessagesAsync();
            Assert.Equal(2, saved.Count); Assert.Equal(2, notifications.Count); Assert.Equal(2, persistedEvents.Count);
            var conversation = Assert.Single(service.Conversations);
            Assert.Equal(2, conversation.Messages.Count); Assert.Equal(2, conversation.UnreadCount);
            Assert.All(conversation.Messages, item =>
            {
                Assert.Equal(timestamp, item.Timestamp); Assert.Equal("same synthetic text", item.Text);
                Assert.Equal("same-synthetic-sender", item.SenderOrRecipient);
            });
            Assert.Contains(conversation.Messages, item => item.SlotId == "slot-a" && item.SourceMessageId == "independent-source-a");
            Assert.Contains(conversation.Messages, item => item.SlotId == "slot-b" && item.SourceMessageId == "independent-source-b");
            Assert.Equal(2, conversation.Messages.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
            // Exact re-observation is rejected atomically, not hidden only in
            // the UI after an extra persistence event or desktop notification.
            await (Task)add.Invoke(service, [first, "slot-a", knownBindings ? BindingA : null])!;
            Assert.Equal(2, (await database.GetAllSmsMessagesAsync()).Count);
            Assert.Equal(2, notifications.Count); Assert.Equal(2, persistedEvents.Count);
            Assert.Equal(2, conversation.Messages.Count); Assert.Equal(2, conversation.UnreadCount);
        });
    });

    private static async Task WithDatabase(Func<PreferenceDatabaseService, string, Task> action, bool initialize = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-incoming-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "preferences.db"); var database = new PreferenceDatabaseService(path);
            if (initialize) await database.InitializeAsync();
            await action(database, path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task OnDispatcherAsync(Func<Task> action)
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
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
