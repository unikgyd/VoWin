using System.Buffers.Binary;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

/// <summary>
/// IP Multimedia Subsystem Presence Service (IMSP, 0x1F).
/// Manages IMS presence publication, subscription, and enabler state.
/// </summary>
public sealed class ImspService : IAsyncDisposable
{
    private const ushort ImspMsgGetEnablerState = 0x0024;

    private readonly QmiClient _client;
    private byte _clientId;

    public ImspService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.IMSP, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<uint> GetEnablerStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.IMSP, ImspMsgGetEnablerState, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x10);
        return tlv != null && tlv.Value.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span)
            : 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.IMSP, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
