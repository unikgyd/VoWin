using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel;

/// <summary>
/// Immutable point-in-time state snapshot of the VoSharp telephony engine.
/// Designed for MVVM / GUI data binding and IPC status serialization.
/// </summary>
public sealed record KernelSnapshot(
    TelephonyState State,
    string StateDescription,
    string? ModemPort,
    bool IsModemConnected,
    SimIdentity? Sim,
    SignalQuality? Signal,
    NetworkRegistration? Network,
    VoWifiState VoWifiState,
    string? VoWifiIp,
    CallInfo? ActiveCall,
    int InboxCount,
    int OutboxCount,
    string? ActiveSlotId,
    int TotalSlots,
    DateTime Timestamp
);
