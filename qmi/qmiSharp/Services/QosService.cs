using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public enum QosFlowStatus : byte
{
    Unknown = 0,
    Activated = 1,
    Suspended = 2,
    Gone = 3
}

public sealed class QosService : IAsyncDisposable
{
    private const ushort QosMsgGetFlowStatus = 0x0021;
    private const ushort QosMsgGetNetworkStatus = 0x0022;

    private readonly QmiClient _client;
    private byte _clientId;

    public QosService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.QOS, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks whether the network supports Quality of Service (QoS) dedicated bearers.
    /// </summary>
    public async Task<bool> GetNetworkStatusAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.QOS, QosMsgGetNetworkStatus, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return (tlv?.AsByte() ?? 0) != 0;
    }

    /// <summary>
    /// Queries the flow status of a specific QoS identifier.
    /// </summary>
    public async Task<QosFlowStatus> GetFlowStatusAsync(uint qosId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddUInt32(0x01, qosId)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.QOS, QosMsgGetFlowStatus, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        // TLV 0x01: Flow Status (uint8)
        var tlv = resp.GetTlv(0x01);
        return (QosFlowStatus)(tlv?.AsByte() ?? 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.QOS, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
