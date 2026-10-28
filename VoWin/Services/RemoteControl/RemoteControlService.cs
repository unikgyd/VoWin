using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using VoWin.Helpers;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

public sealed class RemoteControlService : IRemoteControlService, IHostedService, IAsyncDisposable
{
    private readonly IVoKernelService _kernel;
    private readonly RemoteControlSettingsStore _store;
    private readonly RemoteCommandProcessor _commands;
    private readonly WeixinIlinkClient _weixin = new();
    private readonly QqBotClient _qq = new();
    private readonly TelegramBotClient _telegram;
    private readonly BarkClient _bark;
    private readonly ConcurrentDictionary<long, Task> _inboundTasks = new();
    private readonly ConcurrentQueue<string> _seenOrder = new();
    private readonly ConcurrentDictionary<RemoteChannelKind, string> _notificationFailures = new();
    private long _nextInbound;
    private long _lastPublicHelpTicks;
    private readonly SemaphoreSlim _pairingGate = new(1, 1);
    private readonly object _pairingLock = new();
    private int _pairingAttempts;
    private string _telegramStatus = "未启用";
    private string _barkStatus = "未启用";
    private StatusLight _telegramLight;
    private StatusLight _barkLight;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
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
    public string TelegramStatus => _telegramStatus;
    public string BarkStatus => _barkStatus;
    public GatewayStatusItem TelegramIndicator => InteractiveIndicator(RemoteChannelKind.Telegram,
        new("Telegram", _telegramStatus, _telegramStatus) { Light = _telegramLight });
    public GatewayStatusItem BarkIndicator => new("Bark", _barkStatus, _barkStatus) { Light = _barkLight };
    public GatewayStatusItem WeixinIndicator => InteractiveIndicator(RemoteChannelKind.Weixin, ChannelIndicator("微信", _weixinStatus));
    public GatewayStatusItem QqIndicator => InteractiveIndicator(RemoteChannelKind.Qq, ChannelIndicator("QQ", _qqStatus));
    public string LastActivity => _lastActivity;
    public string PairingCodeStatus => _pairingCodeStatus;
    public event Action? StatusChanged;

    public RemoteControlService(IVoKernelService kernel) : this(kernel, new(), new(), new()) { }

    internal RemoteControlService(IVoKernelService kernel, RemoteControlSettingsStore store, TelegramBotClient telegram, BarkClient bark)
    {
        _kernel = kernel;
        _store = store; _telegram = telegram; _bark = bark;
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
        if (Volatile.Read(ref _disposeTask) is { } disposal) { await disposal.ConfigureAwait(false); return; }
        _kernel.IncomingSmsReceived -= OnIncomingSms;
        _kernel.IncomingCallReceived -= OnIncomingCall;
        await StopWorkersAsync().ConfigureAwait(false);
    }

    public async Task SaveAndRestartAsync(RemoteControlSettings settings, CancellationToken ct = default)
    {
        ValidateSettings(settings);
        await _pairingGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = settings.Clone();
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            Settings = next;
        }
        finally { _pairingGate.Release(); }
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
            var message = $"获取二维码失败：{DiagnosticLogRedactor.Redact(ex.Message)}";
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
        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        lock (_pairingLock)
        {
            _pairingCode = code;
            _pairingExpiresAt = DateTime.UtcNow.AddMinutes(10);
            _pairingAttempts = 0;
            _pairingCodeStatus = $"配对码：{_pairingCode}（10 分钟内有效）";
        }
        RaiseChanged();
        return code;
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
            SetQqStatus($"验证失败：{DiagnosticLogRedactor.Redact(ex.Message)}");
            throw;
        }
    }

    private async Task RestartWorkersAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopWorkersCoreAsync().ConfigureAwait(false);
            _notificationFailures.Clear();
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            _runCts = cts;
            var workers = new List<Task>();
            var settings = Settings.Clone();

            if (Settings.WeixinEnabled && !string.IsNullOrWhiteSpace(Settings.WeixinToken))
                workers.Add(Task.Run(() => _weixin.RunAsync(settings, message => DispatchInbound(message, token), SetWeixinStatus, token), token));
            else SetWeixinStatus(Settings.WeixinEnabled ? "缺少登录凭据" : "未启用");

            if (Settings.QqEnabled && !string.IsNullOrWhiteSpace(Settings.QqAppId) && !string.IsNullOrWhiteSpace(Settings.QqClientSecret))
                workers.Add(Task.Run(() => _qq.RunAsync(settings, message => DispatchInbound(message, token), SetQqStatus, token), token));
            else SetQqStatus(Settings.QqEnabled ? "缺少 AppID 或 AppSecret" : "未启用");

            if (settings.TelegramEnabled)
                workers.Add(Task.Run(() => _telegram.RunAsync(settings, message => DispatchInbound(message, token), SetTelegramStatus, token), token));
            else SetTelegramStatus("未启用", StatusLight.Inactive);
            SetBarkStatus(settings.BarkEnabled ? "已配置 · 等待推送（不接收命令）" : "未启用",
                settings.BarkEnabled ? StatusLight.Ready : StatusLight.Inactive);

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
        try { await Task.WhenAll(_inboundTasks.Values).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        cts.Dispose();
    }

    private Task DispatchInbound(RemoteInboundMessage message, CancellationToken ct)
    {
        // Receiving a long-running call/registration command must not block a subsequent CANCEL/BYE command.
        if (_inboundTasks.Count >= 32 || ct.IsCancellationRequested) return Task.CompletedTask;
        var id = Interlocked.Increment(ref _nextInbound);
        var task = Task.Run(() => HandleInboundAsync(message, ct), ct);
        _inboundTasks[id] = task;
        _ = task.ContinueWith(done => { _ = done.Exception; _inboundTasks.TryRemove(id, out _); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    internal async Task HandleInboundAsync(RemoteInboundMessage message, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !(message.Channel switch
            { RemoteChannelKind.Weixin => Settings.WeixinEnabled, RemoteChannelKind.Qq => Settings.QqEnabled,
                RemoteChannelKind.Telegram => Settings.TelegramEnabled, _ => false })) return;
        var dedupeKey = $"{message.Channel}:{message.ConversationId}:{message.MessageId}";
        if (!_seenInbound.TryAdd(dedupeKey, 0)) return;
        _seenOrder.Enqueue(dedupeKey);
        while (_seenInbound.Count > 4096 && _seenOrder.TryDequeue(out var oldest)) _seenInbound.TryRemove(oldest, out _);

        _lastActivity = $"{DateTime.Now:HH:mm:ss} 收到 {message.Channel} / {ShortId(message.SenderId)} 的命令";
        RaiseChanged();

        if (!IsAllowed(message.Channel, message.SenderId))
        {
            if (!message.IsGroup && message.Channel == RemoteChannelKind.Telegram && message.Text.Trim().ToLowerInvariant() is "/start" or "/id")
            {
                var now = DateTime.UtcNow.Ticks;
                var previous = Interlocked.Read(ref _lastPublicHelpTicks);
                if (now - previous > TimeSpan.FromSeconds(5).Ticks && Interlocked.CompareExchange(ref _lastPublicHelpTicks, now, previous) == previous)
                    await SendReplyAsync(message, $"尚未授权。你的数字用户 ID：{message.SenderId}\n请在电脑添加此 ID，或生成配对码后私聊发送「绑定 六位配对码」。不会公开设备、短信或线路状态。", ct).ConfigureAwait(false);
                return;
            }
            if (await TryPairAsync(message).ConfigureAwait(false))
                await SendReplyAsync(message, "绑定成功。发送「帮助」查看命令；收费和敏感操作仍需电脑端单独授权。", ct).ConfigureAwait(false);
            else { _lastActivity = $"{DateTime.Now:HH:mm:ss} 已拒绝未授权的 {message.Channel} 用户"; RaiseChanged(); }
            return;
        }
        var gate = _senderLocks.GetOrAdd($"{message.Channel}:{message.SenderId}", _ => new SemaphoreSlim(1, 1));
        var acquired = !RemoteCommandProcessor.CanRunAlongsidePendingAction(message.Text);
        if (acquired) await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsAllowed(message.Channel, message.SenderId))
            {
                _lastActivity = $"{DateTime.Now:HH:mm:ss} 已拒绝未授权用户 {message.Channel} / {message.SenderId}";
                RaiseChanged();
                return;
            }

            var response = await _commands.ExecuteAsync(message.Text, ct,
                $"{message.Channel}:{message.SenderId}:{message.ConversationId}", Settings, message.IsGroup,
                text => SendReplyAsync(message, text, ct)).ConfigureAwait(false);
            await SendReplyAsync(message, response, ct).ConfigureAwait(false);
            _lastActivity = $"{DateTime.Now:HH:mm:ss} 已处理 {message.Channel} / {ShortId(message.SenderId)} 的命令";
            RaiseChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lastActivity = $"{DateTime.Now:HH:mm:ss} 远程命令失败：{DiagnosticLogRedactor.Redact(ex.Message)}";
            RaiseChanged();
            try { await SendReplyAsync(message, RemoteMessageMarkdown.Error("处理失败", DiagnosticLogRedactor.Redact(ex.Message)), ct).ConfigureAwait(false); } catch { }
        }
        finally { if (acquired) gate.Release(); }
    }

    private async Task<bool> TryPairAsync(RemoteInboundMessage message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(message.Text.Trim(), @"^/?(?:绑定|bind)\s+(\d{6})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || message.IsGroup) return false;
        await _pairingGate.WaitAsync().ConfigureAwait(false);
        try
        {
        lock (_pairingLock)
        {
            if (_pairingCode == null || DateTime.UtcNow > _pairingExpiresAt) return false;
            _pairingAttempts++;
            if (match.Groups[1].Value != _pairingCode)
            {
                if (_pairingAttempts >= 10)
                { _pairingCode = null; _pairingCodeStatus = "配对尝试过多，请重新生成配对码。"; RaiseChanged(); }
                return false;
            }
            _pairingCode = null;
        }
        var next = Settings.Clone();
        if (message.Channel == RemoteChannelKind.Weixin)
            next.WeixinAllowedUsers = AddAllowedUser(next.WeixinAllowedUsers, message.SenderId);
        else if (message.Channel == RemoteChannelKind.Qq)
            next.QqAllowedUsers = AddAllowedUser(next.QqAllowedUsers, message.SenderId);
        else next.TelegramAllowedUsers = AddAllowedUser(next.TelegramAllowedUsers, message.SenderId);
        await _store.SaveAsync(next).ConfigureAwait(false);
        Settings = next;
        _pairingCodeStatus = $"已绑定 {message.Channel} 用户 {ShortId(message.SenderId)}";
        RaiseChanged();
        return true;
        }
        finally { _pairingGate.Release(); }
    }

    private async Task SendReplyAsync(RemoteInboundMessage message, string text, CancellationToken ct)
    {
        var target = new RemoteReplyTarget(message.Channel, message.ConversationId, message.MessageId, message.ContextToken, message.IsGroup);
        if (message.Channel == RemoteChannelKind.Weixin)
            await _weixin.SendAsync(Settings, target, text, ct).ConfigureAwait(false);
        else if (message.Channel == RemoteChannelKind.Qq)
            await _qq.SendAsync(Settings, target, text, ct).ConfigureAwait(false);
        else await _telegram.SendAsync(Settings, target, text, ct).ConfigureAwait(false);
    }

    private void OnIncomingSms(SmsMessageModel sms) => _ = ObserveNotificationAsync(() => NotifySmsAsync(sms));

    private async Task ObserveNotificationAsync(Func<Task> notify)
    {
        try { await notify().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { _lastActivity = $"{DateTime.Now:HH:mm:ss} 通知未全部提交，请检查通道详情。"; RaiseChanged(); }
    }

    private CancellationToken NotificationToken()
    {
        try { return _runCts?.Token ?? new CancellationToken(true); }
        catch (ObjectDisposedException) { return new CancellationToken(true); }
    }

    private async Task NotifySmsAsync(SmsMessageModel sms)
    {
        var settings = Settings;
        if (sms.IsOutgoing || !settings.NotifyIncomingSms || (settings.NotifyOtpOnly && !sms.HasOtpCode)) return;
        var code = sms.ExtractedOtpCode;
        var time = TimestampDisplayHelper.ToLocalDisplayTime(sms.Timestamp);
        var text = code == null
            ? $"## 💬 收到短信\n\n- **来自**：{RemoteMessageMarkdown.Code(sms.SenderOrRecipient)}\n- **时间**：{time:yyyy-MM-dd HH:mm:ss}\n\n**短信内容**\n\n{RemoteMessageMarkdown.Quote(sms.Text)}"
            : $"## 🔐 收到验证码\n\n### {RemoteMessageMarkdown.Code(code)}\n\n- **来自**：{RemoteMessageMarkdown.Code(sms.SenderOrRecipient)}\n- **时间**：{time:yyyy-MM-dd HH:mm:ss}\n\n**短信内容**\n\n{RemoteMessageMarkdown.Quote(sms.Text)}";
        text = $"线路：{RemoteMessageMarkdown.Code(sms.SlotId ?? "未知线路")}\n\n" + text;
        var barkText = settings.BarkIncludeSmsContent ? text : $"线路：{sms.SlotId ?? "未知"}\n来自 {sms.SenderOrRecipient} 的{(sms.HasOtpCode ? "验证码短信" : "短信")}\n{time:MM-dd HH:mm:ss}\n内容已隐藏，请在 VoWin 查看。";
        await BroadcastAsync(text, sms.HasOtpCode ? "VoWin · 验证码" : "VoWin · 收到短信", barkText).ConfigureAwait(false);
    }

    private void OnIncomingCall(string number, string? slotId)
    {
        if (Settings.NotifyIncomingCalls)
            _ = ObserveNotificationAsync(() => BroadcastAsync($"## 📞 来电提醒\n\n- **号码**：{RemoteMessageMarkdown.Code(number)}\n- **卡槽**：{RemoteMessageMarkdown.Escape(slotId ?? "未知线路")}\n\n> " +
                (slotId == null ? "回复「卡片」选择线路后接听。" : $"回复 `@{slotId} 接听` 或 `@{slotId} 拒接`（需要通话控制权限）"),
                "VoWin · 来电提醒", $"来电：{number}\n线路：{slotId ?? "未知"}\n请在 VoWin 或交互机器人中处理；Bark 不能接听。"));
    }

    private async Task BroadcastAsync(string text, string title, string barkText)
    {
        var settings = Settings;
        var token = NotificationToken();
        if (token.IsCancellationRequested) return;
        var sends = new List<Task>();
        if (settings.WeixinEnabled)
            sends.Add(SendChannelNotificationsAsync(RemoteChannelKind.Weixin, ParseAllowed(settings.WeixinAllowedUsers).Select(user =>
                _weixin.SendAsync(settings, new RemoteReplyTarget(RemoteChannelKind.Weixin, user), text, token))));
        if (settings.QqEnabled)
            sends.Add(SendChannelNotificationsAsync(RemoteChannelKind.Qq, ParseAllowed(settings.QqAllowedUsers).Select(user =>
                _qq.SendAsync(settings, new RemoteReplyTarget(RemoteChannelKind.Qq, user), text, token))));
        if (settings.TelegramEnabled)
            sends.Add(SendChannelNotificationsAsync(RemoteChannelKind.Telegram, ParseAllowed(settings.TelegramAllowedUsers).Select(user =>
                _telegram.SendAsync(settings, new(RemoteChannelKind.Telegram, user), text, token))));
        if (settings.BarkEnabled) sends.Add(SendBarkNotificationAsync(settings, title, barkText));
        try { await Task.WhenAll(sends).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _lastActivity = $"{DateTime.Now:HH:mm:ss} 主动通知部分失败：{DiagnosticLogRedactor.Redact(ex.Message)}";
            RaiseChanged();
        }
    }

    private async Task SendChannelNotificationsAsync(RemoteChannelKind channel, IEnumerable<Task> sends)
    {
        try
        {
            await Task.WhenAll(sends).ConfigureAwait(false);
            _notificationFailures.TryRemove(channel, out _);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _notificationFailures[channel] = DiagnosticLogRedactor.Redact(ex.Message);
            throw;
        }
        finally { RaiseChanged(); }
    }

    private GatewayStatusItem InteractiveIndicator(RemoteChannelKind channel, GatewayStatusItem indicator)
    {
        if (_notificationFailures.TryGetValue(channel, out var error))
            return indicator with { Light = StatusLight.Error, ShortStatus = "通知失败", Detail = error + "；未自动重发。" };
        var allowed = channel switch { RemoteChannelKind.Telegram => Settings.TelegramAllowedUsers,
            RemoteChannelKind.Weixin => Settings.WeixinAllowedUsers, _ => Settings.QqAllowedUsers };
        if (indicator.Light == StatusLight.Ready && !ParseAllowed(allowed).Any())
            return indicator with { Light = StatusLight.Busy, ShortStatus = "待授权", Detail = indicator.Detail + "；请添加授权用户或用配对码私聊绑定。" };
        return indicator;
    }

    private bool IsAllowed(RemoteChannelKind channel, string sender)
        => ParseAllowed(channel switch { RemoteChannelKind.Weixin => Settings.WeixinAllowedUsers,
            RemoteChannelKind.Qq => Settings.QqAllowedUsers, RemoteChannelKind.Telegram => Settings.TelegramAllowedUsers, _ => "" })
            .Contains(sender, StringComparer.Ordinal);

    internal static IEnumerable<string> ParseAllowed(string value) => value
        .Split([',', ';', '\r', '\n', '，', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal);

    private static string AddAllowedUser(string existing, string user)
        => string.Join(",", ParseAllowed(existing).Append(user).Distinct(StringComparer.Ordinal));

    private static void ValidateSettings(RemoteControlSettings settings)
    {
        if (settings.WeixinEnabled && string.IsNullOrWhiteSpace(settings.WeixinToken))
            throw new InvalidOperationException("启用微信前请先扫码登录。");
        if (settings.QqEnabled) ValidateQq(settings);
        if (settings.TelegramEnabled) TelegramBotClient.Validate(settings);
        if (settings.BarkEnabled) BarkClient.Validate(settings);
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

    public ValueTask DisposeAsync()
    {
        // DI captures the same singleton through concrete, interface and hosted
        // service registrations. All aliases must share one completed teardown.
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await StopWorkersAsync().ConfigureAwait(false);
        _kernel.IncomingSmsReceived -= OnIncomingSms;
        _kernel.IncomingCallReceived -= OnIncomingCall;
        // A cancelled transport may still be awaiting a bounded kernel action.
        // Do not dispose its managed semaphore underneath the finally/Release.
        _senderLocks.Clear();
        _lifecycle.Dispose();
        _weixin.Dispose();
        _qq.Dispose();
        _telegram.Dispose();
        _bark.Dispose();
    }

    public async Task TestTelegramAsync(RemoteControlSettings settings, CancellationToken ct = default)
    {
        try
        {
            var username = await _telegram.VerifyAsync(settings, ct).ConfigureAwait(false);
            SetTelegramStatus($"凭据有效 · @{username}；尚未保存的开关不会自动启用", StatusLight.Ready);
        }
        catch (Exception ex) { SetTelegramStatus("验证失败：" + DiagnosticLogRedactor.Redact(ex.Message), StatusLight.Error); throw; }
    }
    public async Task TestBarkAsync(RemoteControlSettings settings, CancellationToken ct = default)
    {
        try
        {
            await _bark.SendAsync(settings, "VoWin · 测试提醒", "这是一条通知测试，不包含短信内容。Bark 不接收交互命令。", ct).ConfigureAwait(false);
            SetBarkStatus("服务器已接受测试推送；请在手机确认送达", StatusLight.Ready);
        }
        catch (Exception ex) { SetBarkStatus("验证失败：" + DiagnosticLogRedactor.Redact(ex.Message), StatusLight.Error); throw; }
    }
    private async Task SendBarkNotificationAsync(RemoteControlSettings settings, string title, string text)
    {
        try { await _bark.SendAsync(settings, title, text, NotificationToken()).ConfigureAwait(false); SetBarkStatus("最近推送已提交（不等于手机送达）", StatusLight.Ready); }
        catch { SetBarkStatus("推送失败，请检查网络或设备 Key；未自动重发", StatusLight.Error); throw; }
    }
    private void SetTelegramStatus(string value, StatusLight light) { _telegramStatus = value; _telegramLight = light; RaiseChanged(); }
    private void SetBarkStatus(string value, StatusLight light) { _barkStatus = value; _barkLight = light; RaiseChanged(); }
    private static GatewayStatusItem ChannelIndicator(string name, string value) => new(name, value, value)
    {
        Light = value.Contains("失败") || value.Contains("异常") || value.Contains("缺少") ? StatusLight.Error
            : value.StartsWith("已连接") || value.StartsWith("凭据有效") ? StatusLight.Ready
            : value.StartsWith("未") ? StatusLight.Inactive : StatusLight.Busy
    };
}
