using System.Net.Http;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

/// <summary>Outbound notifications only. There is deliberately no command receiver or reply target.</summary>
internal sealed class BarkClient : IDisposable
{
    private readonly HttpClient _http;
    public BarkClient(HttpClient? http = null) => _http = http ?? RemotePushHttp.CreateClient();
    public static void Validate(RemoteControlSettings settings)
    {
        _ = RemotePushHttp.BaseUri(settings.BarkServerUrl);
        if (!System.Text.RegularExpressions.Regex.IsMatch(settings.BarkDeviceKey.Trim(), "^[A-Za-z0-9_-]{1,256}$"))
            throw new InvalidOperationException("请填写 Bark App 的设备 Key（不是完整推送网址）。");
    }
    public async Task SendAsync(RemoteControlSettings settings, string title, string body, CancellationToken ct)
    {
        Validate(settings);
        var plain = RemotePushHttp.PlainText(body);
        var notification = RemotePushHttp.SplitText(plain).FirstOrDefault() ?? "VoWin 通知";
        if (notification.Length < plain.Length) notification += "\n（内容过长，请在 VoWin 查看完整短信）";
        using var json = await RemotePushHttp.PostAsync(_http, new Uri(RemotePushHttp.BaseUri(settings.BarkServerUrl), "push"),
            new { device_key = settings.BarkDeviceKey.Trim(), title, body = notification, group = "VoWin" }, ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("code", out var code) || !code.TryGetInt32(out var value) || value != 200)
            throw new RemoteApiException("Bark 未确认推送，请检查设备 Key 和服务器地址。");
    }
    public void Dispose() => _http.Dispose();
}
