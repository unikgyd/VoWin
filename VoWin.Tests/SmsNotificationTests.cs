using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Sms;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class SmsNotificationTests
{
    [Theory]
    [InlineData(1, true, SmsDeliveryState.Sent)]
    [InlineData(1, false, SmsDeliveryState.PartiallySent)]
    [InlineData(0, false, SmsDeliveryState.Failed)]
    public Task SipSmsSubmissionsAreSavedOnUiDispatcherWithoutSendingAgain(int accepted, bool complete, SmsDeliveryState expected) => OnDispatcherAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-sip-sms-history-" + Guid.NewGuid().ToString("N"));
        var preferences = new PreferenceDatabaseService(Path.Combine(directory, "preferences.db")); await preferences.InitializeAsync();
        await using var service = new VoKernelService(preferences, new(Path.Combine(directory, "telephony.json")),
            Path.Combine(directory, "test.log"), uiDispatcher: Dispatcher.CurrentDispatcher);
        await service.RecordSipSmsSubmissionAsync(new("desk", "module-a", new("+15551234567", "private-test-body",
            SmsEncoding.Ucs2, null, 2, accepted, 2, complete, "submission-test", [], DateTime.UtcNow)));
        var model = Assert.Single(Assert.Single(service.Conversations).Messages);
        Assert.Equal(expected, model.DeliveryState); Assert.Equal("module-a", model.SlotId);
        Assert.Contains("SIP desk", model.DeliveryStatus); Assert.Single(await preferences.GetAllSmsMessagesAsync());
        Assert.Empty(service.Kernel.Pool.Slots);
        await service.DisposeAsync();
        var log = await File.ReadAllTextAsync(Path.Combine(directory, "test.log"));
        Assert.DoesNotContain("private-test-body", log); Assert.DoesNotContain("+15551234567", log);
    });
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

    [Theory]
    [InlineData("A normal incoming message", false)]
    [InlineData("Your OTP code is 123456", true)]
    public Task OrdinaryAndOtpSmsBothPersistAndNotifyOnceOnUiDispatcher(string body, bool otp) => OnDispatcherAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "VoWin-sms-notify-" + Guid.NewGuid().ToString("N"));
        var preferences = new PreferenceDatabaseService(Path.Combine(directory, "preferences.db"));
        await preferences.InitializeAsync();
        var notifications = new List<SmsMessageModel>();
        var ui = Dispatcher.CurrentDispatcher;
        await using var service = new VoKernelService(preferences, new(Path.Combine(directory, "telephony.json")),
            Path.Combine(directory, "test.log"), (item, _) =>
            {
                Assert.True(ui.CheckAccess()); notifications.Add(item);
            }, uiDispatcher: ui);
        try
        {
            var add = typeof(VoKernelService).GetMethod("AddIncomingSmsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var message = new SmsMessage(1, SmsStatus.Unread, "10086", body, DateTime.UtcNow, RawPdu: "offline-test-pdu");
            await (Task)add.Invoke(service, [message, "sms-module-a"])!;
            await (Task)add.Invoke(service, [message, "sms-module-a"])!;
            var notified = Assert.Single(notifications);
            Assert.Equal(otp, notified.HasOtpCode); Assert.Equal("sms-module-a", notified.SlotId);
            Assert.Single((await preferences.GetAllSmsMessagesAsync()));
            Assert.Single(Assert.Single(service.Conversations).Messages);
            Assert.Equal(1, service.Conversations[0].UnreadCount);
        }
        finally
        {
            await service.DisposeAsync(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task OutgoingSubmissionKeepsOriginalSimWhileDatabaseSaveIsPending() => OnDispatcherAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "VoWin-sms-owner-" + Guid.NewGuid().ToString("N"));
        var database = new PreferenceDatabaseService(Path.Combine(directory, "preferences.db"));
        await database.InitializeAsync();
        var saved = new TaskCompletionSource<SmsMessageModel>();
        var resume = new TaskCompletionSource();
        var preferences = DispatchProxy.Create<IPreferenceDatabaseService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)(object)preferences).Handler = (method, args) =>
            method.Name == nameof(IPreferenceDatabaseService.SaveSmsMessageAsync)
                ? SaveAsync((SmsMessageModel)args![0]!) : method.Invoke(database, args);
        async Task SaveAsync(SmsMessageModel message)
        {
            await database.SaveSmsMessageAsync(message);
            saved.SetResult(message);
            await resume.Task;
        }
        await using var service = new VoKernelService(preferences, new(Path.Combine(directory, "telephony.json")),
            Path.Combine(directory, "test.log"), uiDispatcher: Dispatcher.CurrentDispatcher);
        try
        {
            var first = new ModemSlot("sms-owner-a", "OFFLINE-A") { TelephonyEnabled = false };
            var second = new ModemSlot("sms-owner-b", "OFFLINE-B") { PreferredTelephonyMode = TelephonyMode.VoWifi };
            Assert.True(service.Kernel.Pool.TryAddSlot(first)); Assert.True(service.Kernel.Pool.TryAddSlot(second));
            Assert.True(service.Kernel.Pool.SelectSlot(first.Id));
            var pending = service.SendSmsAsync("10086", "offline owner test");
            var message = await saved.Task;
            Assert.Equal(first.Id, message.SlotId);
            Assert.True(service.Kernel.Pool.SelectSlot(second.Id));
            resume.SetResult();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Contains("通信已停止", error.Message);
            Assert.Equal(SmsDeliveryState.Failed, message.DeliveryState);
            var persisted = Assert.Single(await database.GetAllSmsMessagesAsync());
            Assert.Equal(first.Id, persisted.SlotId); Assert.Equal(SmsDeliveryState.Failed, persisted.DeliveryState);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendSmsAsync("10086", "offline missing module", slotId: "missing"));
            Assert.Single(await database.GetAllSmsMessagesAsync());
        }
        finally
        {
            resume.TrySetResult();
            await service.DisposeAsync(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task NotificationFailureDoesNotLoseTheSavedSmsOrUnreadState() => OnDispatcherAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "VoWin-sms-alert-" + Guid.NewGuid().ToString("N"));
        var database = new PreferenceDatabaseService(Path.Combine(directory, "preferences.db"));
        await database.InitializeAsync();
        await using var service = new VoKernelService(database, new(Path.Combine(directory, "telephony.json")),
            Path.Combine(directory, "test.log"), (_, _) => throw new InvalidOperationException("offline presenter failure"),
            uiDispatcher: Dispatcher.CurrentDispatcher);
        try
        {
            var add = typeof(VoKernelService).GetMethod("AddIncomingSmsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)add.Invoke(service, [new SmsMessage(1, SmsStatus.Unread, "10086", "ordinary SMS", DateTime.UtcNow), "sms-a"])!;
            Assert.Single(await database.GetAllSmsMessagesAsync());
            Assert.Equal(1, Assert.Single(service.Conversations).UnreadCount);
        }
        finally
        {
            await service.DisposeAsync(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    });
}
