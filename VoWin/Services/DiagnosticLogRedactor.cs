using System.Text.RegularExpressions;

namespace VoWin.Services;

/// <summary>Removes material that a support report must never expose.</summary>
internal static partial class DiagnosticLogRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = text;
        result = TelegramTokenPath().Replace(result, "$1<token-redacted>");
        result = BarkKeyValue().Replace(result, "$1<key-redacted>");
        // Never attempt to scrub individual fields out of a raw credential/AT/PDU dump.
        result = SensitiveHeader().Replace(result, "$1: <redacted>");
        result = SensitiveDump().Replace(result, "$1<raw-payload-omitted>");
        result = ActivationCode().Replace(result, "LPA:<redacted>");
        result = SecretValue().Replace(result, "$1=<redacted>");
        result = SipOrTelUser().Replace(result, "$1<redacted>$3");
        result = Iccid().Replace(result, "<iccid-redacted>");
        result = LongNumericIdentifier().Replace(result, "<numeric-identifier-redacted>");
        result = LabeledIdentity().Replace(result, "$1=<redacted>");
        result = SensitiveAt().Replace(result, "$1<redacted>");
        result = SensitiveAtResponse().Replace(result, "$1<redacted>");
        result = ProxyPassword().Replace(result, "$1<redacted>@");
        result = UserPath().Replace(result, "$1<user>");
        result = SlotIdentifier().Replace(result, match => match.Groups[1].Value + "slot-" + match.Groups[2].Value[..8]);
        result = HexMaterial().Replace(result, "<binary-material-redacted>");
        result = PhoneNumber().Replace(result, "<phone-redacted>");
        return result;
    }

    [GeneratedRegex(@"(?i)\b(nonce|response|res|ck|ik|rand|autn|auts|k_aut|k_encr|msk|emsk|sk_[a-z]+|password|passwd|secret|token|(?:encryption|integrity)key|authorization|proxy-authorization)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^,;\s]+)")]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"(?i)((?:sip:|tel:))(?!<redacted>)([^@;>\s]+)(@[^;>\s]+)?")]
    private static partial Regex SipOrTelUser();

    [GeneratedRegex(@"\b89\d{14,20}\b")]
    private static partial Regex Iccid();

    [GeneratedRegex(@"(?<![\d.])\d{12,20}(?!\d)")]
    private static partial Regex LongNumericIdentifier();

    [GeneratedRegex(@"(?i)\b(imsi|iccid|imei|eid|impi|impu|msisdn|phone(?:number)?)\s*[:=]\s*(?:""[^""]*""|[^,;\s]+)")]
    private static partial Regex LabeledIdentity();

    [GeneratedRegex(@"(?i)(AT\+(?:CSIM|CGLA|CCHO|CSCA)\s*(?:=|:\s*))[^\r\n]*")]
    private static partial Regex SensitiveAt();

    [GeneratedRegex(@"(?i)(\+(?:CSCA|CNUM|CIMI|CCID|ICCID|CSIM|CGLA)\s*:\s*)[^\r\n]*")]
    private static partial Regex SensitiveAtResponse();

    [GeneratedRegex(@"(?i)((?:socks(?:5h?)?|https?)://)[^\s/@]+@")]
    private static partial Regex ProxyPassword();

    [GeneratedRegex(@"(?im)\b(authorization|proxy-authorization|cookie|set-cookie)\s*:\s*(?:(?!\\[rn])[^\r\n])*")]
    private static partial Regex SensitiveHeader();

    [GeneratedRegex(@"(?im)(\[(?:AT|USSD)\]\s*)[^\r\n]*(?:\r?\n(?!\[)[^\r\n]*)*")]
    private static partial Regex SensitiveDump();

    [GeneratedRegex(@"(?i)([A-Z]:[\\/]Users[\\/])[^\\/\s]+")]
    private static partial Regex UserPath();

    [GeneratedRegex(@"(?i)(?<![a-z0-9])[0-9a-f]{32,}(?![a-z0-9])")]
    private static partial Regex HexMaterial();

    [GeneratedRegex(@"(?<![\w.])\+?\d{10,15}(?![\w.])")]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"(?i)\bLPA:[^\s""<>]+")]
    private static partial Regex ActivationCode();

    [GeneratedRegex(@"(?i)(/bot)[0-9]+:[A-Za-z0-9_-]+")]
    private static partial Regex TelegramTokenPath();

    [GeneratedRegex(@"(?i)([""']?(?:device_key|barkkey|barkdevicekey)[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^,;\s]+)")]
    private static partial Regex BarkKeyValue();

    [GeneratedRegex(@"(?i)(\bslot(?:id)?\s*[:=]?\s*)([0-9a-f]{32})(?![0-9a-f])")]
    private static partial Regex SlotIdentifier();
}
