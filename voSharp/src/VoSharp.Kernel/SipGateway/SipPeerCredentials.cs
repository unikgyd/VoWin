using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VoSharp.Kernel.SipGateway;

/// <summary>Separate credentials for requests arriving from the exact fixed peer endpoint.</summary>
public sealed record SipPeerInboundCredentials(string Username, [property: JsonIgnore] string Password)
{
    public string Algorithm { get; init; } = "SHA-256";

    internal void Validate()
    {
        SipPeerCredentialValidation.ValidateUserAndPassword(Username, Password);
        if (Algorithm is not ("SHA-256" or "MD5")) throw new ArgumentException("SIP peer inbound algorithm must be SHA-256 or explicitly selected MD5.");
    }

    public override string ToString() => "SIP peer inbound credentials (secret omitted)";
}

/// <summary>Only a pinned realm and explicit algorithms may receive this outbound credential.</summary>
public sealed record SipPeerOutboundCredentials(string Username, [property: JsonIgnore] string Password, string ExpectedRealm)
{
    public IReadOnlyList<string> AllowedAlgorithms { get; init; } = Array.AsReadOnly(new[] { "SHA-256" });

    internal void Validate()
    {
        SipPeerCredentialValidation.ValidateUserAndPassword(Username, Password);
        if (string.IsNullOrWhiteSpace(ExpectedRealm) || ExpectedRealm == "*" || Encoding.UTF8.GetByteCount(ExpectedRealm) > 253 || ExpectedRealm.Any(char.IsControl))
            throw new ArgumentException("Configure the exact expected SIP peer realm before enabling the peer; unknown realms are not learned automatically.");
        if (AllowedAlgorithms == null || AllowedAlgorithms.Count is < 1 or > 2 ||
            AllowedAlgorithms.Any(algorithm => algorithm is not ("SHA-256" or "MD5")) ||
            AllowedAlgorithms.Distinct(StringComparer.Ordinal).Count() != AllowedAlgorithms.Count)
            throw new ArgumentException("SIP peer outbound algorithms must be unique SHA-256 or explicitly allowed MD5.");
    }

    public SipPeerOutboundCredentials DeepClone() => this with
    { AllowedAlgorithms = Array.AsReadOnly(AllowedAlgorithms.ToArray()) };

    public override string ToString() => "SIP peer outbound credentials (secret omitted)";
}

internal static class SipPeerCredentialValidation
{
    internal static void ValidateUserAndPassword(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || !Regex.IsMatch(username, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$") ||
            string.IsNullOrEmpty(password) || Encoding.UTF8.GetByteCount(password) > 128)
            throw new ArgumentException("SIP peer credentials require a safe username and a nonempty password of at most 128 UTF-8 bytes.");
    }
}
