namespace VoSharp.Ike;

/// <summary>IANA-registered IKEv2 payload types (RFC 7296 §3.2).</summary>
public enum IkePayloadType : byte
{
    None = 0,
    SecurityAssociation = 33,
    KeyExchange = 34,
    IdentificationInitiator = 35,
    IdentificationResponder = 36,
    Certificate = 37,
    CertificateRequest = 38,
    Authentication = 39,
    Nonce = 40,
    Notify = 41,
    Delete = 42,
    VendorId = 43,
    TrafficSelectorInitiator = 44,
    TrafficSelectorResponder = 45,
    Encrypted = 46,
    Configuration = 47,
    Eap = 48
}

/// <summary>IKEv2 exchange types (RFC 7296 §3.1).</summary>
public enum IkeExchangeType : byte
{
    IkeSaInit = 34,
    IkeAuth = 35,
    CreateChildSa = 36,
    Informational = 37
}

/// <summary>IKEv2 message flags (RFC 7296 §3.1).</summary>
[Flags]
public enum IkeFlags : byte
{
    None = 0,
    Initiator = 0x08,
    Version = 0x10,
    Response = 0x20
}

/// <summary>IPsec protocol IDs carried in a proposal (RFC 7296 §3.3.1).</summary>
public enum IkeProtocolId : byte
{
    Ike = 1,
    Ah = 2,
    Esp = 3
}

/// <summary>Transform types (RFC 7296 §3.3.2).</summary>
public static class IkeTransformType
{
    public const byte Encryption = 1;
    public const byte Prf = 2;
    public const byte Integrity = 3;
    public const byte DiffieHellman = 4;
    public const byte ExtendedSequenceNumbers = 5;
}

/// <summary>ENCR transform IDs (IANA IKEv2 Transform Attribute / Transform Type 1).</summary>
public static class IkeEncryptionId
{
    public const ushort DesCbc = 2;
    public const ushort TripleDesCbc = 3;
    public const ushort AesCbc = 12;
    public const ushort AesCtr = 13;
}

/// <summary>PRF transform IDs (Transform Type 2).</summary>
public static class IkePrfId
{
    public const ushort HmacMd5 = 1;
    public const ushort HmacSha1 = 2;
    public const ushort HmacSha2_256 = 5;
    public const ushort HmacSha2_384 = 6;
    public const ushort HmacSha2_512 = 7;
}

/// <summary>INTEG transform IDs (Transform Type 3).</summary>
public static class IkeIntegrityId
{
    public const ushort HmacMd5_96 = 1;
    public const ushort HmacSha1_96 = 2;
    public const ushort AesXCbc96 = 5;
    public const ushort HmacSha2_256_128 = 12;
    public const ushort HmacSha2_384_192 = 13;
    public const ushort HmacSha2_512_256 = 14;
}

/// <summary>Diffie-Hellman group IDs (Transform Type 4).</summary>
public static class IkeDhGroupId
{
    public const ushort Modp768 = 1;
    public const ushort Modp1024 = 2;
    public const ushort Modp1536 = 5;
    public const ushort Modp2048 = 14;
    public const ushort Modp3072 = 15;
    public const ushort Modp4096 = 16;
    public const ushort Ecp256 = 19;
    public const ushort Ecp384 = 20;
    public const ushort Ecp521 = 21;
}

/// <summary>Transform attribute types (RFC 7296 §3.3.5).</summary>
public static class IkeAttributeType
{
    /// <summary>Key length in bits. Only meaningful for ENCR transforms.</summary>
    public const ushort KeyLength = 14;

    /// <summary>Attribute Format bit. Set for Type/Value encoding, clear for Type/Length/Value.</summary>
    public const ushort FormatTv = 0x8000;
}

/// <summary>3GPP / IANA notify message types used by VoWiFi.</summary>
public static class IkeNotifyType
{
    /// <summary>EAP-only authentication (RFC 5998). Many carrier ePDGs inspect this.</summary>
    public const ushort EapOnlyAuthentication = 16417;

    /// <summary>NAT detection source IP (RFC 7296 §3.14.2).</summary>
    public const ushort NatDetectionSourceIp = 16388;

    /// <summary>NAT detection destination IP.</summary>
    public const ushort NatDetectionDestinationIp = 16389;

    /// <summary>3GPP device identity (IMEI / IMEISV), carried in IKE_AUTH.</summary>
    public const ushort DeviceIdentity = 16388 + 100;
}

public static class IkeDefaults
{
    public const byte Version = 0x20;              // IKEv2 => major 2, minor 0
    public const int HeaderLength = 28;
    public const int GenericPayloadHeaderLength = 4;
    public const int NonceLength = 32;
    public const int UdpPort = 500;
    public const int NattPort = 4500;

    /// <summary>NAT-T keepalive interval. RFC 3948 suggests ~20s; voCore uses the same.</summary>
    public const int NattKeepaliveSeconds = 20;
}
