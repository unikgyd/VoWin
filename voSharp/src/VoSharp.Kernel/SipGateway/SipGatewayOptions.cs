using System.Net;

namespace VoSharp.Kernel.SipGateway;

/// <summary>Configuration for the private-network SIP B2BUA.</summary>
public sealed record SipGatewayOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    public int SipPort { get; init; } = 5060;
    public string Realm { get; init; } = "vowin.local";
    public Dictionary<string, string> Accounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int MinimumRegistrationSeconds { get; init; } = 60;
    public int MaximumRegistrationSeconds { get; init; } = 3600;
    public bool TrustClientSdpAddress { get; init; }
    public bool AllowPublicBind { get; init; }

    public void Validate()
    {
        if (BindAddress.Equals(IPAddress.Any) || BindAddress.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("SIP gateway requires an explicit WireGuard/private address; wildcard binding is disabled.");
        if (!AllowPublicBind && !IsPrivateOrLoopback(BindAddress))
            throw new ArgumentException($"{BindAddress} is not a private/WireGuard address. Set AllowPublicBind only when an external firewall is enforced.");
        if (SipPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(SipPort));
        if (string.IsNullOrWhiteSpace(Realm)) throw new ArgumentException("SIP realm is required.", nameof(Realm));
        if (Accounts.Count == 0) throw new ArgumentException("At least one SIP extension is required.", nameof(Accounts));
        if (Accounts.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrEmpty(pair.Value)))
            throw new ArgumentException("SIP extension names and passwords cannot be empty.", nameof(Accounts));
        if (MinimumRegistrationSeconds < 1 || MaximumRegistrationSeconds < MinimumRegistrationSeconds)
            throw new ArgumentException("Invalid registration expiry range.");
    }

    internal static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }
        return address.IsIPv6LinkLocal || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }
}

public sealed record SipGatewayRegistration(
    string Extension,
    string Contact,
    IPEndPoint RemoteEndPoint,
    DateTimeOffset ExpiresAt,
    string UserAgent);

public sealed record SipGatewayStatus(
    bool IsRunning,
    IPEndPoint? LocalEndPoint,
    IReadOnlyList<SipGatewayRegistration> Registrations,
    int ActiveDialogs);
