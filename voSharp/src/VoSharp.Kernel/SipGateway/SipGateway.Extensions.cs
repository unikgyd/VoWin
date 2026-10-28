using System.Net;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;

namespace VoSharp.Kernel.SipGateway;

public sealed partial class SipGateway
{
    private async Task HandleExtensionInviteAsync(SipMessage invite, IPEndPoint remote, string caller, string target, CancellationToken ct)
    {
        // Resolve configured extensions before carrier routes. An offline local
        // extension must NEVER fall through to a SIM or consume airtime.
        var bindings = GetActiveRegistrations().Where(binding => binding.Extension.Equals(target, StringComparison.OrdinalIgnoreCase) &&
            !binding.RemoteEndPoint.Equals(remote)).ToArray();
        if (bindings.Length == 0)
        {
            await SendResponseAsync(invite, remote, 480, "Extension Offline", ct).ConfigureAwait(false);
            return;
        }
        if (!SipGatewaySdp.TryParseAudioOffer(invite.Body, remote.Address, _options.TrustClientSdpAddress, out var offer)) return;
        var media = new RtpSession(null, localAddress: _options.BindAddress);
        media.SetRemoteEndpoint(offer!.Address, offer.Port, offer.PayloadType, offer.Codec);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var dialToken = cancellation.Token;
        var dialog = new Dialog
        {
            LocalCallId = invite.GetHeader("Call-ID") ?? Guid.NewGuid().ToString("N"),
            Extension = caller, PhoneEndPoint = remote, Media = media, Cancellation = cancellation,
            OriginalInvite = invite, RemoteTarget = ExtractContactTarget(invite.GetHeader("Contact")) ?? invite.RequestUri,
            From = invite.GetHeader("From"), To = EnsureTag(invite.GetHeader("To") ?? $"<sip:{target}@{_options.IdentityHost}>")
        };
        if (!_dialogs.TryAdd(dialog.LocalCallId, dialog))
        {
            dialog.Dispose();
            await SendResponseAsync(invite, remote, 482, "Loop Detected", ct).ConfigureAwait(false);
            return;
        }
        Dialog? winner = null;
        using var race = CancellationTokenSource.CreateLinkedTokenSource(dialToken);
        var branches = bindings.Select(binding => InviteBindingAsync(binding,
            new IncomingCallEventArgs(dialog.LocalCallId, caller), race.Token)).ToList();
        try
        {
            await SendResponseAsync(invite, remote, 100, "Trying", ct).ConfigureAwait(false);
            await SendResponseAsync(invite, remote, 180, "Ringing", ct,
                response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
            while (branches.Count > 0 && winner == null)
            {
                var completed = await Task.WhenAny(branches).ConfigureAwait(false);
                branches.Remove(completed);
                winner = await completed.ConfigureAwait(false);
            }
            race.Cancel();
            _ = CleanupLosingBranchesAsync(branches);
            dialToken.ThrowIfCancellationRequested();
            if (winner == null) throw new InvalidOperationException("No extension answered.");
            lock (dialog)
            {
                ObjectDisposedException.ThrowIf(dialog.IsDisposed, dialog);
                dialog.PairedDialogId = winner.LocalCallId;
                winner.PairedDialogId = dialog.LocalCallId;
                _dialogs[winner.LocalCallId] = winner;
                dialog.Media.OnAudioDecoded = winner.Media.SendAudioPcm;
                winner.Media.OnAudioDecoded = dialog.Media.SendAudioPcm;
            }
            await SendAckAsync(winner, CancellationToken.None).ConfigureAwait(false);
            lock (dialog.InviteFinalGate)
            {
                dialToken.ThrowIfCancellationRequested();
                if (Interlocked.CompareExchange(ref dialog.InviteFinalSent, 1, 0) != 0)
                    throw new OperationCanceledException("Extension caller already ended.");
            }
            await SendResponseAsync(invite, remote, 200, "OK", ct, response =>
            {
                response.SetHeader("To", dialog.To!);
                response.SetHeader("Contact", $"<sip:gateway@{FormatHost(_options.BindAddress)}:{_options.SipPort}>");
                response.SetHeader("Content-Type", "application/sdp");
                response.Body = SipGatewaySdp.BuildOffer(_options.BindAddress, media.LocalPort, media.LocalRtcpPort, offer.PayloadType);
            }).ConfigureAwait(false);
            if (_serverResponses.TryGetValue(ServerTransactionKey(invite, remote), out var final))
                _ = Task.Run(() => RetransmitInviteFinalUntilAckAsync(dialog, final.Response));
            RaiseStatus();
        }
        catch (Exception ex)
        {
            race.Cancel();
            _ = CleanupLosingBranchesAsync(branches);
            if (winner != null)
            {
                try
                {
                    await SendAckAsync(winner, CancellationToken.None).ConfigureAwait(false);
                    await SendByeAsync(winner, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                RemoveDialog(winner.LocalCallId);
                winner.Dispose();
            }
            try
            {
                if (Interlocked.CompareExchange(ref dialog.InviteFinalSent, 2, 0) == 0)
                    await SendResponseAsync(invite, remote, ex is OperationCanceledException ? 487 : 480,
                        ex is OperationCanceledException ? "Request Terminated" : "Extension Unavailable", CancellationToken.None,
                        response => response.SetHeader("To", dialog.To!)).ConfigureAwait(false);
            }
            finally { RemoveDialog(dialog.LocalCallId); }
        }
    }
}
