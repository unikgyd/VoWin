using System.Reflection;
using System.Runtime.CompilerServices;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

public class ImsCancelTransactionIdentityTests
{
    public static IEnumerable<object[]> InvalidCancelCases()
    {
        string[] changes = ["branch", "branch-case", "sent-by", "sent-by-port", "transport", "uri", "call-id-case",
            "call-id-missing", "call-id-duplicate", "from", "from-tag", "to", "to-response-tag", "cseq-number",
            "cseq-method", "cseq-method-case", "cseq-extra", "cseq-negative", "cseq-overflow", "method", "method-case",
            "response-not-request", "via-missing", "branch-duplicate", "from-missing", "to-missing", "cseq-duplicate",
            "require", "proxy-require"];
        foreach (var change in changes)
            foreach (var answered in new[] { false, true })
                yield return [change, answered];
    }

    [Theory]
    [MemberData(nameof(InvalidCancelCases))]
    public async Task UnmatchedCancelCannotTerminateOrAcknowledgeAnotherInvite(string change, bool answered)
    {
        var invite = Invite();
        var calls = Pending(invite, answered);
        var original = calls.ActiveCall;
        var cancel = Cancel(invite);
        switch (change)
        {
            case "branch": cancel.SetHeader("Via", "SIP/2.0/UDP proxy.invalid:5060;branch=z9hG4bK-other"); break;
            case "branch-case": cancel.SetHeader("Via", "SIP/2.0/UDP proxy.invalid:5060;branch=z9hG4bK-ORIGINAL"); break;
            case "sent-by": cancel.SetHeader("Via", "SIP/2.0/UDP another.invalid:5060;branch=z9hG4bK-original"); break;
            case "sent-by-port": cancel.SetHeader("Via", "SIP/2.0/UDP proxy.invalid:5090;branch=z9hG4bK-original"); break;
            case "transport": cancel.SetHeader("Via", "SIP/2.0/TCP proxy.invalid:5060;branch=z9hG4bK-original"); break;
            case "uri": cancel.RequestUri = "sip:another@ims.invalid"; break;
            case "call-id-case": cancel.SetHeader("Call-ID", "offline-only-call"); break;
            case "call-id-missing": cancel.Headers.Remove("Call-ID"); break;
            case "call-id-duplicate": cancel.AddHeader("Call-ID", "Offline-Only-Call"); break;
            case "from": cancel.SetHeader("From", "<sip:another@ims.invalid>;tag=remote"); break;
            case "from-tag": cancel.SetHeader("From", "<sip:caller@ims.invalid>;tag=REMOTE"); break;
            case "to": cancel.SetHeader("To", "<sip:another@ims.invalid>"); break;
            case "to-response-tag": cancel.SetHeader("To", invite.GetHeader("To") + ";tag=local-response"); break;
            case "cseq-number": cancel.SetHeader("CSeq", "102 CANCEL"); break;
            case "cseq-method": cancel.SetHeader("CSeq", "101 INVITE"); break;
            case "cseq-method-case": cancel.SetHeader("CSeq", "101 cancel"); break;
            case "cseq-extra": cancel.SetHeader("CSeq", "101 CANCEL extra"); break;
            case "cseq-negative": cancel.SetHeader("CSeq", "-1 CANCEL"); break;
            case "cseq-overflow": cancel.SetHeader("CSeq", "2147483648 CANCEL"); break;
            case "method": cancel.Method = "BYE"; break;
            case "method-case": cancel.Method = "cancel"; break;
            case "response-not-request": cancel.IsRequest = false; break;
            case "via-missing": cancel.Headers.Remove("Via"); break;
            case "branch-duplicate": cancel.SetHeader("Via", invite.GetHeader("Via") + ";branch=z9hG4bK-original"); break;
            case "from-missing": cancel.Headers.Remove("From"); break;
            case "to-missing": cancel.Headers.Remove("To"); break;
            case "cseq-duplicate": cancel.AddHeader("CSeq", "101 CANCEL"); break;
            case "require": cancel.SetHeader("Require", "100rel"); break;
            case "proxy-require": cancel.SetHeader("Proxy-Require", "100rel"); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        var replies = new List<SipMessage>();
        await calls.HandleIncomingCancelAsync(cancel, response => { replies.Add(response); return Task.CompletedTask; });
        Assert.Equal(481, Assert.Single(replies).StatusCode);
        Assert.Equal(CallState.Ringing, calls.State);
        Assert.Same(original, calls.ActiveCall);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LegalCancelUsesOriginalToNotProvisionalResponseTag(bool answered, bool originalToTag)
    {
        var invite = Invite();
        if (originalToTag) invite.SetHeader("To", invite.GetHeader("To") + ";tag=original-invite");
        var calls = Pending(invite, answered);
        var original = calls.ActiveCall;
        var replies = new List<SipMessage>();
        await calls.HandleIncomingCancelAsync(Cancel(invite), response => { replies.Add(response); return Task.CompletedTask; });
        Assert.Equal(200, replies[0].StatusCode);
        if (answered)
        {
            Assert.Single(replies);
            Assert.Equal(CallState.Ringing, calls.State);
            Assert.Same(original, calls.ActiveCall);
        }
        else
        {
            Assert.Equal(2, replies.Count);
            Assert.Equal(487, replies[1].StatusCode);
            Assert.Equal(CallState.Idle, calls.State);
            Assert.Null(calls.ActiveCall);
        }
    }

    [Fact]
    public async Task LegalCancelViaAllowsHostCaseAndReceiverParameters()
    {
        var invite = Invite();
        var calls = Pending(invite, false);
        var cancel = Cancel(invite);
        cancel.SetHeader("Via", "SIP/2.0/UDP PROXY.INVALID:5060;branch=z9hG4bK-original;rport=5099;received=192.0.2.9");
        var replies = new List<SipMessage>();
        await calls.HandleIncomingCancelAsync(cancel, response => { replies.Add(response); return Task.CompletedTask; });
        Assert.Equal(new[] { 200, 487 }, replies.Select(response => response.StatusCode));
    }

    [Theory]
    [InlineData("-1 INVITE", "-1 CANCEL")]
    [InlineData("2147483648 INVITE", "2147483648 CANCEL")]
    [InlineData("101 INVITE extra", "101 CANCEL extra")]
    [InlineData("101 CANCEL", "101 CANCEL")]
    public async Task UnprovableOriginalCseqCannotAuthorizeCancel(string originalCseq, string cancelCseq)
    {
        var invite = Invite();
        invite.SetHeader("CSeq", originalCseq);
        var calls = Pending(invite, false);
        var cancel = Cancel(invite);
        cancel.SetHeader("CSeq", cancelCseq);
        var replies = new List<SipMessage>();
        await calls.HandleIncomingCancelAsync(cancel, response => { replies.Add(response); return Task.CompletedTask; });
        Assert.Equal(481, Assert.Single(replies).StatusCode);
        Assert.Equal(CallState.Ringing, calls.State);
    }

    [Theory]
    [InlineData("SIP/2.0/UDP proxy.invalid:5060;rport")]
    [InlineData("SIP/2.0/UDP [2001:db8::9]:5060;branch=z9hG4bK-ipv6;rport")]
    public async Task LegalCancelSupportsOriginalLegacyAndIpv6Via(string via)
    {
        var invite = Invite();
        invite.SetHeader("Via", via);
        var calls = Pending(invite, false);
        var replies = new List<SipMessage>();
        await calls.HandleIncomingCancelAsync(Cancel(invite), response => { replies.Add(response); return Task.CompletedTask; });
        Assert.Equal(new[] { 200, 487 }, replies.Select(response => response.StatusCode));
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(487, false)]
    [InlineData(200, true)]
    [InlineData(487, true)]
    public async Task AwaitedCancelReplyCannotTerminateReplacementCall(int blockedStatus, bool reusedWireIdentity)
    {
        var invite = Invite();
        var calls = Pending(invite, false);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new List<SipMessage>();
        var cancelTask = calls.HandleIncomingCancelAsync(Cancel(invite), response =>
        {
            replies.Add(response);
            if (response.StatusCode != blockedStatus) return Task.CompletedTask;
            blocked.SetResult();
            return release.Task;
        });
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Replace the lifetime while the old reply callback is genuinely suspended.
        // Even an identical wire identity must not authorize ownership of a new
        // accepted request object/session after the original lifetime has ended.
        var replacement = Invite();
        if (!reusedWireIdentity) replacement.SetHeader("Call-ID", "Offline-Replacement-Call");
        SetPending(calls, replacement, false, "replacement-response");
        var replacementCall = calls.ActiveCall;
        var routeClears = 0;
        var replacementHost = MediaOwner(() => routeClears++);
        Set(calls, "_currentHostIms", replacementHost);
        var ended = new List<CallEndedEventArgs>();
        calls.CallEnded += (_, args) => ended.Add(args);
        release.SetResult();
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(blockedStatus == 200 ? new[] { 200 } : new[] { 200, 487 },
            replies.Select(response => response.StatusCode));
        Assert.All(replies, response => Assert.Equal("Offline-Only-Call", response.GetHeader("Call-ID")));
        Assert.Same(replacementCall, calls.ActiveCall);
        Assert.Equal(CallState.Ringing, calls.State);
        Assert.Same(replacement, Get(calls, "_incomingInvite"));
        Assert.Same(replacementHost, Get(calls, "_currentHostIms"));
        Assert.Empty(ended);
        Assert.Equal(0, routeClears);
    }

    [Fact]
    public async Task AnswerDuringCancel200ReplyCannotBeUndoneByCancel()
    {
        var invite = Invite();
        var calls = Pending(invite, false);
        var original = calls.ActiveCall;
        var replies = new List<SipMessage>();
        await calls.HandleIncomingCancelAsync(Cancel(invite), response =>
        {
            replies.Add(response);
            if (response.StatusCode == 200) Set(calls, "_incomingAnswerAwaitingAck", true);
            return Task.CompletedTask;
        });

        Assert.Equal(200, Assert.Single(replies).StatusCode);
        Assert.Same(original, calls.ActiveCall);
        Assert.Equal(CallState.Ringing, calls.State);
        Assert.True((bool)Get(calls, "_incomingAnswerAwaitingAck")!);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(487, true)]
    [InlineData(200, false)]
    [InlineData(487, false)]
    public async Task CancelReplyRequiresBothRequestAndLocalSessionOwnership(int blockedStatus, bool reusedRequest)
    {
        var invite = Invite();
        var calls = Pending(invite, false);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new List<SipMessage>();
        var cancelTask = calls.HandleIncomingCancelAsync(Cancel(invite), response =>
        {
            replies.Add(response);
            if (response.StatusCode != blockedStatus) return Task.CompletedTask;
            blocked.SetResult();
            return release.Task;
        });
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = reusedRequest ? invite : Invite();
        // Isolate the two parts of the ownership check: reuse the original
        // object with a different local session, or reuse the local tag but
        // replace the accepted request object. Wire values are identical.
        SetPending(calls, replacement, false, reusedRequest ? "replacement-response" : "local-response");
        var replacementCall = calls.ActiveCall;
        release.SetResult();
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(blockedStatus == 200 ? new[] { 200 } : new[] { 200, 487 },
            replies.Select(response => response.StatusCode));
        Assert.Same(replacementCall, calls.ActiveCall);
        Assert.Same(replacement, Get(calls, "_incomingInvite"));
        Assert.Equal(CallState.Ringing, calls.State);
    }

    [Fact]
    public async Task CancelReleasesOriginalMediaBeforeEndObserversCanReplaceIt()
    {
        var invite = Invite();
        var calls = Pending(invite, false);
        var originalClears = 0;
        Set(calls, "_currentHostIms", MediaOwner(() => originalClears++));
        var replacementClears = 0;
        var replacementHost = MediaOwner(() => replacementClears++);
        var replacement = Invite();
        replacement.SetHeader("Call-ID", "Offline-Replacement-Call");
        var observedClears = -1;
        calls.CallStateChanged += (_, _) =>
        {
            observedClears = originalClears;
            SetPending(calls, replacement, false, "replacement-response");
            Set(calls, "_currentHostIms", replacementHost);
        };

        await calls.HandleIncomingCancelAsync(Cancel(invite), _ => Task.CompletedTask);

        Assert.Equal(1, originalClears);
        Assert.Equal(1, observedClears);
        Assert.Equal(0, replacementClears);
        Assert.Same(replacementHost, Get(calls, "_currentHostIms"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelEndNotificationCannotReleaseReplacementMedia(bool stateNotification)
    {
        var invite = Invite();
        var calls = Pending(invite, false);
        var replacement = Invite();
        replacement.SetHeader("Call-ID", "Offline-Replacement-Call");
        var routeClears = 0;
        var replacementHost = MediaOwner(() => routeClears++);
        void Replace()
        {
            SetPending(calls, replacement, false, "replacement-response");
            Set(calls, "_currentHostIms", replacementHost);
        }
        if (stateNotification) calls.CallStateChanged += (_, _) => Replace();
        else calls.CallEnded += (_, _) => Replace();

        await calls.HandleIncomingCancelAsync(Cancel(invite), _ => Task.CompletedTask);

        Assert.Equal(CallState.Ringing, calls.State);
        Assert.Equal("Offline-Replacement-Call", calls.ActiveCall?.CallId);
        Assert.Same(replacement, Get(calls, "_incomingInvite"));
        Assert.Same(replacementHost, Get(calls, "_currentHostIms"));
        Assert.Equal(0, routeClears);
    }

    private static SipMessage Invite()
    {
        var invite = new SipMessage { IsRequest = true, Method = "INVITE", RequestUri = "sip:ue@ims.invalid" };
        invite.SetHeader("Via", "SIP/2.0/UDP proxy.invalid:5060;branch=z9hG4bK-original;rport");
        invite.SetHeader("From", "<sip:caller@ims.invalid>;tag=remote");
        invite.SetHeader("To", "<sip:ue@ims.invalid>");
        invite.SetHeader("Call-ID", "Offline-Only-Call");
        invite.SetHeader("CSeq", "101 INVITE");
        return invite;
    }

    private static SipMessage Cancel(SipMessage invite)
    {
        var cancel = new SipMessage { IsRequest = true, Method = "CANCEL", RequestUri = invite.RequestUri };
        foreach (var header in new[] { "Via", "From", "To", "Call-ID" }) cancel.SetHeader(header, invite.GetHeader(header)!);
        cancel.SetHeader("CSeq", "101 CANCEL");
        return cancel;
    }

    private static ImsCallManager Pending(SipMessage invite, bool answered)
    {
        // Pure protocol fixture: skip the Windows audio constructor entirely.
        // No socket, microphone, playback device, modem, or carrier is opened.
        var calls = (ImsCallManager)RuntimeHelpers.GetUninitializedObject(typeof(ImsCallManager));
        Set(calls, "_sessionTimerGate", new object());
        SetPending(calls, invite, answered, "local-response");
        return calls;
    }

    private static void SetPending(ImsCallManager calls, SipMessage invite, bool answered, string localTag)
    {
        Set(calls, "_incomingInvite", invite);
        Set(calls, "_currentCallId", invite.GetHeader("Call-ID"));
        Set(calls, "_currentFromTag", localTag);
        Set(calls, "_dialogFrom", invite.GetHeader("To") + ";tag=" + localTag);
        Set(calls, "_incomingAnswerAwaitingAck", answered);
        Set(calls, "<State>k__BackingField", CallState.Ringing);
        Set(calls, "<ActiveCall>k__BackingField", new CallInfo(invite.GetHeader("Call-ID")!, "sip:caller@ims.invalid", CallState.Ringing,
            DateTime.UnixEpoch, null, null, null, null, IsOutgoing: false));
    }

    private static HostImsRegistrationClient MediaOwner(Action clear)
    {
        var host = (HostImsRegistrationClient)RuntimeHelpers.GetUninitializedObject(typeof(HostImsRegistrationClient));
        typeof(HostImsRegistrationClient).GetField("_mediaRouteClearer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(host, (Func<CancellationToken, Task>)(_ => { clear(); return Task.CompletedTask; }));
        return host;
    }

    private static void Set(ImsCallManager calls, string name, object? value) =>
        typeof(ImsCallManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(calls, value);

    private static object? Get(ImsCallManager calls, string name) =>
        typeof(ImsCallManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(calls);
}
