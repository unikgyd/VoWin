using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoSharp.Common.Events;
using VoSharp.Common.Utils;
using VoSharp.Euicc;
using VoSharp.Euicc.Asn1;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Transport;
using VoSharp.Kernel;
using VoSharp.Kernel.Events;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests.Events;

public class MockEuiccTransport : IEuiccTransport
{
    public string BackendName => "Mock";
    public Task<int> OpenLogicalChannelAsync(string aid, CancellationToken ct = default) => Task.FromResult(1);
    public Task CloseLogicalChannelAsync(int channelNumber, CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]> TransmitLogicalChannelAsync(int channelNumber, byte[] apdu, CancellationToken ct = default)
    {
        var respTlv = new Tlv(0xBF2D);
        var p1 = new Tlv(0xE3);
        p1.Children.Add(new Tlv(0x5A, Tlv.IccidToBcd("89860123456789012345")));
        p1.Children.Add(new Tlv(0x4F, HexUtils.FromHexString("A0000005591010FFFFFFFF8900001100")));
        p1.Children.Add(new Tlv(0x9F70, new byte[] { 0x01 }));
        p1.Children.Add(new Tlv(0x90, "Personal eSIM"));
        respTlv.Children.Add(p1);

        var payload = respTlv.Encode();
        var fullResp = new byte[payload.Length + 2];
        payload.CopyTo(fullResp, 0);
        fullResp[^2] = 0x90;
        fullResp[^1] = 0x00;
        return Task.FromResult(fullResp);
    }
}

public class TelephonyEventsTests
{
    [Fact]
    public void StateMachine_StateChangedEvent_FiresOnTransitions()
    {
        var sm = new TelephonyStateMachine();
        var raisedEvents = new List<TelephonyStateChangedEventArgs>();

        sm.StateChanged += (s, e) => raisedEvents.Add(e);

        sm.Fire(StateTrigger.TriggerSimReady, "SIM Ready");
        sm.Fire(StateTrigger.TriggerNetAttach, "Attached");

        Assert.Equal(2, raisedEvents.Count);
        Assert.Equal(TelephonyState.Init, raisedEvents[0].From);
        Assert.Equal(TelephonyState.SimReady, raisedEvents[0].To);
        Assert.Equal(StateTrigger.TriggerSimReady, raisedEvents[0].Trigger);

        Assert.Equal(TelephonyState.SimReady, raisedEvents[1].From);
        Assert.Equal(TelephonyState.NetworkRegistered, raisedEvents[1].To);
        Assert.Equal(StateTrigger.TriggerNetAttach, raisedEvents[1].Trigger);
    }

    [Fact]
    public async Task ImsCallManager_Events_FireCorrectly()
    {
        var bus = new AsyncEventBus();
        var calls = new ImsCallManager(bus);

        var stateChanges = new List<CallStateChangedEventArgs>();
        var incomingCalls = new List<IncomingCallEventArgs>();
        var dtmfs = new List<DtmfReceivedEventArgs>();

        calls.CallStateChanged += (s, e) => stateChanges.Add(e);
        calls.IncomingCall += (s, e) => incomingCalls.Add(e);
        calls.DtmfReceived += (s, e) => dtmfs.Add(e);

        // Test incoming INVITE handling
        var inviteMsg = new VoSharp.Sip.SipMessage
        {
            IsRequest = true,
            Method = "INVITE",
            RequestUri = "sip:10086@ims.mnc000.mcc460.3gppnetwork.org",
            SipVersion = "SIP/2.0"
        };
        inviteMsg.SetHeader("From", "<sip:13800138000@ims.mnc000.mcc460.3gppnetwork.org>;tag=123");
        inviteMsg.SetHeader("To", "<sip:10086@ims.mnc000.mcc460.3gppnetwork.org>");
        inviteMsg.SetHeader("Call-ID", "call-abc-123");
        var voWifi = new VoWifiManager(bus);
        await calls.HandleIncomingInviteAsync(inviteMsg, voWifi, _ => Task.CompletedTask);

        Assert.Single(incomingCalls);
        Assert.Equal("13800138000", incomingCalls[0].CallerNumber);
        Assert.Equal("call-abc-123", incomingCalls[0].CallId);
        Assert.NotNull(calls.ActiveCall);
    }

    [Fact]
    public void SmsService_Events_FireOnNotifications()
    {
        var bus = new AsyncEventBus();
        var modem = new ModemDriver("COM99", 115200, bus);
        var smsService = new SmsService(modem, bus);

        var receivedSms = new List<SmsReceivedEventArgs>();
        var statusReports = new List<SmsStatusReportEventArgs>();

        smsService.SmsReceived += (s, e) => receivedSms.Add(e);
        smsService.StatusReportReceived += (s, e) => statusReports.Add(e);

        var testMsg = new SmsMessage(1, SmsStatus.Unread, "10086", "Hello VoSharp", DateTime.UtcNow);
        smsService.NotifySmsReceived(testMsg);

        Assert.Single(receivedSms);
        Assert.Equal("10086", receivedSms[0].Message.SenderOrRecipient);
        Assert.Equal("Hello VoSharp", receivedSms[0].Message.Text);

        var testReport = new SmsStatusReport(42, "10086", 0, "delivered", null, null, DateTime.UtcNow);
        smsService.NotifyStatusReportReceived(testReport);

        Assert.Single(statusReports);
        Assert.Equal(42, statusReports[0].Report.MessageReference);
        Assert.Equal("delivered", statusReports[0].Report.DeliveryStatus);
    }

    [Fact]
    public async Task EuiccManager_Events_FireOnOperations()
    {
        var bus = new AsyncEventBus();
        var transport = new MockEuiccTransport();
        var euicc = new EuiccManager(transport, bus);

        var profileUpdates = new List<EuiccProfilesChangedEventArgs>();
        var ops = new List<EuiccOperationEventArgs>();

        euicc.ProfilesUpdated += (s, e) => profileUpdates.Add(e);
        euicc.OperationCompleted += (s, e) => ops.Add(e);

        await euicc.ListProfilesAsync();

        Assert.Single(profileUpdates);

        await euicc.RenameProfileAsync("898600", "MyProfile");
        Assert.Single(ops);
        Assert.Equal("Rename", ops[0].Action);
        Assert.Equal("898600", ops[0].TargetIccidOrAid);
        Assert.Equal("MyProfile", ops[0].Nickname);
        Assert.True(ops[0].Success);
    }

    [Fact]
    public async Task ModemPool_Events_FireOnSlotLifecycle()
    {
        var bus = new AsyncEventBus();
        var pool = new ModemPool(bus);

        var slotsAdded = new List<ModemSlot>();
        var activeChanges = new List<ActiveSlotChangedEventArgs>();
        var stateChanges = new List<SlotStateChangedEventArgs>();

        pool.SlotAdded += (s, slot) => slotsAdded.Add(slot);
        pool.ActiveSlotChanged += (s, e) => activeChanges.Add(e);
        pool.SlotStateChanged += (s, e) => stateChanges.Add(e);

        // Add a slot (which fails ping gracefully to Error state)
        var slot = await pool.AddOrUpdateSlotAsync("COM98", slotId: "slot1");

        Assert.Single(slotsAdded);
        Assert.Equal("slot1", slotsAdded[0].Id);
        Assert.NotEmpty(activeChanges);
        Assert.Equal("slot1", activeChanges[0].ActiveSlotId);

        // Select slot
        pool.SelectSlot("slot1");
        Assert.True(slot.IsActive);

        // Remove slot
        await pool.RemoveSlotAsync("slot1");
        Assert.Empty(pool.Slots);
    }

    [Fact]
    public async Task VoKernel_IVoKernel_FacadeEvents_ForwardCorrectly()
    {
        var bus = new AsyncEventBus();
        var sm = new TelephonyStateMachine();
        IVoKernel kernel = new VoKernel(bus, sm);

        var stateChanges = new List<TelephonyStateChangedEventArgs>();
        var sysErrors = new List<SystemErrorEventArgs>();

        kernel.TelephonyStateChanged += (s, e) => stateChanges.Add(e);
        kernel.SystemErrorOccurred += (s, e) => sysErrors.Add(e);

        // 1. State machine event forwarding
        sm.Fire(StateTrigger.TriggerSimReady);
        Assert.Single(stateChanges);
        Assert.Equal(TelephonyState.SimReady, stateChanges[0].To);

        // 2. Incoming call event forwarding
        bus.Publish(EventTopics.CallIncoming, "VoWifi", "+123456789");
        await Task.Delay(100);
        Assert.NotNull(kernel.ActiveCall);
        Assert.Equal("+123456789", kernel.ActiveCall.RemoteNumber);

        // 3. System Error forwarding
        bus.Publish(EventTopics.SystemError, "TestModule", "Something went wrong");
        await Task.Delay(100);
        Assert.Single(sysErrors);
        Assert.Equal("TestModule", sysErrors[0].Source);
        Assert.Equal("Something went wrong", sysErrors[0].ErrorMessage);

        await kernel.DisposeAsync();
    }
}
