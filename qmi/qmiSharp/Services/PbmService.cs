using System.Buffers.Binary;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public readonly record struct PbmPhonebookCapabilities(
    ushort MaxRecords,
    ushort UsedRecords,
    byte MaxNumberLength,
    byte MaxNameLength);

public sealed class PbmService : IAsyncDisposable
{
    private const ushort PbmMsgGetCapabilities = 0x0020;
    private const ushort PbmMsgGetAllCapabilities = 0x0021;

    private readonly QmiClient _client;
    private byte _clientId;

    public PbmService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.PBM, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queries the phonebook storage capabilities (SIM / NV capacity and limits).
    /// </summary>
    public async Task<PbmPhonebookCapabilities> GetCapabilitiesAsync(byte sessionType = 1, ushort phonebookType = 1, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x01: Phonebook Information (SessionType uint8, PhonebookType uint16)
        var reqTlv = new TlvBuilder()
            .AddByte(0x01, sessionType)
            .AddUInt16(0x01, phonebookType)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.PBM, PbmMsgGetCapabilities, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        // TLV 0x01 or 0x10: Capability basic information
        // (SessionType uint8, PhonebookType uint16, UsedRecords uint16, MaxRecords uint16, MaxNumLen uint8, MaxNameLen uint8)
        var tlv = resp.GetTlv(0x10) ?? resp.GetTlv(0x01);
        if (tlv != null && tlv.Value.Length >= 9)
        {
            var span = tlv.Value.Span;
            ushort used = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(3, 2));
            ushort max = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(5, 2));
            byte numLen = span[7];
            byte nameLen = span[8];
            return new PbmPhonebookCapabilities(max, used, numLen, nameLen);
        }

        return new PbmPhonebookCapabilities(250, 0, 20, 14);
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.PBM, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
