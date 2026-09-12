using System.Threading.Channels;
using VoSharp.Common.Utils;
using VoSharp.Sip;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class SipTests
{
    [Fact]
    public void TestSipMessage_ParseAndSerialize_Request()
    {
        var raw = "REGISTER sip:ims.mnc000.mcc460.3gppnetwork.org SIP/2.0\r\n" +
                  "Via: SIP/2.0/UDP 192.168.1.100:5060;branch=z9hG4bK123456\r\n" +
                  "From: <sip:460001234567890@ims.mnc000.mcc460.3gppnetwork.org>;tag=tag123\r\n" +
                  "To: <sip:460001234567890@ims.mnc000.mcc460.3gppnetwork.org>\r\n" +
                  "Call-ID: callid123@192.168.1.100\r\n" +
                  "CSeq: 1 REGISTER\r\n" +
                  "Content-Length: 0\r\n\r\n";

        var msg = SipMessage.Parse(raw);
        Assert.True(msg.IsRequest);
        Assert.Equal("REGISTER", msg.Method);
        Assert.Equal("sip:ims.mnc000.mcc460.3gppnetwork.org", msg.RequestUri);
        Assert.Equal("1 REGISTER", msg.GetHeader("CSeq"));
        Assert.Equal("callid123@192.168.1.100", msg.GetHeader("Call-ID"));

        var serialized = msg.ToString();
        Assert.Contains("REGISTER sip:ims.mnc000.mcc460.3gppnetwork.org SIP/2.0", serialized);
        Assert.Contains("CSeq: 1 REGISTER", serialized);
    }

    [Fact]
    public void TestSipMessage_ParseResponse()
    {
        var raw = "SIP/2.0 401 Unauthorized\r\n" +
                  "Via: SIP/2.0/UDP 192.168.1.100:5060;branch=z9hG4bK123456\r\n" +
                  "From: <sip:user@domain.com>;tag=123\r\n" +
                  "To: <sip:user@domain.com>;tag=456\r\n" +
                  "Call-ID: callid123@192.168.1.100\r\n" +
                  "CSeq: 1 REGISTER\r\n" +
                  "WWW-Authenticate: Digest realm=\"ims.mnc000.mcc460.3gppnetwork.org\", nonce=\"23553cbe9637a89d218ae64dae47bf35\", algorithm=AKAv1-MD5, qop=\"auth\"\r\n" +
                  "Content-Length: 0\r\n\r\n";

        var msg = SipMessage.Parse(raw);
        Assert.False(msg.IsRequest);
        Assert.Equal(401, msg.StatusCode);
        Assert.Equal("Unauthorized", msg.ReasonPhrase);

        var authHeader = msg.GetHeader("WWW-Authenticate");
        Assert.NotNull(authHeader);

        var challenge = ImsRegisterBuilder.ParseWwwAuthenticate(authHeader);
        Assert.NotNull(challenge);
        Assert.Equal("ims.mnc000.mcc460.3gppnetwork.org", challenge.Realm);
        Assert.Equal("23553cbe9637a89d218ae64dae47bf35", challenge.Nonce);
        Assert.Equal("AKAv1-MD5", challenge.Algorithm);
    }

    [Fact]
    public async Task SipTransportPreservesBinaryBodiesInBothDirections()
    {
        var inbound = Channel.CreateUnbounded<byte[]>();
        var sent = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<SipMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new SipTransport();
        transport.IncomingRequestReceived += (_, message) => received.TrySetResult(message);
        transport.ConnectCustom(
            bytes =>
            {
                sent.TrySetResult(bytes);
                return Task.CompletedTask;
            },
            token => inbound.Reader.ReadAsync(token).AsTask());

        var body = new byte[] { 0x01, 0x80, 0xff, 0x00, 0xc3 };
        var request = new SipMessage
        {
            IsRequest = true,
            Method = "MESSAGE",
            RequestUri = "sip:smsc@example.test",
            RawBody = body
        };
        request.SetHeader("Call-ID", "binary-message");
        request.SetHeader("CSeq", "1 MESSAGE");
        request.SetHeader("Content-Type", "application/vnd.3gpp.sms");
        request.SetHeader("Content-Length", body.Length.ToString());

        await transport.SendAsync(request);
        Assert.Equal(request.ToBytes(), await sent.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        await inbound.Writer.WriteAsync(request.ToBytes());
        var parsed = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(body, parsed.RawBody);
    }

    [Fact]
    public void TestImsRegisterBuilder_BuildInitialAndAuthenticated()
    {
        var profile = new ImsProfile(
            PrivateIdentity: "460001234567890@ims.mnc000.mcc460.3gppnetwork.org",
            PublicIdentity: "sip:+8613800138000@ims.mnc000.mcc460.3gppnetwork.org",
            HomeDomain: "ims.mnc000.mcc460.3gppnetwork.org",
            Imei: "860123456789012",
            LocalIp: "10.0.0.5",
            LocalPort: 5060
        );

        var callId = "sample-call-id-1234";
        var initial = ImsRegisterBuilder.BuildInitialRegister(profile, callId, 1);

        Assert.Equal("REGISTER", initial.Method);
        Assert.Contains("+sip.instance=\"<urn:gsma:imei:86012345-678901-2>\"", initial.GetHeader("Contact"));
        Assert.Null(initial.GetHeader("Authorization"));

        // Build authenticated with mock 401 challenge and RES
        var challenge = new Crypto.DigestChallenge(
            Realm: profile.HomeDomain,
            Nonce: "23553cbe9637a89d218ae64dae47bf35",
            Opaque: null,
            Algorithm: "AKAv1-MD5",
            Qop: "auth"
        );
        var mockRes = HexUtils.FromHexString("a54211d5e3ba50bf");

        var authenticated = ImsRegisterBuilder.BuildAuthenticatedRegister(profile, callId, 2, challenge, mockRes, fromTag: "testTag01");
        var authHeader = authenticated.GetHeader("Authorization");

        Assert.NotNull(authHeader);
        Assert.Contains("algorithm=AKAv1-MD5", authHeader);
        Assert.Contains("response=", authHeader);
        Assert.Contains("cnonce=", authHeader);
    }

    [Fact]
    public void ImsRegisterBuilder_BracketsIpv6InViaAndContact()
    {
        var profile = new ImsProfile(
            PrivateIdentity: "234330000000000@ims.mnc033.mcc234.3gppnetwork.org",
            PublicIdentity: "sip:234330000000000@ims.mnc033.mcc234.3gppnetwork.org",
            HomeDomain: "ims.mnc033.mcc234.3gppnetwork.org",
            Imei: "352127213600296",
            LocalIp: "2001:db8::1234");

        var register = ImsRegisterBuilder.BuildInitialRegister(profile, "ipv6-call", 1);

        Assert.Contains("[2001:db8::1234]:5060", register.GetHeader("Via"));
        Assert.Contains("@[2001:db8::1234]:5060", register.GetHeader("Contact"));
    }
}
