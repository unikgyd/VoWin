using System.Buffers.Binary;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

/// <summary>
/// Snapdragon Sensor Core Service (SSC, 0x190).
/// Manages integrated motion sensors, pedometer, orientation sensors on Qualcomm SoC platforms.
/// </summary>
public sealed class SscService : IAsyncDisposable
{
    private const ushort SscMsgReset = 0x0000;
    private const ushort SscMsgControl = 0x0020;

    private readonly QmiClient _client;
    private byte _clientId;

    public SscService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.SSC, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ulong> ControlSensorAsync(byte[] commandData, byte reportType = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandData);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x01, commandData)
            .AddByte(0x10, reportType)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.SSC, SscMsgControl, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();

        var tlv = resp.GetTlv(0x10);
        return tlv != null && tlv.Value.Length >= 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(tlv.Value.Span)
            : 0;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resp = await _client.SendMessageAsync(QmiServiceType.SSC, SscMsgReset, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.SSC, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
