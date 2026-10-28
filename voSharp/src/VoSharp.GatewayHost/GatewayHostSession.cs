using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VoSharp.Kernel.SipGateway;
using VoSharp.Telephony.Calls;

namespace VoSharp.GatewayHost;

/// <summary>Versioned private stdio control channel. A management process owns its lifetime.
/// This initial host exposes the actual registrar/internal B2BUA/G.711/MESSAGE core;
/// unavailable carrier/IMS capabilities are rejected rather than simulated.</summary>
public static class GatewayHostSession
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };
    public static IReadOnlyList<string> Capabilities { get; } = Array.AsReadOnly(new[] { "sip.registrar", "sip.internal_calls", "sip.message.internal", "rtp.g711" });
    private sealed record Request(int Version, string? Id, string? Method, JsonElement Params);
    private sealed record Configuration(string BindAddress, int SipPort, string Realm,
        Dictionary<string, string> Accounts, bool MessagesEnabled = false, int MaximumContactsPerAccount = 8,
        GatewaySmsConfiguration? Sms = null, GatewaySmsCallback? SmsCallback = null)
    {
        public override string ToString() => "Gateway configuration (credentials omitted)";
    }

    public static async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        SipGateway? gateway = null;
        GatewaySmsController? controller = null;
        GatewaySmsConfiguration? smsConfiguration = null;
        async Task Write(object frame)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(frame, Json)).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        object Status() => new
        {
            running = gateway?.Status.IsRunning == true,
            sipAddress = gateway?.Status.LocalEndPoint?.Address.ToString(),
            sipPort = gateway?.Status.LocalEndPoint?.Port,
            registeredContacts = gateway?.Status.Registrations.Count ?? 0,
            activeDialogs = gateway?.Status.ActiveDialogs ?? 0,
            messagesEnabled = gateway?.Status.MessagesEnabled == true,
            messagesForwarded = gateway?.Status.MessagesForwarded ?? 0,
            messageFailures = gateway?.Status.MessageFailures ?? 0,
            carrierCalls = false, carrierSms = gateway != null && smsConfiguration?.Active == true, hostIms = false, voWifi = false,
        };
        async Task Stop()
        {
            var stopping = gateway;
            gateway = null;
            if (stopping != null) await stopping.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            controller?.Dispose(); controller = null; smsConfiguration = null;
        }
        try
        {
            await Write(new { version = 1, @event = "ready", capabilities = Capabilities.Append("sms.external_submit").ToArray(),
                platform = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "other",
                framework = RuntimeInformation.FrameworkDescription });
            while (await input.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 131072) return 2;
                Request? request;
                try { request = JsonSerializer.Deserialize<Request>(line, Json); }
                catch (JsonException) { await Write(new { version = 1, id = (string?)null, ok = false, error = "invalid_request" }); continue; }
                if (request is null || request.Version != 1 || request.Id is null ||
                    !Regex.IsMatch(request.Id, "^[A-Za-z0-9_-]{1,64}$"))
                {
                    await Write(new { version = 1, id = (string?)null, ok = false, error = "invalid_request" }); continue;
                }
                string? error = null;
                try
                {
                    switch (request.Method)
                    {
                        case "status": break;
                        case "stop": await Stop().ConfigureAwait(false); break;
                        case "shutdown":
                            await Stop().ConfigureAwait(false);
                            await Write(new { version = 1, id = request.Id, ok = true, result = Status() });
                            return 0;
                        case "sms.received":
                            if (gateway == null || controller == null) throw new InvalidOperationException();
                            var incoming = request.Params.Deserialize<IncomingSms>(Json) ?? throw new ArgumentException();
                            if (incoming.Id is not { Length: > 0 and <= 128 } || incoming.SlotId == null ||
                                incoming.Sender is not { Length: > 0 and <= 100 } || incoming.Text == null ||
                                System.Text.Encoding.UTF8.GetByteCount(incoming.Text) > 16384 || incoming.TimestampUtc.Kind != DateTimeKind.Utc)
                                throw new ArgumentException();
                            controller.Received(incoming.SlotId, incoming.Id, incoming.Sender, incoming.Text, incoming.TimestampUtc);
                            break;
                        case "start":
                            if (gateway != null) { error = "already_running"; break; }
                            var config = request.Params.Deserialize<Configuration>(Json) ?? throw new ArgumentException();
                            if (config.Accounts is null || config.Accounts.Count > 64 || config.Accounts.Any(pair => pair.Value is null || pair.Value.Length > 256) || config.SipPort < 1024)
                                throw new ArgumentException();
                            config.Sms?.Validate(config.Accounts, config.MessagesEnabled);
                            var options = new SipGatewayOptions
                            {
                                BindAddress = IPAddress.Parse(config.BindAddress), SipPort = config.SipPort,
                                Realm = config.Realm, Accounts = config.Accounts,
                                MaximumContactsPerAccount = config.MaximumContactsPerAccount,
                                RequireConfiguredLines = true, AllowCarrierCalls = false,
                                MessagesEnabled = config.MessagesEnabled, AllowOutgoingSms = config.Sms?.AllowOutgoing == true,
                                ForwardIncomingSms = config.Sms?.ForwardIncoming == true,
                                LineRoutes = config.Sms?.Routes() ?? [], ExtensionPolicies = config.Sms?.Policies(config.Accounts.Keys) ?? [],
                                DefaultOutboundLineId = config.Sms?.AllowOutgoing == true ? config.Sms.DefaultLine : null,
                            };
                            var startingController = new GatewaySmsController(config.Sms, config.SmsCallback);
                            SipGateway? starting = null;
                            try { starting = new SipGateway(options, startingController); await starting.StartAsync(ct).ConfigureAwait(false); gateway = starting; controller = startingController; smsConfiguration = config.Sms; }
                            catch { if (starting != null) await starting.DisposeAsync(); startingController.Dispose(); throw; }
                            break;
                        default: error = "unsupported_method"; break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (TimeoutException) { await Write(new { version = 1, id = request.Id, ok = false, error = "shutdown_timeout" }); return 2; }
                catch { error = "operation_failed"; } // Never echo input, credentials or native paths.
                if (error is null) await Write(new { version = 1, id = request.Id, ok = true, result = Status() });
                else await Write(new { version = 1, id = request.Id, ok = false, error });
            }
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        finally { try { await Stop().ConfigureAwait(false); } catch { } }
    }

    private sealed record IncomingSms(string SlotId, string Id, string Sender, string Text, DateTime TimestampUtc);
}
