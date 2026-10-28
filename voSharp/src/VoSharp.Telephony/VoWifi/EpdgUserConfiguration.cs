using System.Net;
using System.Text.RegularExpressions;

namespace VoSharp.Telephony.VoWifi;

/// <summary>Per-profile discovery hints, never a replacement for SIM authentication identity.</summary>
public sealed record EpdgUserConfiguration(string? PreferredAddresses = null, string? AdditionalPlmns = null,
    bool ManualOnly = false)
{
    public EpdgUserConfiguration Normalize()
    {
        var hosts = Tokens(PreferredAddresses).Select(host =>
        {
            host = host.TrimEnd('.');
            if (IPAddress.TryParse(host, out var address))
            {
                if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                var first = address.GetAddressBytes()[0];
                if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
                    address.Equals(IPAddress.Broadcast) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
                    address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && first >= 224)
                    throw new ArgumentException("ePDG must be a usable unicast address, not a local or multicast endpoint.");
                return address.ToString();
            }
            if (host.Length > 253 || !Regex.IsMatch(host,
                    @"^(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$"))
                throw new ArgumentException("ePDG requires a hostname or IP, without scheme, port, path or credentials.");
            // A malformed dotted IPv4 must not silently become a DNS name.
            if (host.All(character => char.IsAsciiDigit(character) || character == '.'))
                throw new ArgumentException("Invalid ePDG IP address.");
            return host.ToLowerInvariant();
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var plmns = Tokens(AdditionalPlmns).Select(value =>
        {
            if (!Regex.IsMatch(value, @"^[0-9]{3}-?[0-9]{2,3}$"))
                throw new ArgumentException("Discovery PLMN requires MCC and MNC: five or six digits, optionally separated by a hyphen.");
            return value.Replace("-", "", StringComparison.Ordinal);
        }).Distinct(StringComparer.Ordinal).ToArray();
        if (hosts.Length > 16 || plmns.Length > 16)
            throw new ArgumentException("At most 16 ePDG addresses and 16 discovery PLMNs can be configured per SIM.");
        if (ManualOnly && hosts.Length == 0)
            throw new ArgumentException("Manual-only discovery requires at least one ePDG address.");
        return this with { PreferredAddresses = string.Join('\n', hosts), AdditionalPlmns = string.Join('\n', plmns) };
    }

    public EpdgDiscoveryOptions ApplyTo(EpdgDiscoveryOptions options)
    {
        var normalized = Normalize();
        var hosts = Tokens(normalized.PreferredAddresses);
        var plmns = Tokens(normalized.AdditionalPlmns);
        return options with
        {
            // Explicit CLI CustomEpdg remains an override; user hints do not
            // erase carrier/network discovery data supplied by the host.
            StaticAddresses = normalized.ManualOnly ? hosts : hosts
                .Concat(options.StaticAddresses ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            CarrierPlmns = plmns
                .Concat(options.CarrierPlmns ?? []).Distinct(StringComparer.Ordinal).ToArray(),
            UserPreferredAddresses = hosts.Concat(options.UserPreferredAddresses ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            MethodPriority = hosts.Length > 0 && !normalized.ManualOnly
                ? new[] { EpdgDiscoveryMethod.Static }.Concat(options.MethodPriority ??
                    [EpdgDiscoveryMethod.Plmn, EpdgDiscoveryMethod.Pco, EpdgDiscoveryMethod.CellularLocation]).Distinct().ToArray()
                : options.MethodPriority,
            PlmnPriority = plmns.Length > 0 && options.PlmnPriority != null
                ? options.PlmnPriority.Concat([EpdgPlmnSource.CarrierConfigured]).Distinct().ToArray() : options.PlmnPriority,
            ManualOnly = normalized.ManualOnly
        };
    }

    private static string[] Tokens(string? text)
    {
        if (text?.Length > 8192) throw new ArgumentException("ePDG discovery configuration is too long.");
        return (text ?? "").Split([' ', ',', ';', '\r', '\n', '\t'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
