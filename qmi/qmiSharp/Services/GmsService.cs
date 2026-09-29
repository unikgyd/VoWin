using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

/// <summary>
/// General Modem Service (GMS, 0xE7).
/// Provides OEM manufacturing diagnostic, loopback, and value testing commands.
/// </summary>
public sealed class GmsService : IAsyncDisposable
{
    private const ushort GmsMsgTestSetValue = 0x0F00;
    private const ushort GmsMsgTestGetValue = 0x0F01;

    private readonly QmiClient _client;
    private byte _clientId;

    public GmsService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.GMS, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<byte> TestGetValueAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.GMS, GmsMsgTestGetValue, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x03) ?? resp.GetTlv(0x01);
        return tlv != null ? tlv.AsByte() : (byte)0;
    }

    public async Task TestSetValueAsync(byte testValue, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddByte(0x01, testValue)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.GMS, GmsMsgTestSetValue, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.GMS, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
