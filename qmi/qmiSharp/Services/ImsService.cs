using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public enum ImsVoiceDomainPreference : ushort
{
    CsVoiceOnly = 0,
    CsVoicePreferred = 1,
    ImsVoicePreferred = 2,
    ImsVoiceOnly = 3
}

/// <summary>
/// IP Multimedia Subsystem Settings Service (IMS, 0x12).
/// Configures IMS user agent, voice domain preferences (VoLTE vs CSFB), and SIP stack policy.
/// </summary>
public sealed class ImsService : IAsyncDisposable
{
    private const ushort ImsMsgSetUserAgent = 0x0049;
    private const ushort ImsMsgGetUserAgent = 0x004A;
    private const ushort ImsMsgSetVoiceDomainPreference = 0x004B;
    private const ushort ImsMsgGetVoiceDomainPreference = 0x004C;

    private readonly QmiClient _client;
    private byte _clientId;

    public ImsService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.IMS, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> GetUserAgentAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.IMS, ImsMsgGetUserAgent, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return tlv != null ? tlv.AsString() : string.Empty;
    }

    public async Task SetUserAgentAsync(string userAgent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddString(0x01, userAgent)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.IMS, ImsMsgSetUserAgent, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async Task<ImsVoiceDomainPreference> GetVoiceDomainPreferenceAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.IMS, ImsMsgGetVoiceDomainPreference, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01);
        return tlv != null && tlv.Value.Length >= 2
            ? (ImsVoiceDomainPreference)BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span)
            : ImsVoiceDomainPreference.CsVoicePreferred;
    }

    public async Task SetVoiceDomainPreferenceAsync(ImsVoiceDomainPreference preference, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddUInt16(0x01, (ushort)preference)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.IMS, ImsMsgSetVoiceDomainPreference, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.IMS, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
