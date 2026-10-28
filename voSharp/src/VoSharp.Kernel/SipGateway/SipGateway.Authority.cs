using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VoSharp.Kernel.SipGateway;

public sealed partial class SipGateway
{
    private bool IsPeerSource(IPEndPoint remote) => _options.Peer?.EndPoint.Equals(remote) == true;

    // This is part of the content binding, NOT the paid operation ID. Keeping
    // that stable ID ensures a role/config migration cannot submit an old SMS
    // again. Old v1 fingerprints fail closed rather than acquiring a new ID.
    private string MessageAuthority(string username, bool fromPeer)
    {
        var peer = fromPeer ? _options.Peer : null;
        var secret = peer?.InboundAuth?.Password ?? _options.Accounts[username];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "sip-authority-v1\0" + (fromPeer ? "peer" : "local") + "\0" + _options.Realm + "\0" +
            username.ToLowerInvariant() + "\0" + peer?.EndPoint + "\0" +
            (peer?.InboundAuth?.Algorithm ?? "MD5") + "\0" + secret))).ToLowerInvariant();
    }
}
