using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

/// <summary>HTTPS Bot API long polling. No inbound listener, webhook takeover or third-party SDK.</summary>
internal sealed class TelegramBotClient : IDisposable
{
    private readonly HttpClient _http;
    public TelegramBotClient(HttpClient? http = null) => _http = http ?? RemotePushHttp.CreateClient();
    public static void Validate(RemoteControlSettings settings)
    {
        _ = RemotePushHttp.BaseUri(settings.TelegramApiBaseUrl);
        if (!Regex.IsMatch(settings.TelegramBotToken.Trim(), @"^[0-9]+:[A-Za-z0-9_-]{16,}$"))
            throw new InvalidOperationException("请填写 BotFather 提供的 Telegram Bot Token。");
        foreach (var user in RemoteControlService.ParseAllowed(settings.TelegramAllowedUsers))
            if (!long.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new InvalidOperationException("Telegram 授权用户须为数字用户 ID，不是 @用户名。");
    }

    private async Task<JsonDocument> CallAsync(RemoteControlSettings settings, string method, object body, CancellationToken ct)
    {
        var uri = new Uri(RemotePushHttp.BaseUri(settings.TelegramApiBaseUrl), $"bot{settings.TelegramBotToken.Trim()}/{method}");
        var json = await RemotePushHttp.PostAsync(_http, uri, body, ct).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return json;
        var code = json.RootElement.TryGetProperty("error_code", out var error) && error.TryGetInt32(out var n) ? n : 0;
        var retryAfter = json.RootElement.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("retry_after", out var retry) &&
            retry.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 1, 86400) : 0;
        json.Dispose();
        throw new RemoteApiException(code switch
        {
            401 or 404 => "Telegram Token 无效，请在 BotFather 核对或重新生成。",
            403 => "Telegram 无法发送给此用户；请先私聊机器人发送 /start，并核对数字用户 ID。",
            409 => "此 Token 正被其他轮询或 Webhook 使用，请先停止另一服务。VoWin 不会抢占。",
            _ => $"Telegram 拒绝请求（{code}），请检查凭据或收件人授权。"
        }, code, retryAfter);
    }

    public async Task<string> VerifyAsync(RemoteControlSettings settings, CancellationToken ct)
    {
        Validate(settings);
        using var me = await CallAsync(settings, "getMe", new { }, ct).ConfigureAwait(false);
        if (!me.RootElement.GetProperty("result").TryGetProperty("username", out var username))
            throw new RemoteApiException("Telegram 未返回机器人账号。");
        using var hook = await CallAsync(settings, "getWebhookInfo", new { }, ct).ConfigureAwait(false);
        if (hook.RootElement.GetProperty("result").TryGetProperty("url", out var url) && !string.IsNullOrEmpty(url.GetString()))
            throw new RemoteApiException("机器人已有 Webhook；请先在原服务停用它。VoWin 不会自动接管。", 409);
        return username.GetString() ?? "bot";
    }

    public async Task RunAsync(RemoteControlSettings settings, Func<RemoteInboundMessage, Task> onMessage,
        Action<string, StatusLight> status, CancellationToken ct)
    {
        long offset = -1;
        var initialized = false;
        var retry = 0;
        string? username = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!initialized || retry > 0) status("正在连接 Telegram…", StatusLight.Busy);
                username ??= await VerifyAsync(settings, ct).ConfigureAwait(false);
                // Discard pre-start queued commands, including the last item: never replay an old paid action.
                using var json = await CallAsync(settings, "getUpdates", new
                { offset, limit = 100, timeout = initialized ? 25 : 0, allowed_updates = new[] { "message" } }, ct).ConfigureAwait(false);
                foreach (var update in json.RootElement.GetProperty("result").EnumerateArray().OrderBy(x => x.GetProperty("update_id").GetInt64()))
                {
                    ct.ThrowIfCancellationRequested();
                    var id = update.GetProperty("update_id").GetInt64();
                    if (initialized && id < offset) continue;
                    // Advance before dispatch. Network reply failure must not repeat SMS/call side effects.
                    offset = checked(id + 1);
                    if (!initialized) continue;
                    var inbound = ParseUpdate(update, username);
                    if (inbound != null) await onMessage(inbound).ConfigureAwait(false);
                }
                initialized = true;
                if (offset < 0) offset = 0;
                retry = 0;
                status("已连接 · 仅接收授权私聊", StatusLight.Ready);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RemoteApiException ex) when (ex.Code is 401 or 404 or 409)
            { status(ex.Message, StatusLight.Error); return; }
            catch (Exception ex)
            {
                var delay = ex is RemoteApiException { RetryAfter: > 0 } api ? api.RetryAfter : Math.Min(60, 2 << Math.Min(retry++, 5));
                status($"连接异常，{delay} 秒后重试。", StatusLight.Busy);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
        status("未连接", StatusLight.Inactive);
    }

    internal static RemoteInboundMessage? ParseUpdate(JsonElement update, string username)
    {
        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var text) ||
            message.TryGetProperty("forward_origin", out _) || message.TryGetProperty("via_bot", out _) ||
            !message.TryGetProperty("from", out var sender) || !message.TryGetProperty("chat", out var chat) ||
            chat.GetProperty("type").GetString() != "private" ||
            sender.TryGetProperty("is_bot", out var bot) && bot.GetBoolean()) return null;
        var user = sender.GetProperty("id").GetInt64();
        var chatId = chat.GetProperty("id").GetInt64();
        if (!message.TryGetProperty("date", out var date) || !date.TryGetInt64(out var timestamp) ||
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp is > 300 or < -120) return null;
        if (user <= 0 || user != chatId) return null;
        var command = text.GetString() ?? "";
        var mention = Regex.Match(command, @"^/([A-Za-z_]+)@([A-Za-z0-9_]+)(?=\s|$)");
        if (mention.Success)
        {
            if (!mention.Groups[2].Value.Equals(username, StringComparison.OrdinalIgnoreCase)) return null;
            command = "/" + mention.Groups[1].Value + command[mention.Length..];
        }
        return new(RemoteChannelKind.Telegram, user.ToString(CultureInfo.InvariantCulture), chatId.ToString(CultureInfo.InvariantCulture),
            command, message.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture));
    }

    public async Task SendAsync(RemoteControlSettings settings, RemoteReplyTarget target, string text, CancellationToken ct)
    {
        if (!long.TryParse(target.ConversationId, out var chatId) || chatId <= 0 || target.IsGroup)
            throw new InvalidOperationException("Telegram 仅向已配置的个人用户发送消息。");
        foreach (var part in RemotePushHttp.SplitText(RemotePushHttp.PlainText(text)))
        {
            using var result = await CallAsync(settings, "sendMessage", new
            {
                chat_id = chatId, text = part,
                reply_markup = new { keyboard = new[] { new[] { "状态", "卡片", "帮助" } }, resize_keyboard = true }
            }, ct).ConfigureAwait(false);
        }
    }
    public void Dispose() => _http.Dispose();
}
