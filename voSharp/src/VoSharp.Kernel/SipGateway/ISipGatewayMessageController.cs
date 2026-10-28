using VoSharp.Telephony.Sms;

namespace VoSharp.Kernel.SipGateway;

public interface ISipGatewayMessageController
{
    event EventHandler<SmsReceivedEventArgs>? SmsReceived;
    Task<SmsSubmitResult> SendSmsAsync(string number, string text, string? slotId, CancellationToken ct);
}

/// <summary>Optional paid transport with a stable authenticated SIP transaction
/// identity. Implementations persist uncertainty before touching the modem.</summary>
public interface ISipGatewayIdempotentMessageController : ISipGatewayMessageController
{
    Task<SmsSubmitResult> SendSmsOnceAsync(string operationId, string number, string text, string? slotId, CancellationToken ct);
}
