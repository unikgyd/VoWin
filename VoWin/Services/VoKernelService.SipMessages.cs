using VoSharp.Kernel.SipGateway;
using VoWin.Models;

namespace VoWin.Services;

public partial class VoKernelService
{
    internal async Task RecordSipSmsSubmissionAsync(SipSmsSubmission submission)
    {
        try
        {
            var result = submission.Result;
            SmsMessageModel? model = null;
            await RunOnUiAsync(() =>
            {
                var conversation = GetOrCreateConversation(result.Recipient);
                model = new()
                {
                    SlotId = submission.SlotId, SenderOrRecipient = result.Recipient, Text = result.Text,
                    Timestamp = result.SubmittedAt, IsOutgoing = true,
                    DeliveryState = result.AllPartsAccepted ? SmsDeliveryState.Sent :
                        result.PartsAccepted > 0 ? SmsDeliveryState.PartiallySent : SmsDeliveryState.Failed,
                    DeliveryStatus = $"SIP {submission.Extension} · {result.SubmissionStatus}",
                    MessageReference = result.ConcatReference
                };
                conversation.Messages.Add(model);
                conversation.UpdateLastMessage(); MoveConversationToTop(conversation);
            });
            await Preferences.SaveSmsMessageAsync(model!);
            AddLog("INFO", "SipGateway", $"SIP SMS submission recorded; slot={submission.SlotId}; accepted-parts={result.PartsAccepted}/{result.PartsTotal}; identities and content omitted.");
        }
        catch (Exception ex)
        {
            AddLog("WARN", "SipGateway", $"SIP SMS history persistence failed ({ex.GetType().Name}); do not automatically resend SMS.");
        }
    }
}
