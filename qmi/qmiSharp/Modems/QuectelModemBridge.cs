using System.IO.Ports;
using System.Text.RegularExpressions;

namespace qmiSharp.Modems;

public readonly record struct ModemDeviceInfo(
    string PortName,
    string Manufacturer,
    string Model,
    string Revision,
    string Imei,
    string SimStatus,
    string Operator,
    int SignalQuality);

/// <summary>
/// Hardware bridge for Quectel / Qualcomm cellular modems on Windows.
/// Coordinates COM port discovery, diagnostics, and hardware call testing with strict safety controls.
/// </summary>
public sealed class QuectelModemBridge : IDisposable
{
    public const string AllowedDialNumber = "17387799413";

    private readonly SerialPort _port;
    private readonly object _lock = new();

    public string PortName => _port.PortName;
    public bool IsOpen => _port.IsOpen;

    public QuectelModemBridge(string portName, int baudRate = 115200)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 3000,
            WriteTimeout = 3000,
            DtrEnable = true,
            RtsEnable = true
        };
        _port.Open();
    }

    public static string? DiscoverModemPort()
    {
        string[] candidates = SerialPort.GetPortNames()
            .OrderByDescending(ParsePortNumber)
            .ToArray();
        foreach (var port in candidates)
        {
            try
            {
                using var p = new SerialPort(port, 115200)
                {
                    ReadTimeout = 1200,
                    WriteTimeout = 1200,
                    DtrEnable = true,
                    RtsEnable = true
                };
                p.Open();
                p.DiscardInBuffer();
                p.DiscardOutBuffer();
                p.Write("AT\r\n");
                Thread.Sleep(200);
                string resp = p.ReadExisting();
                if (resp.Contains("OK"))
                {
                    return port;
                }
                // Try once more in case port was sleeping
                p.Write("AT\r\n");
                Thread.Sleep(300);
                resp = p.ReadExisting();
                if (resp.Contains("OK"))
                {
                    return port;
                }
            }
            catch
            {
                // Port busy or not available
            }
        }
        return null;
    }

    private static int ParsePortNumber(string portName) =>
        int.TryParse(portName.AsSpan(3), out int number) ? number : -1;

    public string SendCommand(string command, int waitMs = 300)
    {
        lock (_lock)
        {
            _port.DiscardInBuffer();
            _port.Write(command + "\r\n");
            Thread.Sleep(waitMs);
            return _port.ReadExisting();
        }
    }

    public ModemDeviceInfo QueryDeviceInfo()
    {
        string ati = SendCommand("ATI", 400);
        string cgsn = SendCommand("AT+CGSN", 300);
        string cpin = SendCommand("AT+CPIN?", 300);
        string cops = SendCommand("AT+COPS?", 300);
        string csq = SendCommand("AT+CSQ", 300);

        string manufacturer = "Quectel";
        string model = "Unknown";
        string revision = "Unknown";

        var lines = ati.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("Model:", StringComparison.OrdinalIgnoreCase))
                model = line.Substring(6).Trim();
            else if (line.StartsWith("Revision:", StringComparison.OrdinalIgnoreCase))
                revision = line.Substring(9).Trim();
            else if (line.Contains("Quectel") || line.Contains("Baiwang"))
                manufacturer = line.Trim();
            else if (Regex.IsMatch(line.Trim(), @"^(QDC|EC|EG|EM|RM|RG)\w+$", RegexOptions.IgnoreCase))
                model = line.Trim();
        }

        string imei = "Unknown";
        var imeiMatch = Regex.Match(cgsn, @"\b\d{15}\b");
        if (imeiMatch.Success) imei = imeiMatch.Value;

        string sim = cpin.Contains("READY") ? "Ready" : "Not Ready";

        string op = "Unknown";
        var opMatch = Regex.Match(cops, @"""([^""]+)""");
        if (opMatch.Success) op = opMatch.Groups[1].Value;

        int signal = 0;
        var csqMatch = Regex.Match(csq, @"\+CSQ:\s*(\d+)");
        if (csqMatch.Success && int.TryParse(csqMatch.Groups[1].Value, out int s))
            signal = s;

        return new ModemDeviceInfo(PortName, manufacturer, model, revision, imei, sim, op, signal);
    }

    /// <summary>
    /// Checks and enables VoLTE/IMS if disabled, which is required for China Telecom LTE voice calling.
    /// </summary>
    public bool EnsureVoLteEnabled(Action<string>? logger = null)
    {
        string ims = SendCommand("AT+QCFG=\"ims\"", 500);
        if (ims.Contains("\"ims\",0"))
        {
            logger?.Invoke("  [VoLTE Setup] IMS/VoLTE is currently disabled in module NV. Enabling VoLTE (AT+QCFG=\"ims\",1)...");
            string resp = SendCommand("AT+QCFG=\"ims\",1", 1000);
            logger?.Invoke($"  [VoLTE Setup Response] {resp.Trim()}");
            Thread.Sleep(1000);
            return resp.Contains("OK");
        }
        return true;
    }

    /// <summary>
    /// Executes a voice dialing test with hardware modem.
    /// STRICT ENFORCEMENT: Only AllowedDialNumber ("17387799413") is permitted.
    /// Dials the number, monitors +CLCC until Alerting (ringing), allows ringing for a few seconds so the phone rings,
    /// then hangs up immediately with ATH to prevent any charges.
    /// </summary>
    public bool TestVoiceDialAndHangup(string number, Action<string>? logger = null, int maxRingingSeconds = 6)
    {
        if (number != AllowedDialNumber)
        {
            throw new InvalidOperationException($"SAFETY VIOLATION: Dialing '{number}' is strictly prohibited. Only '{AllowedDialNumber}' is allowed.");
        }

        logger?.Invoke($"[Voice Safety] Verified authorized number: {number}");
        logger?.Invoke("[Voice Action] Initiating call (ATD" + number + ";)...");

        // Dial voice call
        string dialResp = SendCommand($"ATD{number};", 1500);
        logger?.Invoke($"[Voice Dial Response] {dialResp.Trim()}");

        // Loop to track call state until Alerting (State 2) or Active (State 0) or Timeout
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool alertingSeen = false;
        var alertSw = new System.Diagnostics.Stopwatch();

        while (sw.ElapsedMilliseconds < 35000)
        {
            Thread.Sleep(600);
            string clcc = SendCommand("AT+CLCC", 400).Trim();

            // +CLCC: <id>,<dir>,<stat>,<mode>,<mpty>,<number>,<type>
            // stat: 0=active, 1=held, 2=dialing (alerting), 3=dialing, 4=incoming, 5=waiting, 6=disconnect
            // dir: 0=MO (outgoing), 1=MT (incoming)
            // mode: 0=voice, 1=data
            var lines = clcc.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool foundVoiceCall = false;

            foreach (var line in lines)
            {
                var m = Regex.Match(line, @"\+CLCC:\s*(\d+),(\d+),(\d+),(\d+)");
                if (m.Success)
                {
                    int id = int.Parse(m.Groups[1].Value);
                    int dir = int.Parse(m.Groups[2].Value);
                    int stat = int.Parse(m.Groups[3].Value);
                    int mode = int.Parse(m.Groups[4].Value);

                    // Strictly filter for our outgoing voice call (mode == 0 && dir == 0)
                    if (mode == 0 && dir == 0)
                    {
                        foundVoiceCall = true;
                        if (stat == 2)
                        {
                            logger?.Invoke($"[Voice Call State 2: Dialing] 呼叫发起中，网络正在建立路由寻呼终端... (耗时: {sw.Elapsed.TotalSeconds:F1}s)");
                        }
                        else if (stat == 3)
                        {
                            if (!alertingSeen)
                            {
                                alertingSeen = true;
                                alertSw.Start();
                                logger?.Invoke($"🔔 [Voice Call State 3: Alerting] 电话已打响！目标手机正在振铃中... (耗时: {sw.Elapsed.TotalSeconds:F1}s)");
                            }
                            else
                            {
                                logger?.Invoke($"🔔 [Voice Call State 3: Alerting] 目标手机持续振铃中... (已响铃: {alertSw.Elapsed.TotalSeconds:F1}s)");
                            }

                            if (alertSw.Elapsed.TotalSeconds >= maxRingingSeconds)
                            {
                                logger?.Invoke($"[Voice Auto-Hangup] 振铃已达到设定的安全时长 ({maxRingingSeconds}s)，立即主动挂断，确保不产生任何通话费用！");
                                goto EndCallLoop;
                            }
                        }
                        else if (stat == 0)
                        {
                            logger?.Invoke("⚠️ [Voice Call State 0: Active] 检测到通话接通！为防止扣费，秒级强制挂断！");
                            goto EndCallLoop;
                        }
                        else if (stat == 6)
                        {
                            logger?.Invoke($"[Voice Call State 6: Disconnect] 呼叫已由网络或对端结束。");
                            goto EndCallLoop;
                        }
                        break;
                    }
                }
            }

            if (!foundVoiceCall && (alertingSeen || sw.ElapsedMilliseconds > 15000))
            {
                logger?.Invoke("[Voice Notice] 语音呼叫已退出列表。");
                break;
            }
        }

    EndCallLoop:
        // Immediately hang up
        logger?.Invoke("[Voice Action] Sending ATH to terminate call...");
        string hangupResp = SendCommand("ATH", 600);
        logger?.Invoke($"[Voice Hangup Response] {hangupResp.Trim()}");

        // Verify call fully ended
        Thread.Sleep(500);
        string verifyResp = SendCommand("AT+CLCC", 400);
        logger?.Invoke($"[Voice Final Status] {verifyResp.Trim()}");

        return true;
    }

    public void Dispose()
    {
        if (_port.IsOpen) _port.Close();
        _port.Dispose();
    }
}
