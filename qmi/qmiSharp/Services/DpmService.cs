using System.Buffers.Binary;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;

namespace qmiSharp.Services;

public sealed class DpmService : IAsyncDisposable
{
    private const ushort DpmMsgOpenPort = 0x0020;
    private const ushort DpmMsgClosePort = 0x0021;

    private readonly QmiClient _client;
    private byte _clientId;

    public DpmService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
        {
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.DPM, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens/binds a hardware control port and data port mapping.
    /// </summary>
    public async Task OpenPortAsync(string controlPortName = "smd11", uint endpointType = 2, uint interfaceNumber = 4, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // TLV 0x10: Control Ports
        // Array of (PortName string, EndpointType uint32, InterfaceNumber uint32)
        byte[] nameBytes = Encoding.ASCII.GetBytes(controlPortName);
        byte[] payload = new byte[1 + 1 + nameBytes.Length + 4 + 4];
        payload[0] = 1; // 1 element
        payload[1] = (byte)nameBytes.Length;
        nameBytes.CopyTo(payload.AsSpan(2));
        int off = 2 + nameBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(off, 4), endpointType);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(off + 4, 4), interfaceNumber);

        var reqTlv = new TlvBuilder()
            .AddBytes(0x10, payload)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.DPM, DpmMsgOpenPort, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    /// <summary>
    /// Closes/unbinds a hardware control port and data port mapping.
    /// </summary>
    public async Task ClosePortAsync(string controlPortName = "smd11", CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] nameBytes = Encoding.ASCII.GetBytes(controlPortName);
        byte[] payload = new byte[1 + 1 + nameBytes.Length];
        payload[0] = 1;
        payload[1] = (byte)nameBytes.Length;
        nameBytes.CopyTo(payload.AsSpan(2));

        var reqTlv = new TlvBuilder()
            .AddBytes(0x10, payload)
            .Build();

        var resp = await _client.SendMessageAsync(QmiServiceType.DPM, DpmMsgClosePort, reqTlv, cancellationToken).ConfigureAwait(false);
        resp.CheckResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientId != 0)
        {
            try
            {
                await _client.ReleaseClientIdAsync(QmiServiceType.DPM, _clientId).ConfigureAwait(false);
            }
            catch
            {
            }
            _clientId = 0;
        }
    }
}
