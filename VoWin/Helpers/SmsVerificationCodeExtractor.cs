using System.Text;

namespace VoWin.Helpers;

// Adapted from SimAdmin's simadmin-sms-core/src/verification_code.rs (GPL-3.0-or-later).
// Keep scalar-based windows, scoring and tie breaks aligned with the shared contract vectors.
public static class SmsVerificationCodeExtractor
{
    private static readonly string[] Strong = ["验证码", "校验码", "动态码", "动态验证码", "认证码", "短信码", "登录码", "安全码", "验证代码"];
    private static readonly string[] Action = ["登录", "注册", "绑定", "换绑", "重置", "密码", "支付", "验证", "申请"];
    private static readonly string[] Trust = ["分钟内有效", "有效期", "请勿泄露", "不要告诉", "非本人", "本人操作", "完成验证"];
    private static readonly string[] Negative = ["订单号", "运单号", "快递单号", "工单号", "流水号", "编号", "序号", "金额", "余额", "积分", "卡号", "尾号", "手机号", "账号", "账户", "日期", "版本", "端口"];
    private static readonly string[] Suffixes = ["年", "月", "日", "号", "时", "分", "秒", "点", "元", "角", "块", "次", "折", "%", "gb", "mb", "kb", "tb", "g", "m", "k", "t", "bps"];
    private static readonly string[] Hotlines = ["10086", "10010", "10000", "10001", "12306", "12315", "12345", "95588", "95533", "95566", "95599", "95555", "95559", "95511"];

    public static string? Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var chars = content.EnumerateRunes().Select(r => r.Value is >= 0xff10 and <= 0xff19 ? r.Value - 0xff10 + '0' : r.Value).ToArray();
        string? best = null; var bestScore = int.MinValue; var bestPriority = 0;
        for (var index = 0; index < chars.Length;)
        {
            if (!Digit(chars[index])) { index++; continue; }
            var start = index;
            var firstEnd = End(chars, start); var length = firstEnd - start;
            string? code = null; var end = firstEnd;
            if (length is >= 4 and <= 8) code = Slice(chars, start, firstEnd);
            else if (length >= 2 && firstEnd + 1 < chars.Length && Separator(chars[firstEnd]) && Digit(chars[firstEnd + 1]))
            {
                end = End(chars, firstEnd + 1); var total = length + end - firstEnd - 1;
                if (end - firstEnd - 1 >= 2 && total is >= 4 and <= 8)
                    code = Slice(chars, start, firstEnd) + Slice(chars, firstEnd + 1, end);
            }
            if (code == null) { index = firstEnd; continue; }
            var score = Score(chars, start, end, code);
            var priority = Priority(code.Length);
            if (score >= 70 && (score > bestScore || score == bestScore && priority > bestPriority))
            { best = code; bestScore = score; bestPriority = priority; }
            index = end;
        }
        return best;
    }

    private static int Score(int[] chars, int start, int end, string code)
    {
        var score = code.Length switch { 6 => 60, 4 => 42, 5 => 24, 7 => 22, 8 => 20, _ => 0 };
        var near = Slice(chars, Math.Max(0, start - 32), Math.Min(chars.Length, end + 32));
        var immediate = Slice(chars, Math.Max(0, start - 10), Math.Min(chars.Length, end + 10));
        var before = Slice(chars, Math.Max(0, start - 10), start);
        var after = Slice(chars, end, Math.Min(chars.Length, end + 10));
        var strongImmediate = StrongKeyword(immediate);
        if (StrongKeyword(near)) score += 35;
        if (strongImmediate) score += 20;
        if (ContainsAny(near, Action)) score += 10;
        if (ContainsAny(near, Trust)) score += 10;
        var trimmedBefore = before.TrimEnd(Connectors); var trimmedAfter = after.TrimStart(Connectors);
        if (Strong.Any(k => trimmedBefore.EndsWith(k, StringComparison.Ordinal) || trimmedAfter.StartsWith(k, StringComparison.Ordinal)) ||
            Word(trimmedBefore, "code") || Word(trimmedAfter, "code")) score += 15;
        if (!StrongKeyword(trimmedBefore) && Negative.Any(k => trimmedBefore.EndsWith(k, StringComparison.Ordinal)) ||
            Suffixes.Any(s => AsciiLower(after.TrimStart()).StartsWith(s, StringComparison.Ordinal)) ||
            before.EndsWith('.') || after.StartsWith('.') || before.EndsWith('/') || after.StartsWith('/')) score -= 80;
        if (!strongImmediate && Hotlines.Contains(code, StringComparer.Ordinal)) score -= 80;
        if (ContainsAny(near, Negative)) score -= strongImmediate ? 5 : 35;
        return score;
    }
    private static readonly char[] Connectors = [':', '：', ' ', '\t', '-', '_', '是', '为', '的', '您', '你', '，', ',', '(', ')', '（', '）'];
    private static bool StrongKeyword(string value) => ContainsAny(value, Strong) || AsciiLower(value).Contains("verification code", StringComparison.Ordinal) ||
        AsciiLower(value).Contains("verify code", StringComparison.Ordinal) || new[] { "code", "otp", "pin", "passcode" }.Any(w => Word(value, w));
    private static bool Word(string value, string word)
    {
        value = AsciiLower(value);
        var start = 0;
        while (start < value.Length)
        {
            var index = value.IndexOf(word, start, StringComparison.Ordinal);
            if (index < 0) return false;
            if ((index == 0 || !WordChar(value[index - 1])) && (index + word.Length == value.Length || !WordChar(value[index + word.Length]))) return true;
            start = index + word.Length;
        }
        return false;
    }
    private static bool WordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    private static string AsciiLower(string value) => string.Create(value.Length, value, static (span, original) =>
    { for (var i = 0; i < original.Length; i++) span[i] = original[i] is >= 'A' and <= 'Z' ? (char)(original[i] + 32) : original[i]; });
    private static bool ContainsAny(string value, string[] words) => words.Any(w => value.Contains(w, StringComparison.Ordinal));
    private static bool Digit(int c) => c is >= '0' and <= '9';
    private static bool Separator(int c) => c is '-' or '－' or '‐' or '‑' or '–';
    private static int End(int[] chars, int start) { while (start < chars.Length && Digit(chars[start])) start++; return start; }
    private static int Priority(int len) => len switch { 6 => 5, 4 => 4, 5 => 3, 7 => 2, 8 => 1, _ => 0 };
    private static string Slice(int[] chars, int start, int end)
    {
        var text = new StringBuilder();
        for (var i = start; i < end; i++) text.Append(char.ConvertFromUtf32(chars[i]));
        return text.ToString();
    }
}
