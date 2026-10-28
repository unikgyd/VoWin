using System.Security.Cryptography;
using System.Text;

namespace VoSharp.Telephony.Sms;

/// <summary>Opaque routing identity, not permission or evidence that a modem is currently ready.</summary>
public static class SmsSubscriptionIdentity
{
    public static string? ForIccid(string? iccid) => iccid is { Length: >= 18 and <= 22 } && iccid.All(char.IsAsciiDigit)
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(iccid))).ToLowerInvariant() : null;
    public static bool IsValid(string? binding) => binding is { Length: 64 } && binding.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
