using System.Buffers.Binary;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public readonly record struct ServiceVersion(QmiServiceType Service, ushort Major, ushort Minor);

public sealed class CtlService
{
    private readonly QmiClient _client;

    public CtlService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task SyncAsync(CancellationToken cancellationToken = default) =>
        _client.SyncAsync(cancellationToken);

    public async Task<List<ServiceVersion>> GetVersionInfoAsync(CancellationToken cancellationToken = default)
    {
        // CTL GET_VERSION_INFO (0x0021)
        var req = new QmiPacket(QmiServiceType.Control, 0, 0, 0x0021);
        var resp = await _client.SendRequestAsync(req, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x01) ?? throw new QmiException("Version list TLV 0x01 missing");
        if (tlv.Value.Length < 1)
            throw new QmiException("Version list TLV data truncated");

        int count = tlv.AsByte(0);
        const int entrySize = 5; // 1(service) + 2(major) + 2(minor)
        int expected = 1 + count * entrySize;
        if (tlv.Value.Length < expected)
            throw new QmiException($"Version list truncated: expected {expected}, available {tlv.Value.Length}");

        var list = new List<ServiceVersion>(count);
        for (int i = 0; i < count; i++)
        {
            int off = 1 + i * entrySize;
            byte svc = tlv.AsByte(off);
            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span[(off + 1)..(off + 3)]);
            ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(tlv.Value.Span[(off + 3)..(off + 5)]);
            list.Add(new ServiceVersion((QmiServiceType)svc, major, minor));
        }

        return list;
    }

    public Task<byte> AllocateClientIdAsync(QmiServiceType service, CancellationToken cancellationToken = default) =>
        _client.AllocateClientIdAsync(service, cancellationToken);

    public Task ReleaseClientIdAsync(QmiServiceType service, byte clientId, CancellationToken cancellationToken = default) =>
        _client.ReleaseClientIdAsync(service, clientId, cancellationToken);
}
