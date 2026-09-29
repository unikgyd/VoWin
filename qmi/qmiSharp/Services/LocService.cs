using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public readonly record struct LocPositionInfo(
    uint SessionId,
    double Latitude,
    double Longitude,
    float AltitudeWgs84,
    float HorizontalUncertainty,
    ulong TimestampUtc);

public sealed class LocService : IAsyncDisposable
{
    private const ushort LocMsgRegisterEvents = 0x0021;
    private const ushort LocMsgStart = 0x0022;
    private const ushort LocMsgStop = 0x0023;
    private const ushort LocMsgPositionReportInd = 0x0024;
    private const ushort LocMsgNmeaInd = 0x0026;
    private const ushort LocMsgGetEngineState = 0x002A;

    private readonly QmiClient _client;
    private byte _clientId;

    public event Action<LocPositionInfo>? PositionReported;
    public event Action<string>? NmeaReceived;

    public LocService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.IndicationReceived += OnIndicationReceived;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.LOC, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Registers for GNSS/LOC events such as position reports (0x01) and NMEA sentences (0x02).
    /// </summary>
    public async Task RegisterEventsAsync(ulong eventMask = 0x03, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x01: Event Registration Mask (uint64)
        var tlv = new TlvBuilder()
            .AddUInt64(0x01, eventMask)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.LOC, LocMsgRegisterEvents, tlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Starts a GNSS positioning session.
    /// </summary>
    public async Task StartAsync(uint sessionId = 1, uint minIntervalMs = 1000, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x01: Session ID (uint8)
        // TLV 0x12: Fix Recurrence Type (0x01 = Periodic)
        // TLV 0x13: Min Interval between fixes (uint32)
        var tlv = new TlvBuilder()
            .AddByte(0x01, (byte)sessionId)
            .AddUInt32(0x12, 1) // Periodic
            .AddUInt32(0x13, minIntervalMs)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.LOC, LocMsgStart, tlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Stops an active GNSS positioning session.
    /// </summary>
    public async Task StopAsync(uint sessionId = 1, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var tlv = new TlvBuilder()
            .AddByte(0x01, (byte)sessionId)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.LOC, LocMsgStop, tlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Queries the current GNSS engine state (e.g. 1 = On, 2 = Off).
    /// </summary>
    public async Task<uint> GetEngineStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.LOC, LocMsgGetEngineState, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var stateTlv = resp.GetTlv(0x01) ?? throw new QmiException("GetEngineState response missing TLV 0x01");
        return stateTlv.AsUInt32();
    }

    private void OnIndicationReceived(QmiPacket packet)
    {
        if (packet.KnownServiceType != QmiServiceType.LOC) return;

        if (packet.MessageId == LocMsgPositionReportInd)
        {
            // Parse Position Report TLV 0x01 (SessionId uint8, SessionStatus uint32, Latitude double, Longitude double, Altitude float)
            var tlv = packet.GetTlv(0x01);
            if (tlv != null && tlv.Value.Length >= 29)
            {
                byte sessId = tlv.AsByte(0);
                double lat = BinaryPrimitives.ReadDoubleLittleEndian(tlv.Value.Span.Slice(5, 8));
                double lon = BinaryPrimitives.ReadDoubleLittleEndian(tlv.Value.Span.Slice(13, 8));
                float alt = BinaryPrimitives.ReadSingleLittleEndian(tlv.Value.Span.Slice(21, 4));
                float unc = BinaryPrimitives.ReadSingleLittleEndian(tlv.Value.Span.Slice(25, 4));

                PositionReported?.Invoke(new LocPositionInfo(sessId, lat, lon, alt, unc, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
        }
        else if (packet.MessageId == LocMsgNmeaInd)
        {
            // TLV 0x01: NMEA String
            var tlv = packet.GetTlv(0x01);
            if (tlv != null)
            {
                string nmea = Encoding.ASCII.GetString(tlv.Value.Span).TrimEnd('\0', '\r', '\n');
                NmeaReceived?.Invoke(nmea);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.IndicationReceived -= OnIndicationReceived;
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.LOC, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
