using VoSharp.Modem.At;
using VoSharp.Sim;

namespace VoSharp.Modem;

public class ModemConnectionChangedEventArgs : EventArgs
{
    public string PortName { get; }
    public bool IsOpen { get; }
    public string? SlotId { get; }

    public ModemConnectionChangedEventArgs(string portName, bool isOpen, string? slotId = null)
    {
        PortName = portName;
        IsOpen = isOpen;
        SlotId = slotId;
    }
}

public class SignalChangedEventArgs : EventArgs
{
    public SignalQuality Signal { get; }
    public string? SlotId { get; }

    public SignalChangedEventArgs(SignalQuality signal, string? slotId = null)
    {
        Signal = signal;
        SlotId = slotId;
    }
}

public class NetworkRegistrationChangedEventArgs : EventArgs
{
    public NetworkRegistration Registration { get; }
    public NetworkRegStatus OldStatus { get; }
    public NetworkRegStatus NewStatus { get; }
    public string? SlotId { get; }

    public NetworkRegistrationChangedEventArgs(NetworkRegistration registration, NetworkRegStatus oldStatus, NetworkRegStatus newStatus, string? slotId = null)
    {
        Registration = registration;
        OldStatus = oldStatus;
        NewStatus = newStatus;
        SlotId = slotId;
    }
}

public class SimStateChangedEventArgs : EventArgs
{
    public SimIdentity? Sim { get; }
    public int SimSlot { get; }
    public string State { get; }
    public string? SlotId { get; }

    public SimStateChangedEventArgs(SimIdentity? sim, int simSlot, string state, string? slotId = null)
    {
        Sim = sim;
        SimSlot = simSlot;
        State = state;
        SlotId = slotId;
    }
}

public class FlightModeChangedEventArgs : EventArgs
{
    public bool IsFlightMode { get; }
    public int Cfun { get; }
    public string? SlotId { get; }

    public FlightModeChangedEventArgs(bool isFlightMode, int cfun, string? slotId = null)
    {
        IsFlightMode = isFlightMode;
        Cfun = cfun;
        SlotId = slotId;
    }
}

public class ModemUrcEventArgs : EventArgs
{
    public string UrcLine { get; }
    public string? SlotId { get; }

    public ModemUrcEventArgs(string urcLine, string? slotId = null)
    {
        UrcLine = urcLine;
        SlotId = slotId;
    }
}
