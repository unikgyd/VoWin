namespace VoSharp.StateMachine;

public enum TelephonyState
{
    Init,
    SimReady,
    NetworkSearching,
    NetworkRegistered,
    DataConnecting,
    DataConnected,
    ImsRegistering,
    ImsRegistered,
    CallRinging,
    CallActive,
    CallEnded,
    Error
}

public enum StateTrigger
{
    TriggerSimReady,
    TriggerSimRemoved,
    TriggerNetSearch,
    TriggerNetAttach,
    TriggerNetLost,
    TriggerDataConnect,
    TriggerDataConnected,
    TriggerDataDisconnect,
    TriggerImsRegister,
    TriggerImsRegistered,
    TriggerImsDeregister,
    TriggerCallDial,
    TriggerCallRing,
    TriggerCallAnswer,
    TriggerCallHangup,
    TriggerError,
    TriggerReset
}

public record TransitionRecord(
    TelephonyState From,
    TelephonyState To,
    StateTrigger Trigger,
    DateTime Timestamp,
    string? Reason
);

public class TelephonyStateChangedEventArgs : EventArgs
{
    public TelephonyState From { get; }
    public TelephonyState To { get; }
    public StateTrigger Trigger { get; }
    public string? Reason { get; }
    public DateTime Timestamp { get; }

    public TelephonyStateChangedEventArgs(
        TelephonyState from,
        TelephonyState to,
        StateTrigger trigger,
        string? reason = null,
        DateTime? timestamp = null)
    {
        From = from;
        To = to;
        Trigger = trigger;
        Reason = reason;
        Timestamp = timestamp ?? DateTime.UtcNow;
    }
}
