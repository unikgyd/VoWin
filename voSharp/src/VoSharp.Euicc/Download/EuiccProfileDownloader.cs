using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoSharp.Euicc.Asn1;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Sgp22;
using VoSharp.Euicc.Transport;

namespace VoSharp.Euicc.Download;

/// <summary>Pure .NET SGP.22 ES9+/ES10b profile-download relay. The eUICC performs credential validation and SCP03t decryption.</summary>
internal sealed class EuiccProfileDownloader
{
    private const int StoreDataMss = 120;
    private readonly IEuiccTransport _transport;
    private readonly HttpClient _http;
    private readonly bool _allowUntrustedTls;
    private string? _lastTlsValidationFailure;

    public EuiccProfileDownloader(IEuiccTransport transport, bool allowUntrustedTls = false)
    {
        _transport = transport;
        _allowUntrustedTls = allowUntrustedTls;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
        {
            if (errors == SslPolicyErrors.None)
            {
                _lastTlsValidationFailure = null;
                return true;
            }

            var chainStatuses = chain?.ChainStatus
                .Where(status => status.Status != System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError)
                .Select(status => status.Status)
                .Distinct()
                .ToArray() ?? [];
            var chainErrors = chainStatuses.Select(status => status.ToString()).ToArray();
            var subject = certificate?.Subject ?? "未知证书";
            _lastTlsValidationFailure = chainErrors.Length > 0
                ? $"{errors}；证书链={string.Join(", ", chainErrors)}；Subject={subject}"
                : $"{errors}；Subject={subject}";
            var isOnlyUntrustedRoot = errors == SslPolicyErrors.RemoteCertificateChainErrors &&
                                      chainStatuses.Length > 0 &&
                                      chainStatuses.All(status => status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot);
            return _allowUntrustedTls && isOnlyUntrustedRoot;
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
    }

    public async Task<EuiccWorkerDownloadResult> DownloadAsync(EuiccActivationCode code, string imei, string? confirmationCode, IProgress<EuiccDownloadProgress>? progress, CancellationToken ct)
    {
        var transactionStarted = false; var installStarted = false; var channel = 0; string? transactionId = null; byte[]? cardTransactionId = null;
        var currentStage = "校验 SM-DP+ 地址";
        try
        {
            ValidateSmdpAddress(code.SmdpAddress);
            currentStage = "打开 eUICC 逻辑通道并读取挑战值";
            progress?.Report(new(10, "正在连接 SM-DP+ 并读取 eUICC 挑战值"));
            channel = await _transport.OpenLogicalChannelAsync(Sgp22Client.IsdrAidStandard, ct).ConfigureAwait(false);
            var challenge = FirstValue(await Es10Async(channel, [0xBF, 0x2E, 0x00], ct).ConfigureAwait(false), 0x80) ?? throw new InvalidDataException("eUICC 未返回挑战值。");
            var info1 = await Es10Async(channel, [0xBF, 0x20, 0x00], ct).ConfigureAwait(false);
            currentStage = "向 SM-DP+ 发起下载认证";
            progress?.Report(new(25, "正在向 SM-DP+ 发起下载认证"));
            var initiated = await Es9Async(code.SmdpAddress, "initiateAuthentication", new()
            {
                ["smdpAddress"] = code.SmdpAddress, ["euiccChallenge"] = Convert.ToBase64String(challenge), ["euiccInfo1"] = Convert.ToBase64String(info1)
            }, ["transactionId", "serverSigned1", "serverSignature1", "euiccCiPKIdToBeUsed", "serverCertificate"], ct).ConfigureAwait(false);
            transactionId = GetString(initiated, "transactionId");
            cardTransactionId = FirstValue(GetB64(initiated, "serverSigned1"), 0x80)
                ?? throw new InvalidDataException("SM-DP+ 的 ServerSigned1 缺少 transactionId。");
            transactionStarted = true;
            currentStage = "由 eUICC 验证 SM-DP+ 证书";
            progress?.Report(new(40, "正在由 eUICC 验证 SM-DP+ 证书"));
            var authResponse = await Es10Async(channel, BuildAuthenticateServer(initiated, code.MatchingId, imei), ct).ConfigureAwait(false);
            ThrowIfAuthenticateFailed(authResponse);
            currentStage = "请求 Profile 下载授权";
            progress?.Report(new(52, "正在请求 Profile 下载授权"));
            var authenticated = await Es9Async(code.SmdpAddress, "authenticateClient", new()
            {
                ["transactionId"] = transactionId, ["authenticateServerResponse"] = Convert.ToBase64String(authResponse)
            }, ["profileMetadata", "smdpSigned2", "smdpSignature2", "smdpCertificate"], ct).ConfigureAwait(false);
            var prepareResponse = await Es10Async(channel, BuildPrepareDownload(authenticated, confirmationCode), ct).ConfigureAwait(false);
            currentStage = "下载已绑定的 Profile 包";
            progress?.Report(new(65, "正在下载已绑定的 Profile 包"));
            var bppReply = await Es9Async(code.SmdpAddress, "getBoundProfilePackage", new()
            {
                ["transactionId"] = transactionId, ["prepareDownloadResponse"] = Convert.ToBase64String(prepareResponse)
            }, ["boundProfilePackage"], ct).ConfigureAwait(false);
            var segments = SegmentBoundProfilePackage(GetB64(bppReply, "boundProfilePackage"));
            installStarted = true;
            currentStage = "向 eUICC 写入 Profile 包";
            for (var i = 0; i < segments.Count; i++)
            {
                var response = await Es10Async(channel, segments[i], ct).ConfigureAwait(false);
                if (i == segments.Count - 1)
                {
                    var iccid = ReadInstallationResult(response);
                    currentStage = "向运营商确认安装通知";
                    progress?.Report(new(90, "Profile 已写入，正在确认运营商通知"));
                    return new EuiccWorkerDownloadResult(iccid, false, await DeliverInstallNotificationAsync(channel, response, ct).ConfigureAwait(false));
                }
                progress?.Report(new(
                    70 + Math.Min(18, (i + 1) * 18 / segments.Count),
                    $"正在写入 Profile 包（分片 {i + 1}/{segments.Count}）"));
            }
            throw new InvalidDataException("eUICC 未返回 Profile 安装结果。");
        }
        catch (EuiccProfileDownloaderException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await CancelSafelyAsync(channel, code.SmdpAddress, transactionId, cardTransactionId).ConfigureAwait(false); throw; }
        catch (Exception ex)
        {
            if (transactionStarted && !installStarted) await CancelSafelyAsync(channel, code.SmdpAddress, transactionId, cardTransactionId).ConfigureAwait(false);
            throw new EuiccProfileDownloaderException($"{currentStage}失败：{Translate(ex)}", !transactionStarted, installStarted, ex);
        }
        finally
        {
            _http.Dispose();
            if (channel > 0) try { await _transport.CloseLogicalChannelAsync(channel, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    private async Task<byte[]> Es10Async(int channel, byte[] request, CancellationToken ct)
    {
        if (request.Length == 0) throw new ArgumentException("ES10 request is empty.", nameof(request));
        using var output = new MemoryStream(); byte sequence = 0;
        for (var offset = 0; offset < request.Length;)
        {
            var length = Math.Min(StoreDataMss, request.Length - offset); var last = offset + length == request.Length;
            var apdu = new byte[6 + length]; apdu[0] = 0x80; apdu[1] = 0xE2; apdu[2] = last ? (byte)0x91 : (byte)0x11; apdu[3] = sequence++; apdu[4] = (byte)length;
            Buffer.BlockCopy(request, offset, apdu, 5, length); apdu[^1] = 0x00;
            var response = await _transport.TransmitLogicalChannelAsync(channel, apdu, ct).ConfigureAwait(false);
            if (response.Length < 2) throw new InvalidDataException("eUICC APDU 响应过短。");
            var sw1 = response[^2]; var sw2 = response[^1];
            if (!((sw1 == 0x90 && sw2 == 0x00) || sw1 == 0x91)) throw new InvalidOperationException($"eUICC 拒绝 ES10 命令（SW={sw1:X2}{sw2:X2}）。");
            output.Write(response, 0, response.Length - 2); offset += length;
        }
        return output.ToArray();
    }

    private async Task<JsonElement> Es9Async(string smdp, string function, Dictionary<string, string> body, string[] required, CancellationToken ct)
    {
        var endpoint = new Uri($"https://{smdp}/gsma/rsp2/es9plus/{function}");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
        request.Headers.UserAgent.ParseAdd("gsma-rsp-lpad"); request.Headers.TryAddWithoutValidation("X-Admin-Protocol", "gsma/rsp/v2.2.2");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        // A completed HTTP exchange means any explicitly accepted UntrustedRoot
        // applied only to that handshake. Do not let it contaminate later APDU errors.
        _lastTlsValidationFailure = null;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonElement root;
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new JsonException("响应正文为空");
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "未知类型";
            throw new InvalidDataException(
                $"SM-DP+ {function} 返回的不是有效 JSON（HTTP {(int)response.StatusCode}，{mediaType}）。请检查地址、网络代理或服务端状态。",
                ex);
        }
        var status = root.TryGetProperty("header", out var h) && h.TryGetProperty("functionExecutionStatus", out var f) && f.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (!response.IsSuccessStatusCode || status is not (null or "" or "Executed-Success" or "Executed-WithWarning"))
            throw new InvalidOperationException(TryStatusMessage(root) ?? $"SM-DP+ {function} 失败（HTTP {(int)response.StatusCode}，{status ?? "无状态"}）。");
        foreach (var field in required) if (!root.TryGetProperty(field, out _)) throw new InvalidDataException($"SM-DP+ {function} 未返回 {field}。");
        return root;
    }

    private static byte[] BuildAuthenticateServer(JsonElement init, string matchingId, string imei)
    {
        var digits = new string(imei.Where(char.IsDigit).ToArray()); var bcd = ToBcd(digits.PadRight(16, 'F')[..16]);
        var device = Construct(0xA1, Encode(0x80, bcd[..4]), Encode(0xA1, []), Encode(0x82, bcd));
        var context = string.IsNullOrEmpty(matchingId) ? Construct(0xA0, device) : Construct(0xA0, Encode(0x80, System.Text.Encoding.UTF8.GetBytes(matchingId)), device);
        return Construct(0xBF38, Encode(0x30, Unwrap(GetB64(init, "serverSigned1"), 0x30)), Encode(0x5F37, Unwrap(GetB64(init, "serverSignature1"), 0x5F37)), Encode(0x04, Unwrap(GetB64(init, "euiccCiPKIdToBeUsed"), 0x04)), Encode(0x30, Unwrap(GetB64(init, "serverCertificate"), 0x30)), context);
    }

    private static byte[] BuildPrepareDownload(JsonElement auth, string? confirmationCode)
    {
        var signed2 = GetB64(auth, "smdpSigned2"); var transaction = FirstValue(signed2, 0x80) ?? throw new InvalidDataException("SM-DP+ 授权缺少 transactionId。");
        var fields = new List<byte[]> { Encode(0x30, Unwrap(signed2, 0x30)), Encode(0x5F37, Unwrap(GetB64(auth, "smdpSignature2"), 0x5F37)) };
        if (FirstValue(signed2, 0x01)?.Any(v => v != 0) == true)
        {
            if (string.IsNullOrWhiteSpace(confirmationCode)) throw new InvalidOperationException("该 Profile 需要确认码。");
            fields.Add(Encode(0x04, SHA256.HashData([.. SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(confirmationCode)), .. transaction])));
        }
        fields.Add(Encode(0x30, Unwrap(GetB64(auth, "smdpCertificate"), 0x30))); return Construct(0xBF21, [.. fields]);
    }

    private static List<byte[]> SegmentBoundProfilePackage(byte[] bytes)
    {
        var outer = ReadElement(bytes, 0); if (outer.Tag != 0xBF36 || outer.Total != bytes.Length) throw new InvalidDataException("SM-DP+ 返回的 BPP 格式无效。");
        var segments = new List<byte[]>(); var cursor = outer.ValueOffset; var end = outer.Total; var first = ReadElement(bytes, cursor);
        if (first.Tag != 0xBF23) throw new InvalidDataException("BPP 缺少安全通道初始化数据。");
        segments.Add(bytes[..(cursor + first.Total)]); cursor += first.Total;
        while (cursor < end)
        {
            var element = ReadElement(bytes, cursor);
            if (element.Tag is 0xA1 or 0xA3)
            {
                segments.Add(bytes[cursor..element.ValueOffset]); var child = element.ValueOffset;
                while (child < cursor + element.Total) { var e = ReadElement(bytes, child); segments.Add(bytes[child..(child + e.Total)]); child += e.Total; }
            }
            else segments.Add(bytes[cursor..(cursor + element.Total)]);
            cursor += element.Total;
        }
        return segments;
    }

    private async Task<string?> DeliverInstallNotificationAsync(int channel, byte[] installation, CancellationToken ct)
    {
        var metadata = FindFirst(installation, 0xBF2F); var sequence = metadata is null ? null : FirstValue(metadata.Value, 0x80); var address = metadata is null ? null : FirstValue(metadata.Value, 0x0C);
        if (sequence is null || address is null) return null;
        try
        {
            var pending = await Es10Async(channel, Construct(0xBF2B, Encode(0x80, sequence)), ct).ConfigureAwait(false);
            await HandleNotificationAsync(System.Text.Encoding.UTF8.GetString(address), pending, ct).ConfigureAwait(false);
            await Es10Async(channel, Construct(0xBF30, Encode(0x80, sequence)), ct).ConfigureAwait(false); return null;
        }
        catch (Exception ex) { return "Profile 已写入，但运营商安装通知未确认：" + Translate(ex); }
    }

    private async Task HandleNotificationAsync(string address, byte[] pending, CancellationToken ct)
    {
        ValidateSmdpAddress(address);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{address}/gsma/rsp2/es9plus/handleNotification")) { Content = JsonContent.Create(new Dictionary<string, string> { ["pendingNotification"] = Convert.ToBase64String(pending) }) };
        request.Headers.UserAgent.ParseAdd("gsma-rsp-lpad"); request.Headers.TryAddWithoutValidation("X-Admin-Protocol", "gsma/rsp/v2.2.2");
        // Installation notification remains strict even when the user opts into
        // bypassing TLS validation for the one-time primary SM-DP+ download.
        using var strictClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = TimeSpan.FromSeconds(90) };
        using var response = await strictClient.SendAsync(request, ct).ConfigureAwait(false); if (response.StatusCode != HttpStatusCode.NoContent) throw new InvalidOperationException($"运营商通知确认返回 HTTP {(int)response.StatusCode}。");
    }

    private async Task CancelSafelyAsync(int channel, string smdp, string? transactionId, byte[]? cardTransactionId)
    {
        if (channel <= 0 || string.IsNullOrWhiteSpace(transactionId) || cardTransactionId is null) return;
        try { var card = await Es10Async(channel, Construct(0xBF41, Encode(0x80, cardTransactionId), Encode(0x81, [0])), CancellationToken.None).ConfigureAwait(false); await Es9Async(smdp, "cancelSession", new() { ["transactionId"] = transactionId, ["cancelSessionResponse"] = Convert.ToBase64String(card) }, [], CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    private static void ThrowIfAuthenticateFailed(byte[] response) { var root = FindFirst(response, 0xBF38); if (root?.Children.FirstOrDefault(c => c.Tag == 0xA1) is { } fail) throw new InvalidOperationException($"eUICC 拒绝 SM-DP+ 认证（错误码 {FirstValue(fail.Value, 0x02)?.LastOrDefault() ?? 0}）。"); }
    private static string ReadInstallationResult(byte[] response)
    {
        var result = FindFirst(response, 0xBF37) ?? throw new InvalidDataException("eUICC 未返回 ProfileInstallationResult。"); var data = result.FindFirstRecursive(0xBF27) ?? throw new InvalidDataException("安装结果缺少 Profile 数据。");
        if (data.FindFirstChild(0xA2)?.FindFirstChild(0xA1) is { } fail) throw new InvalidOperationException($"eUICC 拒绝安装 Profile（原因 {FirstValue(fail.Value, 0x81)?.LastOrDefault() ?? 0}）。");
        return data.FindFirstRecursive(0x5A) is { Value: var iccid } ? Tlv.BcdToIccid(iccid) : throw new InvalidDataException("安装结果未返回 ICCID。");
    }

    private static byte[] Encode(uint tag, byte[] value) => new Tlv(tag, value).Encode();
    private static byte[] Construct(uint tag, params byte[][] children) { var root = new Tlv(tag); foreach (var child in children) root.Children.Add(Tlv.Parse(child).tlv); return root.Encode(); }
    private static byte[] Unwrap(byte[] bytes, uint tag) { try { var (t, n) = Tlv.Parse(bytes); return t.Tag == tag && n == bytes.Length ? t.Value : bytes; } catch { return bytes; } }
    private static byte[]? FirstValue(byte[] source, uint tag) => FindFirst(source, tag)?.Value;
    private static Tlv? FindFirst(byte[] source, uint tag) { try { return Tlv.ParseAll(source).SelectMany(x => x.FindAllRecursive(tag)).FirstOrDefault(); } catch { return null; } }
    private static string GetString(JsonElement root, string name) => root.GetProperty(name).GetString() ?? throw new InvalidDataException($"SM-DP+ {name} 为空。");
    private static byte[] GetB64(JsonElement root, string name)
    {
        var value = GetString(root, name).Trim();
        value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        return Convert.FromBase64String(value);
    }
    private static string? TryStatusMessage(JsonElement root)
    {
        if (!root.TryGetProperty("header", out var header) ||
            !header.TryGetProperty("functionExecutionStatus", out var execution) ||
            !execution.TryGetProperty("statusCodeData", out var data)) return null;

        var parts = new List<string>();
        foreach (var name in new[] { "subjectCode", "reasonCode", "message" })
        {
            if (data.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                var text = value.ToString().Trim();
                if (!string.IsNullOrEmpty(text)) parts.Add($"{name}={text}");
            }
        }
        return parts.Count == 0 ? null : string.Join("，", parts);
    }

    private string Translate(Exception exception)
    {
        var exceptionText = string.Join(" → ", EnumerateExceptionMessages(exception));
        var isTlsFailure = exceptionText.Contains("SSL connection", StringComparison.OrdinalIgnoreCase) ||
                           exceptionText.Contains("UntrustedRoot", StringComparison.OrdinalIgnoreCase) ||
                           exceptionText.Contains("certificate", StringComparison.OrdinalIgnoreCase);
        if (isTlsFailure)
        {
            return "SM-DP+ TLS 证书未通过 Windows 信任校验（" +
                   (_lastTlsValidationFailure ?? exceptionText) +
                   "）。请检查系统日期、Windows 根证书更新、HTTPS 检查代理及服务器证书链；不要直接关闭证书校验。";
        }

        var text = exception switch
        {
            TaskCanceledException => "连接 SM-DP+ 超时（最多等待 90 秒）。请检查网络、DNS 或代理设置。",
            HttpRequestException => $"无法连接 SM-DP+：{exceptionText}",
            _ => exception.Message
        };
        return text.Replace("confirmation code", "确认码", StringComparison.OrdinalIgnoreCase)
            .Replace("matchingID", "Matching ID", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateExceptionMessages(Exception exception)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (!string.IsNullOrEmpty(message) && seen.Add(message)) yield return message;
        }
    }
    private static readonly Regex Smdp = new("^(?:[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?|\\[[0-9A-Fa-f:.]+\\])(?::[0-9]{1,5})?$", RegexOptions.Compiled);
    private static void ValidateSmdpAddress(string address) { if (!Smdp.IsMatch(address.Trim())) throw new InvalidOperationException("SM-DP+ 地址无效。"); }
    private static byte[] ToBcd(string value) { var result = new byte[value.Length / 2]; for (var i = 0; i < result.Length; i++) { var low = value[i * 2] == 'F' ? 15 : value[i * 2] - '0'; var high = value[i * 2 + 1] == 'F' ? 15 : value[i * 2 + 1] - '0'; result[i] = (byte)(low | high << 4); } return result; }
    private readonly record struct Element(uint Tag, int ValueOffset, int Total);
    private static Element ReadElement(byte[] bytes, int offset) { var start = offset; uint tag = bytes[offset++]; if ((tag & 31) == 31) { byte b; do { b = bytes[offset++]; tag = (tag << 8) | b; } while ((b & 128) != 0); } int length = bytes[offset++]; if ((length & 128) != 0) { var n = length & 127; length = 0; if (n is 0 or > 4) throw new InvalidDataException("BER 长度无效。"); while (n-- > 0) length = (length << 8) | bytes[offset++]; } if (length < 0 || offset + length > bytes.Length) throw new InvalidDataException("BER 数据截断。"); return new(tag, offset, offset + length - start); }
}

internal sealed record EuiccWorkerDownloadResult(string Iccid, bool Recovered, string? Warning);
internal sealed class EuiccProfileDownloaderException : Exception
{
    public bool RetrySafe { get; }
    public bool Ambiguous { get; }
    public EuiccProfileDownloaderException(string message, bool retrySafe, bool ambiguous, Exception? innerException = null) : base(message, innerException) { RetrySafe = retrySafe; Ambiguous = ambiguous; }
}
