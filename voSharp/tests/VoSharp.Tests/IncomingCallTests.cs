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
    [Theory]
    [InlineData("answer")]
    [InlineData("reject")]
    [InlineData("dtmf")]
    [InlineData("hangup")]
    public async Task MissingExplicitCallTargetNeverFallsBackToAnotherModule(string operation)
    {
        await using var kernel = new VoKernel();
        var slot = new ModemSlot("other-module", "OFFLINE_TEST", eventBus: kernel.EventBus);
        Assert.True(kernel.Pool.TryAddSlot(slot));
        await slot.Calls.HandleIncomingInviteAsync(CreateMockInvite(), slot.VoWifi, _ => Task.CompletedTask);
        var original = slot.Calls.ActiveCall;
        Assert.NotNull(original);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            switch (operation)
            {
                case "answer": await kernel.AnswerAsync("disconnected-module"); break;
                case "reject": await kernel.RejectAsync("disconnected-module"); break;
                case "dtmf": await kernel.SendDtmfAsync('1', "disconnected-module"); break;
                case "hangup": await kernel.HangupAsync("disconnected-module"); break;
            }
        });
        Assert.Equal(original.CallId, slot.Calls.ActiveCall?.CallId);
        Assert.True(slot.Calls.IsIncoming);
    }

    [Fact]
    public async Task ImplicitRejectTargetsPresentedCallNotNewlySelectedModule()
    {
        await using var kernel = new VoKernel();
        var calling = new ModemSlot("calling-module", "OFFLINE_A", eventBus: kernel.EventBus);
        var selected = new ModemSlot("selected-module", "OFFLINE_B", eventBus: kernel.EventBus);
        Assert.True(kernel.Pool.TryAddSlot(calling));
        Assert.True(kernel.Pool.TryAddSlot(selected));
        await calling.Calls.HandleIncomingInviteAsync(CreateMockInvite(), calling.VoWifi, _ => Task.CompletedTask);
        Assert.True(kernel.SelectSlot(selected.Id));
        Assert.Equal(calling.Id, kernel.ActiveCallSlotId);

        Assert.NotNull(await kernel.RejectAsync());
        Assert.Null(calling.Calls.ActiveCall);
        Assert.Null(selected.Calls.ActiveCall);
        Assert.Null(kernel.ActiveCall);
        Assert.Equal(selected.Id, kernel.Pool.ActiveSlotId);
    }

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
        Assert.All(states, e => Assert.Equal("cellular-1", e.SlotId));
        Assert.NotNull(kernel.ActiveCall);

        slot.HandleCellularCallUrc("NO CARRIER");
        Assert.Contains(states, e => e.NewState == CallState.Ended);
        Assert.Single(ended);
        Assert.Equal("cellular-1", ended[0].SlotId);
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

    [Fact]
    public async Task EndingOneSlotDoesNotClearAnotherSlotsTrackedCall()
    {
        await using var kernel = new VoKernel();
        var first = new ModemSlot("cellular-a", "COM_TEST_A", eventBus: kernel.EventBus);
        var second = new ModemSlot("cellular-b", "COM_TEST_B", eventBus: kernel.EventBus);
        Assert.True(kernel.Pool.TryAddSlot(first));
        Assert.True(kernel.Pool.TryAddSlot(second));
        var incoming = new List<IncomingCallEventArgs>();
        kernel.IncomingCall += (_, e) => incoming.Add(e);

        first.HandleCellularCallUrc("+CLIP: \"11111111\",145");
        second.HandleCellularCallUrc("+CLIP: \"22222222\",145");
        Assert.Equal(2, incoming.Count);
        Assert.Equal("cellular-a", incoming[0].SlotId);
        Assert.Equal("cellular-b", incoming[1].SlotId);

        first.HandleCellularCallUrc("NO CARRIER");
        Assert.NotNull(kernel.ActiveCall);
        Assert.Equal(incoming[1].CallId, kernel.ActiveCall.CallId);
        Assert.Equal("cellular-b", kernel.ActiveCallSlotId);
        Assert.Equal("22222222", kernel.ActiveCall.RemoteNumber);

        await kernel.Pool.RemoveSlotAsync("cellular-a");
        Assert.Equal(incoming[1].CallId, kernel.ActiveCall?.CallId);

        second.HandleCellularCallUrc("NO CARRIER");
        Assert.Null(kernel.ActiveCall);
    }

    [Fact]
    public async Task ImsEventsForwardTheOwningSlotId()
    {
        await using var kernel = new VoKernel();
        var slot = new ModemSlot("ims-slot", "COM_TEST", eventBus: kernel.EventBus);
        Assert.True(kernel.Pool.TryAddSlot(slot));
        var incoming = new List<IncomingCallEventArgs>();
        var states = new List<CallStateChangedEventArgs>();
        var ended = new List<CallEndedEventArgs>();
        kernel.IncomingCall += (_, e) => incoming.Add(e);
        kernel.CallStateChanged += (_, e) => states.Add(e);
        kernel.CallEnded += (_, e) => ended.Add(e);

        await slot.Calls.HandleIncomingInviteAsync(CreateMockInvite(), slot.VoWifi, _ => Task.CompletedTask);
        Assert.Equal("ims-slot", Assert.Single(incoming).SlotId);
        Assert.Equal("ims-slot", kernel.ActiveCallSlotId);
        Assert.All(states, e => Assert.Equal("ims-slot", e.SlotId));

        await slot.Calls.RejectAsync();
        Assert.Equal("ims-slot", Assert.Single(ended).SlotId);
        Assert.Null(kernel.ActiveCall);
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
    public async Task AnswerAsync_WaitsForMatchingAckBeforeConnectedState()
    {
        var bus = new AsyncEventBus();
        var callMgr = new ImsCallManager(bus);
        var voWifi = new VoWifiManager(bus);
        var mediaStarted = 0;
        callMgr.AudioStreamStateChanged += (_, _) => Interlocked.Increment(ref mediaStarted);

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

        Assert.Equal(CallState.Ringing, callMgr.State);
        Assert.NotNull(answered);
        Assert.Equal(CallState.Ringing, answered.State);
        Assert.Null(answered.ConnectedAt);
        Assert.Equal(0, mediaStarted);

        // Third reply should be 200 OK
        Assert.Equal(3, replies.Count);
        var okResp = replies[2];
        Assert.Equal(200, okResp.StatusCode);
        Assert.Equal("application/sdp", okResp.GetHeader("Content-Type"));
        Assert.Contains("m=audio", okResp.Body);

        var wrongAck = new SipMessage { IsRequest = true, Method = "ACK" };
        wrongAck.SetHeader("Call-ID", "different-call");
        callMgr.HandleIncomingAck(wrongAck);
        Assert.Equal(CallState.Ringing, callMgr.State);

        var ack = new SipMessage { IsRequest = true, Method = "ACK" };
        ack.SetHeader("From", invite.GetHeader("From")!);
        ack.SetHeader("To", okResp.GetHeader("To")!);
        ack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        ack.SetHeader("CSeq", "101 ACK");
        callMgr.HandleIncomingAck(ack);
        Assert.Equal(CallState.Active, callMgr.State);
        Assert.NotNull(callMgr.ActiveCall?.ConnectedAt);
        Assert.Equal(1, mediaStarted);

        // Teardown
        await callMgr.HangupAsync();
        Assert.Equal(CallState.Idle, callMgr.State);
    }

    [Fact]
    public async Task AnsweredInviteWithoutAck_CancelAndLocalHangupDoNotSendSecondInviteFinal()
    {
        using var callMgr = new ImsCallManager();
        using var voWifi = new VoWifiManager();
        var replies = new List<SipMessage>();
        Task Reply(SipMessage response)
        {
            replies.Add(response);
            return Task.CompletedTask;
        }

        var invite = CreateMockInvite();
        await callMgr.HandleIncomingInviteAsync(invite, voWifi, Reply);
        await callMgr.AnswerAsync();
        Assert.Equal(CallState.Ringing, callMgr.State);

        var cancel = new SipMessage { IsRequest = true, Method = "CANCEL", RequestUri = invite.RequestUri };
        foreach (var header in new[] { "Via", "From", "To" }) cancel.SetHeader(header, invite.GetHeader(header)!);
        cancel.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        cancel.SetHeader("CSeq", "101 CANCEL");
        await callMgr.HandleIncomingCancelAsync(cancel, Reply);
        Assert.Equal(CallState.Ringing, callMgr.State);
        Assert.Equal(200, replies[^1].StatusCode);
        Assert.Equal("101 CANCEL", replies[^1].GetHeader("CSeq"));

        await callMgr.HangupAsync();
        Assert.Equal(CallState.Idle, callMgr.State);
        Assert.DoesNotContain(replies, response => response.StatusCode is 487 or 603);
    }

    [Fact]
    public async Task MandatoryOriginatorQos_UsesReliable183AndWaitsForPrackAndUpdateBeforeRinging()
    {
        using var calls = new ImsCallManager();
        using var voWifi = new VoWifiManager();
        var replies = new List<SipMessage>();
        Task Reply(SipMessage response)
        {
            replies.Add(response);
            return Task.CompletedTask;
        }
        var invite = CreateMockInvite();
        invite.SetHeader("Supported", "timer,precondition,100rel");
        invite.Body += "a=curr:qos local none\r\na=curr:qos remote none\r\n" +
            "a=des:qos mandatory local sendrecv\r\na=des:qos optional remote sendrecv\r\n";

        await calls.HandleIncomingInviteAsync(invite, voWifi, Reply);
        Assert.Equal([100, 183], replies.Select(response => response.StatusCode));
        Assert.Equal(CallState.Incoming, calls.State);
        Assert.Equal("100rel", replies[^1].GetHeader("Require"));
        Assert.Contains("precondition", replies[^1].GetHeader("Supported"));
        Assert.Contains("100rel", replies[^1].GetHeader("Supported"));
        Assert.Contains("UPDATE", replies[^1].GetHeader("Allow"));
        Assert.Equal("1", replies[^1].GetHeader("RSeq"));
        Assert.Contains("RTP/AVP 102", replies[^1].Body);
        Assert.Contains("a=rtpmap:102 AMR/8000", replies[^1].Body);
        Assert.Contains("a=des:qos mandatory remote sendrecv", replies[^1].Body);
        Assert.Contains("a=des:qos optional local sendrecv", replies[^1].Body);
        Assert.Contains("a=conf:qos remote sendrecv", replies[^1].Body);

        var prack = new SipMessage { IsRequest = true, Method = "PRACK" };
        prack.SetHeader("From", invite.GetHeader("From")!);
        prack.SetHeader("To", replies[1].GetHeader("To")!);
        prack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        prack.SetHeader("CSeq", "102 PRACK");
        prack.SetHeader("RAck", "1 101 INVITE");
        await calls.HandleIncomingPrackAsync(prack, Reply);
        Assert.Equal(200, replies[^1].StatusCode);
        Assert.Equal(CallState.Incoming, calls.State);

        var update = new SipMessage { IsRequest = true, Method = "UPDATE" };
        update.SetHeader("From", invite.GetHeader("From")!);
        update.SetHeader("To", replies[1].GetHeader("To")!);
        update.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        update.SetHeader("CSeq", "103 UPDATE");
        update.Body = invite.Body.Replace("a=curr:qos local none", "a=curr:qos local sendrecv");
        await calls.HandleIncomingUpdateAsync(update, Reply);
        Assert.Equal(200, replies[^2].StatusCode);
        Assert.Contains("a=curr:qos remote sendrecv", replies[^2].Body);
        Assert.Equal(180, replies[^1].StatusCode);
        Assert.Equal(CallState.Ringing, calls.State);

        await calls.AnswerAsync();
        Assert.Equal(200, replies[^1].StatusCode);
        Assert.Contains("precondition", replies[^1].GetHeader("Supported"));
        Assert.Contains("UPDATE", replies[^1].GetHeader("Allow"));
        Assert.Empty(replies[^1].Body);
        var ack = new SipMessage { IsRequest = true, Method = "ACK" };
        ack.SetHeader("From", invite.GetHeader("From")!);
        ack.SetHeader("To", replies[^1].GetHeader("To")!);
        ack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        ack.SetHeader("CSeq", "101 ACK");
        calls.HandleIncomingAck(ack);
        Assert.Equal(CallState.Active, calls.State);
        await calls.HangupAsync(sendSignaling: false);
    }

    [Fact]
    public async Task Require100Rel_MakesRingingReliableAndAcceptsItsPrack()
    {
        using var calls = new ImsCallManager();
        using var voWifi = new VoWifiManager();
        var replies = new List<SipMessage>();
        Task Reply(SipMessage response)
        {
            replies.Add(response);
            return Task.CompletedTask;
        }
        var invite = CreateMockInvite();
        invite.SetHeader("Require", "100rel");
        await calls.HandleIncomingInviteAsync(invite, voWifi, Reply);

        Assert.Equal([100, 180], replies.Select(response => response.StatusCode));
        Assert.Equal("100rel", replies[^1].GetHeader("Require"));
        Assert.Equal("1", replies[^1].GetHeader("RSeq"));

        var prack = new SipMessage { IsRequest = true, Method = "PRACK" };
        prack.SetHeader("From", invite.GetHeader("From")!);
        prack.SetHeader("To", replies[^1].GetHeader("To")!);
        prack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        prack.SetHeader("CSeq", "102 PRACK");
        prack.SetHeader("RAck", "1 101 INVITE");
        await calls.HandleIncomingPrackAsync(prack, Reply);
        Assert.Equal(200, replies[^1].StatusCode);

        await calls.HandleIncomingPrackAsync(prack, Reply);
        Assert.Equal(481, replies[^1].StatusCode);
        await calls.HangupAsync(sendSignaling: false);
    }

    [Fact]
    public async Task Require100Rel_WithQos_UsesConsecutiveReliableRseqNumbers()
    {
        using var calls = new ImsCallManager();
        using var voWifi = new VoWifiManager();
        var replies = new List<SipMessage>();
        Task Reply(SipMessage response)
        {
            replies.Add(response);
            return Task.CompletedTask;
        }
        var invite = CreateMockInvite();
        invite.SetHeader("Supported", "precondition");
        invite.SetHeader("Require", "100rel");
        invite.Body += "a=curr:qos local none\r\na=curr:qos remote none\r\n" +
            "a=des:qos mandatory local sendrecv\r\na=des:qos optional remote sendrecv\r\n";
        await calls.HandleIncomingInviteAsync(invite, voWifi, Reply);
        Assert.Equal("1", replies[^1].GetHeader("RSeq"));

        var prack = new SipMessage { IsRequest = true, Method = "PRACK" };
        prack.SetHeader("From", invite.GetHeader("From")!);
        prack.SetHeader("To", replies[^1].GetHeader("To")!);
        prack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        prack.SetHeader("CSeq", "102 PRACK");
        prack.SetHeader("RAck", "1 101 INVITE");
        await calls.HandleIncomingPrackAsync(prack, Reply);
        Assert.Equal(200, replies[^1].StatusCode);

        var update = new SipMessage { IsRequest = true, Method = "UPDATE" };
        update.SetHeader("From", invite.GetHeader("From")!);
        update.SetHeader("To", prack.GetHeader("To")!);
        update.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        update.SetHeader("CSeq", "103 UPDATE");
        update.Body = invite.Body.Replace("a=curr:qos local none", "a=curr:qos local sendrecv");
        await calls.HandleIncomingUpdateAsync(update, Reply);
        Assert.Equal(180, replies[^1].StatusCode);
        Assert.Equal("100rel", replies[^1].GetHeader("Require"));
        Assert.Equal("2", replies[^1].GetHeader("RSeq"));

        var secondPrack = new SipMessage { IsRequest = true, Method = "PRACK" };
        secondPrack.SetHeader("From", invite.GetHeader("From")!);
        secondPrack.SetHeader("To", prack.GetHeader("To")!);
        secondPrack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        secondPrack.SetHeader("CSeq", "104 PRACK");
        secondPrack.SetHeader("RAck", "2 101 INVITE");
        await calls.HandleIncomingPrackAsync(secondPrack, Reply);
        Assert.Equal(200, replies[^1].StatusCode);
        await calls.HangupAsync(sendSignaling: false);
    }

    [Fact]
    public async Task MandatoryOriginatorQos_AcceptsReadyStatusInPrackSdpOffer()
    {
        using var calls = new ImsCallManager();
        using var voWifi = new VoWifiManager();
        var replies = new List<SipMessage>();
        Task Reply(SipMessage response)
        {
            replies.Add(response);
            return Task.CompletedTask;
        }
        var invite = CreateMockInvite();
        invite.SetHeader("Supported", "precondition,100rel");
        invite.Body += "a=curr:qos local none\r\na=curr:qos remote none\r\n" +
            "a=des:qos mandatory local sendrecv\r\na=des:qos optional remote sendrecv\r\n";
        await calls.HandleIncomingInviteAsync(invite, voWifi, Reply);

        var prack = new SipMessage { IsRequest = true, Method = "PRACK" };
        prack.SetHeader("From", invite.GetHeader("From")!);
        prack.SetHeader("To", replies[1].GetHeader("To")!);
        prack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        prack.SetHeader("CSeq", "102 PRACK");
        prack.SetHeader("RAck", "1 101 INVITE");
        prack.Body = invite.Body.Replace("a=curr:qos local none", "a=curr:qos local sendrecv");
        await calls.HandleIncomingPrackAsync(prack, Reply);

        Assert.Equal(200, replies[^2].StatusCode);
        Assert.Contains("a=curr:qos remote sendrecv", replies[^2].Body);
        Assert.Equal(180, replies[^1].StatusCode);
        Assert.Equal(CallState.Ringing, calls.State);
        await calls.HangupAsync(sendSignaling: false);
    }

    [Theory]
    [InlineData("a=des:qos mandatory remote sendrecv\r\n", "precondition,100rel")]
    [InlineData("a=curr:qos local none\r\na=des:qos mandatory local sendrecv\r\n", "precondition")]
    public async Task UnsupportedMandatoryQos_IsRejectedBeforeAlerting(string qos, string supported)
    {
        using var calls = new ImsCallManager();
        using var voWifi = new VoWifiManager();
        var replies = new List<SipMessage>();
        var invite = CreateMockInvite();
        invite.SetHeader("Supported", supported);
        invite.Body += qos;
        await calls.HandleIncomingInviteAsync(invite, voWifi, response =>
        {
            replies.Add(response);
            return Task.CompletedTask;
        });
        Assert.Equal([100, 580], replies.Select(response => response.StatusCode));
        Assert.Equal(CallState.Idle, calls.State);
        Assert.Null(calls.ActiveCall);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyQosCallCancelOrRejectReleasesPreparedRtp(bool reject)
    {
        using var calls = new ImsCallManager();
        using var wifi = new VoWifiManager();
        calls.ConfigureAudioRecording(false, null);
        var invite = CreateMockInvite();
        invite.SetHeader("Supported", "precondition,100rel");
        invite.Body += "a=curr:qos local none\r\na=curr:qos remote none\r\n" +
            "a=des:qos mandatory local sendrecv\r\na=des:qos optional remote sendrecv\r\n";
        await calls.HandleIncomingInviteAsync(invite, wifi, _ => Task.CompletedTask);
        Assert.Equal(CallState.Incoming, calls.State);
        Assert.NotNull(calls.GetMediaConfiguration());
        if (reject)
            await calls.RejectAsync();
        else
        {
            var cancel = new SipMessage { IsRequest = true, Method = "CANCEL", RequestUri = invite.RequestUri };
            foreach (var header in new[] { "Via", "From", "To" }) cancel.SetHeader(header, invite.GetHeader(header)!);
            cancel.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
            cancel.SetHeader("CSeq", "101 CANCEL");
            await calls.HandleIncomingCancelAsync(cancel, _ => Task.CompletedTask);
        }
        Assert.Equal(CallState.Idle, calls.State);
        Assert.Null(calls.ActiveCall);
        Assert.Null(calls.GetMediaConfiguration());
        Assert.Null(calls.GetMediaStats());
        // A new incoming call must not reuse the cancelled early RTP leg.
        invite.SetHeader("Call-ID", "next-offline-qos-call");
        await calls.HandleIncomingInviteAsync(invite, wifi, _ => Task.CompletedTask);
        Assert.Equal(CallState.Incoming, calls.State);
        Assert.NotNull(calls.GetMediaConfiguration());
        await calls.HangupAsync(sendSignaling: false);
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
        Assert.Equal(CallState.Ringing, kernel.Calls.State);
        var ack = new SipMessage { IsRequest = true, Method = "ACK" };
        ack.SetHeader("From", invite.GetHeader("From")!);
        ack.SetHeader("To", replies[^1].GetHeader("To")!);
        ack.SetHeader("Call-ID", invite.GetHeader("Call-ID")!);
        ack.SetHeader("CSeq", "101 ACK");
        kernel.Calls.HandleIncomingAck(ack);
        Assert.Equal(CallState.Active, kernel.Calls.State);

        // Execute "hangup" command
        var hangResult = await kernel.ExecuteCommandAsync("hangup");
        Assert.True(hangResult.Success);
        Assert.Equal(CallState.Idle, kernel.Calls.State);

        await kernel.DisposeAsync();
    }
}
