using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoWin.Models;
using VoWin.Services;
using VoWin.Services.RemoteControl;

namespace VoWin.Tests;

public sealed class RemoteBotTests
{
    private const string FakeToken = "123456:synthetic_test_token_ABCDEFGHIJKLMNOP";
    private static RemoteControlSettings TelegramSettings => new()
    { TelegramEnabled = true, TelegramBotToken = FakeToken, TelegramApiBaseUrl = "https://telegram.test", TelegramAllowedUsers = "42,43" };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request, ct);
    }
    private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) => new(code)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Ok(object result) => Json(new { ok = true, result });
    private static object Update(long id, string text, long user = 42, string type = "private", long? date = null) => new
    {
        update_id = id, message = new { message_id = id, date = date ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            from = new { id = user, is_bot = false }, chat = new { id = user, type }, text }
    };

    [Fact]
    public async Task TelegramStartupDropsOldActionsAndAdvancesOffsetBeforeDispatch()
    {
        var offsets = new List<long>();
        var received = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var bot = new TelegramBotClient(new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getMe")) return Ok(new { username = "test_bot" });
            if (request.RequestUri.AbsolutePath.EndsWith("getWebhookInfo")) return Ok(new { url = "" });
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var offset = payload.RootElement.GetProperty("offset").GetInt64(); offsets.Add(offset);
            return Ok(new[] { Update(offset == -1 ? 10 : 11, offset == -1 ? "/call 12345" : "/status@test_bot") });
        })));
        await bot.RunAsync(TelegramSettings, message => { received.Add(message.Text); stop.Cancel(); return Task.CompletedTask; }, (_, _) => { }, stop.Token);
        Assert.Equal(new long[] { -1, 11 }, offsets);
        Assert.Equal(new[] { "/status" }, received);
    }

    [Fact]
    public async Task ExistingWebhookIsReportedAndNeverDeletedOrTakenOver()
    {
        var methods = new List<string>();
        using var bot = new TelegramBotClient(new HttpClient(new Handler((request, _) =>
        {
            var method = request.RequestUri!.Segments.Last(); methods.Add(method);
            return Task.FromResult(method == "getMe" ? Ok(new { username = "test_bot" }) : Ok(new { url = "https://existing.test/webhook" }));
        })));
        var failure = await Assert.ThrowsAsync<RemoteApiException>(() => bot.VerifyAsync(TelegramSettings, CancellationToken.None));
        Assert.Equal(409, failure.Code); Assert.Equal(new[] { "getMe", "getWebhookInfo" }, methods);
    }

    [Theory]
    [InlineData("group", 0)] [InlineData("private", -1000)]
    public void GroupAndStaleMessagesCannotBecomeCommands(string type, int age)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Update(1, "/call 12345", type: type, date: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + age)));
        Assert.Null(TelegramBotClient.ParseUpdate(json.RootElement, "test_bot"));
    }

    [Fact]
    public void OtherBotsMentionsAndForwardedTextAreIgnored()
    {
        using var wrongBot = JsonDocument.Parse(JsonSerializer.Serialize(Update(1, "/status@other_bot")));
        Assert.Null(TelegramBotClient.ParseUpdate(wrongBot.RootElement, "test_bot"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Update(2, "/call 12345")))!;
        node["message"]!["forward_origin"] = new System.Text.Json.Nodes.JsonObject();
        using var forward = JsonDocument.Parse(node.ToJsonString());
        Assert.Null(TelegramBotClient.ParseUpdate(forward.RootElement, "test_bot"));
    }

    [Fact]
    public async Task OutboundTelegramIsPlainTextAndSplitsWithoutBreakingUnicode()
    {
        var bodies = new List<string>();
        using var bot = new TelegramBotClient(new HttpClient(new Handler(async (request, ct) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(ct)); return Ok(new { });
        })));
        var text = new string('中', 3499) + "😀" + new string('文', 100);
        await bot.SendAsync(TelegramSettings, new(RemoteChannelKind.Telegram, "42"), text, CancellationToken.None);
        var pieces = bodies.Select(body => JsonDocument.Parse(body)).ToArray();
        try
        {
            Assert.Equal(2, pieces.Length);
            Assert.Equal(text, string.Concat(pieces.Select(p => p.RootElement.GetProperty("text").GetString())));
            Assert.All(pieces, p => { Assert.False(p.RootElement.TryGetProperty("parse_mode", out _)); Assert.True(p.RootElement.TryGetProperty("reply_markup", out _)); });
        }
        finally { foreach (var piece in pieces) piece.Dispose(); }
    }

    [Fact]
    public void TextChunkingMakesProgressEvenForAnUnpairedTrailingSurrogate()
    {
        var text = "content\ud800";
        Assert.Equal(text, string.Concat(RemotePushHttp.SplitText(text, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemotePushHttp.SplitText(text, 1).ToArray());
    }

    [Fact]
    public async Task BarkUsesJsonPushAndNeverPutsDeviceKeyInTheUrl()
    {
        string? path = null; string? body = null;
        using var bark = new BarkClient(new HttpClient(new Handler(async (request, ct) =>
        {
            path = request.RequestUri!.ToString(); body = await request.Content!.ReadAsStringAsync(ct); return Json(new { code = 200 });
        })));
        await bark.SendAsync(new() { BarkServerUrl = "https://bark.test", BarkDeviceKey = "synthetic-key" }, "test", "**plain**", CancellationToken.None);
        Assert.Equal("https://bark.test/push", path);
        using var payload = JsonDocument.Parse(body!);
        Assert.Equal("synthetic-key", payload.RootElement.GetProperty("device_key").GetString());
        Assert.Equal("plain", payload.RootElement.GetProperty("body").GetString());
        Assert.DoesNotContain("Bark", Enum.GetNames<RemoteChannelKind>()); // No inbound command channel exists.
    }

    [Fact]
    public async Task FailedNotificationIsNotAutomaticallyRetriedAndSecretsAreNotInErrors()
    {
        var calls = 0;
        using var bot = new TelegramBotClient(new HttpClient(new Handler((_, _) =>
        { calls++; throw new HttpRequestException("request URL bot" + FakeToken); })));
        var failure = await Assert.ThrowsAsync<RemoteApiException>(() => bot.SendAsync(TelegramSettings, new(RemoteChannelKind.Telegram, "42"), "test", CancellationToken.None));
        Assert.Equal(1, calls); Assert.DoesNotContain(FakeToken, failure.Message);
    }

    [Fact]
    public async Task RedirectsAndRateLimitsAreHandledWithoutLeakingCredentials()
    {
        using var redirect = new BarkClient(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { }, HttpStatusCode.Redirect)))));
        await Assert.ThrowsAsync<RemoteApiException>(() => redirect.SendAsync(new() { BarkDeviceKey = "fake-key" }, "test", "test", CancellationToken.None));
        using var rateLimited = new TelegramBotClient(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new
            { ok = false, error_code = 429, parameters = new { retry_after = 17 } }, HttpStatusCode.TooManyRequests)))));
        var limit = await Assert.ThrowsAsync<RemoteApiException>(() => rateLimited.VerifyAsync(TelegramSettings, CancellationToken.None));
        Assert.Equal(17, limit.RetryAfter);
    }

    [Theory]
    [InlineData("http://api.day.app")] [InlineData("https://user:password@example.test")] [InlineData("https://example.test?token=secret")]
    public void UnsafeApiAddressesAreRejected(string value) => Assert.Throws<InvalidOperationException>(() => RemotePushHttp.BaseUri(value));

    [Fact]
    public async Task CredentialsAreProtectedAndNewPermissionsDefaultOffForLegacyFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "vowin-remote-tests-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new RemoteControlSettingsStore(path);
            var settings = TelegramSettings; settings.BarkDeviceKey = "private-bark-key"; settings.AllowRemoteSms = true;
            await store.SaveAsync(settings);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(FakeToken, persisted); Assert.DoesNotContain("private-bark-key", persisted);
            var loaded = await store.LoadAsync(); Assert.Equal(FakeToken, loaded.TelegramBotToken); Assert.True(loaded.AllowRemoteSms);
            Assert.Equal("private-bark-key", loaded.BarkDeviceKey);
            await File.WriteAllTextAsync(path, "{\"WeixinEnabled\":false,\"NotifyIncomingSms\":true}");
            loaded = await store.LoadAsync();
            Assert.False(loaded.TelegramEnabled); Assert.False(loaded.BarkEnabled); Assert.False(loaded.AllowRemoteCalls);
            Assert.False(loaded.AllowRemoteReadSms); Assert.False(loaded.AllowRemoteLineChanges); Assert.False(loaded.AllowRemoteSms);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void DiagnosticRedactionCoversTelegramUrlsAndBarkJsonKeys()
    {
        var result = DiagnosticLogRedactor.Redact("https://api.telegram.org/bot" + FakeToken + "/sendMessage {\"device_key\":\"private-bark-key\"}");
        Assert.DoesNotContain(FakeToken, result); Assert.DoesNotContain("private-bark-key", result);
        Assert.Contains("api.telegram.org", result);
    }

    private sealed class KernelStub : IAsyncDisposable
    {
        public readonly VoKernel Kernel = new();
        public readonly IVoKernelService Service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        public readonly ConcurrentQueue<string> Writes = new();
        public readonly List<SmsMessageModel> Inbox = [];
        public Func<string, Task> Enable = _ => Task.CompletedTask;
        public Action<string> Cancel = _ => { };
        public Action<SmsMessageModel>? SmsReceived;
        public Action<string, string?>? CallReceived;
        public KernelStub()
        {
            Kernel.Pool.TryAddSlot(new("a", "OFFLINE-A")); Kernel.Pool.TryAddSlot(new("b", "OFFLINE-B"));
            var preferences = DispatchProxy.Create<IPreferenceDatabaseService, PhoneCallServiceProxy>();
            ((PhoneCallServiceProxy)preferences).Handler = (m, _) => m.Name == "GetAllSmsMessagesAsync"
                ? Task.FromResult<IReadOnlyList<SmsMessageModel>>(Inbox) : throw new NotSupportedException(m.Name);
            ((PhoneCallServiceProxy)Service).Handler = (m, args) => m.Name switch
            {
                "get_Kernel" => Kernel, "get_Preferences" => preferences,
                "GetGatewayStatus" => GatewayStatusSnapshot.Create(new() { SlotId = (string?)args![0], Title = "synthetic line" }),
                "SelectSlot" => throw new InvalidOperationException("Remote commands must never select the desktop's active slot."),
                "SendSmsAsync" => SendSms(args!), "DialAsync" => Dial(args!),
                "AnswerAsync" or "RejectAsync" or "HangupAsync" => CallAction(m.Name, args!),
                "EnableTelephonyModeAsync" => Enable((string)args![0]!),
                "CancelTelephonyModeConnection" => CancelConnection((string)args![0]!),
                "GetTelephonyModeStatus" => new TelephonyModeStatus(TelephonyMode.HostIms, true, false, "test"),
                "StartVoWifiAsync" or "StopVoWifiAsync" => WifiAction(m.Name, args!),
                "add_IncomingSmsReceived" => ChangeSms(args!, true), "remove_IncomingSmsReceived" => ChangeSms(args!, false),
                "add_IncomingCallReceived" => ChangeCall(args!, true), "remove_IncomingCallReceived" => ChangeCall(args!, false),
                _ => throw new NotSupportedException(m.Name)
            };
        }
        private object? ChangeSms(object?[] args, bool add) { if (add) SmsReceived += (Action<SmsMessageModel>)args[0]!; else SmsReceived -= (Action<SmsMessageModel>)args[0]!; return null; }
        private object? ChangeCall(object?[] args, bool add) { if (add) CallReceived += (Action<string, string?>)args[0]!; else CallReceived -= (Action<string, string?>)args[0]!; return null; }
        private Task<SmsSubmitResult> SendSms(object?[] args)
        { Writes.Enqueue("sms:" + args[3]); return Task.FromResult(new SmsSubmitResult((string)args[0]!, (string)args[1]!, default, null, 1, 1, 1, true, "accepted", [], DateTime.UtcNow)); }
        private Task<CallInfo> Dial(object?[] args) { Writes.Enqueue("dial:" + args[1]); return Task.FromResult(new CallInfo("fake", "12345", CallState.Ringing, DateTime.UtcNow, null, null, null, null)); }
        private Task<CallInfo?> CallAction(string name, object?[] args) { Writes.Enqueue(name + ":" + args[0]); return Task.FromResult<CallInfo?>(null); }
        private Task<bool> WifiAction(string name, object?[] args) { Writes.Enqueue(name + ":" + args[0]); return Task.FromResult(true); }
        private object? CancelConnection(string id) { Writes.Enqueue("cancel:" + id); Cancel(id); return null; }
        public ValueTask DisposeAsync() => Kernel.DisposeAsync();
    }

    [Fact]
    public async Task ConversationSelectionIsIsolatedAndNeverFallsBackToAnotherLine()
    {
        await using var kernel = new KernelStub(); var processor = new RemoteCommandProcessor(kernel.Service);
        var policy = new RemoteControlSettings { AllowRemoteSms = true };
        await processor.ExecuteAsync("用卡 a", default, "user-a", policy);
        await processor.ExecuteAsync("用卡 b", default, "user-b", policy);
        await processor.ExecuteAsync("短信 12345 test-a", default, "user-a", policy);
        await processor.ExecuteAsync("短信 12345 test-b", default, "user-b", policy);
        Assert.Equal(new[] { "sms:a", "sms:b" }, kernel.Writes.ToArray());
        var unknown = await processor.ExecuteAsync("短信 12345 no-selection", default, "new-user", policy);
        Assert.Contains("不会默认借用", unknown);
        await kernel.Kernel.Pool.RemoveSlotAsync("a");
        var missing = await processor.ExecuteAsync("短信 12345 removed", default, "user-a", policy);
        Assert.Contains("不会自动换到其他", missing);
        Assert.Equal(2, kernel.Writes.Count);
    }

    [Theory]
    [InlineData("@a 短信 12345 test")] [InlineData("@a 拨号 12345")] [InlineData("@a hostims 开")]
    [InlineData("@a 验证码")] [InlineData("@a 挂断")] [InlineData("@a 飞行模式 开")]
    public async Task PaidAndSensitiveCommandsRequireSeparatePermissions(string command)
    {
        await using var kernel = new KernelStub();
        var reply = await new RemoteCommandProcessor(kernel.Service).ExecuteAsync(command, default);
        Assert.Contains("未开启", reply); Assert.Empty(kernel.Writes);
    }

    [Fact]
    public async Task GroupCommandsCannotReadCodesAndOtpQueryIsScopedToItsLine()
    {
        await using var kernel = new KernelStub(); var processor = new RemoteCommandProcessor(kernel.Service);
        kernel.Inbox.Add(new() { SlotId = "a", Text = "code 123456", Timestamp = DateTime.UtcNow });
        kernel.Inbox.Add(new() { SlotId = "b", Text = "code 987654", Timestamp = DateTime.UtcNow });
        var settings = new RemoteControlSettings { AllowRemoteReadSms = true };
        Assert.Contains("仅限授权私聊", await processor.ExecuteAsync("@a 验证码", default, permissions: settings, isGroup: true));
        var reply = await processor.ExecuteAsync("@a 验证码", default, permissions: settings);
        Assert.Contains("123456", reply); Assert.DoesNotContain("987654", reply);
    }

    private static HttpClient TelegramHttp(ConcurrentQueue<string>? sent = null) => new(new Handler(async (request, ct) =>
    {
        var method = request.RequestUri!.Segments.Last();
        if (method == "getMe") return Ok(new { username = "test_bot" });
        if (method == "getWebhookInfo") return Ok(new { url = "" });
        if (method == "getUpdates") { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Ok(Array.Empty<object>()); }
        if (method == "sendMessage") { sent?.Enqueue(await request.Content!.ReadAsStringAsync(ct)); return Ok(new { }); }
        throw new InvalidOperationException("Unexpected fake API request");
    }));

    [Fact]
    public async Task NotificationFailureTurnsChannelRedAndValidationFailureIsNotShownAsReady()
    {
        await using var kernel = new KernelStub();
        var path = Path.Combine(Path.GetTempPath(), "vowin-notify-error-" + Guid.NewGuid().ToString("N"), "settings.json");
        var store = new RemoteControlSettingsStore(path); await store.SaveAsync(TelegramSettings);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var method = request.RequestUri!.Segments.Last();
            if (method == "getMe") return Ok(new { username = "test_bot" });
            if (method == "getWebhookInfo") return Ok(new { url = "" });
            if (method == "getUpdates") { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Ok(Array.Empty<object>()); }
            throw new HttpRequestException("https://telegram.test/bot" + FakeToken);
        }));
        await using var remote = new RemoteControlService(kernel.Service, store, new(http), new(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { code = 400 }))))));
        var failed = new TaskCompletionSource<GatewayStatusItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.StatusChanged += () => { if (remote.TelegramIndicator.Light == StatusLight.Error) failed.TrySetResult(remote.TelegramIndicator); };
        try
        {
            await remote.StartAsync(default);
            kernel.SmsReceived!(new() { SlotId = "a", Text = "private body", SenderOrRecipient = "TEST" });
            var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("通知失败", status.ShortStatus); Assert.DoesNotContain(FakeToken, status.Detail);
            await Assert.ThrowsAsync<RemoteApiException>(() => remote.TestBarkAsync(new() { BarkDeviceKey = "fake-key" }));
            Assert.Equal(StatusLight.Error, remote.BarkIndicator.Light);
        }
        finally { await remote.DisposeAsync(); Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task ServiceRejectsUnauthorizedUsersAndDeduplicatesPerConversation()
    {
        await using var kernel = new KernelStub();
        var path = Path.Combine(Path.GetTempPath(), "vowin-remote-service-" + Guid.NewGuid().ToString("N"), "settings.json");
        await using var remote = new RemoteControlService(kernel.Service, new(path), new(TelegramHttp()), new(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { code = 200 }))))));
        try
        {
            var settings = TelegramSettings; settings.AllowRemoteSms = true; await remote.SaveAndRestartAsync(settings);
            await remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "99", "99", "@a 短信 12345 forbidden", "1"), default);
            await remote.HandleInboundAsync(new((RemoteChannelKind)99, "42", "42", "@a 短信 12345 forbidden", "1"), default);
            Assert.Empty(kernel.Writes);
            var message = new RemoteInboundMessage(RemoteChannelKind.Telegram, "42", "42", "@a 短信 12345 allowed", "1");
            await remote.HandleInboundAsync(message, default); await remote.HandleInboundAsync(message, default);
            await remote.HandleInboundAsync(message with { SenderId = "43", ConversationId = "43", Text = "@b 短信 12345 second" }, default);
            Assert.Equal(new[] { "sms:a", "sms:b" }, kernel.Writes.ToArray());
            Assert.DoesNotContain("allowed", remote.LastActivity); Assert.DoesNotContain("second", remote.LastActivity);
        }
        finally { await remote.DisposeAsync(); Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task CancelBypassesBothPendingSenderAndLineLocks()
    {
        await using var kernel = new KernelStub();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kernel.Enable = id => { entered.TrySetResult(); return release.Task; };
        kernel.Cancel = id => release.TrySetCanceled();
        var path = Path.Combine(Path.GetTempPath(), "vowin-remote-cancel-" + Guid.NewGuid().ToString("N"), "settings.json");
        await using var remote = new RemoteControlService(kernel.Service, new(path), new(TelegramHttp()), new(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { code = 200 }))))));
        try
        {
            var settings = TelegramSettings; settings.AllowRemoteLineChanges = true; await remote.SaveAndRestartAsync(settings);
            var start = remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "42", "42", "@a hostims 开", "1"), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "42", "42", "@a 取消连接", "2"), default).WaitAsync(TimeSpan.FromSeconds(3));
            await start.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Contains("cancel:a", kernel.Writes);
        }
        finally { release.TrySetResult(); await remote.DisposeAsync(); Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task PublicStartOnlyReturnsTheSendersOwnIdAndPairingIsPrivateOneUseAndRateLimited()
    {
        await using var kernel = new KernelStub();
        var path = Path.Combine(Path.GetTempPath(), "vowin-pairing-" + Guid.NewGuid().ToString("N"), "settings.json");
        var sent = new ConcurrentQueue<string>();
        await using var remote = new RemoteControlService(kernel.Service, new(path), new(TelegramHttp(sent)), new(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { code = 200 }))))));
        try
        {
            await remote.SaveAndRestartAsync(TelegramSettings);
            await remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "99", "99", "/start", "start"), default);
            using (var reply = JsonDocument.Parse(Assert.Single(sent)))
            { Assert.Contains("99", reply.RootElement.GetProperty("text").GetString()); Assert.DoesNotContain("OFFLINE", reply.RootElement.GetProperty("text").GetString()); }
            var code = remote.CreatePairingCode();
            await remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "99", "99", "绑定 " + code, "bind", IsGroup: true), default);
            Assert.DoesNotContain("99", RemoteControlService.ParseAllowed(remote.Settings.TelegramAllowedUsers));
            await remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "99", "99", "绑定 " + code, "bind-private"), default);
            Assert.Contains("99", RemoteControlService.ParseAllowed(remote.Settings.TelegramAllowedUsers));
            await remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "98", "98", "绑定 " + code, "reuse"), default);
            Assert.DoesNotContain("98", RemoteControlService.ParseAllowed(remote.Settings.TelegramAllowedUsers));
            code = remote.CreatePairingCode();
            for (var attempt = 0; attempt < 10; attempt++)
                await remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "97", "97", "绑定 000000", "wrong-" + attempt), default);
            await remote.HandleInboundAsync(new(RemoteChannelKind.Telegram, "97", "97", "绑定 " + code, "blocked"), default);
            Assert.DoesNotContain("97", RemoteControlService.ParseAllowed(remote.Settings.TelegramAllowedUsers));
            Assert.Contains("尝试过多", remote.PairingCodeStatus); Assert.Empty(kernel.Writes);
        }
        finally { await remote.DisposeAsync(); Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BarkSmsPrivacySwitchIsIndependentAndDoesNotExposeCommands(bool content)
    {
        await using var kernel = new KernelStub();
        var path = Path.Combine(Path.GetTempPath(), "vowin-bark-privacy-" + Guid.NewGuid().ToString("N"), "settings.json");
        var store = new RemoteControlSettingsStore(path);
        var captured = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await store.SaveAsync(new() { BarkEnabled = true, BarkDeviceKey = "fake-key", BarkIncludeSmsContent = content });
        await using var remote = new RemoteControlService(kernel.Service, store, new(TelegramHttp()), new(new HttpClient(new Handler(async (request, ct) =>
        { captured.TrySetResult(await request.Content!.ReadAsStringAsync(ct)); return Json(new { code = 200 }); }))));
        try
        {
            await remote.StartAsync(default);
            kernel.SmsReceived!(new() { SlotId = "b", Text = "Your code is 123456", SenderOrRecipient = "TEST" });
            using var json = JsonDocument.Parse(await captured.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            var body = json.RootElement.GetProperty("body").GetString()!;
            Assert.Equal(content, body.Contains("123456")); Assert.Contains("b", body); Assert.DoesNotContain("回复", body);
        }
        finally { await remote.DisposeAsync(); Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
