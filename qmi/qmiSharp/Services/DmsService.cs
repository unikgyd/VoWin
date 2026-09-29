using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;

namespace qmiSharp.Services;

public readonly record struct DeviceSerialNumbers(string? ESN, string? IMEI, string? MEID, string? IMEISV);

public readonly record struct DeviceCapabilities(
    uint MaxTxChannelRate,
    uint MaxRxChannelRate,
    byte DataServiceCapability,
    byte SimCapability,
    byte[] RadioInterfaces);

public enum DmsOperatingMode : byte
{
    Online = 0,
    LowPower = 1,
    FactoryTest = 2,
    Offline = 3,
    Reset = 4,
    ShuttingDown = 5,
    PersistentLowPower = 6
}

public readonly record struct DmsPowerState(byte Flags, byte BatteryLevel);

public sealed class DmsService : IAsyncDisposable
{
    private readonly QmiClient _client;
    private byte _clientId;

    public DmsService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.DMS, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> GetManufacturerAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.DMS, _clientId, 0, (ushort)DmsMessageId.GetManufacturer);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return resp.GetTlv(0x01)?.AsString() ?? string.Empty;
    }

    public async Task<string> GetModelAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.DMS, _clientId, 0, (ushort)DmsMessageId.GetModel);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return resp.GetTlv(0x01)?.AsString() ?? string.Empty;
    }

    public async Task<string> GetRevisionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.DMS, _clientId, 0, (ushort)DmsMessageId.GetRevision);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return resp.GetTlv(0x01)?.AsString() ?? string.Empty;
    }

    public async Task<DeviceSerialNumbers> GetSerialNumbersAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.DMS, _clientId, 0, (ushort)DmsMessageId.GetIds);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        string? esn = resp.GetTlv(0x10)?.AsString();
        string? imei = resp.GetTlv(0x11)?.AsString();
        string? meid = resp.GetTlv(0x12)?.AsString();
        string? imeisv = resp.GetTlv(0x13)?.AsString();

        return new DeviceSerialNumbers(esn, imei, meid, imeisv);
    }

    public Task<DeviceSerialNumbers> GetDeviceSerialNumbersAsync(CancellationToken cancellationToken = default)
        => GetSerialNumbersAsync(cancellationToken);

    /// <summary>Reads the active UIM ICCID through DMS without changing card state.</summary>
    public async Task<string> GetIccidAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var response = await _client.SendMessageAsync(QmiServiceType.DMS,
            (ushort)DmsMessageId.UimGetIccid, (IEnumerable<QmiTlv>?)null,
            cancellationToken).ConfigureAwait(false);
        response.CheckResult();
        return response.GetTlv(0x01)?.AsString() ?? string.Empty;
    }

    /// <summary>Reads the active UIM IMSI through DMS without changing card state.</summary>
    public async Task<string> GetImsiAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var response = await _client.SendMessageAsync(QmiServiceType.DMS,
            (ushort)DmsMessageId.UimGetImsi, (IEnumerable<QmiTlv>?)null,
            cancellationToken).ConfigureAwait(false);
        response.CheckResult();
        return response.GetTlv(0x01)?.AsString() ?? string.Empty;
    }

    public async Task<DeviceCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.DMS, _clientId, 0, (ushort)DmsMessageId.GetCapabilities);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        if (tlv == null || tlv.Value.Length < 10)
        {
            return new DeviceCapabilities(0, 0, 0, 0, Array.Empty<byte>());
        }

        uint maxTx = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span[..4]);
        uint maxRx = BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span[4..8]);
        byte dataCap = tlv.AsByte(8);
        byte simCap = tlv.AsByte(9);

        byte ifaceCount = tlv.Value.Length > 10 ? tlv.AsByte(10) : (byte)0;
        byte[] ifaces = Array.Empty<byte>();
        if (ifaceCount > 0 && tlv.Value.Length >= 11 + ifaceCount)
        {
            ifaces = tlv.Value.Span.Slice(11, ifaceCount).ToArray();
        }

        return new DeviceCapabilities(maxTx, maxRx, dataCap, simCap, ifaces);
    }

    public async Task<DmsOperatingMode> GetOperatingModeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.DMS, _clientId, 0, (ushort)DmsMessageId.GetOperatingMode);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return (DmsOperatingMode)(resp.GetTlv(0x01)?.AsByte() ?? 0);
    }

    public async Task SetOperatingModeAsync(DmsOperatingMode mode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.DMS, _clientId, 0, (ushort)DmsMessageId.SetOperatingMode, new[]
        {
            QmiTlv.FromByte(0x01, (byte)mode)
        });
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Gets MSISDN (phone number) from modem (0x0024).
    /// </summary>
    public async Task<string> GetMsisdnAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.GetMsisdn, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return resp.GetTlv(0x01)?.AsString() ?? resp.GetTlv(0x10)?.AsString() ?? string.Empty;
    }

    /// <summary>
    /// Gets power state and battery level (0x0026).
    /// </summary>
    public async Task<DmsPowerState> GetPowerStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.GetPowerState, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        if (tlv != null && tlv.Value.Length >= 2)
        {
            return new DmsPowerState(tlv.AsByte(0), tlv.AsByte(1));
        }
        return new DmsPowerState(0, 0);
    }

    /// <summary>
    /// Gets supported RF band capabilities (0x0045).
    /// </summary>
    public async Task<ulong> GetBandCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.GetBandCapabilities, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return tlv != null && tlv.Value.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(tlv.Value.Span) : 0;
    }

    /// <summary>
    /// Gets full modem software version string (0x0051).
    /// </summary>
    public async Task<string> GetSoftwareVersionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.GetSoftwareVersion, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return resp.GetTlv(0x01)?.AsString() ?? string.Empty;
    }

    /// <summary>
    /// Gets modem PRL version (0x0030).
    /// </summary>
    public async Task<ushort> GetPrlVersionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.GetPrlVersion, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return resp.GetTlv(0x01)?.AsUInt16() ?? 0;
    }

    /// <summary>
    /// Gets activation state (0x0031).
    /// </summary>
    public async Task<ushort> GetActivationStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.GetActivationState, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return resp.GetTlv(0x01)?.AsUInt16() ?? 0;
    }

    /// <summary>
    /// Restores factory defaults (0x003A).
    /// </summary>
    public async Task RestoreFactoryDefaultsAsync(string spc = "000000", CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var reqTlv = new TlvBuilder().AddString(0x01, spc).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.RestoreFactoryDefaults, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Gets user lock state (0x0034).
    /// </summary>
    public async Task<bool> GetUserLockStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.GetUserLockState, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return resp.GetTlv(0x01)?.AsByte(0) == 1;
    }

    /// <summary>
    /// Resets DMS service state (0x0000).
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var resp = await _client.SendMessageAsync(QmiServiceType.DMS, (ushort)DmsMessageId.Reset, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.DMS, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
