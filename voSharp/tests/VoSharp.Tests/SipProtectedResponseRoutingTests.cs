using System.Net;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class SipProtectedResponseRoutingTests
{
    [Fact]
    public void ProtectedUdpResponseUsesUeClientAndPcscfServerPorts()
    {
        var ue = IPAddress.Parse("2001:db8::1");
        var pcscf = IPAddress.Parse("2001:db8::2");
        var receivedLocal = new IPEndPoint(ue, 54789); // UE protected server
        var viaDestination = new IPEndPoint(pcscf, 5063); // P-CSCF protected client
        var ueClient = new IPEndPoint(ue, 54788);
        var pcscfServer = new IPEndPoint(pcscf, 5062);

        var (local, remote) = SipTransport.SelectResponseEndpoints(
            true, receivedLocal, viaDestination, ueClient, pcscfServer);

        Assert.Equal(ueClient, local);
        Assert.Equal(pcscfServer, remote);
    }

    [Fact]
    public void PlainUdpResponseKeepsReceivedEndpointAndViaDestination()
    {
        var receivedLocal = new IPEndPoint(IPAddress.Loopback, 5060);
        var viaDestination = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5090);

        var (local, remote) = SipTransport.SelectResponseEndpoints(
            false, receivedLocal, viaDestination, null, null);

        Assert.Equal(receivedLocal, local);
        Assert.Equal(viaDestination, remote);
    }

    [Fact]
    public void ProtectedUdpResponseRejectsMismatchedPcscf()
    {
        var ue = IPAddress.Parse("2001:db8::1");
        var receivedLocal = new IPEndPoint(ue, 54789);
        var viaDestination = new IPEndPoint(IPAddress.Parse("2001:db8::2"), 5063);
        var ueClient = new IPEndPoint(ue, 54788);
        var wrongPcscf = new IPEndPoint(IPAddress.Parse("2001:db8::3"), 5062);

        Assert.Throws<InvalidOperationException>(() => SipTransport.SelectResponseEndpoints(
            true, receivedLocal, viaDestination, ueClient, wrongPcscf));
    }
}
