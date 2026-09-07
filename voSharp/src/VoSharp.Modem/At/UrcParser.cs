using System.Text.RegularExpressions;
using VoSharp.Common.Events;

namespace VoSharp.Modem.At;

public record UrcEvent(
    string RawLine,
    string Category,
    object? ParsedData
);

public static class UrcParser
{
    private static readonly Regex ClipRegex = new(@"^\+CLIP:\s*""([^""]+)""(?:,\s*(\d+))?", RegexOptions.Compiled);
    private static readonly Regex CmtiRegex = new(@"^\+CMTI:\s*""([^""]+)"",\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex CdsiRegex = new(@"^\+CDSI:\s*""([^""]+)"",\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex CregRegex = new(@"^\+(?:CEREG|CREG|CGREG):\s*(?:\d+,)?(\d+)", RegexOptions.Compiled);
    private static readonly Regex CsqRegex = new(@"^\+CSQ:\s*(\d+),(\d+)", RegexOptions.Compiled);
    private static readonly Regex CpinRegex = new(@"^\+CPIN:\s*([A-Z\s]+)", RegexOptions.Compiled);

    public static bool IsUrc(string line)
    {
        line = line.Trim();
        if (string.IsNullOrEmpty(line)) return false;

        if (line.Equals("RING", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("+CRING:", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("NO CARRIER", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("BUSY", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("NO ANSWER", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("NO DIALTONE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return line.StartsWith("+CLIP:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CMTI:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CDSI:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CDS:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CEREG:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CREG:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CGREG:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CSQ:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+CPIN:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("+QIND:", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("RDY", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("CALL READY", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("SMS READY", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExpectedCommandResponse(string? command, string line)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        return command.Trim().ToUpperInvariant() switch
        {
            "AT+CPIN?" => line.StartsWith("+CPIN:", StringComparison.OrdinalIgnoreCase),
            "AT+CSQ" => line.StartsWith("+CSQ:", StringComparison.OrdinalIgnoreCase),
            "AT+CREG?" => line.StartsWith("+CREG:", StringComparison.OrdinalIgnoreCase),
            "AT+CGREG?" => line.StartsWith("+CGREG:", StringComparison.OrdinalIgnoreCase),
            "AT+CEREG?" => line.StartsWith("+CEREG:", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static UrcEvent? Parse(string line, AsyncEventBus? bus = null)
    {
        line = line.Trim();
        if (!IsUrc(line)) return null;

        if (line.Equals("RING", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("+CRING:", StringComparison.OrdinalIgnoreCase))
        {
            bus?.Publish(EventTopics.CallIncoming, "Modem", "RING");
            bus?.Publish(EventTopics.CallState, "Modem", "RINGING");
            return new UrcEvent(line, "Call", "RING");
        }

        if (line.Equals("NO CARRIER", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("BUSY", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("NO ANSWER", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("NO DIALTONE", StringComparison.OrdinalIgnoreCase))
        {
            bus?.Publish(EventTopics.CallEnded, "Modem", line);
            bus?.Publish(EventTopics.CallState, "Modem", "ENDED");
            return new UrcEvent(line, "Call", line);
        }

        var clipMatch = ClipRegex.Match(line);
        if (clipMatch.Success)
        {
            var callerNumber = clipMatch.Groups[1].Value;
            bus?.Publish(EventTopics.CallIncoming, "Modem", callerNumber);
            bus?.Publish(EventTopics.CallState, "Modem", "RINGING");
            return new UrcEvent(line, "CallIncoming", callerNumber);
        }

        var cmtiMatch = CmtiRegex.Match(line);
        if (cmtiMatch.Success)
        {
            var storage = cmtiMatch.Groups[1].Value;
            var index = int.Parse(cmtiMatch.Groups[2].Value);
            bus?.Publish(EventTopics.SmsIncoming, "Modem", new { Storage = storage, Index = index });
            return new UrcEvent(line, "SmsNotification", new { Storage = storage, Index = index });
        }

        var cdsiMatch = CdsiRegex.Match(line);
        if (cdsiMatch.Success)
        {
            var storage = cdsiMatch.Groups[1].Value;
            var index = int.Parse(cdsiMatch.Groups[2].Value);
            bus?.Publish(EventTopics.SmsIncoming, "Modem", new { Storage = storage, Index = index, IsStatusReport = true });
            return new UrcEvent(line, "SmsStatusReportNotification", new { Storage = storage, Index = index });
        }

        var cregMatch = CregRegex.Match(line);
        if (cregMatch.Success)
        {
            var statVal = int.Parse(cregMatch.Groups[1].Value);
            var status = statVal switch
            {
                1 => "HOME",
                2 => "SEARCHING",
                5 => "ROAMING",
                _ => "UNREGISTERED"
            };
            bus?.Publish(EventTopics.NetworkRegistration, "Modem", status);
            return new UrcEvent(line, "Registration", status);
        }

        var csqMatch = CsqRegex.Match(line);
        if (csqMatch.Success)
        {
            var rssi = int.Parse(csqMatch.Groups[1].Value);
            if (rssi == 99 || rssi < 0)
            {
                bus?.Publish(EventTopics.ModemSignal, "Modem", new { RSSI = 0, Bars = 0 });
                return new UrcEvent(line, "Signal", new { RSSI = 0, Bars = 0 });
            }

            var dbm = -113 + (rssi * 2);
            var bars = rssi switch
            {
                >= 19 => 5, // >= -75 dBm (极佳)
                >= 14 => 4, // >= -85 dBm (良好)
                >= 9 => 3,  // >= -95 dBm (一般)
                >= 4 => 2,  // >= -105 dBm (较弱)
                >= 1 => 1,  // > -113 dBm (微弱)
                _ => 0      // <= -113 dBm (无信号)
            };
            bus?.Publish(EventTopics.ModemSignal, "Modem", new { RSSI = dbm, Bars = bars });
            return new UrcEvent(line, "Signal", new { RSSI = dbm, Bars = bars });
        }

        var cpinMatch = CpinRegex.Match(line);
        if (cpinMatch.Success)
        {
            var status = cpinMatch.Groups[1].Value.Trim();
            var simEvent = status.Equals("READY", StringComparison.OrdinalIgnoreCase) ? "READY" : status;
            bus?.Publish(EventTopics.ModemSim, "Modem", simEvent);
            return new UrcEvent(line, "SimStatus", simEvent);
        }

        bus?.Publish(EventTopics.ModemUrc, "Modem", line);
        return new UrcEvent(line, "Generic", line);
    }
}
