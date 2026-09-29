using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Generated;

namespace qmiSharp.Services;

public readonly record struct PdcConfigInfo(uint ConfigType, string ConfigId);

/// <summary>PDC operations. PDC completes requests asynchronously through token-matched indications.</summary>
public sealed class PdcService : IAsyncDisposable
{
    private const ushort GetSelectedConfig = 0x0022;
    private const ushort SetSelectedConfig = 0x0023;
    private const ushort ListConfigs = 0x0024;
    private const ushort ActivateConfig = 0x0027;

    private readonly QmiClient _client;
    private readonly ConcurrentDictionary<(ushort Message, uint Token), TaskCompletionSource<QmiPacket>> _pending = new();
    private int _tokenCounter;
    private byte _clientId;

    public PdcService(QmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.IndicationReceived += OnIndication;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_clientId == 0)
            _clientId = await _client.AllocateClientIdAsync(QmiServiceType.PDC, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<PdcConfigInfo>> ListConfigsAsync(uint configType = (uint)QmiPdcConfigurationType.QMI_PDC_CONFIGURATION_TYPE_SOFTWARE, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        QmiPacket indication = await ExecuteAsync(ListConfigs, token => new TlvBuilder()
            .AddUInt32(0x10, token)
            .AddUInt32(0x11, configType)
            .Build(), cancellationToken).ConfigureAwait(false);

        var result = new List<PdcConfigInfo>();
        var tlv = indication.GetTlv(0x11);
        if (tlv is null || tlv.Value.Length == 0) return result;

        ReadOnlySpan<byte> value = tlv.Value.Span;
        int count = value[0];
        int offset = 1;
        for (int i = 0; i < count; i++)
        {
            if (offset + 5 > value.Length) throw new QmiException("PDC config list is truncated");
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(offset, 4));
            int idLength = value[offset + 4];
            offset += 5;
            if (offset + idLength > value.Length) throw new QmiException("PDC config ID is truncated");
            result.Add(new PdcConfigInfo(type, Encoding.ASCII.GetString(value.Slice(offset, idLength))));
            offset += idLength;
        }
        return result;
    }

    public async Task<string> GetSelectedConfigAsync(uint configType = (uint)QmiPdcConfigurationType.QMI_PDC_CONFIGURATION_TYPE_SOFTWARE, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        QmiPacket indication = await ExecuteAsync(GetSelectedConfig, token => new TlvBuilder()
            .AddUInt32(0x01, configType)
            .AddUInt32(0x10, token)
            .Build(), cancellationToken).ConfigureAwait(false);

        var tlv = indication.GetTlv(0x11);
        if (tlv is null || tlv.Value.Length == 0) return string.Empty;
        int length = tlv.Value.Span[0];
        if (tlv.Value.Length < 1 + length) throw new QmiException("PDC active config ID is truncated");
        return Encoding.ASCII.GetString(tlv.Value.Span.Slice(1, length));
    }

    public async Task SelectConfigAsync(uint configType, string configId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(configId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        byte[] id = Encoding.ASCII.GetBytes(configId);
        if (id.Length > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(configId));

        byte[] typeAndId = new byte[5 + id.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(typeAndId, configType);
        typeAndId[4] = (byte)id.Length;
        id.CopyTo(typeAndId, 5);
        await ExecuteAsync(SetSelectedConfig, token => new TlvBuilder()
            .AddBytes(0x01, typeAndId)
            .AddUInt32(0x10, token)
            .Build(), cancellationToken).ConfigureAwait(false);
    }

    public async Task ActivateConfigAsync(uint configType = (uint)QmiPdcConfigurationType.QMI_PDC_CONFIGURATION_TYPE_SOFTWARE, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(ActivateConfig, token => new TlvBuilder()
            .AddUInt32(0x01, configType)
            .AddUInt32(0x10, token)
            .Build(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<QmiPacket> ExecuteAsync(ushort messageId, Func<uint, IReadOnlyList<QmiTlv>> buildTlvs, CancellationToken cancellationToken)
    {
        TaskCompletionSource<QmiPacket> completion = null!;
        uint token;
        do
        {
            token = unchecked((uint)Interlocked.Increment(ref _tokenCounter));
            if (token == 0) continue;
            completion = new TaskCompletionSource<QmiPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        while (!_pending.TryAdd((messageId, token), completion));

        try
        {
            var response = await _client.SendMessageAsync(QmiServiceType.PDC, messageId, buildTlvs(token), cancellationToken).ConfigureAwait(false);
            response.CheckResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove((messageId, token), out _); }
    }

    private void OnIndication(QmiPacket packet)
    {
        if (packet.KnownServiceType != QmiServiceType.PDC) return;
        var tokenTlv = packet.GetTlv(0x10);
        if (tokenTlv is null || tokenTlv.Value.Length < 4) return;
        uint token = tokenTlv.AsUInt32();
        if (!_pending.TryGetValue((packet.MessageId, token), out var completion)) return;

        var result = packet.GetTlv(0x01);
        if (result is null || result.Value.Length < 2)
            completion.TrySetException(new QmiException("PDC indication is missing its result TLV"));
        else if (result.AsUInt16() != 0)
            completion.TrySetException(new QmiException($"PDC operation 0x{packet.MessageId:X4} failed with indication result {result.AsUInt16()}"));
        else
            completion.TrySetResult(packet);
    }

    public async ValueTask DisposeAsync()
    {
        _client.IndicationReceived -= OnIndication;
        foreach (var completion in _pending.Values)
            completion.TrySetCanceled();
        if (_clientId != 0)
        {
            try { await _client.ReleaseClientIdAsync(QmiServiceType.PDC, _clientId).ConfigureAwait(false); }
            catch { }
            _clientId = 0;
        }
    }
}
