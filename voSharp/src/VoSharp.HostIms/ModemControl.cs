using System.IO.Ports;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VoSharp.HostIms;

internal sealed class ModemControl(string atPort)
{
    internal static void ValidatePort(string port)
    {
        if (!Regex.IsMatch(port, @"^COM[1-9][0-9]{0,3}$", RegexOptions.IgnoreCase)) throw new ArgumentException("An explicit COM port is required.");
    }
    internal async Task<string> At(string command, CancellationToken ct = default)
    {
        ValidatePort(atPort);
        using var port = new SerialPort(atPort, 115200) { ReadTimeout = 2000, WriteTimeout = 2000 };
        port.Open(); port.DiscardInBuffer(); port.Write(command + "\r");
        var response = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(4);
        do
        {
            await Task.Delay(200, ct);
            response.Append(port.ReadExisting());
            if (Regex.IsMatch(response.ToString(), @"(?m)^(OK|ERROR)\s*$")) break;
        } while (DateTime.UtcNow < deadline);
        return response.ToString();
    }
    internal static void RequireOk(string response)
    {
        if (!Regex.IsMatch(response, @"(?m)^OK\s*$")) throw new InvalidOperationException("Modem did not acknowledge the command.");
    }
    internal static string HashImei(string response)
    {
        var matches = Regex.Matches(response, @"(?m)^\s*(\d{15})\s*$");
        if (matches.Count != 1) throw new InvalidDataException("Expected one stable modem IMEI.");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(matches[0].Groups[1].Value)));
    }
    internal async Task MatchIdentity(string expected, CancellationToken ct = default)
    {
        if (HashImei(await At("AT+GSN", ct)) != expected) throw new InvalidOperationException("Different modem at AT port; refusing mutation.");
    }
    internal async Task<string> WaitState(string? expected, int seconds = 120, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            try
            {
                var match = Regex.Match(await At("AT+QCFG=\"ims\"", ct), "\\+QCFG:\\s*\"ims\",([012]),([01])");
                if (match.Success)
                {
                    var state = match.Groups[1].Value + "," + match.Groups[2].Value;
                    if (expected == null || expected == state) return state;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException) { }
            await Task.Delay(2000, ct);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Modem IMS state did not reach " + expected);
    }
    internal async Task Restart(CancellationToken ct = default)
    {
        RequireOk(await At("AT+CFUN=1,1", ct));
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
    }
    internal async Task Restore(string expectedHash, Action<string> log)
    {
        // Recovery deliberately ignores the cancelled parent token.
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            try { await MatchIdentity(expectedHash); break; }
            catch (Exception ex) when (DateTime.UtcNow < deadline && ex is IOException or UnauthorizedAccessException or TimeoutException)
            { await Task.Delay(2000); }
        }
        var state = await WaitState(null, 180);
        if (state == "1,1") return;
        if (state is not ("2,0" or "1,0")) throw new InvalidOperationException("Unexpected IMS state; recovery refused.");
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await MatchIdentity(expectedHash);
            RequireOk(await At("AT+QCFG=\"ims\",1"));
            await Restart();
            try { await WaitState("1,1"); log("RESTORED ims=1,1"); return; }
            catch (TimeoutException) when (attempt == 1) { log("Retrying IMS restoration once."); }
        }
    }
    internal static IPAddress ParseDottedIpv6(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 16 || parts.Any(p => !byte.TryParse(p, out _))) throw new InvalidDataException("Expected sixteen decimal IPv6 octets.");
        return new IPAddress(parts.Select(byte.Parse).ToArray());
    }
    internal static IPAddress[] ParsePcscf(string response, IPAddress pppLocal)
    {
        var lines = response.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("+CGCONTRDP:")).ToArray();
        if (lines.Length != 1) throw new InvalidDataException("CID 5 did not return one IMS context.");
        var fields = lines[0]["+CGCONTRDP:".Length..].Split(',').Select(f => f.Trim().Trim('"')).ToArray();
        if (fields.Length < 9 || fields[0] != "5" || fields[2] != "ims") throw new InvalidDataException("Wrong IMS runtime context.");
        var modemLocal = ParseDottedIpv6(fields[3]);
        if (!RouteIsolation.IsUsable(pppLocal) || !modemLocal.GetAddressBytes().AsSpan(0, 8).SequenceEqual(pppLocal.GetAddressBytes().AsSpan(0, 8)))
            throw new InvalidDataException("PPP source does not share the modem IMS /64.");
        var pcscf = new[] { ParseDottedIpv6(fields[7]), ParseDottedIpv6(fields[8]) }.Distinct().ToArray();
        if (pcscf.Any(a => !RouteIsolation.IsUsable(a) || a.Equals(pppLocal))) throw new InvalidDataException("Invalid IMS P-CSCF.");
        return pcscf;
    }
}
