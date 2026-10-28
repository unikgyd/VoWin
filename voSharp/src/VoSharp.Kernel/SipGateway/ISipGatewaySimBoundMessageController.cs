using VoSharp.Telephony.Sms;

namespace VoSharp.Kernel.SipGateway;

/// <summary>A reply is submitted only through the original verified SIM, never a default line or fallback transport.</summary>
public interface ISipGatewaySimBoundMessageController : ISipGatewayMessageController
{
    string? GetSmsSimBinding(string slotId);
    Task<SmsSubmitResult> SendSmsForSimAsync(string number, string text, string slotId, string expectedBinding, CancellationToken ct);
}
