using VoSharp.GatewayHost;
using System.Text;

Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false);

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("VoSharp.GatewayHost --stdio\nOptional headless SIP core. No modem discovery or carrier calls. SMS requires explicit parent-owned permissions and transport; no listener until an explicit start request.");
    return 0;
}
if (args is not ["--stdio"])
{
    Console.Error.WriteLine("Use --stdio or --help. No automatic SIP or hardware startup.");
    return 2;
}
// Other shared-library diagnostics cannot corrupt or leak into the control stream.
var protocolOutput = Console.Out;
Console.SetOut(Console.Error);
return await GatewayHostSession.RunAsync(Console.In, protocolOutput);
