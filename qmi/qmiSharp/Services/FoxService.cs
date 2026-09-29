using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

/// <summary>
/// Foxconn / Fibocomm Modem Service (FOX, 0xE3).
/// Manages vendor firmware version querying and FCC lock authentication.
/// </summary>
public sealed class FoxService : IAsyncDisposable
{
    private const ushort FoxMsgGetFirmwareVersion = 0x555E;
    private const ushort FoxMsgSetFccAuthentication = 0x5571;

    private readonly QmiClient _client;
    private byte _clientId;

    public FoxService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.FOX, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> GetFirmwareVersionAsync(byte versionType = 0, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddByte(0x01, versionType)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.FOX, FoxMsgGetFirmwareVersion, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return tlv != null ? tlv.AsString() : string.Empty;
    }

    public async Task SetFccAuthenticationAsync(string magicString, byte magicNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(magicString);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddString(0x01, magicString)
            .AddByte(0x02, magicNumber)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.FOX, FoxMsgSetFccAuthentication, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.FOX, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
