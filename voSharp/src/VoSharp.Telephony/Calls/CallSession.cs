namespace VoSharp.Telephony.Calls;

public enum CallState
{
    Idle,
    Dialing,
    Incoming,
    Ringing,
    Active,
    Held,
    Ended
}

public class CallSession
{
    public string CallId { get; } = Guid.NewGuid().ToString("N");
    public string RemoteNumber { get; set; }
    public bool IsOutgoing { get; set; }
    public CallState State { get; set; } = CallState.Idle;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? ConnectedTime { get; set; }
    public DateTime? EndTime { get; set; }

    public CallSession(string remoteNumber, bool isOutgoing)
    {
        RemoteNumber = remoteNumber;
        IsOutgoing = isOutgoing;
        State = isOutgoing ? CallState.Dialing : CallState.Incoming;
    }

    public void Connect()
    {
        State = CallState.Active;
        ConnectedTime = DateTime.UtcNow;
    }

    public void End()
    {
        State = CallState.Ended;
        EndTime = DateTime.UtcNow;
    }
}
