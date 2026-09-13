using System.Text.RegularExpressions;

namespace VoWin.Services;

/// <summary>Removes material that a support report must never expose.</summary>
internal static partial class DiagnosticLogRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = text;
        result = SecretValue().Replace(result, "$1=<redacted>");
        result = SipOrTelUser().Replace(result, "$1<redacted>$3");
        result = Iccid().Replace(result, "<iccid-redacted>");
        result = LongNumericIdentifier().Replace(result, "<numeric-identifier-redacted>");
        result = LabeledIdentity().Replace(result, "$1=<redacted>");
        result = SensitiveAt().Replace(result, "$1<redacted>");
        result = SensitiveAtResponse().Replace(result, "$1<redacted>");
        result = ProxyPassword().Replace(result, "$1<redacted>@");
        return result;
    }

    [GeneratedRegex(@"(?i)\b(nonce|response|res|ck|ik|rand|autn|authorization|proxy-authorization)\s*=\s*(?:""[^""]*""|[^,;\s]+)")]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"(?i)((?:sip:|tel:))([^@;>\s]+)(@[^;>\s]+)?")]
    private static partial Regex SipOrTelUser();

    [GeneratedRegex(@"\b89\d{14,20}\b")]
    private static partial Regex Iccid();

    [GeneratedRegex(@"(?<![\d.])\d{12,20}(?!\d)")]
    private static partial Regex LongNumericIdentifier();

    [GeneratedRegex(@"(?i)\b(imsi|iccid|impi|impu|msisdn|phone(?:number)?)\s*[:=]\s*[^,;\s]+")]
    private static partial Regex LabeledIdentity();

    [GeneratedRegex(@"(?i)(AT\+(?:CSIM|CGLA|CCHO|CSCA)\s*(?:=|:\s*))[^\r\n]*")]
    private static partial Regex SensitiveAt();

    [GeneratedRegex(@"(?i)(\+(?:CSCA|CNUM|CIMI|CCID|ICCID|CSIM|CGLA)\s*:\s*)[^\r\n]*")]
    private static partial Regex SensitiveAtResponse();

    [GeneratedRegex(@"(?i)(socks5h?://[^:/\s]+:)[^@/\s]+@")]
    private static partial Regex ProxyPassword();
}
