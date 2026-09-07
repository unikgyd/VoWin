using System.Text.RegularExpressions;

namespace VoSharp.Euicc.Models;

public sealed record EuiccDownloadProgress(int Percent, string Status);

public sealed record EuiccDownloadResult(
    string Iccid,
    bool InstalledWithWarning = false,
    string? Warning = null);

public sealed class EuiccDownloadUncertainException : Exception
{
    public bool CardCommitMayHaveCompleted { get; }

    public EuiccDownloadUncertainException(string message, bool cardCommitMayHaveCompleted, Exception? innerException = null)
        : base(message, innerException)
    {
        CardCommitMayHaveCompleted = cardCommitMayHaveCompleted;
    }
}

public sealed record EuiccActivationCode(
    string CanonicalCode,
    string SmdpAddress,
    string MatchingId,
    string? Oid,
    bool ConfirmationCodeRequired)
{
    private const int MaxCodeLength = 2048;
    private static readonly Regex ControlCharacters = new(@"[\x00-\x1F\x7F]", RegexOptions.Compiled);

    public static EuiccActivationCode Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("请输入 eSIM 激活链接或选择二维码图片。");

        var code = ExtractCode(value.Trim());
        if (code.Length > MaxCodeLength || ControlCharacters.IsMatch(code))
            throw new FormatException("激活码包含无效字符或长度异常。");

        if (code.StartsWith("1$", StringComparison.OrdinalIgnoreCase))
            code = "LPA:" + code;
        if (!code.StartsWith("LPA:1$", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("只支持 GSMA LPA:1$... 格式的 eSIM 激活码。");

        var parts = code[4..].Split('$');
        if (parts.Length < 2 || parts.Length > 5 || parts[0] != "1")
            throw new FormatException("eSIM 激活码字段数量不正确。");

        var address = parts[1].Trim();
        if (!Uri.TryCreate("https://" + address, UriKind.Absolute, out var smdp) ||
            string.IsNullOrWhiteSpace(smdp.Host) ||
            !string.IsNullOrEmpty(smdp.UserInfo) ||
            smdp.PathAndQuery != "/" ||
            smdp.Fragment.Length != 0)
            throw new FormatException("SM-DP+ 服务器地址无效。");

        var matchingId = parts.Length > 2 ? parts[2].Trim() : string.Empty;
        var oid = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3].Trim() : null;
        var confirmationCodeRequired = parts.Length > 4;
        if (confirmationCodeRequired && parts[4] != "1")
            throw new FormatException("激活码中的确认码标记无效。");
        if (matchingId.Length > 255 || (oid?.Length ?? 0) > 255)
            throw new FormatException("激活码中的 Matching ID 或 OID 长度异常。");

        var canonical = $"LPA:1${address}${matchingId}";
        if (oid is not null || confirmationCodeRequired) canonical += $"${oid ?? string.Empty}";
        if (confirmationCodeRequired) canonical += "$1";
        return new EuiccActivationCode(canonical, address, matchingId, oid, confirmationCodeRequired);
    }

    private static string ExtractCode(string input)
    {
        if (input.StartsWith("LPA:", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("1$", StringComparison.OrdinalIgnoreCase))
            return input;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return input;

        if (uri.Scheme.Equals("lpa", StringComparison.OrdinalIgnoreCase))
            return "LPA:" + input[(input.IndexOf(':') + 1)..].TrimStart('/');

        foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            if (pair.Length != 2) continue;
            var key = Uri.UnescapeDataString(pair[0]);
            if (key.Equals("activationCode", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("activation_code", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("lpa", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }

        return input;
    }
}
