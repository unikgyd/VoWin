using System.Collections.ObjectModel;
using System.Reflection;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoWin.Models;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class PhoneMicrophoneTests
{
    [Fact]
    public async Task UiMuteAndRetryCommandsActuallyReachTheAudioService()
    {
        await using var kernel = new VoKernel();
        var status = new MicrophoneUplinkStatus { CallId = "offline-call", DeviceName = "offline mic", Error = "offline capture stopped" };
        var muteCalls = new List<bool>(); var restarts = 0;
        var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)(object)service).Handler = (method, arguments) => method.Name switch
        {
            "get_Kernel" => kernel, "get_Slots" => new ObservableCollection<ModemSlot>(),
            "get_CallHistory" => new ObservableCollection<CallRecordModel>(), "get_ActiveSlot" => null,
            "get_CurrentCallState" => CallState.Active, "get_HasIncomingCall" => false,
            "GetMicrophoneUplinkStatus" => status,
            "SetMicrophoneMuted" => Mute((bool)arguments![0]!),
            "RestartMicrophoneAsync" => Restart(),
            _ when method.Name.StartsWith("add_") => null,
            _ => throw new NotSupportedException(method.Name)
        };
        bool Mute(bool value) { muteCalls.Add(value); status = status with { IsMuted = value }; return true; }
        Task<bool> Restart() { restarts++; status = status with { IsCapturing = true, Error = null }; return Task.FromResult(true); }
        var model = new PhoneViewModel(service);
        model.RefreshMicrophoneStatus();
        Assert.True(model.ShowMicrophoneStatus); Assert.True(model.CanRestartMicrophone);
        model.ToggleMuteCommand.Execute(null); Assert.True(model.IsMuted); Assert.Equal([true], muteCalls);
        await model.RestartMicrophoneCommand.ExecuteAsync(null);
        Assert.Equal(1, restarts); Assert.True(model.MicrophoneStatus.IsCapturing);
        Assert.True(model.IsMuted); Assert.False(model.IsMicrophoneRestarting);
    }
}
