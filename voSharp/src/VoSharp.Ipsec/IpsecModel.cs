using System.Net;

namespace VoSharp.Ipsec;

/// <summary>Which way an SA protects traffic.</summary>
public enum SaDirection
{
    /// <summary>FWP_DIRECTION_OUTBOUND = 0.</summary>
    Outbound = 0,

    /// <summary>FWP_DIRECTION_INBOUND = 1.</summary>
    Inbound = 1
}

/// <summary>ESP encapsulation mode.</summary>
public enum SaMode
{
    /// <summary>IPSEC_TRAFFIC_TYPE_TRANSPORT = 0. Used by IMS sec-agree (3GPP TS 33.203).</summary>
    Transport = 0,

    /// <summary>IPSEC_TRAFFIC_TYPE_TUNNEL = 1. Used by the ePDG VoWiFi tunnel.</summary>
    Tunnel = 1
}

/// <summary>ESP confidentiality algorithms. The numeric order matches Windows' cipher types.</summary>
public enum IpsecCipherAlgorithm
{
    /// <summary>No encryption (3GPP <c>ealg=null</c>). Maps to IPSEC_TRANSFORM_ESP_AUTH on Windows.</summary>
    None = 0,
    DesCbc = 1,
    TripleDesCbc = 2,
    AesCbc128 = 3,
    AesCbc192 = 4,
    AesCbc256 = 5
}

/// <summary>
/// ESP integrity algorithms. The truncation length is part of the name on purpose:
/// Windows encodes it in the transform enum (IPSEC_AUTH_CONFIG_HMAC_SHA_1_96 vs
/// ..._SHA_256_128) and has no separate length field, so a bare hash-family name
/// is not enough to pick the right transform.
/// </summary>
public enum IpsecAuthAlgorithm
{
    HmacMd5_96 = 0,
    HmacSha1_96 = 1,
    HmacSha256_128 = 2
}

/// <summary>An IKEv2 traffic selector, as carried by the TSi/TSr payloads.</summary>
public sealed record TrafficSelector(
    IPAddress StartAddress,
    IPAddress EndAddress,
    byte IpProtocol = 0,          // 0 = any
    ushort StartPort = 0,
    ushort EndPort = 65535
)
{
    /// <summary>True when the range can be expressed as a single address.</summary>
    public bool IsSingleAddress => StartAddress.Equals(EndAddress);

    /// <summary>True when the port range covers everything, so it can be omitted.</summary>
    public bool IsFullPortRange => StartPort == 0 && EndPort == 65535;
}

/// <summary>
/// One direction of an IPsec SA pair. Platform-neutral: the WFP backend translates these
/// fields into IPSEC_SA_BUNDLE1 / IPSEC_SA0 / IPSEC_SA_AUTH_INFORMATION0 etc.
/// </summary>
public sealed class SecurityAssociation
{
    public required uint Spi { get; init; }
    public required SaDirection Direction { get; init; }
    public required SaMode Mode { get; init; }

    public required IpsecCipherAlgorithm Cipher { get; init; }
    public required IpsecAuthAlgorithm Auth { get; init; }

    /// <summary>Confidentiality key. Empty when <see cref="Cipher"/> is <see cref="IpsecCipherAlgorithm.None"/>.</summary>
    public required byte[] CipherKey { get; init; }

    /// <summary>Integrity key. hmac-sha-1-96 needs 20 bytes (IK padded with four zeros).</summary>
    public required byte[] AuthKey { get; init; }

    /// <summary>Lifetime in seconds. Windows requires a non-zero value; Linux XFRM has no equivalent.</summary>
    public int LifetimeSeconds { get; init; } = 3600;

    /// <summary>Lifetime in kilobytes. 0 means unlimited.</summary>
    public int LifetimeKilobytes { get; init; }

    /// <summary>Lifetime in packets. 0 means unlimited.</summary>
    public int LifetimePackets { get; init; }
}

/// <summary>A complete request to bring up one CHILD_SA (or one sec-agree SA pair).</summary>
public sealed class ChildSaRequest
{
    public required string Name { get; init; }

    /// <summary>Local outer endpoint. For tunnel mode this is the local internet-facing address.</summary>
    public required IPAddress LocalAddress { get; init; }

    /// <summary>Remote outer endpoint (the ePDG, or the P-CSCF in transport mode).</summary>
    public required IPAddress RemoteAddress { get; init; }

    public required SaMode Mode { get; init; }

    /// <summary>The inbound SA (SPI the peer sends with). Required.</summary>
    public required SecurityAssociation Inbound { get; init; }

    /// <summary>The outbound SA (SPI we send with). Required.</summary>
    public required SecurityAssociation Outbound { get; init; }

    /// <summary>Local traffic selector. In tunnel mode this carries the inner address range.</summary>
    public required TrafficSelector LocalSelector { get; init; }

    /// <summary>Remote traffic selector.</summary>
    public required TrafficSelector RemoteSelector { get; init; }

    /// <summary>
    /// Inner (tunnel-mode) local address — the virtual IP assigned by the ePDG via the
    /// IKEv2 Configuration Payload. Required for <see cref="SaMode.Tunnel"/>.
    /// </summary>
    public IPAddress? InnerLocalAddress { get; init; }

    /// <summary>Prefix length of <see cref="InnerLocalAddress"/> (typically 32).</summary>
    public byte InnerLocalPrefixLength { get; init; } = 32;

    /// <summary>NAT-T detected: wrap ESP in UDP (RFC 3948).</summary>
    public bool UdpEncapsulation { get; init; }

    /// <summary>Local UDP encapsulation port (RFC 3948 uses 4500).</summary>
    public ushort UdpEncapLocalPort { get; init; } = 4500;

    /// <summary>Remote UDP encapsulation port (RFC 3948 uses 4500).</summary>
    public ushort UdpEncapRemotePort { get; init; } = 4500;

    /// <summary>Local port for transport mode (IMS sec-agree port-c / port-s). 0 = any.</summary>
    public ushort LocalPort { get; init; }

    /// <summary>Remote port for transport mode. 0 = any.</summary>
    public ushort RemotePort { get; init; }
}

/// <summary>Opaque handle to an installed CHILD_SA, returned by <see cref="IChildSaBackend.InstallChildSaAsync"/>.</summary>
public interface IChildSaHandle : IAsyncDisposable
{
    /// <summary>Backend-native identifier (the WFP IPsec SA context id).</summary>
    ulong ContextId { get; }

    /// <summary>Correlation GUID recorded in IPSEC_SA_BUNDLE1.saLookupContext.</summary>
    Guid LookupContext { get; }

    uint InboundSpi { get; }
    uint OutboundSpi { get; }
}

/// <summary>Snapshot returned by <see cref="IChildSaBackend.QueryChildSaAsync"/>.</summary>
public sealed record ChildSaSnapshot(
    ulong ContextId,
    Guid LookupContext,
    SaMode Mode,
    SaDirection Direction,
    uint Spi,
    IpsecCipherAlgorithm Cipher,
    IpsecAuthAlgorithm Auth,
    int LifetimeSeconds,
    IPAddress LocalAddress,
    IPAddress RemoteAddress
);

/// <summary>
/// Platform-neutral IPsec data plane. The Windows implementation lives in
/// <c>VoSharp.Wfp.WindowsIpsecBackend</c>.
/// </summary>
public interface IChildSaBackend
{
    /// <summary>Installs a CHILD_SA: creates the SA context, sets the inbound SPI, and adds both SA bundles.</summary>
    Task<IChildSaHandle> InstallChildSaAsync(ChildSaRequest request, CancellationToken ct = default);

    /// <summary>
    /// Tears a CHILD_SA down. When <paramref name="graceful"/> is set the SA is expired first
    /// (IPsecSaContextExpire0), which lets in-flight packets drain instead of being dropped.
    /// </summary>
    Task RemoveChildSaAsync(IChildSaHandle handle, bool graceful = false, CancellationToken ct = default);

    /// <summary>
    /// Rekeys a CHILD_SA. There is no in-place rekey in the WFP API, so this installs the
    /// replacement pair first and only then expires the old context — the overlap is what
    /// keeps the tunnel from dropping packets mid-handover.
    /// </summary>
    Task<IChildSaHandle> RekeyChildSaAsync(
        IChildSaHandle existing, ChildSaRequest replacement, CancellationToken ct = default);

    /// <summary>Reads back the SAs currently installed for a context.</summary>
    Task<IReadOnlyList<ChildSaSnapshot>> QueryChildSaAsync(ulong contextId, CancellationToken ct = default);
}

/// <summary>Thrown for any data-plane failure. Never carries key material.</summary>
public sealed class IpsecException : Exception
{
    /// <summary>The Win32 error code, when the failure came from a WFP call.</summary>
    public int? Win32Error { get; init; }

    public IpsecException(string message, int? win32Error = null, Exception? inner = null)
        : base(message, inner)
    {
        Win32Error = win32Error;
    }
}
