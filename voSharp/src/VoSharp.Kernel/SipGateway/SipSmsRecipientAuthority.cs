using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VoSharp.Kernel.SipGateway;

/// <summary>Opaque receiver incarnation. It is not a SIP password or a send permission.</summary>
public static class SipSmsRecipientAuthority
{
    public static bool IsValidEpoch(string? epoch) => epoch is { Length: 32 } &&
        epoch.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string? Local(SipGatewayOptions options, string username)
    {
        if (!options.Accounts.TryGetValue(username, out var password) ||
            !options.LocalAccountEpochs.TryGetValue(username, out var epoch) || !IsValidEpoch(epoch)) return null;
        return Hash("sip-sms-recipient-v1", "local", options.Realm, username.ToLowerInvariant(), epoch, password);
    }

    public static string? Peer(SipGatewayOptions options, string target)
    {
        var peer = options.Peer;
        if (peer?.InboundAuth is not { } inbound || peer.OutboundAuth is not { } outbound ||
            !IsValidEpoch(options.PeerCredentialEpoch)) return null;
        return Hash("sip-sms-recipient-v1", "peer", options.Realm, target, options.PeerCredentialEpoch!,
            peer.EndPoint.ToString(), inbound.Username, inbound.Algorithm, inbound.Password,
            outbound.Username, outbound.ExpectedRealm, string.Join(",", outbound.AllowedAlgorithms), outbound.Password);
    }

    // No persistence is invented for callers that only construct runtime options.
    // A later process gets unrelated epochs, so it cannot consume the old queue.
    internal static SipGatewayOptions WithRuntimeEpochs(SipGatewayOptions options)
    {
        var epochs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var username in options.Accounts.Keys)
            epochs[username] = options.LocalAccountEpochs.TryGetValue(username, out var epoch) && IsValidEpoch(epoch)
                ? epoch : Guid.NewGuid().ToString("N");
        return options with { LocalAccountEpochs = epochs,
            PeerCredentialEpoch = options.Peer == null ? null : IsValidEpoch(options.PeerCredentialEpoch)
                ? options.PeerCredentialEpoch : Guid.NewGuid().ToString("N") };
    }

    private static string Hash(params string[] fields)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            try { BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); digest.AppendData(length); digest.AppendData(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
}
