namespace VoSharp.Telephony.Sms;

public class SmsReceivedEventArgs : EventArgs
{
    public SmsMessage Message { get; }
    public string? SlotId { get; }
    /// <summary>Original source-session SIM identity; null means unproven. Never infer it later from the active SIM.</summary>
    public string? SimBinding { get; }

    public SmsReceivedEventArgs(SmsMessage message, string? slotId = null, string? simBinding = null)
    {
        Message = message;
        SlotId = slotId;
        SimBinding = SmsSubscriptionIdentity.IsValid(simBinding) ? simBinding : null;
    }
}

public class SmsSentEventArgs : EventArgs
{
    public SmsSubmitResult Result { get; }
    public string? SlotId { get; }

    public SmsSentEventArgs(SmsSubmitResult result, string? slotId = null)
    {
        Result = result;
        SlotId = slotId;
    }
}

public class SmsStatusReportEventArgs : EventArgs
{
    public SmsStatusReport Report { get; }
    public string? SlotId { get; }

    public SmsStatusReportEventArgs(SmsStatusReport report, string? slotId = null)
    {
        Report = report;
        SlotId = slotId;
    }
}
