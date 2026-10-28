using System.Net;
using System.Net.Sockets;

namespace VoSharp.Ike;

internal sealed record IkeChildConfiguration(string Assigned, string Pcscf, IReadOnlyList<string> Dns, IkeChildPolicy Policy);
internal sealed class IkeChildAddressFamilyException(string message) : Exception(message);
internal static class IkeChildNegotiation
{
    internal static IkePayload Require(IReadOnlyList<IkePayload> payloads, IkePayloadType kind)
    {
        var values = payloads.Where(p => p.Type == kind).ToArray();
        if (values.Length != 1) throw new IkeFormatException($"Final IKE_AUTH must contain exactly one {kind}."); return values[0];
    }
    internal static IkePayload? MergeIdentity(IkePayload? previous, IReadOnlyList<IkePayload> payloads)
    {
        var values = payloads.Where(p => p.Type == IkePayloadType.IdentificationResponder).ToArray();
        if (values.Length > 1 || values.Any(p => p.Body.Length < 5) || previous != null && values.Any(p => !p.Body.AsSpan().SequenceEqual(previous.Body)))
            throw new IkeFormatException("Responder identity changed during IKE authentication.");
        return values.FirstOrDefault() ?? previous;
    }
    internal static IkeChildConfiguration Configuration(IReadOnlyList<IkePayload> payloads, IkePayload offeredLocal, IkePayload offeredRemote,
        IkeAddressFamilyMode mode, string? fallback)
    {
        foreach (var p in payloads.Where(p => p.Type == IkePayloadType.Notify))
        {
            if (p.Body.Length < 4 || p.Body[0] != 0 || p.Body[1] != 0) throw new IkeFormatException("Malformed CHILD_SA notification.");
            var code = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.Body.AsSpan(2, 2));
            if (code < 16384 || code is 16391 or 16387) throw new IkeFormatException($"CHILD_SA rejected or selected an unoffered mode ({code}).");
        }
        var local = IkeTrafficSelectors.Parse(Require(payloads, IkePayloadType.TrafficSelectorInitiator).Body);
        var remote = IkeTrafficSelectors.Parse(Require(payloads, IkePayloadType.TrafficSelectorResponder).Body);
        IkeTrafficSelectors.ValidateNarrowing(IkeTrafficSelectors.Parse(offeredLocal.Body), local);
        IkeTrafficSelectors.ValidateNarrowing(IkeTrafficSelectors.Parse(offeredRemote.Body), remote);
        var config = IkeConfigurationReply.Parse(Require(payloads, IkePayloadType.Configuration).Body);
        var preferred = mode == IkeAddressFamilyMode.Ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        var addresses = config.AssignedAddresses.Where(Usable).Distinct().OrderBy(ip => ip.AddressFamily == preferred ? 0 : 1).ToArray();
        var candidates = config.PcscfAddresses.ToList();
        if (IPAddress.TryParse(fallback, out var fallbackIp)) candidates.Add(fallbackIp);
        if (!addresses.Any(ip => candidates.Any(peer => peer.AddressFamily == ip.AddressFamily && Usable(peer))))
            throw new IkeChildAddressFamilyException("No coherent assigned-address/P-CSCF family was provided.");
        foreach (var address in addresses)
        {
            if (!local.Any(s => s.Supports(address, 17))) continue;
            var peer = candidates.FirstOrDefault(ip => ip.AddressFamily == address.AddressFamily && Usable(ip) && remote.Any(s => s.Allows(ip, 17, 5060)));
            if (peer != null) return new(address.ToString(), peer.ToString(), config.DnsAddresses.Select(ip => ip.ToString()).ToArray(), new(local, remote, address));
        }
        throw new IkeFormatException("No assigned-address/P-CSCF pair is usable inside the negotiated UDP traffic selectors.");
    }
    private static bool Usable(IPAddress ip) => !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) && !ip.Equals(IPAddress.Broadcast) &&
        !IPAddress.IsLoopback(ip) && !ip.IsIPv6Multicast && !ip.IsIPv6LinkLocal && !ip.IsIPv4MappedToIPv6 &&
        (ip.AddressFamily != AddressFamily.InterNetwork || ip.GetAddressBytes()[0] is > 0 and < 224);
}
