using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public enum SarRfState : uint
{
    Default = 0,
    State1 = 1,
    State2 = 2,
    State3 = 3,
    State4 = 4
}

public sealed class SarService : IAsyncDisposable
{
    private const ushort SarMsgRfSetState = 0x0001;
    private const ushort SarMsgRfGetState = 0x0002;

    private readonly QmiClient _client;
    private byte _clientId;

    public SarService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.SAR, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the current RF Specific Absorption Rate (SAR) state.
    /// </summary>
    public async Task<SarRfState> GetRfSarStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.SAR, SarMsgRfGetState, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var stateTlv = resp.GetTlv(0x01) ?? throw new QmiException("GetRfSarState missing TLV 0x01");
        return (SarRfState)stateTlv.AsUInt32();
    }

    /// <summary>
    /// Sets the RF SAR power reduction state.
    /// </summary>
    public async Task SetRfSarStateAsync(SarRfState state, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddUInt32(0x01, (uint)state)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.SAR, SarMsgRfSetState, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.SAR, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
