using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoWin.Models;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class PhoneCallOwnershipTests
{
    [Theory]
    [InlineData("HangupAsync", CallState.Active)]
    [InlineData("RejectAsync", CallState.Active)]
    [InlineData("HangupAsync", CallState.Held)]
    public async Task LateCallActionCompletionDoesNotOverwritePromotedActiveCall(string operation, CallState promotedState)
    {
        await using var kernel = new VoKernel();
        var first = new ModemSlot("first", "OFFLINE_A");
        var second = new ModemSlot("second", "OFFLINE_B");
        Assert.True(kernel.Pool.TryAddSlot(first)); Assert.True(kernel.Pool.TryAddSlot(second));
        Track(kernel, first.Id, "first-call", CallState.Active);
        var hangup = new TaskCompletionSource<CallInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        string? target = null;
        ((PhoneCallServiceProxy)service).Handler = (method, arguments) => method.Name switch
        {
            "get_Kernel" => kernel,
            "get_Slots" => new ObservableCollection<ModemSlot>([first, second]),
            "get_ActiveSlot" => first,
            "get_CallHistory" => new ObservableCollection<CallRecordModel>(),
            "get_CurrentCallState" => kernel.ActiveCall?.State ?? CallState.Idle,
            "get_CurrentCallNumber" => kernel.ActiveCall?.RemoteNumber,
            "get_HasIncomingCall" => kernel.ActiveCall?.State == CallState.Incoming,
            "get_IncomingCallerNumber" => null,
            _ when method.Name == operation => CaptureHangup(arguments),
            _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
            _ => throw new NotSupportedException(method.Name)
        };
        Task<CallInfo?> CaptureHangup(object?[]? arguments) { target = (string?)arguments![0]; return hangup.Task; }
        var model = new PhoneViewModel(service) { SelectedSlot = first };
        try
        {
            model.SelectedSlot = second;
            Assert.Contains(first.DisplayTitle, model.CurrentCallLine);
            Assert.DoesNotContain(second.DisplayTitle, model.CurrentCallLine);
            model.SelectedSlot = first;
            var pending = (operation == "HangupAsync" ? model.HangupCommand : model.RejectCommand).ExecuteAsync(null);
            Assert.Equal(first.Id, target); Assert.False(pending.IsCompleted);
            Track(kernel, first.Id, "first-call", CallState.Ended);
            Track(kernel, second.Id, "second-call", promotedState);
            hangup.SetResult(null);
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(promotedState == CallState.Held ? CallPhase.Held : CallPhase.Connected, model.CurrentPhase);
            Assert.False(model.IsEndedVisible);
            Assert.False(model.IsHangupOperationPending);
            Assert.Null(model.CallActionError);
            Assert.Equal(second.Id, kernel.ActiveCallSlotId);
        }
        finally
        {
            ((DispatcherTimer)typeof(PhoneViewModel).GetField("_durationTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(model)!).Stop();
        }
    }

    private static void Track(VoKernel kernel, string slot, string call, CallState state) =>
        typeof(VoKernel).GetMethod("TrackCall", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(kernel, [slot, call, "10086", true, state, null]);
}

public class PhoneCallServiceProxy : DispatchProxy
{
    internal Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
