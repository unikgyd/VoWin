using System.Net;
using System.Security.Cryptography;
using VoSharp.Ipsec;
using VoSharp.Sip;

namespace VoSharp.Telephony;

/// <summary>
/// The two bidirectional transport-mode SA pairs needed for IMS sec-agree.
/// This is only an installation plan: it does not install Windows WFP filters
/// or SAs, reserve SPIs, or make a Host IMS registration IPsec-capable.
/// </summary>
public sealed class HostImsSaPlan : IDisposable
{
    private readonly byte[][] _keyCopies;
    private bool _disposed;

    private HostImsSaPlan(ChildSaRequest clientPair, ChildSaRequest serverPair, byte[][] keyCopies)
    {
        ClientPair = clientPair;
        ServerPair = serverPair;
        _keyCopies = keyCopies;
    }

    /// <summary>UE client port ↔ P-CSCF server port.</summary>
    public ChildSaRequest ClientPair { get; }

    /// <summary>UE server port ↔ P-CSCF client port.</summary>
    public ChildSaRequest ServerPair { get; }

    public static HostImsSaPlan Create(
        IPAddress ueAddress, IPAddress pcscfAddress, SecurityAgreement agreement,
        byte[] ck, byte[] ik)
    {
        ArgumentNullException.ThrowIfNull(ueAddress);
        ArgumentNullException.ThrowIfNull(pcscfAddress);
        ArgumentNullException.ThrowIfNull(agreement);
        if (ueAddress.AddressFamily != pcscfAddress.AddressFamily ||
            ueAddress.Equals(IPAddress.Any) || ueAddress.Equals(IPAddress.IPv6Any) ||
            pcscfAddress.Equals(IPAddress.Any) || pcscfAddress.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("IMS SA endpoints must be concrete addresses of the same family.");

        var ue = agreement.Selected;
        if (!SecurityAgreementBuilder.IsValidProtectedPort(ue.PortClient) ||
            !SecurityAgreementBuilder.IsValidProtectedPort(ue.PortServer) ||
            !SecurityAgreementBuilder.IsValidProtectedPort(agreement.PcscfClientPort) ||
            !SecurityAgreementBuilder.IsValidProtectedPort(agreement.PcscfServerPort) ||
            ue.PortClient == ue.PortServer ||
            agreement.PcscfClientPort == agreement.PcscfServerPort)
            throw new ArgumentException("IMS SA plan requires distinct valid protected ports.", nameof(agreement));
        var spis = new[] { ue.SpiClient, ue.SpiServer, agreement.PcscfClientSpi, agreement.PcscfServerSpi };
        if (spis.Any(spi => spi < 256) || spis.Distinct().Count() != spis.Length)
            throw new ArgumentException("IMS SA plan requires four distinct unreserved ESP SPIs.", nameof(agreement));

        var auth = ue.IntegrityAlgorithm switch
        {
            "hmac-sha-1-96" => IpsecAuthAlgorithm.HmacSha1_96,
            "hmac-md5-96" => IpsecAuthAlgorithm.HmacMd5_96,
            _ => throw new ArgumentException("Unsupported IMS SA integrity algorithm.", nameof(agreement))
        };
        var cipher = ue.EncryptionAlgorithm switch
        {
            "null" => IpsecCipherAlgorithm.None,
            "aes-cbc" => IpsecCipherAlgorithm.AesCbc128,
            "des-ede3-cbc" => IpsecCipherAlgorithm.TripleDesCbc,
            _ => throw new ArgumentException("Unsupported IMS SA encryption algorithm.", nameof(agreement))
        };

        var (authKey, cipherKey) = SecurityAgreementBuilder.ExpandKeys(
            ck, ik, ue.IntegrityAlgorithm, ue.EncryptionAlgorithm);
        var copies = new List<byte[]>(8);
        try
        {
            SecurityAssociation Sa(uint spi, SaDirection direction)
            {
                var authCopy = (byte[])authKey.Clone();
                var cipherCopy = (byte[])cipherKey.Clone();
                copies.Add(authCopy);
                copies.Add(cipherCopy);
                return new SecurityAssociation
                {
                    Spi = spi,
                    Direction = direction,
                    Mode = SaMode.Transport,
                    Cipher = cipher,
                    Auth = auth,
                    CipherKey = cipherCopy,
                    AuthKey = authCopy,
                    // TS 33.203 keeps the network-layer SA alive until SIP tears
                    // it down; WFP represents the specified 2^32-1 seconds.
                    LifetimeSeconds = uint.MaxValue
                };
            }

            ChildSaRequest Pair(string name, int localPort, int remotePort, uint inboundSpi, uint outboundSpi) =>
                new()
                {
                    Name = name,
                    LocalAddress = ueAddress,
                    RemoteAddress = pcscfAddress,
                    Mode = SaMode.Transport,
                    Inbound = Sa(inboundSpi, SaDirection.Inbound),
                    Outbound = Sa(outboundSpi, SaDirection.Outbound),
                    LocalSelector = new TrafficSelector(ueAddress, ueAddress,
                        StartPort: (ushort)localPort, EndPort: (ushort)localPort),
                    RemoteSelector = new TrafficSelector(pcscfAddress, pcscfAddress,
                        StartPort: (ushort)remotePort, EndPort: (ushort)remotePort),
                    LocalPort = (ushort)localPort,
                    RemotePort = (ushort)remotePort
                };

            // An SPI is chosen by the receiver, not the sender. The UE's SPIs
            // are inbound and the P-CSCF's SPIs are outbound from the UE.
            var client = Pair("IMS client-port pair", ue.PortClient, agreement.PcscfServerPort,
                ue.SpiClient, agreement.PcscfServerSpi);
            var server = Pair("IMS server-port pair", ue.PortServer, agreement.PcscfClientPort,
                ue.SpiServer, agreement.PcscfClientSpi);
            return new HostImsSaPlan(client, server, copies.ToArray());
        }
        catch
        {
            foreach (var copy in copies) CryptographicOperations.ZeroMemory(copy);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authKey);
            CryptographicOperations.ZeroMemory(cipherKey);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in _keyCopies) CryptographicOperations.ZeroMemory(key);
    }
}
