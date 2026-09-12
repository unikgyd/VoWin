using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VoSharp.Ike.Transport;

/// <summary>
/// Implements RFC 1928 SOCKS5 client protocol for TCP connections and UDP ASSOCIATE relay.
/// Allows per-SIM dedicated proxy routing for IKEv2 and user-space ESP datagrams.
/// </summary>
public sealed class Socks5Client : IDisposable
{
    public string ProxyHost { get; }
    public int ProxyPort { get; }
    public string? Username { get; }
    public string? Password { get; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    private Socket? _controlSocket;
    private IPEndPoint? _udpRelayEndpoint;
    private bool _disposed;

    public Socket? ControlSocket => _controlSocket;

    public Socks5Client(string proxyHost, int proxyPort, string? username = null, string? password = null)
    {
        ProxyHost = proxyHost ?? throw new ArgumentNullException(nameof(proxyHost));
        ProxyPort = proxyPort;
        Username = username;
        Password = password;
    }

    /// <summary>
    /// Parses a URL such as "socks5://127.0.0.1:1080" or "socks5://user:pass@10.0.0.1:1080".
    /// </summary>
    public static Socks5Client? TryParse(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl) || proxyUrl.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var uri = new Uri(proxyUrl.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) || proxyUrl.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase)
                ? proxyUrl
                : "socks5://" + proxyUrl);

            // IKEv2 and ESP need SOCKS5 UDP ASSOCIATE.  An HTTP proxy cannot
            // carry these datagrams, so never reinterpret an http:// URL as a
            // SOCKS endpoint and accidentally fall back to a direct route.
            if (!uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals("socks5h", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535)
                return null;

            string? user = null;
            string? pass = null;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                user = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1)
                    pass = Uri.UnescapeDataString(parts[1]);
            }

            int port = uri.Port > 0 ? uri.Port : 1080;
            return new Socks5Client(uri.Host, port, user, pass);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Connects to the SOCKS5 proxy and performs method handshake and optional RFC 1929 authentication.
    /// </summary>
    public async Task ConnectAndAuthenticateAsync(CancellationToken ct = default)
    {
        _controlSocket?.Dispose();
        _udpRelayEndpoint = null;
        _controlSocket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(Timeout);

        await _controlSocket.ConnectAsync(ProxyHost, ProxyPort, linkedCts.Token).ConfigureAwait(false);

        // Handshake: 0x05 (version), NMETHODS, METHODS...
        var methods = new List<byte> { 0x00 }; // 0x00 = NO AUTH
        if (!string.IsNullOrEmpty(Username))
            methods.Add(0x02); // 0x02 = USER/PASS

        var greeting = new byte[2 + methods.Count];
        greeting[0] = 0x05;
        greeting[1] = (byte)methods.Count;
        for (int i = 0; i < methods.Count; i++) greeting[2 + i] = methods[i];

        await SendAllAsync(_controlSocket, greeting, linkedCts.Token).ConfigureAwait(false);

        var response = new byte[2];
        await ReadExactAsync(_controlSocket, response, linkedCts.Token).ConfigureAwait(false);
        if (response[0] != 0x05)
            throw new InvalidOperationException($"Invalid SOCKS5 greeting response from {ProxyHost}:{ProxyPort}");

        if (response[1] == 0xFF)
            throw new InvalidOperationException("SOCKS5 proxy rejected authentication methods (0xFF).");

        if (response[1] == 0x02)
        {
            // RFC 1929 Username / Password Authentication
            if (string.IsNullOrEmpty(Username))
                throw new InvalidOperationException("SOCKS5 proxy requires username/password, but none provided.");

            var uBytes = Encoding.UTF8.GetBytes(Username);
            var pBytes = Encoding.UTF8.GetBytes(Password ?? string.Empty);

            var authBuf = new byte[3 + uBytes.Length + pBytes.Length];
            authBuf[0] = 0x01; // Version 1
            authBuf[1] = (byte)uBytes.Length;
            Buffer.BlockCopy(uBytes, 0, authBuf, 2, uBytes.Length);
            authBuf[2 + uBytes.Length] = (byte)pBytes.Length;
            Buffer.BlockCopy(pBytes, 0, authBuf, 3 + uBytes.Length, pBytes.Length);

            await SendAllAsync(_controlSocket, authBuf, linkedCts.Token).ConfigureAwait(false);

            var authResp = new byte[2];
            await ReadExactAsync(_controlSocket, authResp, linkedCts.Token).ConfigureAwait(false);
            if (authResp[1] != 0x00)
                throw new InvalidOperationException($"SOCKS5 proxy username/password authentication failed (code {authResp[1]}).");
        }
    }

    /// <summary>
    /// Executes a SOCKS5 UDP ASSOCIATE (CMD=0x03) request and returns the assigned UDP relay endpoint.
    /// </summary>
    public async Task<IPEndPoint> UdpAssociateAsync(CancellationToken ct = default)
    {
        if (_udpRelayEndpoint != null && _controlSocket?.Connected == true)
            return _udpRelayEndpoint;

        if (_controlSocket == null || !_controlSocket.Connected)
            await ConnectAndAuthenticateAsync(ct).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(Timeout);

        // Send UDP ASSOCIATE request: 05 03 00 01 (IPv4) 00 00 00 00 00 00 (0.0.0.0:0)
        var req = new byte[] { 0x05, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        await SendAllAsync(_controlSocket!, req, linkedCts.Token).ConfigureAwait(false);

        // Read reply header (4 bytes: VER, REP, RSV, ATYP)
        var header = new byte[4];
        await ReadExactAsync(_controlSocket!, header, linkedCts.Token).ConfigureAwait(false);

        if (header[0] != 0x05)
            throw new InvalidOperationException($"SOCKS5 unexpected reply version: {header[0]}");

        if (header[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 UDP ASSOCIATE request rejected with REP code {header[1]:X2}");

        IPAddress relayIp;
        byte atyp = header[3];
        if (atyp == 0x01) // IPv4
        {
            var ipBytes = new byte[4];
            await ReadExactAsync(_controlSocket!, ipBytes, linkedCts.Token).ConfigureAwait(false);
            relayIp = new IPAddress(ipBytes);
        }
        else if (atyp == 0x04) // IPv6
        {
            var ipBytes = new byte[16];
            await ReadExactAsync(_controlSocket!, ipBytes, linkedCts.Token).ConfigureAwait(false);
            relayIp = new IPAddress(ipBytes);
        }
        else if (atyp == 0x03) // Domain name
        {
            var lenByte = new byte[1];
            await ReadExactAsync(_controlSocket!, lenByte, linkedCts.Token).ConfigureAwait(false);
            var domainBytes = new byte[lenByte[0]];
            await ReadExactAsync(_controlSocket!, domainBytes, linkedCts.Token).ConfigureAwait(false);
            var domain = Encoding.ASCII.GetString(domainBytes);
            var resolved = await Dns.GetHostAddressesAsync(domain, linkedCts.Token).ConfigureAwait(false);
            relayIp = resolved.FirstOrDefault() ?? throw new InvalidOperationException($"Could not resolve SOCKS5 relay domain '{domain}'");
        }
        else
        {
            throw new NotSupportedException($"Unsupported SOCKS5 ATYP {atyp:X2}");
        }

        var portBytes = new byte[2];
        await ReadExactAsync(_controlSocket!, portBytes, linkedCts.Token).ConfigureAwait(false);
        int relayPort = BinaryPrimitives.ReadUInt16BigEndian(portBytes);

        // If proxy returned 0.0.0.0, use the proxy's own IP address
        if (relayIp.Equals(IPAddress.Any) || relayIp.Equals(IPAddress.IPv6Any))
        {
            if (IPAddress.TryParse(ProxyHost, out var hostIp))
            {
                relayIp = hostIp;
            }
            else
            {
                var ips = await Dns.GetHostAddressesAsync(ProxyHost, linkedCts.Token).ConfigureAwait(false);
                relayIp = ips.FirstOrDefault() ?? IPAddress.Loopback;
            }
        }

        _udpRelayEndpoint = new IPEndPoint(relayIp, relayPort);
        return _udpRelayEndpoint;
    }

    /// <summary>
    /// Sends one UDP datagram through the SOCKS5 relay and waits for a reply.
    /// This verifies the UDP data plane, rather than only the TCP handshake
    /// and UDP ASSOCIATE control response.
    /// </summary>
    public async Task<byte[]> UdpRoundTripAsync(
        ReadOnlyMemory<byte> payload,
        IPEndPoint targetEndpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetEndpoint);
        if (payload.IsEmpty)
            throw new ArgumentException("UDP probe payload cannot be empty.", nameof(payload));

        var relayEndpoint = await UdpAssociateAsync(ct).ConfigureAwait(false);
        using var udpSocket = new Socket(relayEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var anyAddress = relayEndpoint.AddressFamily == AddressFamily.InterNetwork
            ? IPAddress.Any
            : IPAddress.IPv6Any;
        udpSocket.Bind(new IPEndPoint(anyAddress, 0));
        udpSocket.Connect(relayEndpoint);

        var request = EncapsulateUdpDatagram(payload.Span, targetEndpoint);
        await udpSocket.SendAsync(request, SocketFlags.None, ct).ConfigureAwait(false);

        var responseBuffer = new byte[65535];
        var received = await udpSocket.ReceiveAsync(responseBuffer, SocketFlags.None, ct).ConfigureAwait(false);
        var response = DecapsulateUdpDatagram(responseBuffer.AsMemory(0, received));
        if (response == null)
            throw new InvalidOperationException("SOCKS5 relay returned a malformed UDP datagram.");

        return response.Value.ToArray();
    }

    /// <summary>
    /// Encapsulates a payload into an RFC 1928 SOCKS5 UDP datagram targeting <paramref name="targetEndpoint"/>.
    /// </summary>
    public static byte[] EncapsulateUdpDatagram(ReadOnlySpan<byte> payload, IPEndPoint targetEndpoint)
    {
        var targetIp = targetEndpoint.Address;
        if (targetIp.AddressFamily == AddressFamily.InterNetwork)
        {
            var ipBytes = targetIp.GetAddressBytes();
            var datagram = new byte[10 + payload.Length];
            datagram[0] = 0x00; // RSV
            datagram[1] = 0x00; // RSV
            datagram[2] = 0x00; // FRAG
            datagram[3] = 0x01; // ATYP = IPv4
            Buffer.BlockCopy(ipBytes, 0, datagram, 4, 4);
            BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(8, 2), (ushort)targetEndpoint.Port);
            payload.CopyTo(datagram.AsSpan(10));
            return datagram;
        }
        else if (targetIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ipBytes = targetIp.GetAddressBytes();
            var datagram = new byte[22 + payload.Length];
            datagram[0] = 0x00;
            datagram[1] = 0x00;
            datagram[2] = 0x00;
            datagram[3] = 0x04; // ATYP = IPv6
            Buffer.BlockCopy(ipBytes, 0, datagram, 4, 16);
            BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(20, 2), (ushort)targetEndpoint.Port);
            payload.CopyTo(datagram.AsSpan(22));
            return datagram;
        }
        else
        {
            throw new NotSupportedException($"Unsupported target address family {targetIp.AddressFamily}");
        }
    }

    /// <summary>
    /// Decapsulates an incoming RFC 1928 SOCKS5 UDP datagram, stripping the SOCKS5 header.
    /// </summary>
    public static ReadOnlyMemory<byte>? DecapsulateUdpDatagram(ReadOnlyMemory<byte> raw)
    {
        if (raw.Length < 10) return null;
        var span = raw.Span;
        if (span[0] != 0 || span[1] != 0) return null; // RSV check
        // RFC 1928 UDP fragmentation is optional and this client has no reassembly state. Passing
        // a nonzero fragment upward would make an incomplete IKE/ESP packet look authentic.
        if (span[2] != 0) return null;
        byte atyp = span[3];
        int headerLen;
        if (atyp == 0x01) headerLen = 10; // 4 header + 4 IPv4 + 2 port
        else if (atyp == 0x04) headerLen = 22; // 4 header + 16 IPv6 + 2 port
        else if (atyp == 0x03)
        {
            int domainLen = span[4];
            headerLen = 5 + domainLen + 2;
        }
        else return null;

        if (raw.Length <= headerLen) return null;
        return raw.Slice(headerLen);
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var sent = await socket.SendAsync(buffer[offset..], SocketFlags.None, ct).ConfigureAwait(false);
            if (sent == 0)
                throw new InvalidOperationException("Socket connection closed while sending to SOCKS5 proxy.");
            offset += sent;
        }
    }

    private static async Task ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidOperationException("Socket connection closed prematurely by SOCKS5 proxy.");
            offset += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controlSocket?.Dispose();
        _controlSocket = null;
        _udpRelayEndpoint = null;
    }
}
