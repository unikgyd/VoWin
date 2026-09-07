namespace VoSharp.Telephony.Calls;

public class CallStateChangedEventArgs : EventArgs
{
    public string CallId { get; }
    public string TargetNumber { get; }
    public CallState OldState { get; }
    public CallState NewState { get; }
    public string? Codec { get; }
    public string? WavRecordingPath { get; }
    public bool IsOutgoing { get; }
    public string? SlotId { get; }

    public CallStateChangedEventArgs(
        string callId,
        string targetNumber,
        CallState oldState,
        CallState newState,
        string? codec = null,
        string? wavRecordingPath = null,
        bool isOutgoing = true,
        string? slotId = null)
    {
        CallId = callId;
        TargetNumber = targetNumber;
        OldState = oldState;
        NewState = newState;
        Codec = codec;
        WavRecordingPath = wavRecordingPath;
        IsOutgoing = isOutgoing;
        SlotId = slotId;
    }
}

public class IncomingCallEventArgs : EventArgs
{
    public string CallId { get; }
    public string CallerNumber { get; }
    public string? DisplayName { get; }
    public bool IsVoWifi { get; }
    public DateTime Timestamp { get; }
    public string? SlotId { get; }

    public IncomingCallEventArgs(
        string callId,
        string callerNumber,
        string? displayName = null,
        bool isVoWifi = true,
        DateTime? timestamp = null,
        string? slotId = null)
    {
        CallId = callId;
        CallerNumber = callerNumber;
        DisplayName = displayName ?? callerNumber;
        IsVoWifi = isVoWifi;
        Timestamp = timestamp ?? DateTime.UtcNow;
        SlotId = slotId;
    }
}

public class CallConnectedEventArgs : EventArgs
{
    public string CallId { get; }
    public string TargetNumber { get; }
    public string? Codec { get; }
    public DateTime ConnectedAt { get; }
    public string? SlotId { get; }

    public CallConnectedEventArgs(
        string callId,
        string targetNumber,
        string? codec,
        DateTime connectedAt,
        string? slotId = null)
    {
        CallId = callId;
        TargetNumber = targetNumber;
        Codec = codec;
        ConnectedAt = connectedAt;
        SlotId = slotId;
    }
}

public class CallEndedEventArgs : EventArgs
{
    public string CallId { get; }
    public string TargetNumber { get; }
    public TimeSpan? Duration { get; }
    public string? Reason { get; }
    public string? WavRecordingPath { get; }
    public DateTime EndedAt { get; }
    public string? SlotId { get; }

    public CallEndedEventArgs(
        string callId,
        string targetNumber,
        TimeSpan? duration,
        string? reason,
        string? wavRecordingPath,
        DateTime endedAt,
        string? slotId = null)
    {
        CallId = callId;
        TargetNumber = targetNumber;
        Duration = duration;
        Reason = reason;
        WavRecordingPath = wavRecordingPath;
        EndedAt = endedAt;
        SlotId = slotId;
    }
}

public class DtmfReceivedEventArgs : EventArgs
{
    public char Digit { get; }
    public int DurationMs { get; }
    public string? SlotId { get; }

    public DtmfReceivedEventArgs(char digit, int durationMs = 160, string? slotId = null)
    {
        Digit = digit;
        DurationMs = durationMs;
        SlotId = slotId;
    }
}

public class AudioStreamStateChangedEventArgs : EventArgs
{
    public bool IsTransmitting { get; }
    public bool IsReceiving { get; }
    public int SampleRate { get; }
    public string? Codec { get; }
    public string? SlotId { get; }

    public AudioStreamStateChangedEventArgs(bool isTransmitting, bool isReceiving, int sampleRate = 8000, string? codec = null, string? slotId = null)
    {
        IsTransmitting = isTransmitting;
        IsReceiving = isReceiving;
        SampleRate = sampleRate;
        Codec = codec;
        SlotId = slotId;
    }
}
