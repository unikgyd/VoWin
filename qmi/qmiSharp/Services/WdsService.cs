using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;

namespace qmiSharp.Services;

public enum WdsPacketServiceStatus : byte
{
    Unknown = 0,
    Disconnected = 1,
    Connected = 2,
    Suspended = 3,
    Authenticating = 4
}

public readonly record struct ChannelRates(
    uint CurrentTxRate,
    uint CurrentRxRate,
    uint MaxTxRate,
    uint MaxRxRate);

public readonly record struct WdsCurrentSettings(
    System.Net.IPAddress? Ipv4Address,
    System.Net.IPAddress? SubnetMask,
    System.Net.IPAddress? Gateway,
    System.Net.IPAddress? PrimaryDns,
    System.Net.IPAddress? SecondaryDns)
{
    public string? ApnName { get; init; }
    public byte? ProfileType { get; init; }
    public byte? ProfileIndex { get; init; }
    public IReadOnlyList<System.Net.IPAddress> PcscfServers { get; init; } = [];
}

public readonly record struct WdsProfileSummary(
    byte ProfileType,
    byte ProfileIndex,
    string ProfileName);

public readonly record struct WdsProfileSettings(
    string ProfileName,
    byte PdpType,
    string ApnName,
    string? Username,
    byte AuthType);

public readonly record struct WdsPacketStatistics(
    ulong TxBytesOk,
    ulong RxBytesOk,
    uint TxPacketsOk,
    uint RxPacketsOk,
    uint TxPacketErrors,
    uint RxPacketErrors);

public sealed class WdsService : IAsyncDisposable
{
    private static readonly TimeSpan StartNetworkTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AbortTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopNetworkTimeout = TimeSpan.FromSeconds(30);
    private readonly QmiClient _client;
    private readonly bool _dedicatedClientId;
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private byte _clientId;
    private uint? _ownedPacketDataHandle;
    private bool _bearerStateIndeterminate;

    public WdsService(QmiClient client, bool dedicatedClientId = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dedicatedClientId = dedicatedClientId;
    }

    public byte ClientId => _clientId;
    public uint? OwnedPacketDataHandle => _ownedPacketDataHandle;
    public bool IsBearerStateIndeterminate => _bearerStateIndeterminate;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = _dedicatedClientId
                ? await _client.AllocateDedicatedClientIdAsync(QmiServiceType.WDS, cancellationToken).ConfigureAwait(false)
                : await _client.AllocateClientIdAsync(QmiServiceType.WDS, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<QmiPacket> SendAsync(WdsMessageId messageId, IEnumerable<QmiTlv>? tlvs,
        CancellationToken cancellationToken) =>
        _client.SendRequestAsync(new QmiPacket(QmiServiceType.WDS, _clientId, 0,
            (ushort)messageId, tlvs), cancellationToken);

    public async Task<WdsPacketServiceStatus> GetPacketServiceStatusAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var req = new QmiPacket(QmiServiceType.WDS, _clientId, 0, (ushort)WdsMessageId.GetPacketServiceStatus);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        if (tlv == null || tlv.Value.Length < 1)
        {
            return WdsPacketServiceStatus.Unknown;
        }

        return (WdsPacketServiceStatus)tlv.AsByte(0);
    }

    public async Task<ChannelRates> GetCurrentChannelRateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var req = new QmiPacket(QmiServiceType.WDS, _clientId, 0, (ushort)WdsMessageId.GetChannelRates);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        if (tlv == null || tlv.Value.Length < 16)
        {
            return new ChannelRates(0, 0, 0, 0);
        }

        uint tx = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span[..4]);
        uint rx = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span[4..8]);
        uint maxTx = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span[8..12]);
        uint maxRx = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span[12..16]);

        return new ChannelRates(tx, rx, maxTx, maxRx);
    }

    public async Task<uint> StartNetworkInterfaceAsync(string apn, byte ipFamily = 4, CancellationToken cancellationToken = default)
    {
        await _networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bearerStateIndeterminate)
                throw new QmiException("WDS bearer state is indeterminate; do not start another call on this CID.");
            if (_ownedPacketDataHandle is not null)
                throw new InvalidOperationException("This WDS client already owns a packet-data call.");
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            var builder = new TlvBuilder();
            if (!string.IsNullOrEmpty(apn))
                builder.AddString(0x14, apn);
            builder.AddByte(0x19, ipFamily);

            var req = new QmiPacket(QmiServiceType.WDS, _clientId, 0, (ushort)WdsMessageId.StartNetwork, builder.Build());
            QmiPacket resp;
            try
            {
                resp = await _client.SendRequestAsync(req, StartNetworkTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                if (req.TransactionId == 0) throw;
                // StartNetwork is abortable (WDS 0x0002, TLV 0x01 = original transaction ID).
                try
                {
                    var abort = new QmiPacket(QmiServiceType.WDS, _clientId, 0,
                        (ushort)WdsMessageId.Abort,
                        [QmiTlv.FromUInt16(0x01, req.TransactionId)]);
                    var abortResponse = await _client.SendRequestAsync(abort, AbortTimeout)
                        .ConfigureAwait(false);
                    abortResponse.CheckResult();
                }
                catch (Exception abortError)
                {
                    _bearerStateIndeterminate = true;
                    throw new QmiException("WDS StartNetwork was interrupted but abort was not acknowledged; bearer state is indeterminate.",
                        new AggregateException(ex, abortError));
                }
                throw;
            }
            resp.CheckResult();

            var handleTlv = resp.GetTlv(0x01);
            if (handleTlv is null || handleTlv.Value.Length != 4)
            {
                _bearerStateIndeterminate = true;
                throw new QmiException("WDS StartNetwork succeeded without a valid handle; bearer state is indeterminate.");
            }
            var handle = handleTlv.AsUInt32();
            _ownedPacketDataHandle = handle;
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(AbortTimeout);
                    await StopNetworkCoreAsync(handle, cleanup.Token).ConfigureAwait(false);
                    _ownedPacketDataHandle = null;
                }
                catch (Exception stopError)
                {
                    throw new QmiException("WDS StartNetwork completed during cancellation but StopNetwork failed; bearer is still owned by this CID.",
                        stopError);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            return handle;
        }
        finally { _networkGate.Release(); }
    }

    public async Task StopNetworkInterfaceAsync(uint packetDataHandle, CancellationToken cancellationToken = default)
    {
        await _networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ownedPacketDataHandle != packetDataHandle)
                throw new InvalidOperationException("This WDS client does not own the requested packet-data handle.");
            await StopNetworkCoreAsync(packetDataHandle, cancellationToken).ConfigureAwait(false);
            _ownedPacketDataHandle = null;
        }
        finally { _networkGate.Release(); }
    }

    private async Task StopNetworkCoreAsync(uint packetDataHandle, CancellationToken cancellationToken)
    {
        var req = new QmiPacket(QmiServiceType.WDS, _clientId, 0, (ushort)WdsMessageId.StopNetwork, new[]
        {
            QmiTlv.FromUInt32(0x01, packetDataHandle)
        });

        var resp = await _client.SendRequestAsync(req, StopNetworkTimeout, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async Task<WdsCurrentSettings> GetCurrentSettingsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var req = new QmiPacket(QmiServiceType.WDS, _clientId, 0, (ushort)WdsMessageId.GetCurrentSettings, new[]
        {
            // Profile ID, APN, DNS, IPv4 address, gateway, P-CSCF address/list.
            QmiTlv.FromUInt32(0x10, 0x00000F19)
        });
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        return ParseCurrentSettings(resp);
    }

    internal static WdsCurrentSettings ParseCurrentSettings(QmiPacket resp)
    {
        ArgumentNullException.ThrowIfNull(resp);

        System.Net.IPAddress? ip = ParseIp(resp.GetTlv(0x1E));
        System.Net.IPAddress? subnet = ParseIp(resp.GetTlv(0x21));
        System.Net.IPAddress? gw = ParseIp(resp.GetTlv(0x20));
        System.Net.IPAddress? dns1 = ParseIp(resp.GetTlv(0x15));
        System.Net.IPAddress? dns2 = ParseIp(resp.GetTlv(0x16));

        var profile = resp.GetTlv(0x1F);
        if (profile is not null && profile.Value.Length != 2)
            throw new FormatException("WDS current-settings profile ID is malformed.");
        var pcscf = resp.GetTlv(0x23);
        var pcscfServers = new List<System.Net.IPAddress>();
        if (pcscf is not null)
        {
            var value = pcscf.Value.Span;
            if (value.Length < 1 || value.Length != 1 + value[0] * 4)
                throw new FormatException("WDS current-settings P-CSCF list is malformed.");
            for (var offset = 1; offset < value.Length; offset += 4)
                pcscfServers.Add(ParseIp(new QmiTlv(0x23, value.Slice(offset, 4).ToArray()))!);
        }
        return new WdsCurrentSettings(ip, subnet, gw, dns1, dns2)
        {
            ApnName = resp.GetTlv(0x14)?.AsString(),
            ProfileType = profile?.AsByte(0),
            ProfileIndex = profile?.AsByte(1),
            PcscfServers = pcscfServers
        };
    }

    /// <summary>
    /// Gets list of APN profiles (0x002A).
    /// </summary>
    public async Task<IReadOnlyList<WdsProfileSummary>> GetProfileListAsync(byte profileType = 0, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder().AddByte(0x10, profileType).Build();
        var resp = await SendAsync(WdsMessageId.GetProfileList, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        return ParseProfileList(resp.GetTlv(0x01));
    }

    internal static IReadOnlyList<WdsProfileSummary> ParseProfileList(QmiTlv? tlv)
    {
        if (tlv is null)
            throw new FormatException("WDS profile list response is missing TLV 0x01.");
        var span = tlv.Value.Span;
        if (span.Length == 0)
            throw new FormatException("WDS profile list has no count byte.");
        var count = span[0];
        var offset = 1;
        var list = new List<WdsProfileSummary>(count);
        for (var i = 0; i < count; i++)
        {
            if (offset + 3 > span.Length)
                throw new FormatException("WDS profile list entry is truncated.");
            var type = span[offset++];
            var index = span[offset++];
            var nameLength = span[offset++];
            if (offset + nameLength > span.Length)
                throw new FormatException("WDS profile list name is truncated.");
            var name = Encoding.ASCII.GetString(span.Slice(offset, nameLength));
            offset += nameLength;
            list.Add(new WdsProfileSummary(type, index, name));
        }
        if (offset != span.Length)
            throw new FormatException("WDS profile list contains trailing bytes.");
        return list;
    }

    /// <summary>
    /// Gets settings for a specific APN profile (0x002B).
    /// </summary>
    public async Task<WdsProfileSettings> GetProfileSettingsAsync(byte profileType, byte profileIndex, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, new[] { profileType, profileIndex })
            .Build();

        var resp = await SendAsync(WdsMessageId.GetProfileSettings, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        string profileName = resp.GetTlv(0x10)?.AsString() ?? string.Empty;
        byte pdpType = resp.GetTlv(0x11)?.AsByte(0) ?? (byte)0;
        string apnName = resp.GetTlv(0x14)?.AsString() ?? string.Empty;
        string? username = resp.GetTlv(0x1B)?.AsString();
        byte authType = resp.GetTlv(0x1D)?.AsByte(0) ?? (byte)0;

        return new WdsProfileSettings(profileName, pdpType, apnName, username, authType);
    }

    /// <summary>
    /// Creates a new APN profile (0x0027).
    /// </summary>
    public async Task<(byte ProfileType, byte ProfileIndex)> CreateProfileAsync(
        byte profileType,
        string apn,
        byte pdpType = 0,
        string? profileName = null,
        string? username = null,
        string? password = null,
        byte authType = 0,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var builder = new TlvBuilder()
            .AddByte(0x01, profileType)
            .AddByte(0x11, pdpType)
            .AddString(0x14, apn);

        if (!string.IsNullOrEmpty(profileName))
            builder.AddString(0x10, profileName);
        if (!string.IsNullOrEmpty(username))
            builder.AddString(0x1B, username);
        if (!string.IsNullOrEmpty(password))
            builder.AddString(0x1C, password);
        if (authType > 0)
            builder.AddByte(0x1D, authType);

        var resp = await SendAsync(WdsMessageId.CreateProfile, builder.Build(), cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01) ?? throw new QmiException("CreateProfile missing TLV 0x01");
        return (tlv.AsByte(0), tlv.AsByte(1));
    }

    /// <summary>
    /// Modifies an existing APN profile (0x0028).
    /// </summary>
    public async Task ModifyProfileAsync(
        byte profileType,
        byte profileIndex,
        string apn,
        byte? pdpType = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var builder = new TlvBuilder()
            .AddBytes(0x01, new[] { profileType, profileIndex })
            .AddString(0x14, apn);

        if (pdpType.HasValue)
            builder.AddByte(0x11, pdpType.Value);
        if (!string.IsNullOrEmpty(profileName))
            builder.AddString(0x10, profileName);

        var resp = await SendAsync(WdsMessageId.ModifyProfile, builder.Build(), cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Deletes an APN profile (0x0029).
    /// </summary>
    public async Task DeleteProfileAsync(byte profileType, byte profileIndex, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder().AddBytes(0x01, new[] { profileType, profileIndex }).Build();
        var resp = await SendAsync(WdsMessageId.DeleteProfile, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Retrieves packet counters and transfer statistics (0x0024).
    /// </summary>
    public async Task<WdsPacketStatistics> GetPacketStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Stats mask: Tx/Rx bytes, Tx/Rx packets, errors (0xFF)
        var reqTlv = new TlvBuilder().AddUInt32(0x01, 0x000000FF).Build();
        var resp = await SendAsync(WdsMessageId.GetPacketStatistics, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        uint txPkts = resp.GetTlv(0x10)?.AsUInt32() ?? 0;
        uint rxPkts = resp.GetTlv(0x11)?.AsUInt32() ?? 0;
        uint txErr = resp.GetTlv(0x12)?.AsUInt32() ?? 0;
        uint rxErr = resp.GetTlv(0x13)?.AsUInt32() ?? 0;
        ulong txBytes = resp.GetTlv(0x19)?.Value.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(resp.GetTlv(0x19)!.Value.Span) : (resp.GetTlv(0x14)?.AsUInt32() ?? 0);
        ulong rxBytes = resp.GetTlv(0x1A)?.Value.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(resp.GetTlv(0x1A)!.Value.Span) : (resp.GetTlv(0x15)?.AsUInt32() ?? 0);

        return new WdsPacketStatistics(txBytes, rxBytes, txPkts, rxPkts, txErr, rxErr);
    }

    /// <summary>
    /// Gets current data bearer technology (0x0037).
    /// </summary>
    public async Task<byte> GetDataBearerTechnologyAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await SendAsync(WdsMessageId.GetDataBearerTechnology, null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        return resp.GetTlv(0x01)?.AsByte(0) ?? (byte)0;
    }

    /// <summary>
    /// Sets IP family preference (IPv4=4, IPv6=6) (0x004D).
    /// </summary>
    public async Task SetIpFamilyAsync(byte ipFamily, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder().AddByte(0x01, ipFamily).Build();
        var resp = await SendAsync(WdsMessageId.SetIpFamily, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Puts the active data connection into dormant state (0x0025).
    /// </summary>
    public async Task GoDormantAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await SendAsync(WdsMessageId.GoDormant, null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Brings the active data connection from dormancy back to active state (0x0026).
    /// </summary>
    public async Task GoActiveAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await SendAsync(WdsMessageId.GoActive, null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Resets the WDS service state (0x0000).
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await SendAsync(WdsMessageId.Reset, null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    private static System.Net.IPAddress? ParseIp(QmiTlv? tlv)
    {
        if (tlv == null || tlv.Value.Length < 4) return null;
        // QMI encodes IPv4 addresses as a little-endian guint32. IPAddress expects
        // bytes in network order.
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span[..4]);
        return new System.Net.IPAddress(new byte[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ownedPacketDataHandle is { } handle)
            {
                using var cleanup = new CancellationTokenSource(StopNetworkTimeout);
                await StopNetworkCoreAsync(handle, cleanup.Token).ConfigureAwait(false);
                _ownedPacketDataHandle = null;
            }
            if (_bearerStateIndeterminate)
                throw new QmiException("WDS bearer state is indeterminate; refusing to release its CID.");
            if (_clientId != 0)
            {
                try
                {
                    if (_dedicatedClientId)
                        await _client.ReleaseDedicatedClientIdAsync(QmiServiceType.WDS, _clientId).ConfigureAwait(false);
                    else
                        await _client.ReleaseClientIdAsync(QmiServiceType.WDS, _clientId).ConfigureAwait(false);
                    _clientId = 0;
                }
                catch when (!_dedicatedClientId)
                {
                    // Preserve the historical best-effort cleanup for shared read-only clients.
                    _clientId = 0;
                }
            }
        }
        finally { _networkGate.Release(); }
    }
}
