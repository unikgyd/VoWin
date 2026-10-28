using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;

namespace qmiSharp.Services;

public readonly record struct ImsRegistrationStatus(
    QmiImsaImsRegistrationStatus? Status,
    ushort? ErrorCode,
    string? ErrorMessage,
    QmiImsaRegistrationTechnology? RegistrationTechnology)
{
    public bool IsRegistered => Status == QmiImsaImsRegistrationStatus.QMI_IMSA_IMS_REGISTERED;
}

public readonly record struct ImsServicesStatus(
    QmiImsaServiceStatus? Sms,
    QmiImsaServiceStatus? Voice,
    QmiImsaServiceStatus? VideoTelephony)
{
    public bool SmsSupported => Sms == QmiImsaServiceStatus.QMI_IMSA_SERVICE_AVAILABLE;
    public bool VoiceSupported => Voice == QmiImsaServiceStatus.QMI_IMSA_SERVICE_AVAILABLE;
    public bool VideoTelephonySupported => VideoTelephony == QmiImsaServiceStatus.QMI_IMSA_SERVICE_AVAILABLE;
}

public sealed class ImsaService : IAsyncDisposable
{
    private const ushort ImsaMsgGetImsRegistrationStatus = 0x0020;
    private const ushort ImsaMsgGetImsServicesStatus = 0x0021;
    private const ushort ImsaMsgRegistrationStatusInd = 0x0023;
    private const ushort ImsaMsgServiceStatusInd = 0x0024;

    private readonly QmiClient _client;
    private byte _clientId;

    public event Action<ImsRegistrationStatus>? RegistrationStatusChanged;
    public event Action<ImsServicesStatus>? ServicesStatusChanged;

    public ImsaService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.IndicationReceived += OnIndicationReceived;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.IMSA, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the IMS/VoLTE registration status from the baseband.
    /// </summary>
    public async Task<ImsRegistrationStatus> GetRegistrationStatusAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.IMSA, ImsaMsgGetImsRegistrationStatus, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        return ParseRegistrationStatus(resp);
    }

    /// <summary>
    /// Gets the status of IMS sub-services (SMS, Voice/VoLTE, Video).
    /// </summary>
    public async Task<ImsServicesStatus> GetServicesStatusAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.IMSA, ImsaMsgGetImsServicesStatus, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        return ParseServicesStatus(resp);
    }

    internal static ImsRegistrationStatus ParseRegistrationStatus(QmiPacket packet, bool indication = false)
    {
        var status = packet.GetTlv(indication ? (byte)0x11 : (byte)0x12)?.AsUInt32();
        var error = packet.GetTlv(indication ? (byte)0x10 : (byte)0x11)?.AsUInt16();
        var message = packet.GetTlv(indication ? (byte)0x12 : (byte)0x13)?.AsString();
        var technology = packet.GetTlv(indication ? (byte)0x13 : (byte)0x14)?.AsUInt32();
        return new ImsRegistrationStatus(
            status is null ? null : (QmiImsaImsRegistrationStatus)status.Value,
            error, message,
            technology is null ? null : (QmiImsaRegistrationTechnology)technology.Value);
    }

    internal static ImsServicesStatus ParseServicesStatus(QmiPacket packet) => new(
        ReadServiceStatus(packet, 0x10),
        ReadServiceStatus(packet, 0x11),
        ReadServiceStatus(packet, 0x12));

    private static QmiImsaServiceStatus? ReadServiceStatus(QmiPacket packet, byte type)
    {
        var raw = packet.GetTlv(type)?.AsUInt32();
        return raw is null ? null : (QmiImsaServiceStatus)raw.Value;
    }

    private void OnIndicationReceived(QmiPacket packet)
    {
        if (packet.KnownServiceType != QmiServiceType.IMSA) return;

        if (packet.MessageId == ImsaMsgRegistrationStatusInd)
        {
            RegistrationStatusChanged?.Invoke(ParseRegistrationStatus(packet, indication: true));
        }
        else if (packet.MessageId == ImsaMsgServiceStatusInd)
        {
            ServicesStatusChanged?.Invoke(ParseServicesStatus(packet));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.IndicationReceived -= OnIndicationReceived;
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.IMSA, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
