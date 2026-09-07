namespace VoSharp.Telephony.Sms;

public class SmsReceivedEventArgs : EventArgs
{
    public SmsMessage Message { get; }
    public string? SlotId { get; }

    public SmsReceivedEventArgs(SmsMessage message, string? slotId = null)
    {
        Message = message;
        SlotId = slotId;
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
