using System.Buffers.Binary;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public readonly record struct PdsServiceState(
    bool Enabled,
    byte TrackingStatus);

/// <summary>
/// Position Determination Service (PDS, 0x06) - Legacy Qualcomm GPS subsystem.
/// Predecessor to the modern LOC service (0x10).
/// </summary>
public sealed class PdsService : IAsyncDisposable
{
    private const ushort PdsMsgReset = 0x0000;
    private const ushort PdsMsgSetEventReport = 0x0001;
    private const ushort PdsMsgGetGpsServiceState = 0x0020;
    private const ushort PdsMsgSetGpsServiceState = 0x0021;
    private const ushort PdsMsgGetAutoTrackingState = 0x0030;
    private const ushort PdsMsgSetAutoTrackingState = 0x0031;

    private readonly QmiClient _client;
    private byte _clientId;

    public PdsService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.PDS, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PdsServiceState> GetGpsServiceStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.PDS, PdsMsgGetGpsServiceState, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var stateTlv = resp.GetTlv(0x01);
        var trackingTlv = resp.GetTlv(0x10);

        bool enabled = stateTlv != null && stateTlv.AsByte() == 1;
        byte tracking = trackingTlv != null ? trackingTlv.AsByte() : (byte)0;

        return new PdsServiceState(enabled, tracking);
    }

    public async Task SetGpsServiceStateAsync(bool enable, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddByte(0x01, enable ? (byte)1 : (byte)0)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.PDS, PdsMsgSetGpsServiceState, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async Task<bool> GetAutoTrackingStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.PDS, PdsMsgGetAutoTrackingState, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return tlv != null && tlv.AsByte() == 1;
    }

    public async Task SetAutoTrackingStateAsync(bool autoTrack, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddByte(0x01, autoTrack ? (byte)1 : (byte)0)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.PDS, PdsMsgSetAutoTrackingState, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.PDS, PdsMsgReset, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.PDS, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
