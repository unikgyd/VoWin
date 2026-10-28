using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Telephony.Sms;

namespace VoSharp.Kernel.SipGateway;

public sealed partial class SipGateway
{
    internal static bool IsCanonicalSmsReplyNumber(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A\+[1-9][0-9]{6,14}\z");

    /// <summary>
    /// Snapshot receive-time number semantics without dial rules, default-line
    /// selection or any paid grant. The host must persist this before awaiting
    /// its Inbox commit; an unknown original SIM must still be read-only.
    /// </summary>
    public string CaptureSmsReplyNumber(string? lineId, string sender)
    {
        if (SmsForwardStopping) return "";
        if (IsCanonicalSmsReplyNumber(sender)) return sender;
        if (lineId is null || !_options.LineRoutes.TryGetValue(lineId, out var route) || !route.Enabled ||
            !route.NormalizeE164 || string.IsNullOrEmpty(route.CountryCode) ||
            !Regex.IsMatch(route.CountryCode, @"\A[1-9][0-9]{0,2}\z") ||
            route.NationalNumberLength is < 6 or > 14 ||
            sender is null || sender.Length != route.NationalNumberLength ||
            !Regex.IsMatch(sender, @"\A[0-9]+\z")) return "";
        var candidate = "+" + route.CountryCode + sender;
        return IsCanonicalSmsReplyNumber(candidate) ? candidate : "";
    }

    private static string SmsReplyLineHash(string line) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line.ToLowerInvariant())))[..12].ToLowerInvariant();

    // The line identifies WHERE to send; the source-session binding identifies
    // WHICH card may send. Neither grants permission to send a paid message.
    // Unproven sources and short/alphanumeric senders deliberately cannot reply.
    internal static string SmsReplyUser(string line, string sender, string? simBinding) =>
        SmsSubscriptionIdentity.IsValid(simBinding) && Regex.IsMatch(sender, @"\A\+?[0-9]{7,15}\z")
            ? "sms2-" + SmsReplyLineHash(line) + "-" + EncodeSmsReplyBinding(Convert.FromHexString(simBinding!)) + "-" + sender
            : ReadOnlySmsReplyUser(line, sender, simBinding);

    private static string ReadOnlySmsReplyUser(string line, string sender, string? simBinding) =>
        "sms-ro-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "sip-sms-read-only-v1\0" + line.ToLowerInvariant() + "\0" + simBinding + "\0" + sender))).ToLowerInvariant();

    private static string ForwardSmsReplyUser(string line, string sender, string? simBinding, string? captured)
    {
        // Legacy sources may retain only an already international sender.
        // Never infer a country here, even if a new route could normalize it.
        var canonical = captured ?? (IsCanonicalSmsReplyNumber(sender) ? sender : "");
        return IsCanonicalSmsReplyNumber(canonical)
            ? SmsReplyUser(line, canonical, simBinding)
            : ReadOnlySmsReplyUser(line, sender, simBinding);
    }

    internal static bool TryParseSmsReplyUser(string user, out string lineHash, out string simBinding, out string number)
    {
        // Asterisk's extension buffer admits at most 79 characters. Keep the
        // complete 256-bit card digest, but encode its bytes rather than its
        // hex text: the longest possible alias is 78 characters, not 99.
        // URI users and the base64url payload are case sensitive.
        var match = Regex.Match(user, @"\A(?i:sms2)-([0-9a-fA-F]{12})-([A-Za-z0-9_-]{43})-(\+?[0-9]{7,15})\z");
        lineHash = simBinding = number = "";
        if (!match.Success) return false;
        var encoded = match.Groups[2].Value;
        var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=");
        // Reject alternate spellings with nonzero unused padding bits.
        if (bytes.Length != 32 || EncodeSmsReplyBinding(bytes) != encoded) return false;
        lineHash = match.Groups[1].Value.ToLowerInvariant();
        simBinding = Convert.ToHexString(bytes).ToLowerInvariant();
        number = match.Groups[3].Value;
        return true;
    }

    private static string EncodeSmsReplyBinding(byte[] binding) =>
        Convert.ToBase64String(binding).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsSmsReplyNamespace(string user) => user.StartsWith("sms-", StringComparison.OrdinalIgnoreCase) ||
        user.StartsWith("sms2-", StringComparison.OrdinalIgnoreCase);
}
