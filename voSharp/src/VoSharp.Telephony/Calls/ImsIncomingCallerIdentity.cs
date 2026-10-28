using System.Text.RegularExpressions;
using VoSharp.Sip;

namespace VoSharp.Telephony.Calls;

/// <summary>Presentation identity from the authenticated carrier leg, never from a SIP phone.</summary>
internal sealed record ImsIncomingCallerIdentity(string Number, string Source, bool IsPrivate)
{
    public static ImsIncomingCallerIdentity Read(SipMessage invite, bool trustedCarrier)
    {
        var privacy = Values(invite, "Privacy")
            .SelectMany(value => value.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Never turn a restricted carrier identity into a public From on the PBX leg.
        if (privacy.Contains("id") || privacy.Contains("user") ||
            (privacy.Contains("critical") && privacy.Any(value =>
                !new[] { "none", "header", "session", "critical" }.Contains(value, StringComparer.OrdinalIgnoreCase))))
            return new("Anonymous", "withheld", true);

        if (trustedCarrier && FindNumber(Values(invite, "P-Asserted-Identity")) is { } asserted)
            return new(asserted, "P-Asserted-Identity", false);
        if (FindNumber(Values(invite, "From")) is { } from)
            return new(from, "From", false);

        var anonymous = Regex.IsMatch(invite.GetHeader("From") ?? string.Empty,
            @"(?i)(?:sips?:anonymous(?:@|[;>])|""anonymous"")");
        return new(anonymous ? "Anonymous" : "Unknown", "unavailable", false);
    }

    private static IEnumerable<string> Values(SipMessage message, string header) =>
        message.Headers.TryGetValue(header, out var values) ? values : [];

    private static string? FindNumber(IEnumerable<string> values)
    {
        var candidates = new List<(bool IsTel, string Number)>();
        foreach (var value in values)
        {
            // A quoted display name may contain URI-looking text. Only inspect actual URIs.
            var addresses = Regex.Replace(value, @"""(?:\\.|[^""\\])*""", string.Empty);
            foreach (var address in addresses.Split(','))
            {
                var nameAddress = Regex.Match(address, @"<(?<uri>[^<>]+)>");
                var uri = nameAddress.Success ? nameAddress.Groups["uri"].Value.Trim() : address.Trim();
                var match = Regex.Match(uri, @"(?i)^(?<scheme>sips?|tel):(?<user>[^<>\s;@?,]+)(?=@|;|$)");
                if (!match.Success) continue;
                var user = Uri.UnescapeDataString(match.Groups["user"].Value);
                if (user.Length > 64 || !Regex.IsMatch(user, @"^\+?[0-9][0-9().-]*$")) continue;
                var number = Regex.Replace(user, @"[().-]", string.Empty);
                if (number.Count(char.IsAsciiDigit) > 32) continue;
                candidates.Add((match.Groups["scheme"].Value.Equals("tel", StringComparison.OrdinalIgnoreCase), number));
            }
        }
        // IMS can assert a SIP IMPU and a tel URI; prefer the actual telephone identity.
        return candidates.OrderByDescending(candidate => candidate.IsTel).Select(candidate => candidate.Number).FirstOrDefault();
    }

    public override string ToString() => $"Caller identity source={Source}; private={IsPrivate}; number omitted.";
}
