using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

/// <summary>Pure C# client for Tencent Weixin iLink Bot API, wire-compatible with openclaw-weixin.</summary>
internal sealed class WeixinIlinkClient : IDisposable
{
    private const string DefaultBaseUrl = "https://ilinkai.weixin.qq.com";
    private const int ClientVersion = 0x00020408; // openclaw-weixin 2.4.8
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, string> _contexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seenMessages = new(StringComparer.Ordinal);
    private string? _loginQrCode;
    private string _loginBaseUrl = DefaultBaseUrl;

    public async Task RunAsync(
        RemoteControlSettings settings,
        Func<RemoteInboundMessage, Task> onMessage,
        Action<string> setStatus,
        CancellationToken ct)
    {
        var buffer = string.Empty;
        var baseUrl = NormalizeBaseUrl(settings.WeixinBaseUrl);
        setStatus("正在连接微信 iLink…");
        try
        {
            try { await NotifyLifecycleAsync(settings, "ilink/bot/msg/notifystart", ct).ConfigureAwait(false); }
            catch { /* Official client treats lifecycle notification as best effort. */ }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        get_updates_buf = buffer,
                        base_info = BaseInfo()
                    });
                    using var request = CreateRequest(HttpMethod.Post, baseUrl, "ilink/bot/getupdates", settings.WeixinToken, body);
                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pollCts.CancelAfter(TimeSpan.FromSeconds(45));
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pollCts.Token).ConfigureAwait(false);
                    var raw = await response.Content.ReadAsStringAsync(pollCts.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var root = JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidDataException("微信返回空响应");
                    var ret = root["ret"]?.GetValue<int>() ?? 0;
                    var errcode = root["errcode"]?.GetValue<int>() ?? 0;
                    var errorCode = ret != 0 ? ret : errcode;
                    if (errorCode == -14)
                    {
                        buffer = string.Empty;
                        setStatus("微信凭据已过期；暂停请求 1 小时，可重新扫码立即恢复");
                        await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (errorCode != 0) throw new InvalidOperationException($"微信 iLink 错误 {errorCode}: {root["errmsg"]}");
                    buffer = root["get_updates_buf"]?.GetValue<string>() ?? buffer;
                    setStatus("已连接");

                    if (root["msgs"] is not JsonArray messages) continue;
                    foreach (var node in messages)
                    {
                        if (node is not JsonObject msg) continue;
                        var sender = msg["from_user_id"]?.GetValue<string>()?.Trim();
                        if (string.IsNullOrEmpty(sender)) continue;
                        var messageId = msg["message_id"]?.ToJsonString() ?? msg["client_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                        if (!_seenMessages.TryAdd(messageId, 0)) continue;
                        if (_seenMessages.Count > 4096) _seenMessages.Clear();

                        var text = ExtractText(msg["item_list"] as JsonArray);
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        var context = msg["context_token"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(context)) _contexts[sender] = context;
                        await onMessage(new RemoteInboundMessage(RemoteChannelKind.Weixin, sender, sender,
                            text.Trim(), messageId, context)).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException)
                {
                    // A quiet long poll may be held by the server; start a fresh poll.
                    continue;
                }
                catch (Exception ex)
                {
                    setStatus($"连接异常，5 秒后重试：{SafeMessage(ex)}");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await NotifyLifecycleAsync(settings, "ilink/bot/msg/notifystop", stopCts.Token).ConfigureAwait(false); } catch { }
            setStatus("未连接");
        }
    }

    public async Task SendAsync(RemoteControlSettings settings, RemoteReplyTarget target, string text, CancellationToken ct)
    {
        var context = target.ContextToken;
        if (string.IsNullOrEmpty(context)) _contexts.TryGetValue(target.ConversationId, out context);
        var payload = new
        {
            msg = new
            {
                from_user_id = "",
                to_user_id = target.ConversationId,
                client_id = $"vowin-{Guid.NewGuid():N}",
                message_type = 2,
                message_state = 2,
                item_list = new[] { new { type = 1, text_item = new { text } } },
                context_token = context
            },
            base_info = BaseInfo()
        };
        using var request = CreateRequest(HttpMethod.Post, NormalizeBaseUrl(settings.WeixinBaseUrl),
            "ilink/bot/sendmessage", settings.WeixinToken, JsonSerializer.Serialize(payload));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(raw)?.AsObject();
        var ret = root?["ret"]?.GetValue<int>() ?? 0;
        if (ret != 0) throw new InvalidOperationException($"微信发送失败 {ret}: {root?["errmsg"]}");
    }

    public async Task<WeixinLoginStartResult> BeginLoginAsync(CancellationToken ct)
    {
        _loginBaseUrl = DefaultBaseUrl;
        using var req = CreateRequest(HttpMethod.Post, DefaultBaseUrl,
            "ilink/bot/get_bot_qrcode?bot_type=3", null,
            JsonSerializer.Serialize(new { local_token_list = Array.Empty<string>() }));
        using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(raw)?.AsObject();
        _loginQrCode = root?["qrcode"]?.GetValue<string>();
        var qrContent = root?["qrcode_img_content"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(_loginQrCode) || string.IsNullOrWhiteSpace(qrContent)
            ? new(false, null, "微信未返回二维码")
            : new(true, qrContent, "请使用微信扫描二维码");
    }

    public async Task<WeixinLoginPollResult> PollLoginAsync(string? verificationCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_loginQrCode)) return new(false, false, "请先获取二维码");
        var qrCode = _loginQrCode;
        var deadline = DateTime.UtcNow.AddMinutes(8);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var endpoint = $"ilink/bot/get_qrcode_status?qrcode={Uri.EscapeDataString(qrCode)}";
            if (!string.IsNullOrWhiteSpace(verificationCode))
                endpoint += $"&verify_code={Uri.EscapeDataString(verificationCode.Trim())}";
            try
            {
                using var req = CreateRequest(HttpMethod.Get, _loginBaseUrl, endpoint, null, null);
                using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var root = JsonNode.Parse(raw)?.AsObject();
                var status = root?["status"]?.GetValue<string>() ?? "wait";
                switch (status)
                {
                    case "confirmed":
                        return new(true, false, "微信连接成功",
                            root?["bot_token"]?.GetValue<string>(), root?["ilink_bot_id"]?.GetValue<string>(),
                            root?["baseurl"]?.GetValue<string>(), root?["ilink_user_id"]?.GetValue<string>());
                    case "need_verifycode":
                    case "verify_code_blocked":
                        return new(false, true, "扫码需要配对码，请输入微信显示的配对码后继续");
                    case "scaned":
                        verificationCode = null;
                        break;
                    case "expired":
                        _loginQrCode = null;
                        return new(false, false, "二维码已过期，请重新获取");
                    case "binded_redirect":
                        return new(false, false, "该微信已绑定其他客户端，请先在原客户端解除绑定");
                    case "scaned_but_redirect":
                        var host = root?["redirect_host"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(host)) _loginBaseUrl = $"https://{host}";
                        break;
                }
            }
            catch (HttpRequestException) { }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return new(false, false, "等待扫码超时，请重试");
    }

    private static object BaseInfo() => new { channel_version = "2.4.8", bot_agent = "VoWin/1.0" };

    private async Task NotifyLifecycleAsync(RemoteControlSettings settings, string endpoint, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, NormalizeBaseUrl(settings.WeixinBaseUrl), endpoint,
            settings.WeixinToken, JsonSerializer.Serialize(new { base_info = BaseInfo() }));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string baseUrl, string endpoint, string? token, string? json)
    {
        var req = new HttpRequestMessage(method, new Uri(new Uri(NormalizeBaseUrl(baseUrl)), endpoint));
        req.Headers.TryAddWithoutValidation("iLink-App-Id", "bot");
        req.Headers.TryAddWithoutValidation("iLink-App-ClientVersion", ClientVersion.ToString());
        if (method == HttpMethod.Post)
        {
            req.Headers.TryAddWithoutValidation("AuthorizationType", "ilink_bot_token");
            req.Headers.TryAddWithoutValidation("X-WECHAT-UIN", Convert.ToBase64String(Encoding.UTF8.GetBytes(Random.Shared.NextInt64(0, uint.MaxValue + 1L).ToString())));
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.Trim()}");
            req.Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json");
        }
        return req;
    }

    private static string ExtractText(JsonArray? items)
    {
        if (items == null) return string.Empty;
        var parts = new List<string>();
        foreach (var n in items.OfType<JsonObject>())
        {
            var text = n["text_item"]?["text"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    private static string NormalizeBaseUrl(string? value)
        => (string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.Trim()).TrimEnd('/') + "/";

    private static string SafeMessage(Exception ex) => ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
    public void Dispose() => _http.Dispose();
}
