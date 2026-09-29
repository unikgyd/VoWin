using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public readonly record struct ImsRegistrationStatus(
    bool IsRegistered,
    ushort ErrorCode,
    string ErrorMessage,
    uint RegistrationTechnology);

public readonly record struct ImsServicesStatus(
    bool SmsSupported,
    bool VoiceSupported,
    bool VideoTelephonySupported);

public sealed class ImsaService : IAsyncDisposable
{
    private const ushort ImsaMsgGetImsRegistrationStatus = 0x0020;
    private const ushort ImsaMsgGetImsServicesStatus = 0x0021;
    private const ushort ImsaMsgRegistrationStatusInd = 0x0022;
    private const ushort ImsaMsgServiceStatusInd = 0x0023;

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

        // TLV 0x01: IMS Registration Status (1 byte: 0 = Not registered, 1 = Registered, 2 = Registering)
        // TLV 0x10: IMS Registration Error Code (2 bytes uint16)
        // TLV 0x11: IMS Registration Error Message (string)
        // TLV 0x12: IMS Registration Technology (4 bytes uint32: 0 = 3GPP LTE, 1 = 3GPP2, 2 = WLAN)
        bool reg = (resp.GetTlv(0x01)?.AsByte() ?? 0) == 1;
        ushort errCode = resp.GetTlv(0x10)?.AsUInt16() ?? 0;
        string errMsg = resp.GetTlv(0x11)?.AsString() ?? string.Empty;
        uint tech = resp.GetTlv(0x12)?.AsUInt32() ?? 0;

        return new ImsRegistrationStatus(reg, errCode, errMsg, tech);
    }

    /// <summary>
    /// Gets the status of IMS sub-services (SMS, Voice/VoLTE, Video).
    /// </summary>
    public async Task<ImsServicesStatus> GetServicesStatusAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.IMSA, ImsaMsgGetImsServicesStatus, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        // TLV 0x10: SMS service status (uint8: 0 = Not supported, 1 = Supported, 2 = Not available)
        // TLV 0x11: Voice service status (uint8)
        // TLV 0x12: Video Telephony service status (uint8)
        bool sms = (resp.GetTlv(0x10)?.AsByte() ?? 0) == 1;
        bool voice = (resp.GetTlv(0x11)?.AsByte() ?? 0) == 1;
        bool vt = (resp.GetTlv(0x12)?.AsByte() ?? 0) == 1;

        return new ImsServicesStatus(sms, voice, vt);
    }

    private void OnIndicationReceived(QmiPacket packet)
    {
        if (packet.KnownServiceType != QmiServiceType.IMSA) return;

        if (packet.MessageId == ImsaMsgRegistrationStatusInd)
        {
            bool reg = (packet.GetTlv(0x01)?.AsByte() ?? 0) == 1;
            ushort errCode = packet.GetTlv(0x10)?.AsUInt16() ?? 0;
            string errMsg = packet.GetTlv(0x11)?.AsString() ?? string.Empty;
            uint tech = packet.GetTlv(0x12)?.AsUInt32() ?? 0;
            RegistrationStatusChanged?.Invoke(new ImsRegistrationStatus(reg, errCode, errMsg, tech));
        }
        else if (packet.MessageId == ImsaMsgServiceStatusInd)
        {
            bool sms = (packet.GetTlv(0x10)?.AsByte() ?? 0) == 1;
            bool voice = (packet.GetTlv(0x11)?.AsByte() ?? 0) == 1;
            bool vt = (packet.GetTlv(0x12)?.AsByte() ?? 0) == 1;
            ServicesStatusChanged?.Invoke(new ImsServicesStatus(sms, voice, vt));
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
