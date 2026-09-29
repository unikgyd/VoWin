using System.Buffers.Binary;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public readonly record struct DsdSystemInfo(
    byte Technology,
    uint Rat,
    ulong SoMask);

public sealed class DsdService : IAsyncDisposable
{
    private const ushort DsdMsgGetSystemStatus = 0x0022;
    private const ushort DsdMsgRegisterSystemStatusChange = 0x0025;
    private const ushort DsdMsgSystemStatusInd = 0x0025;

    private readonly QmiClient _client;
    private byte _clientId;

    public event Action<List<DsdSystemInfo>>? SystemStatusChanged;

    public DsdService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.IndicationReceived += OnIndicationReceived;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.DSD, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queries the current data system status (radio access technologies and availability).
    /// </summary>
    public async Task<List<DsdSystemInfo>> GetSystemStatusAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.DSD, DsdMsgGetSystemStatus, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        return ParseSystemStatus(resp.GetTlv(0x01));
    }

    /// <summary>
    /// Registers for data system status change indications.
    /// </summary>
    public async Task RegisterSystemStatusChangeAsync(bool enable = true, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddByte(0x01, enable ? (byte)1 : (byte)0)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.DSD, DsdMsgRegisterSystemStatusChange, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    private void OnIndicationReceived(QmiPacket packet)
    {
        if (packet.KnownServiceType != QmiServiceType.DSD) return;

        if (packet.MessageId == DsdMsgSystemStatusInd)
        {
            var systems = ParseSystemStatus(packet.GetTlv(0x01));
            SystemStatusChanged?.Invoke(systems);
        }
    }

    private static List<DsdSystemInfo> ParseSystemStatus(QmiTlv? tlv)
    {
        var list = new List<DsdSystemInfo>();
        if (tlv == null || tlv.Value.Length < 1) return list;

        var span = tlv.Value.Span;
        byte count = span[0];
        const int itemSize = 1 + 4 + 8; // Tech(1) + Rat(4) + SoMask(8) = 13 bytes
        int offset = 1;

        for (int i = 0; i < count && offset + itemSize <= span.Length; i++)
        {
            byte tech = span[offset];
            uint rat = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 1, 4));
            ulong soMask = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 5, 8));
            list.Add(new DsdSystemInfo(tech, rat, soMask));
            offset += itemSize;
        }

        return list;
    }

    public async ValueTask DisposeAsync()
    {
        _client.IndicationReceived -= OnIndicationReceived;
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.DSD, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
