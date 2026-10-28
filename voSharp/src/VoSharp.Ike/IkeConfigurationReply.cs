using System.Buffers.Binary;
using System.Net;

namespace VoSharp.Ike;

/// <summary>Configuration body from an authenticated IKE exchange, not a raw
/// unverified IKE packet. All fields are validated before any result escapes.
/// Layout: RFC 7296 3.15; P-CSCF attributes: 3GPP TS 24.302.</summary>
internal sealed class IkeConfigurationReply
{
    public List<IPAddress> AssignedAddresses { get; } = [];
    public List<IPAddress> DnsAddresses { get; } = [];
    public List<IPAddress> PcscfAddresses { get; } = [];
    // A subnet is not automatically an assigned host address. Allocating an
    // IID also requires the negotiated selectors and a per-SIM lifetime owner.
    public List<(IPAddress Prefix, byte Length)> Ipv6Subnets { get; } = [];

    public static IkeConfigurationReply Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length is < 4 or > 4096 || body[0] != 2)
            throw new FormatException("Invalid IKE configuration reply.");
        var result = new IkeConfigurationReply();
        int offset = 4, count = 0;
        while (offset < body.Length)
        {
            if (++count > 128 || body.Length - offset < 4)
                throw new FormatException("Truncated or excessive IKE configuration attributes.");
            int type = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset, 2)) & 0x7fff;
            int length = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset + 2, 2));
            offset += 4;
            if (length > body.Length - offset)
                throw new FormatException("Truncated IKE configuration value.");
            var value = body.Slice(offset, length);
            offset += length;
            int expected = type switch { 1 or 3 or 20 => 4, 10 or 21 => 16, 15 => 17, _ => -1 };
            if (expected >= 0 && length != expected || type == 8 && length is not (16 or 17))
                throw new FormatException("Invalid IKE address attribute length.");
            if ((type == 15 || type == 8 && length == 17) && value[16] > 128)
                throw new FormatException("Invalid IKE IPv6 prefix length.");
            switch (type)
            {
                case 1: result.AssignedAddresses.Add(new(value)); break;
                case 8: result.AssignedAddresses.Add(new(value[..16])); break;
                case 3 or 10: result.DnsAddresses.Add(new(value)); break;
                case 20 or 21: result.PcscfAddresses.Add(new(value)); break;
                case 15: result.Ipv6Subnets.Add((new(value[..16]), value[16])); break;
                // Well-framed unknown attributes are forward-compatible.
            }
        }
        return result;
    }
}
