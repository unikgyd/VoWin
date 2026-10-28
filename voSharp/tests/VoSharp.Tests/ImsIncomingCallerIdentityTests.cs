using VoSharp.Sip;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class ImsIncomingCallerIdentityTests
{
    [Theory]
    [InlineData("<sip:anonymous@anonymous.invalid>", "<tel:+8613800000123>", null, "+8613800000123", "P-Asserted-Identity")]
    [InlineData("<sip:1001@ims.invalid>", "<sips:+491234567890@ims.invalid;user=phone>", "none", "+491234567890", "P-Asserted-Identity")]
    [InlineData("<sip:1001@ims.invalid>", "<sip:opaque@ims.invalid>, <tel:+639171234567>", null, "+639171234567", "P-Asserted-Identity")]
    [InlineData("<sip:1001@ims.invalid>", "<sip:9999@ims.invalid>, <tel:+639171234567>", null, "+639171234567", "P-Asserted-Identity")]
    [InlineData("<sip:1001@ims.invalid>", "<sip:%2B66812345678@ims.invalid>", null, "+66812345678", "P-Asserted-Identity")]
    [InlineData("<sip:1001@ims.invalid>", "<tel:+49-(123)-456.789>", null, "+49123456789", "P-Asserted-Identity")]
    [InlineData("<sip:1001@ims.invalid>", "<sip:123abc@ims.invalid>", null, "1001", "From")]
    [InlineData("<tel:1002;phone-context=example.invalid>", null, null, "1002", "From")]
    [InlineData("\"From name\" <SIP:+8613800000123@ims.invalid>", null, null, "+8613800000123", "From")]
    [InlineData("\"tel:+8613800000123\" <sip:anonymous@anonymous.invalid>", null, null, "Anonymous", "unavailable")]
    [InlineData("tel:+8613800000123 <sip:anonymous@anonymous.invalid>", null, null, "Anonymous", "unavailable")]
    [InlineData("<sip:opaque@ims.invalid>", "\"tel:+8613800000123\" <sip:opaque@ims.invalid>", null, "Unknown", "unavailable")]
    [InlineData("<sip:anonymous@anonymous.invalid>", "<sip:123abc@ims.invalid>", null, "Anonymous", "unavailable")]
    [InlineData("<sip:1001@ims.invalid>", "<tel:+8613800000123>", "id", "Anonymous", "withheld")]
    [InlineData("<sip:1001@ims.invalid>", "<tel:+8613800000123>", "header; ID; critical", "Anonymous", "withheld")]
    [InlineData("<sip:1001@ims.invalid>", "<tel:+8613800000123>", "user", "Anonymous", "withheld")]
    [InlineData("<sip:1001@ims.invalid>", "<tel:+8613800000123>", "unknown-extension; critical", "Anonymous", "withheld")]
    [InlineData("<sip:1001@ims.invalid>", "<tel:+8613800000123>", "header;session", "+8613800000123", "P-Asserted-Identity")]
    public void CarrierPresentationReadsAssertedTelephoneIdentityAndHonorsPrivacy(
        string from, string? asserted, string? privacy, string number, string source)
    {
        var invite = new SipMessage();
        invite.SetHeader("From", from);
        if (asserted != null) invite.SetHeader("P-Asserted-Identity", asserted);
        if (privacy != null) invite.SetHeader("Privacy", privacy);
        var identity = ImsIncomingCallerIdentity.Read(invite, trustedCarrier: true);
        Assert.Equal(number, identity.Number);
        Assert.Equal(source, identity.Source);
        Assert.Equal(source == "withheld", identity.IsPrivate);
        Assert.DoesNotContain("13800000123", identity.ToString());
        Assert.DoesNotContain("639171234567", identity.ToString());
    }

    [Fact]
    public void UntrustedPeerCannotAssertCarrierIdentityOrSupplyPreferredIdentity()
    {
        var invite = new SipMessage();
        invite.SetHeader("From", "<sip:1001@example.invalid>");
        invite.SetHeader("P-Asserted-Identity", "<tel:+8613800000123>");
        invite.SetHeader("P-Preferred-Identity", "<tel:+8613900000123>");
        Assert.Equal("1001", ImsIncomingCallerIdentity.Read(invite, trustedCarrier: false).Number);
        invite.Headers.Remove("P-Asserted-Identity");
        Assert.Equal("1001", ImsIncomingCallerIdentity.Read(invite, trustedCarrier: true).Number);
    }

    [Fact]
    public void MultipleHeaderValuesPreferTelAndPrivacyCannotBeOverriddenByNone()
    {
        var invite = new SipMessage();
        invite.SetHeader("From", "<sip:1001@example.invalid>");
        invite.AddHeader("P-Asserted-Identity", "<sip:9999@example.invalid>");
        invite.AddHeader("P-Asserted-Identity", "<tel:+8613800000123>");
        Assert.Equal("+8613800000123", ImsIncomingCallerIdentity.Read(invite, true).Number);
        invite.AddHeader("Privacy", "none");
        invite.AddHeader("Privacy", "id");
        Assert.Equal("Anonymous", ImsIncomingCallerIdentity.Read(invite, true).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImsIncomingEventAndCallInfoUseSamePrivacySafeIdentity(bool privacy)
    {
        using var calls = new ImsCallManager();
        using var wifi = new VoWifiManager();
        calls.ConfigureAudioRecording(false, null);
        IncomingCallEventArgs? incoming = null;
        calls.IncomingCall += (_, args) => incoming = args;
        var invite = new SipMessage { IsRequest = true, Method = "INVITE", RequestUri = "sip:callee@example.invalid" };
        invite.SetHeader("From", "<sip:anonymous@anonymous.invalid>;tag=caller");
        invite.SetHeader("To", "<sip:callee@example.invalid>");
        invite.SetHeader("Call-ID", "caller-presentation-offline");
        invite.SetHeader("CSeq", "1 INVITE");
        invite.SetHeader("P-Asserted-Identity", "<tel:+8613800000123>");
        if (privacy) invite.SetHeader("Privacy", "id");
        await calls.HandleIncomingInviteAsync(invite, wifi, _ => Task.CompletedTask);
        var expected = privacy ? "Anonymous" : "+8613800000123";
        Assert.Equal(expected, calls.ActiveCall?.TargetNumber);
        Assert.Equal(expected, incoming?.CallerNumber);
        Assert.Equal(CallState.Ringing, calls.State);
    }
}
