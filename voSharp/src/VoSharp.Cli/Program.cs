using VoSharp.Common.Events;
using VoSharp.Kernel;
using VoSharp.Kernel.Ipc;
using VoSharp.Modem;
using VoSharp.StateMachine;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("==========================================================");
Console.WriteLine("           voSharp - Windows Telephony Kernel             ");
Console.WriteLine("       Pure C# State Machine & Subsystem Orchestrator     ");
Console.WriteLine("==========================================================");

bool clientMode = args.Contains("--client");

if (clientMode)
{
    Console.WriteLine("[CLI] Running in Named Pipe Client Mode (Target: \\\\.\\pipe\\voSharp_kernel)...");
    await using var client = new NamedPipeClient();
    try
    {
        await client.ConnectAsync(3000);
        Console.WriteLine("[CLI] Connected to voSharp Kernel. Type commands below ('quit' to exit):");
        while (true)
        {
            Console.Write("voSharp-cli> ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) continue;
            if (input is "quit" or "exit") break;

            var response = await client.SendCommandAsync(input);
            Console.WriteLine(response);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CLI] Connection error: {ex.Message}");
    }
    return;
}

// Daemon / Host Mode
await using var kernel = new VoKernel();

// Hook state changes for live console telemetry
kernel.StateMachine.OnEnter(TelephonyState.SimReady, (from, to, payload) =>
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[State] Transition -> {to} (Payload: {payload})");
    Console.ResetColor();
});

kernel.StateMachine.OnEnter(TelephonyState.NetworkRegistered, (from, to, payload) =>
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"[State] Transition -> {to} (Network: {payload})");
    Console.ResetColor();
});

kernel.StateMachine.OnEnter(TelephonyState.CallRinging, (from, to, payload) =>
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[State] Transition -> {to} (Call ringing: {payload})");
    Console.ResetColor();
});

kernel.StateMachine.OnEnter(TelephonyState.CallActive, (from, to, payload) =>
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[State] Transition -> {to} (Call in progress: {payload})");
    Console.ResetColor();
});

kernel.StateMachine.OnEnter(TelephonyState.CallEnded, (from, to, payload) =>
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"[State] Transition -> {to} (Call ended)");
    Console.ResetColor();
});

kernel.EventBus.Subscribe("sms.received", ev =>
{
    if (ev.Payload is VoSharp.Telephony.Sms.SmsMessage sms)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"\n[SMS RECEIVED] Index: {sms.Index} | From: {sms.SenderOrRecipient} | Time: {sms.Timestamp:yyyy-MM-dd HH:mm:ss}\n  Content: {sms.Text}");
        Console.ResetColor();
        Console.Write("voSharp> ");
    }
});

kernel.EventBus.Subscribe("vowifi.state.changed", ev =>
{
    Console.ForegroundColor = ConsoleColor.Blue;
    Console.WriteLine($"\n[VoWiFi] State changed -> {ev.Payload}");
    Console.ResetColor();
    Console.Write("voSharp> ");
});

kernel.EventBus.Subscribe("call.dialing", ev =>
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n[SIP CALL] Dialing target... (SDP Offer negotiated)");
    Console.ResetColor();
    Console.Write("voSharp> ");
});

kernel.EventBus.Subscribe("call.ringing", ev =>
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n[SIP CALL] 180 Ringing! Remote party is ringing (Audible ringback playing on Windows speakers)...");
    Console.ResetColor();
    Console.Write("voSharp> ");
});

kernel.EventBus.Subscribe("call.connected", ev =>
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n[SIP CALL CONNECTED] 200 OK! Call established. Live audio output active on Windows PC speakers!");
    Console.ResetColor();
    Console.Write("voSharp> ");
});

kernel.EventBus.Subscribe("call.ended", ev =>
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"\n[SIP CALL ENDED] Call terminated. Audio recording saved to disk.");
    Console.ResetColor();
    Console.Write("voSharp> ");
});

// Start Windows Named Pipe Server
await using var pipeServer = new NamedPipeServer(kernel);
pipeServer.Start();
Console.WriteLine($"[IPC] Windows Named Pipe Server active at \\\\.\\pipe\\{pipeServer.PipeName}");

// Probing available COM ports
var details = WindowsModemDetector.GetDetailedPorts();
Console.WriteLine($"[Hardware] Detected {details.Count} COM port(s):");
foreach (var d in details)
{
    var tag = d.IsQuectel ? " (Quectel / DJI Cellular)" : (d.IsBluetooth ? " (Bluetooth)" : "");
    Console.WriteLine($"  - {d.PortName}{tag}");
}

var autoPort = args.FirstOrDefault(a => a.StartsWith("COM", StringComparison.OrdinalIgnoreCase));
if (!string.IsNullOrEmpty(autoPort))
{
    Console.WriteLine($"[Hardware] Connecting to specified port {autoPort}...");
    if (await kernel.AttachModemAsync(autoPort))
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Hardware] Connected to modem on {autoPort} successfully!");
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine($"[Hardware] Could not initialize modem on {autoPort}. Running in simulated mode.");
    }
}
else
{
    Console.WriteLine("[Hardware] Auto-probing cellular modem port...");
    var detectedPort = await WindowsModemDetector.ProbeModemPortAsync();
    if (!string.IsNullOrEmpty(detectedPort) && await kernel.AttachModemAsync(detectedPort))
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Hardware] Auto-attached DJI / Quectel cellular modem on {detectedPort}!");
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine("[Hardware] No active cellular modem responded. You can use 'attach <COM_PORT>' anytime.");
    }
}

Console.WriteLine("\n[Kernel] State: " + kernel.StateMachine.CurrentState);
Console.WriteLine("Available commands:");
Console.WriteLine("  ports                         - Scan & list available COM serial ports");
Console.WriteLine("  attach <port>                 - Attach cellular modem (e.g. attach COM3)");
Console.WriteLine("  status                        - View system state and active calls");
Console.WriteLine("  history                       - View state transition history");
Console.WriteLine("  signal                        - Check signal RSSI & bars");
Console.WriteLine("  sim                           - Check SIM identity (IMSI/ICCID)");
Console.WriteLine("  flightmode [on|off]           - Check or set modem airplane/flight mode (CFUN)");
Console.WriteLine("  roaming [on|off]              - Check or configure domestic & international roaming");
Console.WriteLine("  cops                          - Query current mobile operator and network");
Console.WriteLine("  at <command>                  - Execute raw AT command (e.g. at AT+CSQ, at AT+QNWINFO)");
Console.WriteLine("  slot [1|2]                    - Query or switch DJI hardware SIM slot (1=Physical SIM, 2=Built-in eSIM)");
Console.WriteLine("  euicc eid                     - Query eUICC 32-digit EID");
Console.WriteLine("  euicc list                    - List all installed eSIM profiles");
Console.WriteLine("  euicc switch <iccid/aid>      - Switch and activate target eSIM profile");
Console.WriteLine("  euicc disable <iccid/aid>     - Deactivate target eSIM profile");
Console.WriteLine("  euicc delete <iccid/aid>      - Delete target eSIM profile");
Console.WriteLine("  euicc rename <iccid/aid> <n>  - Set nickname for eSIM profile");
Console.WriteLine("  euicc backend [pcsc|modem]    - Switch eUICC backend between PC/SC and AT Modem");
Console.WriteLine("  euicc info                    - Query eUICC chip capabilities");
Console.WriteLine("  sms list [all|unread|read]    - List stored SMS messages");
Console.WriteLine("  sms read <index>              - Read SMS message by index");
Console.WriteLine("  sms delete <index|all>        - Delete SMS message by index or all");
Console.WriteLine("  sms send <num> <text>         - Send 3GPP PDU SMS (e.g. sms send 10086 CXCX)");
Console.WriteLine("  vowifi status                 - Check VoWiFi & IMS registration status");
Console.WriteLine("  vowifi start [epdg]           - Establish VoWiFi IPsec tunnel & IMS registration");
Console.WriteLine("  vowifi stop                   - Disconnect VoWiFi tunnel");
Console.WriteLine("  vowifi info                   - Query 3GPP ePDG FQDN & resolved IP endpoints");
Console.WriteLine("  call dial <num> / call <num>  - Dial phone call via VoWiFi SIP/RTP with live PC audio (e.g. call 185)");
Console.WriteLine("  call hangup / hangup          - Terminate active call and save WAV audio recording");
Console.WriteLine("  call dtmf <digit>             - Send in-band / SIP INFO DTMF tone during call");
Console.WriteLine("  reboot                        - Reboot modem baseband (AT+CFUN=1,1)");
Console.WriteLine("  dtmf <digit>                  - Send modem hardware DTMF tone");
Console.WriteLine("  mmi <code>                    - Parse & execute MMI code (e.g. mmi *#06#)");
Console.WriteLine("  aka <rand> <autn>             - Execute 3GPP Milenage AKA evaluation");
Console.WriteLine("  reset                         - Reset state machine to INIT");
Console.WriteLine("  quit / exit                   - Terminate voSharp Kernel\n");

while (true)
{
    Console.Write("voSharp> ");
    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) continue;
    if (input is "quit" or "exit")
    {
        Console.WriteLine("Shutting down voSharp kernel daemon...");
        break;
    }

    var result = await kernel.ExecuteCommandAsync(input);
    if (result.Success)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(result.ToJson());
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Error] {result.Message}");
        Console.ResetColor();
    }
}
