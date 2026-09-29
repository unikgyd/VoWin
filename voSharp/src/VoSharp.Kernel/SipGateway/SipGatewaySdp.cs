using System.Net;
using System.Text.RegularExpressions;

namespace VoSharp.Kernel.SipGateway;

internal sealed record SipAudioOffer(IPAddress Address, int Port, byte PayloadType, string Codec);

internal static class SipGatewaySdp
{
    public static bool TryParseAudioOffer(string? body, IPAddress packetSource, bool trustAddress, out SipAudioOffer? offer)
    {
        offer = null;
        if (string.IsNullOrWhiteSpace(body)) return false;
        var connection = Regex.Match(body, @"(?im)^c=IN\s+IP[46]\s+(\S+)\s*$");
        var media = Regex.Match(body, @"(?im)^m=audio\s+(\d+)\s+RTP/AVP\s+([^\r\n]+)");
        if (!media.Success || !int.TryParse(media.Groups[1].Value, out var port) || port is < 1 or > 65535) return false;
        var address = packetSource;
        if (trustAddress && connection.Success && IPAddress.TryParse(connection.Groups[1].Value, out var advertised)) address = advertised;

        var payloads = media.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => byte.TryParse(value, out var parsed) ? (byte?)parsed : null).OfType<byte>().ToArray();
        if (payloads.Contains((byte)8)) offer = new SipAudioOffer(address, port, 8, "PCMA");
        else if (payloads.Contains((byte)0)) offer = new SipAudioOffer(address, port, 0, "PCMU");
        return offer != null;
    }

    public static string BuildOffer(IPAddress address, int rtpPort, int rtcpPort, byte payloadType = 8)
    {
        var family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IP6" : "IP4";
        var codec = payloadType == 0 ? "PCMU" : "PCMA";
        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"v=0\r\no=VoWin {sessionId} {sessionId} IN {family} {address}\r\n" +
               $"s=VoWin SIP Gateway\r\nc=IN {family} {address}\r\nt=0 0\r\n" +
               $"m=audio {rtpPort} RTP/AVP {payloadType} 101\r\na=rtcp:{rtcpPort}\r\n" +
               $"a=rtpmap:{payloadType} {codec}/8000\r\na=rtpmap:101 telephone-event/8000\r\n" +
               "a=fmtp:101 0-15\r\na=ptime:20\r\na=sendrecv\r\n";
    }
}
