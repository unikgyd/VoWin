using System;
using System.Text.RegularExpressions;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Modems;
using qmiSharp.Services;
using qmiSharp.Transport;

namespace qmiSharp.Cli;

/// <summary>
/// Implements standard libqmi `qmicli` command line interface for Windows.
/// Provides seamless command parity with Linux qmicli tools.
/// </summary>
public static class QmiCliRunner
{
    public static bool IsQmiCliCommand(string[] args)
    {
        return args.Any(a => a.StartsWith("--dms-") ||
                             a.StartsWith("--nas-") ||
                             a.StartsWith("--uim-") ||
                             a.StartsWith("--wds-") ||
                             a.StartsWith("--voice-") ||
                             a.StartsWith("--wms-") ||
                             a.StartsWith("--loc-") ||
                             a.StartsWith("--pdc-") ||
                             a.StartsWith("--oma-") ||
                             a.StartsWith("--pds-"));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string? devicePath = null;
        string? tcpEndpoint = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-d" && i + 1 < args.Length)
            {
                devicePath = args[i + 1];
            }
            else if (args[i].StartsWith("--device="))
            {
                devicePath = args[i]["--device=".Length..];
            }
            else if (args[i].StartsWith("--tcp="))
            {
                tcpEndpoint = args[i]["--tcp=".Length..];
            }
        }

        IQmiTransport transport;
        string target;
        if (!string.IsNullOrWhiteSpace(tcpEndpoint))
        {
            int separator = tcpEndpoint.LastIndexOf(':');
            if (separator <= 0 || !int.TryParse(tcpEndpoint[(separator + 1)..], out int port))
                throw new ArgumentException("--tcp must be in host:port form");
            string host = tcpEndpoint[..separator];
            transport = new SocketQmiTransport(host, port);
            target = tcpEndpoint;
        }
        else if (!string.IsNullOrWhiteSpace(devicePath))
        {
            if (devicePath.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An AT COM port is not a raw QMI endpoint. Use --hardware-info for the Windows modem, --tcp=host:port for a QMI proxy, or --device=<Win32 raw-QMI device path>.");
            transport = new Win32DeviceTransport(devicePath);
            target = devicePath;
        }
        else
        {
            throw new ArgumentException("Raw QMI commands require --tcp=host:port or --device=<Win32 raw-QMI device path>. Windows AT COM ports cannot carry QMUX frames.");
        }

        Console.WriteLine($"[qmicli] Target QMI endpoint: {target}");

        await using var ownedTransport = transport;
        await using var client = await QmiClient.CreateAsync(transport).ConfigureAwait(false);

        foreach (var arg in args)
        {
            if (arg == "--dms-get-model")
            {
                await using var dms = new DmsService(client);
                string model = await dms.GetModelAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Model: '{model}'");
            }
            else if (arg == "--dms-get-manufacturer")
            {
                await using var dms = new DmsService(client);
                string mfr = await dms.GetManufacturerAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Manufacturer: '{mfr}'");
            }
            else if (arg == "--dms-get-revision")
            {
                await using var dms = new DmsService(client);
                string rev = await dms.GetRevisionAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Revision: '{rev}'");
            }
            else if (arg == "--dms-get-ids")
            {
                await using var dms = new DmsService(client);
                var ids = await dms.GetSerialNumbersAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] ESN: '{ids.ESN}'\n    IMEI: '{ids.IMEI}'\n    MEID: '{ids.MEID}'\n    IMEISV: '{ids.IMEISV}'");
            }
            else if (arg == "--dms-get-operating-mode")
            {
                await using var dms = new DmsService(client);
                var mode = await dms.GetOperatingModeAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Operating mode: '{mode}'");
            }
            else if (arg == "--dms-get-msisdn")
            {
                await using var dms = new DmsService(client);
                string msisdn = await dms.GetMsisdnAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] MSISDN: '{msisdn}'");
            }
            else if (arg == "--nas-get-signal-info")
            {
                await using var nas = new NasService(client);
                var sig = await nas.GetSignalStrengthAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Signal Info:\n    RSSI: {sig.Rssi} dBm\n    LTE RSRQ: {sig.Rsrq} dB\n    LTE RSRP: {sig.Rsrp} dBm\n    LTE SNR: {sig.Snr:F1} dB");
            }
            else if (arg == "--nas-get-serving-system")
            {
                await using var nas = new NasService(client);
                var ss = await nas.GetServingSystemAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Serving System:\n    Registration state: '{ss.RegistrationState}'\n    CS attached: '{ss.CsAttached}'\n    PS attached: '{ss.PsAttached}'\n    PLMN: '{ss.PlmnName}' (MCC: {ss.Mcc}, MNC: {ss.Mnc})\n    Radio interface: '{ss.RadioInterface}'\n    Roaming: {ss.Roaming}");
            }
            else if (arg == "--nas-get-home-network")
            {
                await using var nas = new NasService(client);
                var home = await nas.GetHomeNetworkAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Home Network:\n    MCC: '{home.Mcc}'\n    MNC: '{home.Mnc}'\n    Name: '{home.Name}'");
            }
            else if (arg == "--uim-get-card-status")
            {
                await using var uim = new UimService(client);
                var card = await uim.GetCardStatusAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Card Status:\n    Card state: '{card.State}'\n    Num cards: {card.NumCards}");
            }
            else if (arg == "--uim-get-slot-status")
            {
                await using var uim = new UimService(client);
                var slots = await uim.GetSlotStatusAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Physical Slots ({slots.Count}):");
                foreach (var s in slots)
                {
                    Console.WriteLine($"    Slot {s.PhysicalSlotNumber}: Active: {s.IsActive}, CardPresent: {s.CardPresent}");
                }
            }
            else if (arg == "--wds-get-packet-service-status")
            {
                await using var wds = new WdsService(client);
                var status = await wds.GetPacketServiceStatusAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Packet service status: '{status}'");
            }
            else if (arg == "--wds-get-channel-rates")
            {
                await using var wds = new WdsService(client);
                var rates = await wds.GetCurrentChannelRateAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Channel rates: Tx: {rates.CurrentTxRate} bps, Rx: {rates.CurrentRxRate} bps (Max: {rates.MaxTxRate}/{rates.MaxRxRate})");
            }
            else if (arg == "--wds-get-current-settings")
            {
                await using var wds = new WdsService(client);
                var settings = await wds.GetCurrentSettingsAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Current Settings:\n    IP: {settings.Ipv4Address}\n    Mask: {settings.SubnetMask}\n    Gateway: {settings.Gateway}\n    DNS1: {settings.PrimaryDns}\n    DNS2: {settings.SecondaryDns}");
            }
            else if (arg == "--wds-get-packet-statistics")
            {
                await using var wds = new WdsService(client);
                var stats = await wds.GetPacketStatisticsAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] Packet Statistics:\n    Tx OK: {stats.TxPacketsOk} pkts ({stats.TxBytesOk} bytes)\n    Rx OK: {stats.RxPacketsOk} pkts ({stats.RxBytesOk} bytes)\n    Errors: Tx {stats.TxPacketErrors}, Rx {stats.RxPacketErrors}");
            }
            else if (arg == "--wms-get-routes")
            {
                await using var wms = new WmsService(client);
                var routes = await wms.GetRoutesAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] SMS Routes ({routes.Count}):");
                foreach (var r in routes)
                {
                    Console.WriteLine($"    Type: {r.MessageType}, Class: {r.MessageClass}, Storage: {r.StorageType}, ReceiptAction: {r.ReceiptAction}");
                }
            }
            else if (arg == "--wms-get-smsc-address")
            {
                await using var wms = new WmsService(client);
                string smsc = await wms.GetSmscAddressAsync().ConfigureAwait(false);
                Console.WriteLine($"[{target}] SMSC Address: '{smsc}'");
            }
            else if (arg.StartsWith("--voice-dial="))
            {
                string number = arg["--voice-dial=".Length..];
                if (number != QuectelModemBridge.AllowedDialNumber)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[SECURITY REJECTED] Dialing {number} is restricted by user policy. Only {QuectelModemBridge.AllowedDialNumber} is permitted.");
                    Console.ResetColor();
                    return 1;
                }

                Console.WriteLine($"[qmicli] Dialing {number} via QMI Voice Service...");
                await using var voice = new VoiceService(client);
                voice.CallStatusChanged += info =>
                {
                    Console.WriteLine($"[qmicli VOICE EVENT] Call ID {info.CallId}: State = {info.State}");
                };

                byte callId = await voice.DialCallAsync(number).ConfigureAwait(false);
                Console.WriteLine($"[qmicli] Call originated. Call ID: {callId}. Ringing for 5 seconds...");
                await Task.Delay(5000).ConfigureAwait(false);
                await voice.EndCallAsync(callId).ConfigureAwait(false);
                Console.WriteLine($"[qmicli] Call ID {callId} ended safely.");
            }
        }

        return 0;
    }
}
