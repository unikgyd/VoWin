using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public enum OmaSessionState : byte
{
    Complete = 0,
    InProgress = 1,
    Failed = 2,
    Retrying = 3
}

public readonly record struct OmaSessionInfo(
    OmaSessionState State,
    uint SessionType,
    uint FailReason);

public sealed class OmaService : IAsyncDisposable
{
    private const ushort OmaMsgStartSession = 0x0022;
    private const ushort OmaMsgCancelSession = 0x0023;
    private const ushort OmaMsgGetSessionInfo = 0x0024;

    private readonly QmiClient _client;
    private byte _clientId;

    public OmaService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.OMA, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets current OMA-DM session status.
    /// </summary>
    public async Task<OmaSessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.OMA, OmaMsgGetSessionInfo, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        // TLV 0x01: Session Info (SessionState uint8)
        // TLV 0x10: Session Type (uint32)
        // TLV 0x11: Fail Reason (uint32)
        var state = (OmaSessionState)(resp.GetTlv(0x01)?.AsByte() ?? 0);
        uint type = resp.GetTlv(0x10)?.AsUInt32() ?? 0;
        uint reason = resp.GetTlv(0x11)?.AsUInt32() ?? 0;

        return new OmaSessionInfo(state, type, reason);
    }

    /// <summary>
    /// Starts an OMA-DM session (e.g. 0 = Device Configure, 1 = FUMO Firmware Update).
    /// </summary>
    public async Task StartSessionAsync(uint sessionType = 0, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddUInt32(0x01, sessionType)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.OMA, OmaMsgStartSession, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Cancels any active OMA-DM session.
    /// </summary>
    public async Task CancelSessionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.OMA, OmaMsgCancelSession, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.OMA, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
