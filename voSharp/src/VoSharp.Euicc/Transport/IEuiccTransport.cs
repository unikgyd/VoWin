using System.Text.RegularExpressions;
using VoSharp.Common.Utils;
using VoSharp.Modem;
using VoSharp.Sim.Pcsc;

namespace VoSharp.Euicc.Transport;

public interface IEuiccTransport
{
    string BackendName { get; }
    Task<int> OpenLogicalChannelAsync(string aid, CancellationToken ct = default);
    Task<byte[]> TransmitLogicalChannelAsync(int channel, byte[] apdu, CancellationToken ct = default);
    Task CloseLogicalChannelAsync(int channel, CancellationToken ct = default);
}

/// <summary>
/// Implements eUICC transport over AT Modem commands (+CSIM MANAGE CHANNEL / SELECT / GET RESPONSE, with +CCHO fallback).
/// </summary>
public class AtModemEuiccTransport : IEuiccTransport
{
    private readonly ModemDriver _modem;
    private bool _useCcho = false;
    public string BackendName => _useCcho ? "AT Modem (CCHO/CGLA)" : "AT Modem (CSIM Logical Channel)";

    public AtModemEuiccTransport(ModemDriver modem)
    {
        _modem = modem;
    }

    public async Task<int> OpenLogicalChannelAsync(string aid, CancellationToken ct = default)
    {
        // 1. Primary path: MANAGE CHANNEL (open): 00 70 00 00 01 -> "<channel> 90 00"
        byte[] openChannelApdu = new byte[] { 0x00, 0x70, 0x00, 0x00, 0x01 };
        byte[]? openResp = null;

        try
        {
            openResp = await _modem.SendCsimApduAsync(openChannelApdu, ct).ConfigureAwait(false);
        }
        catch
        {
            openResp = null;
        }

        if (openResp != null && openResp.Length >= 3 && openResp[^2] == 0x90 && openResp[^1] == 0x00)
        {
            int channel = openResp[0];
            if (channel > 0)
            {
                // Try candidate AIDs (Standard GSMA, XeSIM, eSTK.me 0, eSTK.me 1, GSMA Test)
                var candidateAids = new List<string> { aid, "A0000005591010FFFFFFFF8900000177", "A06573746B6D65FFFF4953442D522030", "A06573746B6D65FFFF4953442D522031", "A0000005591010FFFFFFFF8900000101" };

                foreach (var targetAid in candidateAids.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var aidBytes = HexUtils.FromHexString(targetAid);
                        var selectApdu = new List<byte> { EncodeLogicalChannelCla(0x00, channel), 0xA4, 0x04, 0x00, (byte)aidBytes.Length };
                        selectApdu.AddRange(aidBytes);

                        var selectResp = await _modem.SendCsimApduAsync(selectApdu.ToArray(), ct).ConfigureAwait(false);
                        if (selectResp.Length >= 2)
                        {
                            int selectSw = (selectResp[^2] << 8) | selectResp[^1];
                            if (selectSw == 0x9000 || (selectSw >> 8) == 0x61)
                            {
                                if ((selectSw >> 8) == 0x61)
                                {
                                    // Drain FCP
                                    try
                                    {
                                        var getResp = new byte[] { EncodeLogicalChannelCla(0x80, channel), 0xC0, 0x00, 0x00, (byte)(selectSw & 0xFF) };
                                        await _modem.SendCsimApduAsync(getResp, ct).ConfigureAwait(false);
                                    }
                                    catch { }
                                }
                                _useCcho = false;
                                return channel;
                            }
                        }
                    }
                    catch { }
                }

                // Close channel if SELECT ISD-R failed on all candidate AIDs
                try
                {
                    await _modem.SendCsimApduAsync(new byte[] { 0x00, 0x70, 0x80, (byte)channel, 0x00 }, ct).ConfigureAwait(false);
                }
                catch { }

                throw new InvalidOperationException("当前卡片未检测到 eUICC / eSIM (ISD-R) 应用。此卡为普通物理实体 SIM 卡，不支持电子切卡。");
            }
        }

        // 2. Fallback: AT+CCHO
        try
        {
            var resp = await _modem.Session.ExecuteCommandAsync($"AT+CCHO=\"{aid}\"", timeoutMs: 5000, ct: ct).ConfigureAwait(false);
            if (resp.Success)
            {
                foreach (var line in resp.Lines)
                {
                    var match = Regex.Match(line, @"\+CCHO:\s*(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var ch) && ch > 0)
                    {
                        _useCcho = true;
                        return ch;
                    }
                }
            }
        }
        catch { }

        throw new InvalidOperationException("模组未能建立 eUICC 逻辑通道，当前卡片可能不支持 eUICC 或通道繁忙。");
    }

    public async Task<byte[]> TransmitLogicalChannelAsync(int channel, byte[] apdu, CancellationToken ct = default)
    {
        if (channel is <= 0 or >= 20)
            throw new ArgumentOutOfRangeException(nameof(channel), "ISO 7816 logical channel must be between 1 and 19.");
        if (apdu.Length < 4)
            throw new ArgumentException("APDU is too short.", nameof(apdu));

        byte[] framed = (byte[])apdu.Clone();
        framed[0] = EncodeLogicalChannelCla(framed[0], channel);

        if (_useCcho)
        {
            var hexStr = HexUtils.ToHexString(framed);
            var resp = await _modem.Session.ExecuteCommandAsync($"AT+CGLA={channel},{hexStr.Length},\"{hexStr}\"", timeoutMs: 30000, ct: ct).ConfigureAwait(false);
            if (!resp.Success)
                throw new InvalidOperationException($"Failed to transmit APDU over channel {channel}: {string.Join(" ", resp.Lines)}");

            foreach (var line in resp.Lines)
            {
                var match = Regex.Match(line, @"\+CGLA:\s*\d+,\s*""?([0-9A-Fa-f]+)""?");
                if (match.Success)
                {
                    return HexUtils.FromHexString(match.Groups[1].Value);
                }
            }
            throw new InvalidOperationException($"Could not extract APDU response: {string.Join(" ", resp.Lines)}");
        }

        // Over CSIM: Inject channel into CLA low 2 bits
        // STORE DATA during profile installation can legitimately take longer
        // than ordinary SIM APDUs while the eUICC verifies and commits a segment.
        var csimResp = await _modem.SendCsimApduAsync(framed, ct, timeoutMs: 30000).ConfigureAwait(false);
        if (csimResp.Length < 2)
            throw new InvalidOperationException("eUICC APDU 响应数据过短");

        int sw = (csimResp[^2] << 8) | csimResp[^1];
        var payload = csimResp.Take(csimResp.Length - 2).ToList();

        // 61xx loop (GET RESPONSE)
        int guard = 0;
        while ((sw >> 8) == 0x61 && guard++ < 24)
        {
            byte len = (byte)(sw & 0xFF);
            byte[] getResp = new byte[] { EncodeLogicalChannelCla(0x80, channel), 0xC0, 0x00, 0x00, len };
            var frag = await _modem.SendCsimApduAsync(getResp, ct, timeoutMs: 30000).ConfigureAwait(false);
            if (frag.Length < 2) break;
            sw = (frag[^2] << 8) | frag[^1];
            payload.AddRange(frag.Take(frag.Length - 2));
        }

        payload.Add((byte)(sw >> 8));
        payload.Add((byte)(sw & 0xFF));
        return payload.ToArray();
    }

    public async Task CloseLogicalChannelAsync(int channel, CancellationToken ct = default)
    {
        if (channel <= 0) return;

        if (_useCcho)
        {
            try
            {
                await _modem.Session.ExecuteCommandAsync($"AT+CCHC={channel}", timeoutMs: 3000, ct: ct).ConfigureAwait(false);
            }
            catch { }
            return;
        }

        try
        {
            // MANAGE CHANNEL (close): 00 70 80 <channel> 00
            byte[] closeApdu = new byte[] { 0x00, 0x70, 0x80, (byte)channel, 0x00 };
            await _modem.SendCsimApduAsync(closeApdu, ct).ConfigureAwait(false);
        }
        catch { }
    }

    private static byte EncodeLogicalChannelCla(byte cla, int channel)
    {
        if (channel is >= 0 and < 4)
            return (byte)((cla & 0xFC) | channel);
        if (channel is >= 4 and < 20)
            return (byte)((cla & 0xB0) | 0x40 | (channel - 4));
        throw new ArgumentOutOfRangeException(nameof(channel), "ISO 7816 logical channel must be between 0 and 19.");
    }
}

/// <summary>
/// Implements eUICC transport over Windows PC/SC smartcard interface (winscard.dll).
/// </summary>
public class PcscEuiccTransport : IEuiccTransport, IAsyncDisposable
{
    private readonly PcscReader _pcsc;
    private readonly string? _readerName;
    private bool _connected;

    public string BackendName => "PC/SC SmartCard";

    public PcscEuiccTransport(PcscReader? pcsc = null, string? readerName = null)
    {
        _pcsc = pcsc ?? new PcscReader();
        _readerName = readerName;
    }

    private void EnsureConnected()
    {
        if (_connected) return;

        var readers = _pcsc.ListReaders();
        if (readers.Length == 0)
            throw new InvalidOperationException("No PC/SC smart card readers found");

        var targetReader = _readerName ?? readers[0];
        if (!_pcsc.Connect(targetReader))
            throw new InvalidOperationException($"Failed to connect to smart card reader: {targetReader}");

        _connected = true;
    }

    public Task<int> OpenLogicalChannelAsync(string aid, CancellationToken ct = default)
    {
        EnsureConnected();

        // 1. MANAGE CHANNEL - open: 00 70 00 00 01
        var openChannelApdu = new byte[] { 0x00, 0x70, 0x00, 0x00, 0x01 };
        var openResp = _pcsc.TransmitApdu(openChannelApdu);
        if (openResp == null || openResp.Length < 3 || openResp[^2] != 0x90 || openResp[^1] != 0x00)
            throw new InvalidOperationException($"PC/SC card rejected opening logical channel: SW={HexUtils.ToHexString(openResp ?? [])}");

        int channel = openResp[0];

        // 2. SELECT AID on allocated channel: [channel, A4, 04, 00, aidLen, aidBytes...]
        var aidBytes = HexUtils.FromHexString(aid);
        var selectApdu = new List<byte> { EncodeLogicalChannelCla(0x00, channel), 0xA4, 0x04, 0x00, (byte)aidBytes.Length };
        selectApdu.AddRange(aidBytes);

        var selectResp = _pcsc.TransmitApdu(selectApdu.ToArray());
        if (selectResp == null || selectResp.Length < 2 || (selectResp[^2] != 0x90 && selectResp[^2] != 0x61))
        {
            _ = CloseLogicalChannelAsync(channel, ct);
            throw new InvalidOperationException($"PC/SC card rejected selecting AID {aid} on channel {channel}: SW={HexUtils.ToHexString(selectResp ?? [])}");
        }

        return Task.FromResult(channel);
    }

    public Task<byte[]> TransmitLogicalChannelAsync(int channel, byte[] apdu, CancellationToken ct = default)
    {
        EnsureConnected();
        if (apdu.Length < 4) throw new ArgumentException("APDU is too short.", nameof(apdu));
        var framed = (byte[])apdu.Clone();
        framed[0] = EncodeLogicalChannelCla(framed[0], channel);
        var resp = _pcsc.TransmitApdu(framed);
        if (resp == null)
            throw new InvalidOperationException("PC/SC APDU transmission failed");

        return Task.FromResult(resp);
    }

    public Task CloseLogicalChannelAsync(int channel, CancellationToken ct = default)
    {
        if (_connected && channel > 0)
        {
            try
            {
                // MANAGE CHANNEL - close: 00 70 80 <channel> 00
                var closeApdu = new byte[] { 0x00, 0x70, 0x80, (byte)channel, 0x00 };
                _pcsc.TransmitApdu(closeApdu);
            }
            catch { }
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_connected)
        {
            _pcsc.Dispose();
            _connected = false;
        }
        return ValueTask.CompletedTask;
    }

    private static byte EncodeLogicalChannelCla(byte cla, int channel)
    {
        if (channel is >= 0 and < 4)
            return (byte)((cla & 0xFC) | channel);
        if (channel is >= 4 and < 20)
            return (byte)((cla & 0xB0) | 0x40 | (channel - 4));
        throw new ArgumentOutOfRangeException(nameof(channel), "ISO 7816 logical channel must be between 0 and 19.");
    }
}
