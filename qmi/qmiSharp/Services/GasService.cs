using System.Buffers.Binary;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

/// <summary>
/// General Application Service (GAS, 0xE8).
/// Manages OEM USB compositions, mode switching, and hardware endpoint configuration.
/// </summary>
public sealed class GasService : IAsyncDisposable
{
    private const ushort GasMsgSetUsbComposition = 0x0203;
    private const ushort GasMsgGetUsbComposition = 0x0204;

    private readonly QmiClient _client;
    private byte _clientId;

    public GasService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.GAS, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<uint> GetUsbCompositionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.GAS, GasMsgGetUsbComposition, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return tlv != null && tlv.Value.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(tlv.Value.Span)
            : 0;
    }

    public async Task SetUsbCompositionAsync(uint compositionId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddUInt32(0x01, compositionId)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.GAS, GasMsgSetUsbComposition, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.GAS, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
