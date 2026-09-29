using System.Diagnostics;
using System.Globalization;
using System.Text;
using qmiSharp.Generated;
using qmiSharp.Modems;

namespace qmiSharp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (args.Contains("--hardware-info"))
                return await PrintHardwareInfoAsync(GetOption(args, "--at-port=")).ConfigureAwait(false);
            if (args.Contains("--catalog"))
            {
                PrintCatalog();
                return 0;
            }
            if (args.Contains("--audit"))
            {
                PrintAudit();
                return 0;
            }
            if (Cli.QmiCliRunner.IsQmiCliCommand(args))
                return await Cli.QmiCliRunner.RunAsync(args).ConfigureAwait(false);

            PrintHelp();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"qmiSharp: {exception.Message}");
            return 1;
        }
    }

    private static string? GetOption(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

    private static void PrintHelp()
    {
        Console.WriteLine("qmiSharp for Windows");
        Console.WriteLine();
        Console.WriteLine("  --hardware-info [--at-port=COMx]   Read-only AT and Windows Mobile Broadband probe");
        Console.WriteLine("  --audit                             Show catalog coverage (metadata, not API implementation)");
        Console.WriteLine("  --catalog                           List catalog entries");
        Console.WriteLine("  --tcp=host:port <qmicli option>     Run QMI over a frame-preserving proxy");
        Console.WriteLine("  --device=<path> <qmicli option>     Run QMI over an explicit raw Win32 device");
        Console.WriteLine();
        Console.WriteLine("AT COM ports are not raw QMI endpoints. On standard Windows qcusbwwan/MBN drivers,");
        Console.WriteLine("use --hardware-info; QMI commands require an explicitly exposed raw endpoint or proxy.");
    }

    private static async Task<int> PrintHardwareInfoAsync(string? requestedPort)
    {
        string? port = requestedPort ?? QuectelModemBridge.DiscoverModemPort();
        if (port is null)
        {
            Console.Error.WriteLine("No responsive AT modem port was found.");
            return 2;
        }

        using (var modem = new QuectelModemBridge(port))
        {
            ModemDeviceInfo info = modem.QueryDeviceInfo();
            Console.WriteLine($"AT port:       {info.PortName}");
            Console.WriteLine($"Manufacturer:  {info.Manufacturer}");
            Console.WriteLine($"Model:         {info.Model}");
            Console.WriteLine($"Revision:      {info.Revision}");
            Console.WriteLine($"IMEI:          {info.Imei}");
            Console.WriteLine($"SIM:           {info.SimStatus}");
            Console.WriteLine($"Operator:      {info.Operator}");
            Console.WriteLine($"Signal (CSQ):  {info.SignalQuality}");
            Console.WriteLine($"USB net mode:  {OneLine(modem.SendCommand("AT+QCFG=\"usbnet\"", 400))}");
            Console.WriteLine($"IMS setting:   {OneLine(modem.SendCommand("AT+QCFG=\"ims\"", 400))}");
        }

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine();
            Console.WriteLine("Windows Mobile Broadband:");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding oemEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            using var process = Process.Start(new ProcessStartInfo("netsh", "mbn show interfaces")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oemEncoding,
                StandardErrorEncoding = oemEncoding,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is not null)
            {
                string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                Console.WriteLine(string.IsNullOrWhiteSpace(output) ? error.Trim() : output.Trim());
            }
        }
        return 0;
    }

    private static string OneLine(string response) => string.Join(" ", response
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(x => !x.Equals("OK", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("AT+", StringComparison.OrdinalIgnoreCase)));

    private static void PrintCatalog()
    {
        foreach (var item in QmiProtocolCatalog.AllMessages.OrderBy(x => x.Service).ThenBy(x => x.MessageId).ThenBy(x => x.IsIndication))
            Console.WriteLine($"{item.Service,-8} {(item.IsIndication ? "IND" : "MSG")} 0x{item.MessageId:X4} {item.Name}");
    }

    private static void PrintAudit()
    {
        int messages = QmiProtocolCatalog.AllMessages.Count(x => !x.IsIndication);
        int indications = QmiProtocolCatalog.AllMessages.Count(x => x.IsIndication);
        Console.WriteLine($"Catalog entries: {QmiProtocolCatalog.AllMessages.Count} ({messages} messages, {indications} indications)");
        Console.WriteLine("This number measures generated metadata coverage only; it does not claim a high-level API for every entry.");
        foreach (var group in QmiProtocolCatalog.AllMessages.GroupBy(x => x.Service).OrderBy(x => x.Key))
            Console.WriteLine($"  {group.Key,-8} {group.Count(),3}");
    }
}
