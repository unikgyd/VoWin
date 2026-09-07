using System.Net.Http;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

/// <summary>Pure C# QQ Open Platform Bot API + Gateway WebSocket client.</summary>
internal sealed class QqBotClient : IDisposable
{
    private const string TokenBase = "https://bots.qq.com";
    private const string ApiBase = "https://api.sgroup.qq.com";
    private const int GroupAndC2cIntent = 1 << 25;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTime _tokenExpiresAt;
    private int? _lastSequence;
    private long _lastHeartbeatAckTicks;

    public async Task RunAsync(RemoteControlSettings settings, Func<RemoteInboundMessage, Task> onMessage,
        Action<string> setStatus, CancellationToken ct)
    {
        var retry = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                setStatus("正在连接 QQ 官方 Gateway…");
                await RunConnectionAsync(settings, onMessage, setStatus, ct).ConfigureAwait(false);
                retry = 0;
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _accessToken = null;
                var delay = Math.Min(60, new[] { 2, 5, 10, 20, 30, 60 }[Math.Min(retry++, 5)]);
                setStatus($"连接异常，{delay} 秒后重试：{SafeMessage(ex)}");
                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            }
        }
        setStatus("未连接");
    }

    private async Task RunConnectionAsync(RemoteControlSettings settings,
        Func<RemoteInboundMessage, Task> onMessage, Action<string> setStatus, CancellationToken ct)
    {
        var token = await GetTokenAsync(settings, ct).ConfigureAwait(false);
        var gateway = await GetGatewayAsync(token, ct).ConfigureAwait(false);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", "VoWin/1.0");
        await ws.ConnectAsync(new Uri(gateway), ct).ConfigureAwait(false);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref _lastHeartbeatAckTicks, DateTime.UtcNow.Ticks);
        Task? heartbeat = null;
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(ws, connectionCts.Token).ConfigureAwait(false);
                if (json == null)
                {
                    if ((int?)ws.CloseStatus == 4004) _accessToken = null;
                    break;
                }
                var root = JsonNode.Parse(json)?.AsObject();
                if (root == null) continue;
                var op = root["op"]?.GetValue<int>() ?? -1;
                if (root["s"] != null) _lastSequence = root["s"]!.GetValue<int>();

                switch (op)
                {
                    case 10:
                        var interval = root["d"]?["heartbeat_interval"]?.GetValue<int>() ?? 30000;
                        await SendJsonAsync(ws, new { op = 2, d = new { token = $"QQBot {token}", intents = GroupAndC2cIntent, shard = new[] { 0, 1 } } }, ct).ConfigureAwait(false);
                        heartbeat = Task.Run(async () =>
                        {
                            try { await HeartbeatAsync(ws, interval, connectionCts.Token).ConfigureAwait(false); }
                            catch when (!ct.IsCancellationRequested) { connectionCts.Cancel(); throw; }
                        }, connectionCts.Token);
                        break;
                    case 0:
                        var eventName = root["t"]?.GetValue<string>() ?? string.Empty;
                        if (eventName == "READY") { setStatus("已连接"); break; }
                        if (eventName is "C2C_MESSAGE_CREATE" or "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE")
                        {
                            var d = root["d"] as JsonObject;
                            if (d == null) break;
                            var isGroup = eventName.StartsWith("GROUP", StringComparison.Ordinal);
                            var sender = isGroup
                                ? d["author"]?["member_openid"]?.GetValue<string>()
                                : d["author"]?["user_openid"]?.GetValue<string>();
                            var conversation = isGroup ? d["group_openid"]?.GetValue<string>() : sender;
                            var text = d["content"]?.GetValue<string>() ?? string.Empty;
                            text = Regex.Replace(text, @"<@!?[^>]+>", string.Empty).Trim();
                            var messageId = d["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                            if (!string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(conversation) && !string.IsNullOrWhiteSpace(text))
                                await onMessage(new RemoteInboundMessage(RemoteChannelKind.Qq, sender, conversation,
                                    text, messageId, IsGroup: isGroup)).ConfigureAwait(false);
                        }
                        break;
                    case 7:
                    case 9:
                        return;
                    case 11:
                        Volatile.Write(ref _lastHeartbeatAckTicks, DateTime.UtcNow.Ticks);
                        break;
                }
            }
        }
        finally
        {
            connectionCts.Cancel();
            if (heartbeat != null) try { await heartbeat.ConfigureAwait(false); } catch { }
        }
    }

    public async Task SendAsync(RemoteControlSettings settings, RemoteReplyTarget target, string text, CancellationToken ct)
    {
        var token = await GetTokenAsync(settings, ct).ConfigureAwait(false);
        var scope = target.IsGroup ? "groups" : "users";
        var url = $"{ApiBase}/v2/{scope}/{Uri.EscapeDataString(target.ConversationId)}/messages";
        var body = new Dictionary<string, object?>
        {
            ["content"] = text.Length > 1900 ? text[..1900] : text,
            ["msg_type"] = 0,
            ["msg_seq"] = Random.Shared.Next(1, 65535)
        };
        if (!string.IsNullOrWhiteSpace(target.MessageId)) body["msg_id"] = target.MessageId;
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"QQBot {token}");
        req.Headers.TryAddWithoutValidation("User-Agent", "VoWin/1.0");
        using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"QQ 发送失败 HTTP {(int)response.StatusCode}: {raw[..Math.Min(raw.Length, 200)]}");
    }

    public async Task ValidateCredentialsAsync(RemoteControlSettings settings, CancellationToken ct)
    {
        var token = await GetTokenAsync(settings, ct, true).ConfigureAwait(false);
        _ = await GetGatewayAsync(token, ct).ConfigureAwait(false);
    }

    private async Task<string> GetTokenAsync(RemoteControlSettings settings, CancellationToken ct, bool force = false)
    {
        if (!force && !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiresAt.AddMinutes(-5)) return _accessToken;
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiresAt.AddMinutes(-5)) return _accessToken;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{TokenBase}/app/getAppAccessToken")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { appId = settings.QqAppId.Trim(), clientSecret = settings.QqClientSecret }), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("User-Agent", "VoWin/1.0");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var root = JsonNode.Parse(raw)?.AsObject();
            _accessToken = root?["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("QQ 未返回 access_token");
            var seconds = root?["expires_in"]?.GetValue<int>() ?? 7200;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(seconds);
            return _accessToken;
        }
        finally { _tokenGate.Release(); }
    }

    private async Task<string> GetGatewayAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/gateway");
        req.Headers.TryAddWithoutValidation("Authorization", $"QQBot {token}");
        req.Headers.TryAddWithoutValidation("User-Agent", "VoWin/1.0");
        using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(raw)?["url"]?.GetValue<string>() ?? throw new InvalidOperationException("QQ 未返回 Gateway 地址");
    }

    private async Task HeartbeatAsync(ClientWebSocket ws, int intervalMs, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1000, intervalMs)));
        var sentOnce = false;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false) && ws.State == WebSocketState.Open)
        {
            if (sentOnce && DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastHeartbeatAckTicks), DateTimeKind.Utc)
                > TimeSpan.FromMilliseconds(intervalMs * 2.5))
                throw new TimeoutException("QQ Gateway 心跳响应超时");
            await SendJsonAsync(ws, new { op = 1, d = _lastSequence }, ct).ConfigureAwait(false);
            sentOnce = true;
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await ws.SendAsync(data, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 1024 * 1024) throw new InvalidDataException("QQ Gateway 消息超过 1 MiB");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private static string SafeMessage(Exception ex) => ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
    public void Dispose() { _tokenGate.Dispose(); _http.Dispose(); }
}
