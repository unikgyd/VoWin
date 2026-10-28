using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoSharp.Kernel.SipGateway;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;

namespace VoSharp.GatewayHost;

internal sealed class GatewaySmsController : ISipGatewayCallController, ISipGatewayIdempotentMessageController, IDisposable
{
    private readonly HttpClient? _client;
    private readonly GatewaySmsConfiguration? _configuration;
    public GatewaySmsController(GatewaySmsConfiguration? configuration, GatewaySmsCallback? callback)
    {
        _configuration = configuration;
        if (configuration?.AllowOutgoing != true) return;
        ArgumentNullException.ThrowIfNull(callback);
        callback.Validate();
        _client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(2) })
        { BaseAddress = new Uri($"http://127.0.0.1:{callback.Port}/"), Timeout = TimeSpan.FromSeconds(85), MaxResponseContentBufferSize = 4096 };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", callback.Token);
    }
    public event EventHandler<SmsReceivedEventArgs>? SmsReceived;
    public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
    public event EventHandler<CallEndedEventArgs>? CallEnded { add { } remove { } }
    public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct) => Task.FromException<SipGatewayCarrierCall>(new NotSupportedException());
    public Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct) => Task.FromException<SipGatewayCarrierCall>(new NotSupportedException());
    public Task HangupAsync(string? slotId, CancellationToken ct) => Task.CompletedTask;
    public Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) => Task.FromException(new NotSupportedException());
    public ImsCallManager? GetImsMedia(string? slotId) => null;

    public Task<SmsSubmitResult> SendSmsAsync(string number, string text, string? slotId, CancellationToken ct) => Task.FromException<SmsSubmitResult>(new InvalidOperationException("A stable SIP transaction identity is required."));
    public async Task<SmsSubmitResult> SendSmsOnceAsync(string operationId, string number, string text, string? slotId, CancellationToken ct)
    {
        if (_client == null || slotId == null || !System.Text.RegularExpressions.Regex.IsMatch(operationId, "^[0-9a-f]{64}$") || _configuration?.Lines?.GetValueOrDefault(slotId)?.AllowOutgoing != true)
            return Result("failed");
        // No application retry. The parent also deduplicates this operation ID so
        // a transport-level duplicate cannot submit the same paid operation twice.
        try
        {
            using var response = await _client.PostAsJsonAsync("submit", new { operationId, slotId, number, text }, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result("unknown");
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var result = JsonDocument.Parse(body);
            return result.RootElement.GetProperty("outcome").GetString() switch
            {
                "submitted" => Result("submitted_parts_unreported"),
                "failed" => Result("failed"),
                _ => Result("unknown"),
            };
        }
        catch { return Result("unknown"); } // Lost response is not proof that nothing was sent.

        SmsSubmitResult Result(string status) => new(number, text, SmsEncoding.Unknown, null,
            0, 0, 0, false, status, [], DateTime.UtcNow); // MM does not report per-part counts here.
    }
    public void Received(string slotId, string id, string sender, string text, DateTime timestamp)
    {
        if (_configuration?.ForwardIncoming != true || _configuration.Lines?.GetValueOrDefault(slotId)?.ForwardIncoming != true)
            throw new InvalidOperationException("SMS forwarding not authorized.");
        SmsReceived?.Invoke(this, new(new SmsMessage(0, SmsStatus.Unread, sender, text,
            timestamp.ToUniversalTime(), Direction: SmsDirection.Received, Id: id), slotId));
    }
    public void Dispose() => _client?.Dispose();
}
