using System.Text.RegularExpressions;

namespace VoSharp.Telephony.Mmi;

public record MmiCommand(
    string RawCode,
    string Service,
    string Action,
    string? Target
);

public static class MmiParser
{
    private static readonly Regex MmiRegex = new(@"^(?<prefix>\*#|\*|#|##)(?<code>\d{2,3})(?:\*(?<target>[^*#]+))?#$", RegexOptions.Compiled);

    public static MmiCommand? Parse(string input)
    {
        input = input.Trim();
        if (input == "*#06#")
        {
            return new MmiCommand(input, "IMEI", "QUERY", null);
        }

        var match = MmiRegex.Match(input);
        if (!match.Success) return null;

        var prefix = match.Groups["prefix"].Value;
        var code = match.Groups["code"].Value;
        var target = match.Groups["target"].Success ? match.Groups["target"].Value : null;

        var action = prefix switch
        {
            "*#" => "QUERY",
            "*" => "ACTIVATE",
            "#" => "DEACTIVATE",
            "##" => "ERASE",
            _ => "UNKNOWN"
        };

        var service = code switch
        {
            "21" => "CallForwardingUnconditional",
            "67" => "CallForwardingBusy",
            "61" => "CallForwardingNoReply",
            "62" => "CallForwardingNotReachable",
            "43" => "CallWaiting",
            "30" => "CLIP",
            "31" => "CLIR",
            _ => $"Service_{code}"
        };

        return new MmiCommand(input, service, action, target);
    }
}
