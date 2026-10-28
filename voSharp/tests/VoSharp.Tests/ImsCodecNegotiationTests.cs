using System.Net;
using System.Reflection;
using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

public class ImsCodecNegotiationTests
{
    [Theory]
    [InlineData("8", "", true)]
    [InlineData("0", "", true)]
    [InlineData("8", "a=rtpmap:8 PCMA/8000/1\r\n", true)]
    [InlineData("96", "a=rtpmap:96 PCMA/8000\r\n", true)]
    [InlineData("97", "a=rtpmap:97 PCMU/8000/1\r\n", true)]
    [InlineData("8", "a=rtpmap:8 PCMA/8000/2\r\n", false)]
    [InlineData("8", "a=rtpmap:8 PCMA/16000\r\n", false)]
    [InlineData("0", "a=rtpmap:0 PCMA/8000\r\n", false)]
    [InlineData("96", "a=rtpmap:96 PCMA/8000/0\r\n", false)]
    [InlineData("96", "a=rtpmap:96 PCMA/8000/2\r\n", false)]
    [InlineData("96", "a=rtpmap:96 PCMA/8000/1/1\r\n", false)]
    [InlineData("96", "a=rtpmap:96 PCMA/8000garbage\r\n", false)]
    [InlineData("3", "a=rtpmap:3 AMR/8000\r\n", false)]
    [InlineData("104", "a=rtpmap:104 AMR-WB/16000\r\n", false)]
    [InlineData("101", "a=rtpmap:101 telephone-event/8000\r\n", false)]
    [InlineData("8", "a=rtpmap:8 PCMA/8000\r\na=rtpmap:8 PCMU/8000\r\n", false)]
    [InlineData("8", "a=rtpmap:8 PCMA/8000\r\na=rtpmap:8 PCMA/8000\r\n", true)]
    [InlineData("96 8", "a=rtpmap:96 PCMA/8000/2\r\n", true)]
    public async Task RealMediaSetupChecksCodecClockChannelsAndPayloadAssignments(string formats, string mappings, bool accepted)
    {
        using var manager = new ImsCallManager();
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(ImsCallManager).GetField("_rtp", hidden)!.SetValue(manager, rtp);
        var setup = typeof(ImsCallManager).GetMethod("SetRtpFromSdpAnswerAsync", hidden)!;
        var sdp = $"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 45000 RTP/AVP {formats}\r\n{mappings}";
        var task = (Task)setup.Invoke(manager, [sdp, CancellationToken.None, null, false])!;
        if (accepted) await task;
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.False(rtp.IsAmrCodecInitialized);
    }
}
