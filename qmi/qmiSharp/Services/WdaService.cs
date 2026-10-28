using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public enum WdaLinkProtocol : uint
{
    Ethernet = 1,
    RawIP = 2
}

public readonly record struct WdaDataFormat(WdaLinkProtocol LinkProtocol, uint UlAggregationSize, uint DlAggregationSize)
{
    public uint? UplinkAggregationProtocol { get; init; }
    public uint? DownlinkAggregationProtocol { get; init; }
}

public sealed class WdaService : IAsyncDisposable
{
    private readonly QmiClient _client;
    private byte _clientId;

    public WdaService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.WDA, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<WdaDataFormat> GetDataFormatAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // WDA GET_DATA_FORMAT (0x0021)
        var req = new QmiPacket(QmiServiceType.WDA, _clientId, 0, 0x0021);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return ParseDataFormat(resp, WdaLinkProtocol.RawIP, 0, 0);
    }

    public async Task<WdaDataFormat> SetDataFormatAsync(
        WdaLinkProtocol linkProtocol = WdaLinkProtocol.RawIP,
        uint? ulAggregationSize = null,
        uint? dlAggregationSize = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // WDA SET_DATA_FORMAT (0x0020)
        var builder = new TlvBuilder()
            .AddUInt32(0x11, (uint)linkProtocol);

        if (ulAggregationSize.HasValue)
        {
            builder.AddUInt32(0x1C, ulAggregationSize.Value);
        }
        if (dlAggregationSize.HasValue)
        {
            builder.AddUInt32(0x16, dlAggregationSize.Value);
        }

        var resp = await _client.SendMessageAsync(QmiServiceType.WDA, 0x0020, builder.Build(), cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
        return ParseDataFormat(resp, linkProtocol, ulAggregationSize.GetValueOrDefault(),
            dlAggregationSize.GetValueOrDefault());
    }

    internal static WdaDataFormat ParseDataFormat(QmiPacket response, WdaLinkProtocol defaultProtocol,
        uint defaultUlSize, uint defaultDlSize)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new WdaDataFormat(
            (WdaLinkProtocol)(response.GetTlv(0x11)?.AsUInt32() ?? (uint)defaultProtocol),
            response.GetTlv(0x18)?.AsUInt32() ?? defaultUlSize,
            response.GetTlv(0x16)?.AsUInt32() ?? defaultDlSize)
        {
            UplinkAggregationProtocol = response.GetTlv(0x12)?.AsUInt32(),
            DownlinkAggregationProtocol = response.GetTlv(0x13)?.AsUInt32()
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.WDA, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
