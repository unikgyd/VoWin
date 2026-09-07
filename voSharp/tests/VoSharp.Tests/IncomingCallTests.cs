using System.Net;
using System.Text;
using VoSharp.Common.Events;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Sip;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class IncomingCallTests
{
    [Fact]
    public async Task CellularRingAndClipAreForwardedThroughKernel()
    {
        await using var kernel = new VoKernel();
        var slot = new ModemSlot("cellular-1", "COM_TEST", eventBus: kernel.EventBus);
        Assert.True(kernel.Pool.TryAddSlot(slot));

        var incoming = new List<IncomingCallEventArgs>();
        var states = new List<CallStateChangedEventArgs>();
        var ended = new List<CallEndedEventArgs>();
        kernel.IncomingCall += (_, e) => incoming.Add(e);
        kernel.CallStateChanged += (_, e) => states.Add(e);
        kernel.CallEnded += (_, e) => ended.Add(e);

        slot.HandleCellularCallUrc("+CRING: VOICE");
        slot.HandleCellularCallUrc("+CLIP: \"13800138000\",145");

        Assert.NotEmpty(incoming);
        Assert.Equal("13800138000", incoming[^1].CallerNumber);
        Assert.False(incoming[^1].IsVoWifi);
        Assert.Equal("cellular-1", incoming[^1].SlotId);
        Assert.Contains(states, e => e.NewState == CallState.Incoming && !e.IsOutgoing);
        Assert.NotNull(kernel.ActiveCall);

        slot.HandleCellularCallUrc("NO CARRIER");
        Assert.Contains(states, e => e.NewState == CallState.Ended);
        Assert.Single(ended);
        Assert.Null(kernel.ActiveCall);
    }

    [Fact]
    public async Task CellularCallRaisedDuringAttachIsDeliveredAfterSlotIsAdded()
    {
        await using var kernel = new VoKernel();
        var slot = new ModemSlot("cellular-buffered", "COM_TEST", eventBus: kernel.EventBus);

        // Automatic discovery initializes the modem before the pool announces the slot.
        // A RING/CLIP received in that window must be retained for the kernel/UI.
        slot.HandleCellularCallUrc("RING");
        slot.HandleCellularCallUrc("+CLIP: \"+8613900139000\",145");

        var incoming = new List<IncomingCallEventArgs>();
        kernel.IncomingCall += (_, e) => incoming.Add(e);

        Assert.True(kernel.Pool.TryAddSlot(slot));

        Assert.NotEmpty(incoming);
        Assert.Equal("+8613900139000", incoming[^1].CallerNumber);
        Assert.Equal("cellular-buffered", incoming[^1].SlotId);
        Assert.False(incoming[^1].IsVoWifi);
        Assert.NotNull(kernel.ActiveCall);
    }

    private static SipMessage CreateMockInvite(string callerNumber = "+8613800000000", string localIp = "6.155.204.12")
    {
        var sdp = new StringBuilder();
        sdp.Append("v=0\r\n");
        sdp.Append("o=- 123456 123456 IN IP4 10.82.115.115\r\n");
        sdp.Append("s=IMS\r\n");
        sdp.Append("c=IN IP4 10.82.115.115\r\n");
        sdp.Append("t=0 0\r\n");
        sdp.Append("m=audio 40000 RTP/AVP 102 101\r\n");
        sdp.Append("a=rtpmap:102 AMR/8000/1\r\n");
        sdp.Append("a=fmtp:102 mode-change-capability=2; max-red=0\r\n");
        var sdpStr = sdp.ToString();

        var invite = new SipMessage
        {
            IsRequest = true,
            Method = "INVITE",
            RequestUri = $"sip:{localIp}:5060",
            SipVersion = "SIP/2.0",
            Body = sdpStr
        };
        invite.SetHeader("Via", "SIP/2.0/UDP 10.82.115.115:5060;branch=z9hG4bK-mock-branch;rport");
        invite.SetHeader("From", $"<sip:{callerNumber}@ims.example.com>;tag=caller-tag-123");
        invite.SetHeader("To", $"<sip:ue@{localIp}>");
        invite.SetHeader("Call-ID", "mock-call-id-999@10.82.115.115");
        invite.SetHeader("CSeq", "101 INVITE");
        invite.SetHeader("Contact", $"<sip:{callerNumber}@10.82.115.115:5060>");
        invite.SetHeader("Content-Type", "application/sdp");
        invite.SetHeader("Content-Length", sdpStr.Length.ToString());
        return invite;
    }

    [Fact]
    public async Task HandleIncomingInvite_Sends100And180_SetsRingingState()
    {
        var bus = new AsyncEventBus();
        var callMgr = new ImsCallManager(bus);
        var voWifi = new VoWifiManager(bus);

        var replies = new List<SipMessage>();
        Func<SipMessage, Task> replySender = msg =>
        {
            replies.Add(msg);
            return Task.CompletedTask;
        };

        var invite = CreateMockInvite();
        await callMgr.HandleIncomingInviteAsync(invite, voWifi, replySender);

        // Verify replies: 100 Trying, then 180 Ringing
        Assert.Equal(2, replies.Count);
        Assert.Equal(100, replies[0].StatusCode);
        Assert.Equal(180, replies[1].StatusCode);

        // Verify call state
        Assert.Equal(CallState.Ringing, callMgr.State);
        Assert.NotNull(callMgr.ActiveCall);
        Assert.False(callMgr.ActiveCall.IsOutgoing);
        Assert.Contains("13800000000", callMgr.ActiveCall.TargetNumber);
    }

    [Fact]
    public async Task AnswerAsync_Sends200OkWithSdp_SetsActiveState()
    {
        var bus = new AsyncEventBus();
        var callMgr = new ImsCallManager(bus);
        var voWifi = new VoWifiManager(bus);

        var replies = new List<SipMessage>();
        Func<SipMessage, Task> replySender = msg =>
        {
            replies.Add(msg);
            return Task.CompletedTask;
        };

        var invite = CreateMockInvite();
        await callMgr.HandleIncomingInviteAsync(invite, voWifi, replySender);

        // Answer the call
        var answered = await callMgr.AnswerAsync();

        Assert.Equal(CallState.Active, callMgr.State);
        Assert.NotNull(answered);
        Assert.Equal(CallState.Active, answered.State);
        Assert.NotNull(answered.ConnectedAt);

        // Third reply should be 200 OK
        Assert.Equal(3, replies.Count);
        var okResp = replies[2];
        Assert.Equal(200, okResp.StatusCode);
        Assert.Equal("application/sdp", okResp.GetHeader("Content-Type"));
        Assert.Contains("m=audio", okResp.Body);

        // Teardown
        await callMgr.HangupAsync();
        Assert.Equal(CallState.Idle, callMgr.State);
    }

    [Fact]
    public async Task RejectAsync_Sends603Decline_TerminatesCall()
    {
        var bus = new AsyncEventBus();
        var callMgr = new ImsCallManager(bus);
        var voWifi = new VoWifiManager(bus);

        var replies = new List<SipMessage>();
        Func<SipMessage, Task> replySender = msg =>
        {
            replies.Add(msg);
            return Task.CompletedTask;
        };

        var invite = CreateMockInvite();
        await callMgr.HandleIncomingInviteAsync(invite, voWifi, replySender);

        // Reject the call
        var rejected = await callMgr.RejectAsync();

        Assert.Equal(CallState.Idle, callMgr.State);
        Assert.Equal(3, replies.Count);
        Assert.Equal(603, replies[2].StatusCode);
        Assert.Equal("Decline", replies[2].ReasonPhrase);
    }

    [Fact]
    public async Task HandleIncomingCancel_Sends200OkAnd487_TerminatesCall()
    {
        var bus = new AsyncEventBus();
        var callMgr = new ImsCallManager(bus);
        var voWifi = new VoWifiManager(bus);

        var replies = new List<SipMessage>();
        Func<SipMessage, Task> replySender = msg =>
        {
            replies.Add(msg);
            return Task.CompletedTask;
        };

        var invite = CreateMockInvite();
        await callMgr.HandleIncomingInviteAsync(invite, voWifi, replySender);

        // Remote sends CANCEL
        var cancel = new SipMessage
        {
            IsRequest = true,
            Method = "CANCEL",
            RequestUri = invite.RequestUri,
            SipVersion = "SIP/2.0"
        };
        cancel.SetHeader("Via", invite.GetHeader("Via")!);
        cancel.SetHeader("From", invite.GetHeader("From")!);
        cancel.SetHeader("To", invite.GetHeader("To")!);
        cancel.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        cancel.SetHeader("CSeq", "101 CANCEL");

        await callMgr.HandleIncomingCancelAsync(cancel, replySender);

        Assert.Equal(CallState.Idle, callMgr.State);
        Assert.Equal(4, replies.Count);
        Assert.Equal(200, replies[2].StatusCode); // 200 OK to CANCEL
        Assert.Equal(487, replies[3].StatusCode); // 487 Request Terminated to INVITE
    }

    [Fact]
    public async Task VoKernel_AnswerAndRejectCommands_WorkProperly()
    {
        var bus = new AsyncEventBus();
        var kernel = new VoKernel(bus);

        // Simulate incoming call on active slot
        var invite = CreateMockInvite("+639171234567");
        var replies = new List<SipMessage>();
        Func<SipMessage, Task> replySender = msg =>
        {
            replies.Add(msg);
            return Task.CompletedTask;
        };

        await kernel.Calls.HandleIncomingInviteAsync(invite, kernel.VoWifi, replySender);
        Assert.Equal(CallState.Ringing, kernel.Calls.State);

        // Execute "answer" command
        var ansResult = await kernel.ExecuteCommandAsync("answer");
        Assert.True(ansResult.Success);
        Assert.Equal(CallState.Active, kernel.Calls.State);

        // Execute "hangup" command
        var hangResult = await kernel.ExecuteCommandAsync("hangup");
        Assert.True(hangResult.Success);
        Assert.Equal(CallState.Idle, kernel.Calls.State);

        await kernel.DisposeAsync();
    }
}
