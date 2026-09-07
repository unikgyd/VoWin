using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

public sealed class RemoteControlService : IRemoteControlService, IHostedService, IAsyncDisposable
{
    private readonly IVoKernelService _kernel;
    private readonly RemoteControlSettingsStore _store = new();
    private readonly RemoteCommandProcessor _commands;
    private readonly WeixinIlinkClient _weixin = new();
    private readonly QqBotClient _qq = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _senderLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seenInbound = new(StringComparer.Ordinal);
    private CancellationTokenSource? _runCts;
    private Task[] _workers = [];
    private string? _pairingCode;
    private DateTime _pairingExpiresAt;
    private string _weixinStatus = "未配置";
    private string _qqStatus = "未配置";
    private string _lastActivity = "尚无远程活动";
    private string _pairingCodeStatus = "未开启配对";

    public RemoteControlSettings Settings { get; private set; } = new();
    public string WeixinStatus => _weixinStatus;
    public string QqStatus => _qqStatus;
    public string LastActivity => _lastActivity;
    public string PairingCodeStatus => _pairingCodeStatus;
    public event Action? StatusChanged;

    public RemoteControlService(IVoKernelService kernel)
    {
        _kernel = kernel;
        _commands = new RemoteCommandProcessor(kernel);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _kernel.IncomingSmsReceived += OnIncomingSms;
        _kernel.IncomingCallReceived += OnIncomingCall;
        await RestartWorkersAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _kernel.IncomingSmsReceived -= OnIncomingSms;
        _kernel.IncomingCallReceived -= OnIncomingCall;
        await StopWorkersAsync().ConfigureAwait(false);
    }

    public async Task SaveAndRestartAsync(RemoteControlSettings settings, CancellationToken ct = default)
    {
        ValidateSettings(settings);
        Settings = settings.Clone();
        await _store.SaveAsync(Settings, ct).ConfigureAwait(false);
        await RestartWorkersAsync(ct).ConfigureAwait(false);
    }

    public async Task<WeixinLoginStartResult> BeginWeixinLoginAsync(CancellationToken ct = default)
    {
        SetWeixinStatus("正在获取登录二维码…");
        try
        {
            var result = await _weixin.BeginLoginAsync(ct).ConfigureAwait(false);
            SetWeixinStatus(result.Message);
            return result;
        }
        catch (Exception ex)
        {
            var message = $"获取二维码失败：{ex.Message}";
            SetWeixinStatus(message);
            return new(false, null, message);
        }
    }

    public async Task<WeixinLoginPollResult> PollWeixinLoginAsync(string? verificationCode, CancellationToken ct = default)
    {
        SetWeixinStatus("等待微信确认…");
        var result = await _weixin.PollLoginAsync(verificationCode, ct).ConfigureAwait(false);
        SetWeixinStatus(result.Message);
        if (!result.Connected) return result;

        var next = Settings.Clone();
        next.WeixinEnabled = true;
        next.WeixinToken = result.Token ?? string.Empty;
        next.WeixinBotId = result.BotId ?? string.Empty;
        next.WeixinBaseUrl = string.IsNullOrWhiteSpace(result.BaseUrl) ? "https://ilinkai.weixin.qq.com" : result.BaseUrl!;
        next.WeixinUserId = result.UserId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(result.UserId))
            next.WeixinAllowedUsers = AddAllowedUser(next.WeixinAllowedUsers, result.UserId!);
        await SaveAndRestartAsync(next, ct).ConfigureAwait(false);
        return result;
    }

    public string CreatePairingCode()
    {
        _pairingCode = Random.Shared.Next(100000, 999999).ToString();
        _pairingExpiresAt = DateTime.UtcNow.AddMinutes(10);
        _pairingCodeStatus = $"配对码：{_pairingCode}（10 分钟内有效）";
        RaiseChanged();
        return _pairingCode;
    }

    public async Task TestQqAsync(RemoteControlSettings settings, CancellationToken ct = default)
    {
        ValidateQq(settings);
        SetQqStatus("正在验证 QQ 凭据…");
        try
        {
            await _qq.ValidateCredentialsAsync(settings, ct).ConfigureAwait(false);
            SetQqStatus("凭据有效，可以连接");
        }
        catch (Exception ex)
        {
            SetQqStatus($"验证失败：{ex.Message}");
            throw;
        }
    }

    private async Task RestartWorkersAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopWorkersCoreAsync().ConfigureAwait(false);
            var cts = new CancellationTokenSource();
            _runCts = cts;
            var workers = new List<Task>();

            if (Settings.WeixinEnabled && !string.IsNullOrWhiteSpace(Settings.WeixinToken))
                workers.Add(Task.Run(() => _weixin.RunAsync(Settings, HandleInboundAsync, SetWeixinStatus, cts.Token), cts.Token));
            else SetWeixinStatus(Settings.WeixinEnabled ? "缺少登录凭据" : "未启用");

            if (Settings.QqEnabled && !string.IsNullOrWhiteSpace(Settings.QqAppId) && !string.IsNullOrWhiteSpace(Settings.QqClientSecret))
                workers.Add(Task.Run(() => _qq.RunAsync(Settings, HandleInboundAsync, SetQqStatus, cts.Token), cts.Token));
            else SetQqStatus(Settings.QqEnabled ? "缺少 AppID 或 AppSecret" : "未启用");

            _workers = workers.ToArray();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopWorkersAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopWorkersCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopWorkersCoreAsync()
    {
        var cts = Interlocked.Exchange(ref _runCts, null);
        var workers = Interlocked.Exchange(ref _workers, []);
        if (cts == null) return;
        cts.Cancel();
        try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        cts.Dispose();
    }

    private async Task HandleInboundAsync(RemoteInboundMessage message)
    {
        var dedupeKey = $"{message.Channel}:{message.MessageId}";
        if (!_seenInbound.TryAdd(dedupeKey, 0)) return;
        if (_seenInbound.Count > 4096) _seenInbound.Clear();

        _lastActivity = $"{DateTime.Now:HH:mm:ss} 收到 {message.Channel} / {ShortId(message.SenderId)}：{Trim(message.Text, 60)}";
        RaiseChanged();

        var gate = _senderLocks.GetOrAdd($"{message.Channel}:{message.SenderId}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsAllowed(message.Channel, message.SenderId))
            {
                if (await TryPairAsync(message).ConfigureAwait(false))
                {
                    await SendReplyAsync(message, "绑定成功。发送“帮助”查看命令。").ConfigureAwait(false);
                    return;
                }
                _lastActivity = $"{DateTime.Now:HH:mm:ss} 已拒绝未授权用户 {message.Channel} / {message.SenderId}";
                RaiseChanged();
                return;
            }

            var response = await _commands.ExecuteAsync(message.Text, CancellationToken.None).ConfigureAwait(false);
            await SendReplyAsync(message, response).ConfigureAwait(false);
            _lastActivity = $"{DateTime.Now:HH:mm:ss} 已执行 {message.Channel} / {ShortId(message.SenderId)}：{Trim(message.Text, 60)}";
            RaiseChanged();
        }
        catch (Exception ex)
        {
            _lastActivity = $"{DateTime.Now:HH:mm:ss} 远程命令失败：{ex.Message}";
            RaiseChanged();
            try { await SendReplyAsync(message, $"处理失败：{ex.Message}").ConfigureAwait(false); } catch { }
        }
        finally { gate.Release(); }
    }

    private async Task<bool> TryPairAsync(RemoteInboundMessage message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(message.Text.Trim(), @"^/?(?:绑定|bind)\s+(\d{6})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || _pairingCode == null || DateTime.UtcNow > _pairingExpiresAt || match.Groups[1].Value != _pairingCode) return false;
        var next = Settings.Clone();
        if (message.Channel == RemoteChannelKind.Weixin)
            next.WeixinAllowedUsers = AddAllowedUser(next.WeixinAllowedUsers, message.SenderId);
        else
            next.QqAllowedUsers = AddAllowedUser(next.QqAllowedUsers, message.SenderId);
        Settings = next;
        _pairingCode = null;
        _pairingCodeStatus = $"已绑定 {message.Channel} 用户 {ShortId(message.SenderId)}";
        await _store.SaveAsync(Settings).ConfigureAwait(false);
        RaiseChanged();
        return true;
    }

    private async Task SendReplyAsync(RemoteInboundMessage message, string text)
    {
        var target = new RemoteReplyTarget(message.Channel, message.ConversationId, message.MessageId, message.ContextToken, message.IsGroup);
        if (message.Channel == RemoteChannelKind.Weixin)
            await _weixin.SendAsync(Settings, target, text, CancellationToken.None).ConfigureAwait(false);
        else
            await _qq.SendAsync(Settings, target, text, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnIncomingSms(SmsMessageModel sms) => _ = NotifySmsAsync(sms);

    private async Task NotifySmsAsync(SmsMessageModel sms)
    {
        var settings = Settings;
        if (!settings.NotifyIncomingSms || (settings.NotifyOtpOnly && !sms.HasOtpCode)) return;
        var code = sms.ExtractedOtpCode;
        var text = code == null
            ? $"收到短信\n来自：{sms.SenderOrRecipient}\n时间：{sms.Timestamp:yyyy-MM-dd HH:mm:ss}\n内容：{sms.Text}"
            : $"收到验证码：{code}\n来自：{sms.SenderOrRecipient}\n时间：{sms.Timestamp:yyyy-MM-dd HH:mm:ss}\n内容：{sms.Text}";
        await BroadcastAsync(text).ConfigureAwait(false);
    }

    private void OnIncomingCall(string number, string? slotId)
    {
        if (Settings.NotifyIncomingCalls)
            _ = BroadcastAsync($"来电提醒\n号码：{number}\n卡槽：{slotId ?? _kernel.ActiveSlot?.Name ?? "当前卡"}\n可发送：接听 / 拒接");
    }

    private async Task BroadcastAsync(string text)
    {
        var settings = Settings;
        var sends = new List<Task>();
        if (settings.WeixinEnabled)
            sends.AddRange(ParseAllowed(settings.WeixinAllowedUsers).Select(user =>
                _weixin.SendAsync(settings, new RemoteReplyTarget(RemoteChannelKind.Weixin, user), text, CancellationToken.None)));
        if (settings.QqEnabled)
            sends.AddRange(ParseAllowed(settings.QqAllowedUsers).Select(user =>
                _qq.SendAsync(settings, new RemoteReplyTarget(RemoteChannelKind.Qq, user), text, CancellationToken.None)));
        try { await Task.WhenAll(sends).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _lastActivity = $"{DateTime.Now:HH:mm:ss} 主动通知部分失败：{ex.Message}";
            RaiseChanged();
        }
    }

    private bool IsAllowed(RemoteChannelKind channel, string sender)
        => ParseAllowed(channel == RemoteChannelKind.Weixin ? Settings.WeixinAllowedUsers : Settings.QqAllowedUsers)
            .Contains(sender, StringComparer.Ordinal);

    private static IEnumerable<string> ParseAllowed(string value) => value
        .Split([',', ';', '\r', '\n', '，', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal);

    private static string AddAllowedUser(string existing, string user)
        => string.Join(",", ParseAllowed(existing).Append(user).Distinct(StringComparer.Ordinal));

    private static void ValidateSettings(RemoteControlSettings settings)
    {
        if (settings.WeixinEnabled && string.IsNullOrWhiteSpace(settings.WeixinToken))
            throw new InvalidOperationException("启用微信前请先扫码登录。");
        if (settings.QqEnabled) ValidateQq(settings);
        if (!Uri.TryCreate(settings.WeixinBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("微信 API 地址必须是 HTTPS 地址。");
    }

    private static void ValidateQq(RemoteControlSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.QqAppId) || string.IsNullOrWhiteSpace(settings.QqClientSecret))
            throw new InvalidOperationException("请填写 QQ 机器人 AppID 和 AppSecret。");
    }

    private void SetWeixinStatus(string value) { _weixinStatus = value; RaiseChanged(); }
    private void SetQqStatus(string value) { _qqStatus = value; RaiseChanged(); }
    private void RaiseChanged() { try { StatusChanged?.Invoke(); } catch { } }
    private static string ShortId(string id) => id.Length <= 12 ? id : id[..6] + "…" + id[^4..];
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        await StopWorkersAsync().ConfigureAwait(false);
        foreach (var gate in _senderLocks.Values) gate.Dispose();
        _lifecycle.Dispose();
        _weixin.Dispose();
        _qq.Dispose();
    }
}
