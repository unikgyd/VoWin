using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;

namespace qmiSharp.Services;

public readonly record struct SignalStrength(int Rssi, int? Rsrq, double? Snr, int? Rsrp)
{
    public byte RadioInterface { get; init; }
}

public enum NasRegistrationState : byte
{
    NotRegistered = 0,
    Registered = 1,
    Searching = 2,
    RegistrationDenied = 3,
    Unknown = 4
}

public readonly record struct ServingSystemInfo(
    NasRegistrationState RegistrationState,
    bool CsAttached,
    bool PsAttached,
    byte RadioInterface,
    string? PlmnName,
    ushort? Mcc,
    ushort? Mnc,
    bool Roaming);

public readonly record struct SysInfo(
    uint CellId,
    ushort Tac,
    bool HasLteInfo,
    byte ServiceDomain);

public readonly record struct OperatorNameInfo(
    string? LongName,
    string? ShortName);

public readonly record struct NasScannedNetwork(
    ushort Mcc,
    ushort Mnc,
    byte Status,
    string Description,
    byte Rat);

public readonly record struct NasSystemSelectionPreference(
    ushort ModePreference,
    ulong BandPreference,
    ushort RoamingPreference,
    ulong LteBandPreference);

public readonly record struct NasCellLocationInfo(
    uint CellId,
    ushort Tac,
    ushort Pci,
    ushort Earfcn);

public readonly record struct NasRfBandInfo(
    byte RadioInterface,
    ushort ActiveBand,
    uint Channel);

public sealed class NasService : IAsyncDisposable
{
    private readonly QmiClient _client;
    private byte _clientId;

    public NasService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.NAS, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SignalStrength> GetSignalStrengthAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.NAS, _clientId, 0, (ushort)NasMessageId.GetSignalStrength);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        return ParseSignalStrength(resp);
    }

    internal static SignalStrength ParseSignalStrength(QmiPacket resp)
    {
        ArgumentNullException.ThrowIfNull(resp);

        var strength = resp.GetTlv(0x01);
        if (strength is null || strength.Value.Length < 2)
            throw new FormatException("NAS signal-strength response omitted its strength and radio interface.");
        int rssi = strength.AsSByte(0);
        byte radioInterface = strength.AsByte(1);
        int? rsrq = null;
        double? snr = null;
        int? rsrp = null;

        var tlv16 = resp.GetTlv(0x16);
        if (tlv16 != null && tlv16.Value.Length >= 2)
        {
            rsrq = tlv16.AsSByte(0);
        }

        var tlv17 = resp.GetTlv(0x17);
        if (tlv17 != null && tlv17.Value.Length >= 2)
        {
            snr = tlv17.AsInt16(0) * 0.1;
        }

        var tlv18 = resp.GetTlv(0x18);
        if (tlv18 != null && tlv18.Value.Length >= 2)
        {
            rsrp = tlv18.AsInt16(0);
        }

        return new SignalStrength(rssi, rsrq, snr, rsrp) { RadioInterface = radioInterface };
    }

    public async Task<ServingSystemInfo> GetServingSystemAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.NAS, _clientId, 0, (ushort)NasMessageId.GetServingSystem);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        return ParseServingSystem(resp);
    }

    internal static ServingSystemInfo ParseServingSystem(QmiPacket resp)
    {
        ArgumentNullException.ThrowIfNull(resp);

        var tlv01 = resp.GetTlv(0x01);
        if (tlv01 is null || tlv01.Value.Length < 5)
            throw new FormatException("NAS serving-system response omitted its registration state.");
        var radioCount = tlv01.AsByte(4);
        if (tlv01.Value.Length < 5 + radioCount)
            throw new FormatException("NAS serving-system radio-interface list is truncated.");
        var regState = (NasRegistrationState)tlv01.AsByte(0);
        if (!Enum.IsDefined(regState))
            throw new FormatException("NAS serving-system response has an unknown registration state.");
        bool csAttached = tlv01.AsByte(1) == 1;
        bool psAttached = tlv01.AsByte(2) == 1;
        byte radioInterface = radioCount > 0 ? tlv01.AsByte(5) : (byte)0;
        bool roaming = false;
        string? plmnName = null;
        ushort? mcc = null;
        ushort? mnc = null;

        var tlv10 = resp.GetTlv(0x10);
        if (tlv10 != null && tlv10.Value.Length >= 1)
        {
            // QMI Roaming Indicator: 0 = Roaming, 1 = Home/Off
            roaming = tlv10.AsByte(0) == 0;
        }

        var tlv12 = resp.GetTlv(0x12);
        if (tlv12 != null)
        {
            if (tlv12.Value.Length < 5)
                throw new FormatException("NAS current-PLMN response is truncated.");
            mcc = BinaryPrimitives.ReadUInt16LittleEndian(tlv12.Value.Span[..2]);
            mnc = BinaryPrimitives.ReadUInt16LittleEndian(tlv12.Value.Span[2..4]);
            byte nameLen = tlv12.AsByte(4);
            if (tlv12.Value.Length < 5 + nameLen)
                throw new FormatException("NAS current-PLMN name is truncated.");
            plmnName = DecodePackedGsm7Name(tlv12.Value.Span.Slice(5, nameLen));
        }

        return new ServingSystemInfo(regState, csAttached, psAttached, radioInterface, plmnName, mcc, mnc, roaming);
    }

    private static string? DecodePackedGsm7Name(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        var count = bytes.Length * 8 / 7;
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            var bit = i * 7;
            var offset = bit / 8;
            var shift = bit % 8;
            var septet = (bytes[offset] >> shift) & 0x7F;
            if (shift > 1 && offset + 1 < bytes.Length)
                septet |= (bytes[offset + 1] << (8 - shift)) & 0x7F;
            chars[i] = septet switch
            {
                0x00 => '@',
                0x0D => '\r',
                >= 0x20 and <= 0x7A => (char)septet,
                _ => '\0'
            };
            if (chars[i] == '\0') return null; // Do not invent an operator name.
        }
        return new string(chars).TrimEnd('\r').Trim();
    }

    public async Task<SysInfo> GetSysInfoAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.NAS, _clientId, 0, 0x004D);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        uint cellId = 0;
        ushort tac = 0;
        bool hasLteInfo = false;
        byte serviceDomain = 0;

        var tlv13 = resp.GetTlv(0x13);
        if (tlv13 != null && tlv13.Value.Length >= 1)
        {
            serviceDomain = tlv13.AsByte(0);
        }

        var tlv21 = resp.GetTlv(0x21);
        if (tlv21 != null && tlv21.Value.Length >= 6)
        {
            hasLteInfo = true;
            cellId = BinaryPrimitives.ReadUInt32LittleEndian(tlv21.Value.Span[..4]);
            tac = BinaryPrimitives.ReadUInt16LittleEndian(tlv21.Value.Span[4..6]);
        }

        return new SysInfo(cellId, tac, hasLteInfo, serviceDomain);
    }

    public async Task<OperatorNameInfo> GetOperatorNameAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new QmiPacket(QmiServiceType.NAS, _clientId, 0, 0x0039);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        string? longName = null;
        string? shortName = null;

        var tlv10 = resp.GetTlv(0x10);
        if (tlv10 != null && tlv10.Value.Length >= 1)
        {
            byte len = tlv10.AsByte(0);
            if (tlv10.Value.Length >= 1 + len)
            {
                longName = Encoding.UTF8.GetString(tlv10.Value.Span.Slice(1, len));
            }
        }

        var tlv11 = resp.GetTlv(0x11);
        if (tlv11 != null && tlv11.Value.Length >= 1)
        {
            byte len = tlv11.AsByte(0);
            if (tlv11.Value.Length >= 1 + len)
            {
                shortName = Encoding.UTF8.GetString(tlv11.Value.Span.Slice(1, len));
            }
        }

        return new OperatorNameInfo(longName, shortName);
    }

    /// <summary>
    /// Performs an active network scan for visible mobile operators (0x0021).
    /// </summary>
    public async Task<IReadOnlyList<NasScannedNetwork>> PerformNetworkScanAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.NetworkScan, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var list = new List<NasScannedNetwork>();
        var tlv10 = resp.GetTlv(0x10);
        if (tlv10 != null && tlv10.Value.Length >= 2)
        {
            var span = tlv10.Value.Span;
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(span[..2]);
            int offset = 2;
            for (int i = 0; i < count && offset < span.Length; i++)
            {
                if (offset + 5 > span.Length) break;
                ushort mcc = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                ushort mnc = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 2, 2));
                byte status = span[offset + 4];
                offset += 5;

                byte descLen = offset < span.Length ? span[offset++] : (byte)0;
                string desc = string.Empty;
                if (descLen > 0 && offset + descLen <= span.Length)
                {
                    desc = Encoding.UTF8.GetString(span.Slice(offset, descLen));
                    offset += descLen;
                }
                list.Add(new NasScannedNetwork(mcc, mnc, status, desc, 0));
            }
        }
        return list;
    }

    /// <summary>
    /// Gets current system selection preference (mode, bands, roaming) (0x0034).
    /// </summary>
    public async Task<NasSystemSelectionPreference> GetSystemSelectionPreferenceAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.GetSystemSelectionPreference, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        ushort modePref = resp.GetTlv(0x11)?.AsUInt16() ?? 0;
        ulong bandPref = resp.GetTlv(0x12)?.Value.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(resp.GetTlv(0x12)!.Value.Span) : 0;
        ushort roamPref = resp.GetTlv(0x14)?.AsUInt16() ?? 0;
        ulong lteBandPref = resp.GetTlv(0x15)?.Value.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(resp.GetTlv(0x15)!.Value.Span) : 0;

        return new NasSystemSelectionPreference(modePref, bandPref, roamPref, lteBandPref);
    }

    /// <summary>
    /// Sets system selection preference (lock to LTE/GSM/WCDMA or bands) (0x0033).
    /// </summary>
    public async Task SetSystemSelectionPreferenceAsync(ushort modePreference, ulong? lteBandPreference = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var builder = new TlvBuilder().AddUInt16(0x11, modePreference);
        if (lteBandPreference.HasValue)
        {
            var bandBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bandBytes, lteBandPreference.Value);
            builder.AddBytes(0x15, bandBytes);
        }

        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.SetSystemSelectionPreference, builder.Build(), cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Gets cell location information (Cell ID, TAC, PCI, EARFCN) (0x0043).
    /// </summary>
    public async Task<NasCellLocationInfo> GetCellLocationInfoAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.GetCellLocationInfo, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv12 = resp.GetTlv(0x12); // LTE info
        if (tlv12 != null && tlv12.Value.Length >= 10)
        {
            var span = tlv12.Value.Span;
            uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
            ushort pci = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2));
            ushort tac = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
            ushort earfcn = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2));
            return new NasCellLocationInfo(cellId, tac, pci, earfcn);
        }

        return new NasCellLocationInfo(0, 0, 0, 0);
    }

    /// <summary>
    /// Gets active RF band information (0x0031).
    /// </summary>
    public async Task<IReadOnlyList<NasRfBandInfo>> GetRfBandInfoAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.GetRfBandInformation, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var list = new List<NasRfBandInfo>();
        var tlv01 = resp.GetTlv(0x01);
        if (tlv01 != null && tlv01.Value.Length >= 1)
        {
            var span = tlv01.Value.Span;
            byte count = span[0];
            int offset = 1;
            for (int i = 0; i < count && offset + 7 <= span.Length; i++)
            {
                byte iface = span[offset];
                ushort band = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 1, 2));
                uint chan = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 3, 4));
                list.Add(new NasRfBandInfo(iface, band, chan));
                offset += 7;
            }
        }
        return list;
    }

    /// <summary>
    /// Gets Home Network PLMN and carrier name (0x0025).
    /// </summary>
    public async Task<(ushort Mcc, ushort Mnc, string Name)> GetHomeNetworkAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.GetHomeNetwork, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv01 = resp.GetTlv(0x01);
        ushort mcc = 0, mnc = 0;
        string name = string.Empty;

        if (tlv01 != null && tlv01.Value.Length >= 4)
        {
            mcc = BinaryPrimitives.ReadUInt16LittleEndian(tlv01.Value.Span[..2]);
            mnc = BinaryPrimitives.ReadUInt16LittleEndian(tlv01.Value.Span[2..4]);
            if (tlv01.Value.Length > 5)
            {
                name = Encoding.UTF8.GetString(tlv01.Value.Span.Slice(5));
            }
        }

        return (mcc, mnc, name);
    }

    /// <summary>
    /// Initiates network registration (automatic or manual) (0x0022).
    /// </summary>
    public async Task InitiateNetworkRegisterAsync(bool automatic, ushort? mcc = null, ushort? mnc = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var builder = new TlvBuilder();
        if (automatic)
        {
            builder.AddByte(0x01, 0x01); // 1 = Automatic
        }
        else if (mcc.HasValue && mnc.HasValue)
        {
            builder.AddByte(0x01, 0x02); // 2 = Manual
            var plmn = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(plmn.AsSpan(0, 2), mcc.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(plmn.AsSpan(2, 2), mnc.Value);
            builder.AddBytes(0x10, plmn);
        }

        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.InitiateNetworkRegister, builder.Build(), cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Attaches or detaches the packet-switched network (0x0023).
    /// </summary>
    public async Task AttachDetachAsync(bool attach, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder().AddByte(0x01, attach ? (byte)1 : (byte)2).Build();
        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.AttachDetach, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Resets NAS state (0x0000).
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.NAS, (ushort)NasMessageId.Reset, (IEnumerable<QmiTlv>?)null, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.NAS, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
