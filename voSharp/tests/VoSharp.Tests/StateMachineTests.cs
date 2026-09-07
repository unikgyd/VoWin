using VoSharp.Common.Events;
using VoSharp.StateMachine;
using Xunit;

namespace VoSharp.Tests;

public class StateMachineTests
{
    [Fact]
    public void TestStateMachine_NormalLifecycleTransitions()
    {
        var sm = new TelephonyStateMachine();
        Assert.Equal(TelephonyState.Init, sm.CurrentState);

        // 1. SIM Ready
        bool ok = sm.Fire(StateTrigger.TriggerSimReady, "SIM Ready payload");
        Assert.True(ok);
        Assert.Equal(TelephonyState.SimReady, sm.CurrentState);

        // 2. Net Attach
        ok = sm.Fire(StateTrigger.TriggerNetAttach, "CMCC 46000");
        Assert.True(ok);
        Assert.Equal(TelephonyState.NetworkRegistered, sm.CurrentState);

        // 3. Data Connect
        ok = sm.Fire(StateTrigger.TriggerDataConnect);
        Assert.True(ok);
        Assert.Equal(TelephonyState.DataConnecting, sm.CurrentState);

        ok = sm.Fire(StateTrigger.TriggerDataConnected);
        Assert.True(ok);
        Assert.Equal(TelephonyState.DataConnected, sm.CurrentState);

        // 4. IMS Register
        ok = sm.Fire(StateTrigger.TriggerImsRegister);
        Assert.True(ok);
        Assert.Equal(TelephonyState.ImsRegistering, sm.CurrentState);

        ok = sm.Fire(StateTrigger.TriggerImsRegistered);
        Assert.True(ok);
        Assert.Equal(TelephonyState.ImsRegistered, sm.CurrentState);

        // 5. Call Ringing -> Active -> Ended
        ok = sm.Fire(StateTrigger.TriggerCallRing);
        Assert.True(ok);
        Assert.Equal(TelephonyState.CallRinging, sm.CurrentState);

        ok = sm.Fire(StateTrigger.TriggerCallAnswer);
        Assert.True(ok);
        Assert.Equal(TelephonyState.CallActive, sm.CurrentState);

        ok = sm.Fire(StateTrigger.TriggerCallHangup);
        Assert.True(ok);
        Assert.Equal(TelephonyState.CallEnded, sm.CurrentState);

        // History count check
        var history = sm.GetHistory();
        Assert.Equal(9, history.Count);
    }

    [Fact]
    public void TestStateMachine_InvalidTransition_Rejected()
    {
        var sm = new TelephonyStateMachine();
        Assert.Equal(TelephonyState.Init, sm.CurrentState);

        // Cannot jump directly to CallActive from Init
        bool ok = sm.Fire(StateTrigger.TriggerCallAnswer);
        Assert.False(ok);
        Assert.Equal(TelephonyState.Init, sm.CurrentState);
    }

    [Fact]
    public async Task TestStateMachine_EventBus_AutoTransition()
    {
        await using var bus = new AsyncEventBus();
        var sm = new TelephonyStateMachine();
        sm.ConnectEventBus(bus);

        var tcs = new TaskCompletionSource<bool>();
        sm.OnEnter(TelephonyState.SimReady, (from, to, payload) =>
        {
            tcs.TrySetResult(true);
        });

        bus.Publish(EventTopics.ModemSim, "Detector", "READY");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(TelephonyState.SimReady, sm.CurrentState);
    }
}
