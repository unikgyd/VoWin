using System.IO.Ports;
using System.Text.RegularExpressions;
using VoSharp.Common.Events;
using VoSharp.Common.Utils;
using VoSharp.Modem.At;

namespace VoSharp.Modem;

public class ModemDriver : IAsyncDisposable
{
    private readonly AtSession _session;
    private readonly AsyncEventBus? _eventBus;
    private volatile bool _isRadioStateKnown;
    private volatile bool _isRadioDisabled;
    private volatile bool _radioStatusReportingEnabled;

    public event EventHandler<ModemConnectionChangedEventArgs>? ConnectionChanged;
    public event EventHandler<SignalChangedEventArgs>? SignalChanged;
    public event EventHandler<NetworkRegistrationChangedEventArgs>? RegistrationChanged;
    public event EventHandler<SimStateChangedEventArgs>? SimStateChanged;
    public event EventHandler<FlightModeChangedEventArgs>? FlightModeChanged;
    public event EventHandler<ModemUrcEventArgs>? UrcReceived;

    public ModemDriver(string portName, int baudRate = 115200, AsyncEventBus? eventBus = null)
    {
        _session = new AtSession(portName, baudRate, eventBus);
        _session.UrcPublicationFilter = ShouldPublishUrc;
        _eventBus = eventBus;
        _session.UrcReceived += (s, urc) =>
        {
            try { UrcReceived?.Invoke(this, new ModemUrcEventArgs(urc)); } catch { }
        };
    }

    public bool IsOpen => _session.IsOpen;
    public string PortName => _session.PortName;
    public AtSession Session => _session;
    public string? FirmwareRevision { get; private set; }

    /// <summary>Sets the physical DTR signal while retaining the AT reader.</summary>
    public void SetDataTerminalReady(bool asserted) => _session.SetDataTerminalReady(asserted);

    public void Open()
    {
        _session.Open();
        try { ConnectionChanged?.Invoke(this, new ModemConnectionChangedEventArgs(PortName, true)); } catch { }
    }

    /// <summary>Temporarily releases the AT COM handle without disposing the driver.</summary>
    public bool Close() => _session.Close();

    /// <summary>Closes the AT channel and waits for its reader to exit.</summary>
    public Task<bool> CloseAsync(CancellationToken ct = default) => _session.CloseAsync(ct);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT", 1000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            // Disable AT echo
            await _session.ExecuteCommandAsync("ATE0", 1000, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async Task<string> GetImeiAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+GSN", 2000, ct).ConfigureAwait(false);
        if (!resp.Success)
            resp = await _session.ExecuteCommandAsync("AT+CGSN", 2000, ct).ConfigureAwait(false);

        if (resp.Success)
        {
            foreach (var line in resp.Lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("AT", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = Regex.Match(trimmed, @"\d{14,18}");
                if (match.Success) return match.Value;
            }
        }
        return string.Empty;
    }

    public async Task<string> GetFirmwareRevisionAsync(CancellationToken ct = default)
    {
        var response = await _session.ExecuteCommandAsync("AT+QGMR", 2000, ct).ConfigureAwait(false);
        if (!response.Success)
            response = await _session.ExecuteCommandAsync("ATI", 2000, ct).ConfigureAwait(false);

        FirmwareRevision = response.Lines
            .Select(line => line.Trim())
            .FirstOrDefault(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith("AT", StringComparison.OrdinalIgnoreCase) &&
                !line.Equals("OK", StringComparison.OrdinalIgnoreCase));
        return FirmwareRevision ?? string.Empty;
    }

    public async Task<string> GetImsiAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CIMI", 2000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            foreach (var line in resp.Lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("AT", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = Regex.Match(trimmed, @"\d{14,16}");
                if (match.Success) return match.Value;
            }
        }
        return string.Empty;
    }

    public async Task<string> GetIccidAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+QCCID", 2000, ct).ConfigureAwait(false);
        if (!resp.Success)
            resp = await _session.ExecuteCommandAsync("AT+CCID", 2000, ct).ConfigureAwait(false);

        if (resp.Success)
        {
            foreach (var line in resp.Lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("AT", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = Regex.Match(trimmed, @"\d{18,22}");
                if (match.Success) return match.Value;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Reads the subscriber's own number from EF-MSISDN through the standard
    /// 3GPP AT+CNUM command. Many data-only and prepaid profiles intentionally
    /// leave this file empty; an empty result therefore means "not supplied by
    /// the card", not a modem or parsing failure.
    /// </summary>
    public async Task<string> GetPhoneNumberAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CNUM", 3000, ct).ConfigureAwait(false);
        return resp.Success ? ParseOwnPhoneNumber(resp.Lines) : string.Empty;
    }

    internal static string ParseOwnPhoneNumber(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            // 3GPP TS 27.007: +CNUM: [<alpha>],<number>,<type>[,...]
            // Quectel firmware may omit <alpha>, but keeps the comma before the
            // quoted number. Accept both forms without treating the alpha label
            // itself as a phone number.
            var match = Regex.Match(
                line,
                @"^\s*\+CNUM:\s*(?:""[^""]*""\s*)?,?\s*""?(?<number>\+?[0-9][0-9 ()-]*)""?\s*,\s*(?<type>\d+)",
                RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var number = Regex.Replace(match.Groups["number"].Value, @"[\s()-]", "");
            if (number.Length == 0) continue;

            // TON 145 denotes an international number. Some cards omit the
            // literal '+' even though the type says it is international.
            if (match.Groups["type"].Value == "145" && number[0] != '+')
                number = "+" + number;

            return number;
        }

        return string.Empty;
    }

    public async Task<bool> RefreshSimAsync(CancellationToken ct = default, bool preserveFlightMode = false)
    {
        // Cycle the baseband so CIMI/QCCID/CNUM cannot return the previous
        // profile from a modem cache. Restore CFUN=4 when the caller was in
        // flight mode instead of accidentally enabling cellular RF.
        await _session.ExecuteCommandAsync("AT+CFUN=0", 3000, ct).ConfigureAwait(false);
        try { await Task.Delay(800, ct).ConfigureAwait(false); } catch { }
        var targetCfun = preserveFlightMode ? 4 : 1;
        var resp = await _session.ExecuteCommandAsync($"AT+CFUN={targetCfun}", 3000, ct).ConfigureAwait(false);
        _isRadioStateKnown = true;
        _isRadioDisabled = preserveFlightMode;
        _radioStatusReportingEnabled = !preserveFlightMode;

        // Wait for CPIN: READY (up to 6s)
        for (int i = 0; i < 12; i++)
        {
            try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
            var cpin = await _session.ExecuteCommandAsync("AT+CPIN?", 1000, ct).ConfigureAwait(false);
            if (cpin.Success && cpin.FirstDataLine.Contains("READY"))
            {
                _eventBus?.Publish(EventTopics.ModemSim, "Modem", "READY");
                return true;
            }
        }

        return resp.Success;
    }

    public async Task<bool> SetFlightModeAsync(bool enable, CancellationToken ct = default)
    {
        var previousKnown = _isRadioStateKnown;
        var previousDisabled = _isRadioDisabled;
        var previousReportingEnabled = _radioStatusReportingEnabled;

        // Some modems emit a transient registration status before CFUN returns OK.
        _isRadioStateKnown = true;
        _isRadioDisabled = enable;
        _radioStatusReportingEnabled = !enable;
        try
        {
            string cmd = enable ? "AT+CFUN=4" : "AT+CFUN=1";
            var resp = await _session.ExecuteCommandAsync(cmd, 3000, ct).ConfigureAwait(false);
            if (resp.Success)
            {
                _eventBus?.Publish("modem.flightmode", "Modem", enable ? "ON" : "OFF");
                try { FlightModeChanged?.Invoke(this, new FlightModeChangedEventArgs(enable, enable ? 4 : 1)); } catch { }
                return true;
            }
        }
        catch
        {
            _isRadioStateKnown = previousKnown;
            _isRadioDisabled = previousDisabled;
            _radioStatusReportingEnabled = previousReportingEnabled;
            throw;
        }
        _isRadioStateKnown = previousKnown;
        _isRadioDisabled = previousDisabled;
        _radioStatusReportingEnabled = previousReportingEnabled;
        return false;
    }

    public async Task<int> GetFlightModeAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CFUN?", 2000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            var match = Regex.Match(resp.FirstDataLine, @"\+CFUN:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var cfun))
            {
                _isRadioDisabled = cfun is 0 or 4;
                _isRadioStateKnown = true;
                return cfun;
            }
        }
        return 1;
    }

    private bool ShouldPublishUrc(string line)
    {
        if (!IsRadioStatusUrc(line)) return true;
        return _isRadioStateKnown && !_isRadioDisabled && _radioStatusReportingEnabled;
    }

    internal static bool IsRadioStatusUrc(string line) =>
        line.StartsWith("+CREG:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("+CEREG:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("+CGREG:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("+CSQ:", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> RebootBasebandAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CFUN=1,1", 3000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    public async Task<SignalQuality> GetSignalAsync(CancellationToken ct = default)
    {
        if (_isRadioStateKnown && !_isRadioDisabled)
        {
            _radioStatusReportingEnabled = true;
        }
        var resp = await _session.ExecuteCommandAsync("AT+CSQ", 2000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            var match = Regex.Match(resp.FirstDataLine, @"\+CSQ:\s*(\d+),(\d+)");
            if (match.Success)
            {
                var raw = int.Parse(match.Groups[1].Value);
                if (raw == 99 || raw < 0)
                {
                    var noSig = new SignalQuality(99, 0, 0, "LTE/NR");
                    try { SignalChanged?.Invoke(this, new SignalChangedEventArgs(noSig)); } catch { }
                    return noSig;
                }

                var dbm = -113 + (raw * 2);
                var bars = raw switch
                {
                    >= 19 => 5, // >= -75 dBm (极佳)
                    >= 14 => 4, // >= -85 dBm (良好)
                    >= 9 => 3,  // >= -95 dBm (一般)
                    >= 4 => 2,  // >= -105 dBm (较弱)
                    >= 1 => 1,  // > -113 dBm (微弱)
                    _ => 0      // <= -113 dBm (无信号)
                };
                var sq = new SignalQuality(raw, dbm, bars, "LTE/NR");
                try { SignalChanged?.Invoke(this, new SignalChangedEventArgs(sq)); } catch { }
                return sq;
            }
        }
        var defSq = new SignalQuality(99, 0, 0, "Unknown");
        try { SignalChanged?.Invoke(this, new SignalChangedEventArgs(defSq)); } catch { }
        return defSq;
    }

    public async Task<NetworkRegistration> GetRegistrationAsync(CancellationToken ct = default)
    {
        if (_isRadioStateKnown && !_isRadioDisabled)
        {
            _radioStatusReportingEnabled = true;
        }
        var resp = await _session.ExecuteCommandAsync("AT+CEREG?", 2000, ct).ConfigureAwait(false);
        if (!resp.Success)
            resp = await _session.ExecuteCommandAsync("AT+CREG?", 2000, ct).ConfigureAwait(false);

        var regStatus = NetworkRegStatus.Unknown;
        if (resp.Success)
        {
            var match = Regex.Match(resp.FirstDataLine, @"\+(?:CEREG|CREG):\s*(?:\d+,)?(\d+)");
            if (match.Success)
            {
                regStatus = (NetworkRegStatus)int.Parse(match.Groups[1].Value);
            }
        }

        var copsResp = await _session.ExecuteCommandAsync("AT+COPS?", 2000, ct).ConfigureAwait(false);
        string? opName = null;
        if (copsResp.Success)
        {
            var match = Regex.Match(copsResp.FirstDataLine, @"\+COPS:\s*\d+,\d+,""([^""]+)""");
            if (match.Success) opName = match.Groups[1].Value;
        }

        var reg = new NetworkRegistration(regStatus, opName, "LTE", null);
        try { RegistrationChanged?.Invoke(this, new NetworkRegistrationChangedEventArgs(reg, NetworkRegStatus.Unknown, regStatus)); } catch { }
        return reg;
    }

    public async Task<bool> DialVoiceAsync(string phoneNumber, CancellationToken ct = default)
    {
        _eventBus?.Publish(EventTopics.CallDialing, "Modem", phoneNumber);
        _eventBus?.Publish(EventTopics.CallState, "Modem", "RINGING");
        var resp = await _session.ExecuteCommandAsync($"ATD{phoneNumber};", 5000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    public async Task<bool> HangupVoiceAsync(CancellationToken ct = default)
    {
        _eventBus?.Publish(EventTopics.CallEnded, "Modem", "HANGUP");
        _eventBus?.Publish(EventTopics.CallState, "Modem", "ENDED");
        var resp = await _session.ExecuteCommandAsync("ATH", 3000, ct).ConfigureAwait(false);
        if (!resp.Success)
            resp = await _session.ExecuteCommandAsync("AT+CHUP", 3000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    public async Task<bool> AnswerVoiceAsync(CancellationToken ct = default)
    {
        _eventBus?.Publish(EventTopics.CallState, "Modem", "ACTIVE");
        var resp = await _session.ExecuteCommandAsync("ATA", 3000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    /// <summary>
    /// Reads the baseband call table. Only entries with mode=0 are voice calls;
    /// Quectel firmware may also expose always-on IMS/data bearers in +CLCC.
    /// </summary>
    public async Task<IReadOnlyList<ModemVoiceCall>> GetVoiceCallsAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CLCC", 2000, ct).ConfigureAwait(false);
        if (!resp.Success) return Array.Empty<ModemVoiceCall>();

        return ParseVoiceCalls(resp.Lines);
    }

    public static IReadOnlyList<ModemVoiceCall> ParseVoiceCalls(IEnumerable<string> lines)
    {
        var result = new List<ModemVoiceCall>();
        foreach (var line in lines)
        {
            var match = Regex.Match(line,
                "\\+CLCC:\\s*(\\d+),(\\d+),(\\d+),(\\d+),(\\d+)(?:,\"([^\"]*)\",(\\d+))?");
            if (!match.Success) continue;

            result.Add(new ModemVoiceCall(
                Index: int.Parse(match.Groups[1].Value),
                IsOutgoing: match.Groups[2].Value == "0",
                Status: int.Parse(match.Groups[3].Value),
                Mode: int.Parse(match.Groups[4].Value),
                IsMultiparty: match.Groups[5].Value == "1",
                Number: match.Groups[6].Success ? match.Groups[6].Value : null,
                NumberType: match.Groups[7].Success ? int.Parse(match.Groups[7].Value) : null));
        }

        return result.Where(call => call.Mode == 0).ToArray();
    }

    /// <summary>
    /// Enables the Quectel USB Audio Class PCM route for a cellular voice call.
    /// A false result means the installed modem firmware does not expose voice PCM.
    /// </summary>
    public async Task<bool> EnableUsbVoiceAudioAsync(CancellationToken ct = default)
    {
        var set = await _session.ExecuteCommandAsync("AT+QPCMV=1,2", 2000, ct).ConfigureAwait(false);
        if (!set.Success) return false;

        var query = await _session.ExecuteCommandAsync("AT+QPCMV?", 2000, ct).ConfigureAwait(false);
        return query.Success && query.Lines.Any(line =>
            Regex.IsMatch(line, @"\+QPCMV:\s*1\s*,\s*2", RegexOptions.IgnoreCase));
    }

    public async Task DisableUsbVoiceAudioAsync(CancellationToken ct = default)
    {
        try { await _session.ExecuteCommandAsync("AT+QPCMV=0", 2000, ct).ConfigureAwait(false); }
        catch { }
    }

    public async Task<bool> SendDtmfAsync(char digit, CancellationToken ct = default)
    {
        _eventBus?.Publish(EventTopics.CallDtmf, "Modem", digit.ToString());
        var resp = await _session.ExecuteCommandAsync($"AT+VTS={digit}", 2000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    public async Task<int> GetSimSlotAsync(CancellationToken ct = default)
    {
        // Try Quectel AT+QUIMSLOT?
        var resp = await _session.ExecuteCommandAsync("AT+QUIMSLOT?", 2000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            var match = Regex.Match(resp.FirstDataLine, @"\+QUIMSLOT:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var slot))
                return slot;
        }

        // Try standard AT+QDSIM?
        var resp2 = await _session.ExecuteCommandAsync("AT+QDSIM?", 2000, ct).ConfigureAwait(false);
        if (resp2.Success)
        {
            var match = Regex.Match(resp2.FirstDataLine, @"\+QDSIM:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var slot))
                return slot;
        }

        return 1;
    }

    public async Task<(byte[] Res, byte[] Ck, byte[] Ik)?> AuthenticateUsimAkaAsync(byte[] rand, byte[] autn, CancellationToken ct = default)
    {
        if (rand.Length != 16 || autn.Length != 16)
            return null;

        // 3GPP TS 31.102 USIM Authenticate APDU:
        // CLA=00, INS=88, P1=00, P2=81 (3G context / UMTS AKA), Lc=22 (34 bytes), Data: 10 <RAND 16B> 10 <AUTN 16B>
        var payload = new byte[34];
        payload[0] = 0x10;
        Array.Copy(rand, 0, payload, 1, 16);
        payload[17] = 0x10;
        Array.Copy(autn, 0, payload, 18, 16);

        var apduHex = "0088008122" + Convert.ToHexString(payload);

        // 1. Try CCHO / CGLA with USIM ADF
        int channelId = 0;
        foreach (var aid in new[] { "A0000000871002", "A0000000871002FF86FF0389FFFFFFFF" })
        {
            var openResp = await _session.ExecuteCommandAsync($"AT+CCHO=\"{aid}\"", 2000, ct).ConfigureAwait(false);
            if (openResp.Success)
            {
                var m = Regex.Match(openResp.RawOutput ?? "", @"\+CCHO:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var cid) && cid > 0)
                {
                    channelId = cid;
                    break;
                }
            }
        }

        string? rawApduHexResp = null;
        if (channelId > 0)
        {
            try
            {
                var claByte = (byte)channelId;
                var modApduHex = claByte.ToString("X2") + apduHex[2..];
                var cglaCmd = $"AT+CGLA={channelId},{modApduHex.Length},\"{modApduHex}\"";
                var cglaResp = await _session.ExecuteCommandAsync(cglaCmd, 4000, ct).ConfigureAwait(false);
                if (cglaResp.Success)
                {
                    var match = Regex.Match(cglaResp.RawOutput ?? "", @"\+CGLA:\s*\d+,\s*""?([0-9A-Fa-f]+)""?");
                    if (match.Success)
                        rawApduHexResp = match.Groups[1].Value;
                }
            }
            finally
            {
                await _session.ExecuteCommandAsync($"AT+CCHC={channelId}", 2000, ct).ConfigureAwait(false);
            }
        }

        // 2. Fallback to AT+CSIM
        if (string.IsNullOrEmpty(rawApduHexResp))
        {
            var csimCmd = $"AT+CSIM={apduHex.Length},\"{apduHex}\"";
            var csimResp = await _session.ExecuteCommandAsync(csimCmd, 4000, ct).ConfigureAwait(false);
            if (csimResp.Success)
            {
                var match = Regex.Match(csimResp.RawOutput ?? "", @"\+CSIM:\s*\d+,\s*""?([0-9A-Fa-f]+)""?");
                if (match.Success)
                    rawApduHexResp = match.Groups[1].Value;
            }
        }

        if (string.IsNullOrEmpty(rawApduHexResp))
            return null;

        var respBytes = Convert.FromHexString(rawApduHexResp);
        // 3GPP TS 31.102 Success response format:
        // Tag 0xDB (GSM/UMTS Success), Len, Tag 0x... RES, Tag 0x... CK, Tag 0x... IK
        if (respBytes.Length >= 4 && respBytes[0] == 0xDB)
        {
            int offset = 1;
            int totalLen = respBytes[offset++];
            if (offset < respBytes.Length)
            {
                int resLen = respBytes[offset++];
                if (offset + resLen <= respBytes.Length)
                {
                    var res = respBytes[offset..(offset + resLen)];
                    offset += resLen;

                    if (offset < respBytes.Length)
                    {
                        int ckLen = respBytes[offset++];
                        if (offset + ckLen <= respBytes.Length)
                        {
                            var ck = respBytes[offset..(offset + ckLen)];
                            offset += ckLen;

                            if (offset < respBytes.Length)
                            {
                                int ikLen = respBytes[offset++];
                                if (offset + ikLen <= respBytes.Length)
                                {
                                    var ik = respBytes[offset..(offset + ikLen)];
                                    return (res, ck, ik);
                                }
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    public async Task<bool> SetSimSlotAsync(int slot, CancellationToken ct = default)
    {
        // Quectel / DJI Cellular Dongle hardware SIM slot switch (1=Physical SIM / 2=Built-in eSIM)
        var resp = await _session.ExecuteCommandAsync($"AT+QUIMSLOT={slot}", 5000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            try { SimStateChanged?.Invoke(this, new SimStateChangedEventArgs(null, slot, "SWITCHED")); } catch { }
            return true;
        }

        var resp2 = await _session.ExecuteCommandAsync($"AT+QDSIM={slot}", 5000, ct).ConfigureAwait(false);
        if (resp2.Success)
        {
            try { SimStateChanged?.Invoke(this, new SimStateChangedEventArgs(null, slot, "SWITCHED")); } catch { }
            return true;
        }
        return false;
    }

    public async Task<byte[]> SendCsimApduAsync(byte[] apdu, CancellationToken ct = default, int timeoutMs = 10000)
    {
        var apduHex = HexUtils.ToHexString(apdu);
        AtResponse? resp = null;

        // Retry up to 3 times for transient +CME ERROR: 0 (SIM busy / channel race per VoCat)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            resp = await _session.ExecuteCommandAsync($"AT+CSIM={apduHex.Length},\"{apduHex}\"", timeoutMs, ct).ConfigureAwait(false);
            if (resp.Success) break;

            bool isTransient = resp.Lines.Any(l => l.Contains("+CME ERROR: 0", StringComparison.OrdinalIgnoreCase));
            if (isTransient && attempt < 2)
            {
                await Task.Delay(60, ct).ConfigureAwait(false);
                continue;
            }
            break;
        }

        if (resp == null || !resp.Success)
            throw new InvalidOperationException($"AT+CSIM failed: {string.Join(" ", resp?.Lines ?? Array.Empty<string>())}");

        foreach (var line in resp.Lines)
        {
            var match = Regex.Match(line, @"\+CSIM:\s*\d+,\s*""?([0-9A-Fa-f]+)""?");
            if (match.Success)
            {
                return HexUtils.FromHexString(match.Groups[1].Value);
            }
        }

        throw new InvalidOperationException($"Could not extract CSIM response from: {string.Join(" ", resp.Lines)}");
    }

    public async Task<AtResponse> SendRawAtCommandAsync(string command, int timeoutMs = 3000, CancellationToken ct = default)
    {
        return await _session.ExecuteCommandAsync(command, timeoutMs, ct).ConfigureAwait(false);
    }

    public async Task<AtResponse> SendPduWithPromptAsync(int tpduLength, string pduHex, CancellationToken ct = default)
    {
        return await _session.ExecutePromptCommandAsync($"AT+CMGS={tpduLength}", pduHex, promptTimeoutMs: 3000, completionTimeoutMs: 15000, ct: ct).ConfigureAwait(false);
    }


    /// <summary>
    /// Configures modem for 3GPP PDU SMS mode, preferred storage, and unsolicited event notifications (AT+CNMI).
    /// </summary>
    public async Task<bool> InitializeSmsModeAsync(CancellationToken ct = default)
    {
        if (!IsOpen) return false;

        try
        {
            // Enable caller ID and extended call result codes so incoming
            // cellular calls are delivered as RING/+CRING and +CLIP URCs.
            await _session.ExecuteCommandAsync("AT+CLIP=1", 1000, ct).ConfigureAwait(false);
            await _session.ExecuteCommandAsync("AT+CRC=1", 1000, ct).ConfigureAwait(false);

            // 1. Select Phase 2+ SMS service
            await _session.ExecuteCommandAsync("AT+CSMS=1", 1000, ct).ConfigureAwait(false);

            // 2. Set SMS format to PDU mode (0)
            await _session.ExecuteCommandAsync("AT+CMGF=0", 1000, ct).ConfigureAwait(false);

            // 3. Set Preferred Storage to SIM (SM) or device memory (ME)
            var cpmsResp = await _session.ExecuteCommandAsync("AT+CPMS=\"SM\",\"SM\",\"SM\"", 2000, ct).ConfigureAwait(false);
            if (!cpmsResp.Success)
            {
                await _session.ExecuteCommandAsync("AT+CPMS=\"ME\",\"ME\",\"ME\"", 2000, ct).ConfigureAwait(false);
            }

            // 4. Configure New Message Indications to TE (+CMTI for incoming SMS, +CDSI for status reports)
            // mode=2 (buffer when busy), mt=1 (+CMTI with memory & index), bm=0, ds=1 (+CDSI for delivery reports), bfr=0
            var cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=2,1,0,1,0", 2000, ct).ConfigureAwait(false);
            if (!cnmiResp.Success)
            {
                cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=2,1,0,0,0", 2000, ct).ConfigureAwait(false);
                if (!cnmiResp.Success)
                {
                    cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=1,1,0,0,0", 2000, ct).ConfigureAwait(false);
                    if (!cnmiResp.Success)
                    {
                        // Direct-to-TE delivery fallback (+CMT/+CDS).
                        cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=2,2,0,1,0", 2000, ct).ConfigureAwait(false);
                        if (!cnmiResp.Success)
                            cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=2,2,0,0,0", 2000, ct).ConfigureAwait(false);
                    }
                }
            }

            return cnmiResp.Success;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { ConnectionChanged?.Invoke(this, new ModemConnectionChangedEventArgs(PortName, false)); } catch { }
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}

public record SerialPortDetail(
    string PortName,
    string DeviceKey,
    bool IsQuectel,
    bool IsAtPort,
    bool IsNmeaOrDm,
    bool IsBluetooth
);

public record ModemVoiceCall(
    int Index,
    bool IsOutgoing,
    int Status,
    int Mode,
    bool IsMultiparty,
    string? Number,
    int? NumberType
);

public static class WindowsModemDetector
{
    public static List<SerialPortDetail> GetDetailedPorts()
    {
        var list = new List<SerialPortDetail>();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
                if (key != null)
                {
                    foreach (var valName in key.GetValueNames())
                    {
                        var port = key.GetValue(valName)?.ToString();
                        if (string.IsNullOrEmpty(port)) continue;

                        bool isBth = valName.Contains("Bth", StringComparison.OrdinalIgnoreCase);
                        bool isQuectel = valName.Contains("QCUSB", StringComparison.OrdinalIgnoreCase) ||
                                         valName.Contains("Quectel", StringComparison.OrdinalIgnoreCase);

                        // In Qualcomm / Quectel USB architecture:
                        // _3 / _4 is the AT Command interface (COM6)
                        // _1 is Diagnostic Monitor (DM / COM8)
                        // _2 is NMEA GPS stream (COM7)
                        bool isAt = isQuectel && (valName.EndsWith("_3") || valName.EndsWith("_4") || port.Equals("COM6", StringComparison.OrdinalIgnoreCase));
                        bool isNmeaDm = isQuectel && (valName.EndsWith("_1") || valName.EndsWith("_2"));

                        list.Add(new SerialPortDetail(port, valName, isQuectel, isAt, isNmeaDm, isBth));
                    }
                }
            }
            catch { }
        }

        if (list.Count == 0)
        {
            foreach (var p in SerialPort.GetPortNames())
            {
                list.Add(new SerialPortDetail(p, p, false, false, false, false));
            }
        }

        return list;
    }


    public static string[] GetAvailableComPorts()
    {
        var details = GetDetailedPorts();
        return details.Select(d => d.PortName).Distinct().ToArray();
    }

    public static async Task<string?> ProbeModemPortAsync(int baudRate = 115200, CancellationToken ct = default)
    {
        var details = GetDetailedPorts();

        // Sort candidates:
        // Priority 1: Verified AT ports (e.g. COM6 / QCUSB_COM6_3)
        // Priority 2: Standard non-cellular serial ports
        // Exclude: Bluetooth virtual ports & Quectel NMEA/DM ports (which do not accept AT commands)
        var candidates = details
            .Where(d => !d.IsBluetooth && !d.IsNmeaOrDm)
            .OrderByDescending(d => d.IsAtPort)
            .ThenByDescending(d => d.IsQuectel)
            .Select(d => d.PortName)
            .Distinct()
            .ToList();

        foreach (var port in candidates)
        {
            try
            {
                using var probeCts = new CancellationTokenSource(800);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);

                await using var driver = new ModemDriver(port, baudRate);
                driver.Open();

                if (await driver.PingAsync(linked.Token).ConfigureAwait(false))
                {
                    return port;
                }
            }
            catch
            {
                // Ignore port open or permission errors
            }
        }

        return null;
    }
}
