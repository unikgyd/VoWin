using System.Buffers.Binary;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

/// <summary>
/// IP Multimedia Subsystem Data Connectivity Manager (IMSDCM, 0x302).
/// Manages PDP connection triggers and data channel handshakes for IMS.
/// </summary>
public sealed class ImsdcmService : IAsyncDisposable
{
    private const ushort ImsdcmMsgPdpActivate = 0x0020;

    private readonly QmiClient _client;
    private byte _clientId;

    public ImsdcmService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.IMSDCM, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ActivatePdpAsync(string apn, uint apnType = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apn);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x01: APN string + parameters
        var reqTlv = new TlvBuilder()
            .AddString(0x01, apn)
            .AddUInt32(0x10, apnType)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.IMSDCM, ImsdcmMsgPdpActivate, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.IMSDCM, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
