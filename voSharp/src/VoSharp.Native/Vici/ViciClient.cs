using System.Buffers.Binary;
using System.Net.Sockets;

namespace VoSharp.Native.Vici;

/// <summary>
/// strongSwan VICI protocol client.
/// Communicates with charon-svc over TCP 127.0.0.1:4502 to:
///   - Load/unload IKEv2 connections
///   - Initiate/terminate IKE SAs
///   - List active SAs (get tunnel IP, SPIs, traffic counters)
///   - Subscribe to events (ike-updown, child-updown, etc.)
///
/// Protocol reference: strongSwan VICI README §Binary Protocol
/// </summary>
public class ViciClient : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private bool _disposed;

    public string Host { get; }
    public int Port { get; }
    public bool IsConnected => _tcp?.Connected == true;

    public ViciClient(string host = "127.0.0.1", int port = 4502)
    {
        Host = host;
        Port = port;
    }

    // ── Connection ──────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
    }

    // ── High-Level Commands ─────────────────────────────────────────────────

    /// <summary>
    /// Loads a VoWiFi IKEv2 connection configuration into charon.
    /// Equivalent to: swanctl --load-conns
    /// </summary>
    public async Task<ViciMessage> LoadConnectionAsync(
        string connName,
        string epdgIp,
        string imsiNai,
        string epdgFqdn,
        string proposals = "aes256-sha256-ecp256, aes128-sha256-modp2048",
        string espProposals = "aes256-sha256, aes128-sha256",
        CancellationToken ct = default)
    {
        var child = new ViciMessage()
            .Set("remote_ts", "0.0.0.0/0")
            .Set("local_ts", "dynamic")
            .SetList("esp_proposals", espProposals.Split(',', StringSplitOptions.TrimEntries));

        var local = new ViciMessage()
            .Set("auth", "eap-aka")
            .Set("id", imsiNai)
            .Set("eap_id", imsiNai);

        var remote = new ViciMessage()
            .Set("auth", "pubkey")
            .Set("id", epdgFqdn);

        var conn = new ViciMessage()
            .SetList("remote_addrs", new[] { epdgIp })
            .Set("version", "2")
            .Set("send_certreq", "no")
            .Set("reauth_time", "0")
            .SetList("proposals", proposals.Split(',', StringSplitOptions.TrimEntries))
            .SetSection("local", local)
            .SetSection("remote", remote)
            .SetSection("children", new ViciMessage().SetSection("vowifi-ims", child));

        var request = new ViciMessage()
            .SetSection(connName, conn);

        return await SendCommandAsync("load-conn", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Initiates an IKE SA for the named connection.
    /// Equivalent to: swanctl --initiate --child vowifi-ims
    /// </summary>
    public async Task<ViciMessage> InitiateAsync(
        string childName = "vowifi-ims",
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var request = new ViciMessage()
            .Set("child", childName)
            .Set("timeout", timeoutSeconds.ToString());

        return await SendCommandAsync("initiate", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates an IKE SA by connection name or unique ID.
    /// </summary>
    public async Task<ViciMessage> TerminateAsync(
        string? ikeName = null,
        string? ikeId = null,
        CancellationToken ct = default)
    {
        var request = new ViciMessage();
        if (ikeName != null) request.Set("ike", ikeName);
        if (ikeId != null) request.Set("ike-id", ikeId);
        request.Set("timeout", "5");

        return await SendCommandAsync("terminate", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all active IKE SAs and their child SAs.
    /// Returns parsed SA information including assigned virtual IPs.
    /// </summary>
    public async Task<List<ViciIkeSa>> ListSasAsync(CancellationToken ct = default)
    {
        var response = await SendCommandAsync("list-sas", new ViciMessage(), ct).ConfigureAwait(false);
        return ParseSaList(response);
    }

    /// <summary>
    /// Gets the strongSwan daemon version and runtime stats.
    /// </summary>
    public async Task<ViciMessage> GetVersionAsync(CancellationToken ct = default)
    {
        return await SendCommandAsync("version", new ViciMessage(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets daemon statistics (uptime, workers, IKE SAs, etc.)
    /// </summary>
    public async Task<ViciMessage> GetStatsAsync(CancellationToken ct = default)
    {
        return await SendCommandAsync("stats", new ViciMessage(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unloads a previously loaded connection configuration.
    /// </summary>
    public async Task<ViciMessage> UnloadConnectionAsync(string connName, CancellationToken ct = default)
    {
        var request = new ViciMessage().Set("name", connName);
        return await SendCommandAsync("unload-conn", request, ct).ConfigureAwait(false);
    }

    // ── Event Subscription ──────────────────────────────────────────────────

    /// <summary>
    /// Registers for a VICI event stream (e.g., "ike-updown", "child-updown").
    /// Returns an async enumerable of event messages.
    /// </summary>
    public async Task RegisterEventAsync(string eventName, CancellationToken ct = default)
    {
        await SendPacketAsync(ViciPacketType.EventRegister, eventName, null, ct).ConfigureAwait(false);
        var (type, _, _) = await ReceivePacketAsync(ct).ConfigureAwait(false);
        if (type != ViciPacketType.EventConfirm)
            throw new InvalidOperationException($"VICI: event registration for '{eventName}' failed (got {type}).");
    }

    /// <summary>
    /// Waits for the next event. Must call RegisterEventAsync first.
    /// </summary>
    public async Task<(string EventName, ViciMessage Data)?> WaitForEventAsync(CancellationToken ct = default)
    {
        var (type, name, data) = await ReceivePacketAsync(ct).ConfigureAwait(false);
        if (type == ViciPacketType.Event && name != null && data != null)
            return (name, ViciMessage.Deserialize(data));
        return null;
    }

    // ── Low-Level Protocol ──────────────────────────────────────────────────

    private async Task<ViciMessage> SendCommandAsync(string command, ViciMessage request, CancellationToken ct)
    {
        EnsureConnected();
        var payload = request.Serialize();
        await SendPacketAsync(ViciPacketType.CmdRequest, command, payload, ct).ConfigureAwait(false);

        var (type, _, responseData) = await ReceivePacketAsync(ct).ConfigureAwait(false);

        if (type == ViciPacketType.CmdUnknown)
            throw new InvalidOperationException($"VICI: unknown command '{command}'. Is the correct strongSwan version installed?");

        if (type != ViciPacketType.CmdResponse || responseData == null)
            throw new InvalidOperationException($"VICI: unexpected response type {type} for command '{command}'.");

        return ViciMessage.Deserialize(responseData);
    }

    /// <summary>
    /// Sends a VICI packet: [4-byte BE length][1-byte type][1-byte name-len][name bytes][payload]
    /// </summary>
    private async Task SendPacketAsync(ViciPacketType type, string? name, byte[]? payload, CancellationToken ct)
    {
        var nameBytes = name != null ? System.Text.Encoding.UTF8.GetBytes(name) : Array.Empty<byte>();
        int bodyLen = 1 + (name != null ? 1 + nameBytes.Length : 0) + (payload?.Length ?? 0);

        var packet = new byte[4 + bodyLen];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)bodyLen);
        int offset = 4;
        packet[offset++] = (byte)type;

        if (name != null)
        {
            packet[offset++] = (byte)nameBytes.Length;
            Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
            offset += nameBytes.Length;
        }

        if (payload != null)
        {
            Buffer.BlockCopy(payload, 0, packet, offset, payload.Length);
        }

        await _stream!.WriteAsync(packet, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives a VICI packet: [4-byte BE length][1-byte type][optional name + payload]
    /// </summary>
    private async Task<(ViciPacketType Type, string? Name, byte[]? Data)> ReceivePacketAsync(CancellationToken ct)
    {
        // Read 4-byte length header
        var lenBuf = new byte[4];
        await ReadExactAsync(lenBuf, ct).ConfigureAwait(false);
        uint bodyLen = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);

        if (bodyLen == 0)
            return (ViciPacketType.CmdUnknown, null, null);

        var body = new byte[bodyLen];
        await ReadExactAsync(body, ct).ConfigureAwait(false);

        var type = (ViciPacketType)body[0];
        int offset = 1;
        string? name = null;

        // Event and CmdRequest/CmdResponse may have a name field
        if (type is ViciPacketType.CmdResponse or ViciPacketType.CmdUnknown
            or ViciPacketType.EventConfirm or ViciPacketType.EventUnknown
            or ViciPacketType.Event)
        {
            // CmdResponse doesn't have name; Event does
            if (type == ViciPacketType.Event && offset < body.Length)
            {
                int nameLen = body[offset++];
                name = System.Text.Encoding.UTF8.GetString(body, offset, nameLen);
                offset += nameLen;
            }
        }

        byte[]? data = offset < body.Length ? body[offset..] : null;
        return (type, name, data);
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int n = await _stream!.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)
                                    .ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("VICI: charon-svc closed the connection.");
            totalRead += n;
        }
    }

    private void EnsureConnected()
    {
        if (_stream == null || _tcp?.Connected != true)
            throw new InvalidOperationException("VICI: not connected. Call ConnectAsync() first.");
    }

    // ── SA Parsing ──────────────────────────────────────────────────────────

    private static List<ViciIkeSa> ParseSaList(ViciMessage response)
    {
        var result = new List<ViciIkeSa>();

        foreach (var (saName, saObj) in response.Data)
        {
            if (saObj is not ViciMessage saMsg) continue;

            var childSas = new List<ViciChildSa>();
            var childSection = saMsg.GetSection("child-sas");
            if (childSection != null)
            {
                foreach (var (childName, childObj) in childSection.Data)
                {
                    if (childObj is not ViciMessage cm) continue;
                    childSas.Add(new ViciChildSa(
                        Name:      childName,
                        UniqueId:  cm.GetString("uniqueid")  ?? "",
                        State:     cm.GetString("state")     ?? "",
                        Protocol:  cm.GetString("protocol")  ?? "ESP",
                        Mode:      cm.GetString("mode")      ?? "TUNNEL",
                        SpiIn:     cm.GetString("spi-in"),
                        SpiOut:    cm.GetString("spi-out"),
                        EncAlg:    cm.GetString("encr-alg"),
                        IntegAlg:  cm.GetString("integ-alg"),
                        BytesIn:   long.TryParse(cm.GetString("bytes-in"),    out var bi) ? bi : 0,
                        BytesOut:  long.TryParse(cm.GetString("bytes-out"),   out var bo) ? bo : 0,
                        PacketsIn: long.TryParse(cm.GetString("packets-in"),  out var pi) ? pi : 0,
                        PacketsOut:long.TryParse(cm.GetString("packets-out"), out var po) ? po : 0,
                        LocalTs:   cm.GetString("local-ts"),
                        RemoteTs:  cm.GetString("remote-ts")
                    ));
                }
            }

            result.Add(new ViciIkeSa(
                Name:          saName,
                UniqueId:      saMsg.GetString("uniqueid")     ?? "",
                State:         saMsg.GetString("state")        ?? "",
                LocalHost:     saMsg.GetString("local-host")   ?? "",
                RemoteHost:    saMsg.GetString("remote-host")  ?? "",
                LocalId:       saMsg.GetString("local-id"),
                RemoteId:      saMsg.GetString("remote-id"),
                InitiatorSpi:  saMsg.GetString("initiator-spi"),
                ResponderSpi:  saMsg.GetString("responder-spi"),
                AssignedIp:    saMsg.GetString("local-vips"),
                ChildSas:      childSas,
                PcscfIp:       FindPcscf(saMsg)
            ));
        }

        return result;
    }

    /// <summary>
    /// Looks for the P-CSCF address in a <c>list-sas</c> entry.
    /// strongSwan only reports it when the <c>p-cscf</c> plugin is present, so most builds
    /// return null here.
    /// </summary>
    private static string? FindPcscf(ViciMessage sa)
    {
        foreach (var key in new[] { "pcscf-ip4", "pcscf", "remote-pcscf" })
        {
            var value = sa.GetString(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _stream?.Dispose();
            _tcp?.Dispose();
        }
    }
}
