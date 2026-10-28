using System.Text.RegularExpressions;

namespace VoSharp.Telephony.Calls;

/// <summary>Conservative exact match for a single Chinese mobile test caller.</summary>
public static class ImsCallerNumberMatcher
{
    public static bool Matches(string expected, string received)
    {
        var expectedNational = Normalize(expected);
        return expectedNational is not null &&
            string.Equals(expectedNational, Normalize(received), StringComparison.Ordinal);
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        if (!Regex.IsMatch(text, @"^\+?[0-9][0-9 ()-]*$")) return null;
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 13 && digits.StartsWith("86", StringComparison.Ordinal))
            digits = digits[2..];
        return digits.Length == 11 && digits[0] == '1' ? digits : null;
    }
}
