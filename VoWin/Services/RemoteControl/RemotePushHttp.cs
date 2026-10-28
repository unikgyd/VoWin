using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoWin.Services.RemoteControl;

internal sealed class RemoteApiException(string message, int code = 0, int retryAfter = 0) : Exception(message)
{
    public int Code { get; } = code;
    public int RetryAfter { get; } = retryAfter;
}

internal static class RemotePushHttp
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(40) };

    public static Uri BaseUri(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("API 地址须为 HTTPS，不含账号、查询参数或片段。");
        return uri;
    }

    public static async Task<JsonDocument> PostAsync(HttpClient http, Uri uri, object body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Neither request URI nor provider error text is safe to log: both can contain credentials.
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new RemoteApiException("API 返回重定向，已拒绝转发凭据。", (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > 1024 * 1024)
                throw new RemoteApiException("API 响应过大。", (int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + length > 1024 * 1024) throw new RemoteApiException("API 响应过大。");
                await memory.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            }
            JsonDocument json;
            try { json = JsonDocument.Parse(memory.ToArray()); }
            catch (JsonException) { throw new RemoteApiException($"API 响应不是有效 JSON（HTTP {(int)response.StatusCode}）。"); }
            if (!response.IsSuccessStatusCode)
            {
                var retry = json.RootElement.TryGetProperty("parameters", out var parameters) &&
                    parameters.TryGetProperty("retry_after", out var seconds) && seconds.TryGetInt32(out var n) ? Math.Clamp(n, 1, 86400) : 0;
                json.Dispose();
                throw new RemoteApiException($"API 请求失败（HTTP {(int)response.StatusCode}）。", (int)response.StatusCode, retry);
            }
            return json;
        }
        catch (HttpRequestException) { throw new RemoteApiException("无法连接 API，请检查网络、代理和服务器地址。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RemoteApiException("API 请求超时；为避免重复投递，不自动重发通知。"); }
    }

    public static string PlainText(string text)
    {
        var plain = Regex.Replace(text, @"(?m)^#{1,3}\s+|^>\s?", "").Replace("**", "").Replace("`", "");
        return Regex.Replace(plain, @"\\([\\*_{}\[\]()#+\-.!|>])", "$1");
    }

    public static IEnumerable<string> SplitText(string text, int maximum = 3500)
    {
        if (maximum < 2) throw new ArgumentOutOfRangeException(nameof(maximum));
        for (var start = 0; start < text.Length;)
        {
            var count = Math.Min(maximum, text.Length - start);
            if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1]) &&
                char.IsLowSurrogate(text[start + count])) count--;
            yield return text.Substring(start, count);
            start += count;
        }
    }
}
