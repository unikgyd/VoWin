using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Kernel.SipGateway;
using VoSharp.Telephony.Calls;
using VoWin.Models;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class SipDesktopIncomingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IncomingHasLocalPickupControlsAndRemotePickupIsNotShownAsAnOutgoingCall(bool remote)
    {
        await using var kernel = new VoKernel();
        var slot = new ModemSlot("offline-a", "OFFLINE_A");
        Assert.True(kernel.Pool.TryAddSlot(slot));
        Track(kernel, slot.Id, "incoming-a", CallState.Incoming);
        var answerCount = 0;
        var proxy = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)proxy).Handler = (method, arguments) => method.Name switch
        {
            "get_Kernel" => kernel,
            "get_Slots" => new ObservableCollection<ModemSlot>([slot]),
            "get_ActiveSlot" => slot,
            "get_CallHistory" => new ObservableCollection<CallRecordModel>(),
            "get_CurrentCallState" => kernel.ActiveCall?.State ?? CallState.Idle,
            "get_CurrentCallNumber" => "offline caller",
            "get_HasIncomingCall" => kernel.ActiveCall?.State == CallState.Incoming,
            "get_IncomingCallerNumber" => "offline caller",
            "IsPresentedCallEvent" => arguments![0] as string == kernel.ActiveCall?.CallId,
            "AnswerAsync" => Answer(),
            _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
            _ => throw new NotSupportedException(method.Name)
        };
        Task<CallInfo?> Answer() { answerCount++; return Task.FromResult<CallInfo?>(null); }
        var model = new PhoneViewModel(proxy);
        try
        {
            Assert.Equal(CallPhase.Incoming, model.CurrentPhase);
            Assert.True(model.ShowIncomingControls); Assert.True(model.CanAcceptIncoming);
            Assert.False(model.ShowHangupControl);
            Assert.False(kernel.ActiveCall!.IsOutgoing);
            // Presentation is tested synchronously, independently of the live
            // app dispatcher that the separate render test creates/shuts down.
            model.PresentSipIncomingPickup(new("incoming-a", "other-slot", !remote));
            Assert.False(model.IsSipRemoteCall); // Another modem must never steal the local popup.
            model.PresentSipIncomingPickup(new("incoming-a", slot.Id, !remote));
            Assert.Equal(remote, model.IsSipRemoteCall);
            Assert.Equal(!remote, model.ShowIncomingControls);
            Assert.Equal(remote, model.ShowHangupControl);
            if (remote)
            {
                Assert.Equal("其他分机接听中", model.CallStatusTitle);
                await model.AnswerCommand.ExecuteAsync(null);
                Assert.Equal(0, answerCount);
                Track(kernel, slot.Id, "incoming-a", CallState.Active);
                Assert.Equal("其他分机通话中", model.CallStatusTitle);
                Assert.Contains("本机麦克风未启用", model.CallStatusSubtitle);
            }
            else
            {
                await model.AnswerCommand.ExecuteAsync(null);
                Assert.Equal(1, answerCount);
            }
            Track(kernel, slot.Id, "incoming-a", CallState.Ended);
            Track(kernel, slot.Id, "incoming-b", CallState.Incoming);
            Assert.False(model.IsSipRemoteCall);
            Assert.True(model.ShowIncomingControls); // Old pickup metadata cannot suppress a new call.
        }
        finally
        {
            ((DispatcherTimer)typeof(PhoneViewModel).GetField("_durationTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(model)!).Stop();
        }
    }

    private static void Track(VoKernel kernel, string slot, string call, CallState state) =>
        typeof(VoKernel).GetMethod("TrackCall", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(kernel, [slot, call, "offline caller", false, state, null]);
}
