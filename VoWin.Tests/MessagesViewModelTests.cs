using System.Collections.ObjectModel;
using System.Reflection;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Sms;
using VoWin.Models;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class MessagesViewModelTests
{
    private static (MessagesViewModel Model, ObservableCollection<ModemSlot> Slots) Create(
        Func<object?[], Task<SmsSubmitResult>> send, TelephonyMode mode = TelephonyMode.HostIms)
    {
        var slots = new ObservableCollection<ModemSlot> { new("sms-module-a", "OFFLINE-SMS-A") { PreferredTelephonyMode = mode } };
        var conversations = new ObservableCollection<SmsConversationModel> { new() { ContactNumber = "10086" } };
        var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)(object)service).Handler = (method, arguments) => method.Name switch
        {
            "get_Slots" => slots,
            "get_Conversations" => conversations,
            "get_ActiveSlot" => slots.FirstOrDefault(),
            "SendSmsAsync" => send(arguments!),
            _ => throw new NotSupportedException(method.Name)
        };
        return (new(service) { MessageText = "offline routing test" }, slots);
    }

    private static SmsSubmitResult Result(bool accepted, int acceptedParts = 0, int totalParts = 1) =>
        new("10086", "offline routing test", SmsEncoding.Gsm7Bit, null, totalParts,
            accepted ? totalParts : acceptedParts, totalParts, accepted,
            accepted ? "accepted" : "rejected", [], DateTime.UtcNow);

    [Theory]
    [InlineData(TelephonyMode.HostIms)]
    [InlineData(TelephonyMode.ModemBridge)]
    [InlineData(TelephonyMode.VoWifi)]
    public async Task DefaultSendUsesSelectedModuleRouteWithoutForcingVoWifi(TelephonyMode mode)
    {
        object?[]? submitted = null;
        var (model, slots) = Create(args => { submitted = args; return Task.FromResult(Result(true)); }, mode);
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.NotNull(submitted);
        Assert.Equal(slots[0].Id, submitted[3]);
        Assert.Equal(false, submitted[4]); Assert.Equal(false, submitted[5]);
        Assert.Empty(model.MessageText);
    }

    [Fact]
    public async Task ExplicitCellularOverrideNeverAlsoForcesVoWifi()
    {
        object?[]? submitted = null;
        var (model, _) = Create(args => { submitted = args; return Task.FromResult(Result(true)); });
        model.ForceCellular = true;
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.NotNull(submitted); Assert.Equal(false, submitted[4]); Assert.Equal(true, submitted[5]);
    }

    [Fact]
    public async Task EntireSubmissionRejectedRestoresDraftAndReportsFailureNotPartialSuccess()
    {
        var (model, _) = Create(_ => Task.FromResult(Result(false)));
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal("offline routing test", model.MessageText);
        Assert.StartsWith("发送失败", model.StatusMessage);
        Assert.False(model.IsSending);
    }

    [Fact]
    public async Task PartiallyAcceptedLongMessageIsNotAutomaticallyResentOrRestoredAsWholeDraft()
    {
        var sends = 0;
        var (model, _) = Create(_ => { sends++; return Task.FromResult(Result(false, 1, 2)); });
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal(1, sends); Assert.Empty(model.MessageText);
        Assert.Contains("1/2", model.StatusMessage);
    }

    [Fact]
    public async Task DisconnectedSelectedModuleDoesNotSilentlySendThroughAnotherSim()
    {
        var sends = 0;
        var (model, slots) = Create(_ => { sends++; return Task.FromResult(Result(true)); });
        slots.Add(new("sms-module-b", "OFFLINE-SMS-B"));
        slots.RemoveAt(0);
        Assert.False(model.SendMessageCommand.CanExecute(null));
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal(0, sends); Assert.Equal("offline routing test", model.MessageText);
        model.SelectedSlot = slots[0];
        Assert.True(model.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public async Task LateFailureDoesNotOverwriteANewConversationDraft()
    {
        var failure = new TaskCompletionSource<SmsSubmitResult>();
        var (model, _) = Create(_ => failure.Task);
        var pending = model.SendMessageCommand.ExecuteAsync(null);
        model.SelectedConversation = new() { ContactNumber = "10010" };
        failure.SetException(new InvalidOperationException("offline route rejected"));
        await pending;
        Assert.Empty(model.MessageText);
        Assert.False(model.IsSending);
    }

    [Fact]
    public async Task LateFailureDoesNotOverwriteTextTypedWhileSending()
    {
        var failure = new TaskCompletionSource<SmsSubmitResult>();
        var (model, _) = Create(_ => failure.Task);
        var pending = model.SendMessageCommand.ExecuteAsync(null);
        model.MessageText = "new draft";
        failure.SetException(new InvalidOperationException("offline route rejected"));
        await pending;
        Assert.Equal("new draft", model.MessageText);
    }

    [Fact]
    public void PartialSubmissionIsNotOfferedAsAnEntireFailedMessageToRetry()
    {
        var message = new SmsMessageModel { DeliveryState = SmsDeliveryState.PartiallySent };
        Assert.False(message.IsFailed);
        Assert.Contains("部分提交", message.StatusDisplay);
    }

    [Theory]
    [InlineData(SmsDeliveryState.PartiallySent, "sms-module-a")]
    [InlineData(SmsDeliveryState.Failed, "sms-module-b")]
    public async Task RetryDoesNotResendPartialSubmissionsOrSwitchTheOriginalSim(SmsDeliveryState state, string slotId)
    {
        var sends = 0;
        var (model, _) = Create(_ => { sends++; return Task.FromResult(Result(true)); });
        await model.RetryMessageCommand.ExecuteAsync(new SmsMessageModel { Text = "old failed SMS", SlotId = slotId, DeliveryState = state });
        Assert.Equal(0, sends); Assert.Equal("offline routing test", model.MessageText);
    }

    [Fact]
    public async Task RetryOfAnEntirelyRejectedMessageOnItsOriginalSimIsAllowed()
    {
        var sends = 0;
        var (model, _) = Create(_ => { sends++; return Task.FromResult(Result(true)); });
        await model.RetryMessageCommand.ExecuteAsync(new SmsMessageModel { Text = "old failed SMS", SlotId = "sms-module-a", DeliveryState = SmsDeliveryState.Failed });
        Assert.Equal(1, sends); Assert.Empty(model.MessageText);
    }

    [Fact]
    public void DeliveryStateChangesNotifyBubbleBindings()
    {
        var message = new SmsMessageModel { IsOutgoing = true, DeliveryState = SmsDeliveryState.Sending };
        var changes = new List<string?>();
        Assert.IsAssignableFrom<System.ComponentModel.INotifyPropertyChanged>(message);
        ((System.ComponentModel.INotifyPropertyChanged)message).PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        message.DeliveryState = SmsDeliveryState.Failed;
        Assert.Contains(nameof(SmsMessageModel.StatusDisplay), changes);
        Assert.Contains(nameof(SmsMessageModel.DeliveryStatusText), changes);
        Assert.Contains(nameof(SmsMessageModel.IsFailed), changes);
    }
}
