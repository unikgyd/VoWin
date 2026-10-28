using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VoWin.Models;

namespace VoWin.Services;

internal sealed record SmsApiResponse(int Status, object Body, int RetryAfter = 0);
internal sealed record SmsApiMessage(string Id, string? SlotId, string Sender, string Text, DateTime TimestampUtc,
    string? Code, bool TextTruncated);

/// <summary>Read-only, bounded queries against persisted incoming SMS. Never calls the modem/kernel.</summary>
internal sealed class SmsApiRequestProcessor(SmsApiSettings settings, IPreferenceDatabaseService database,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly byte[] _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Token));
    private readonly object _rateLock = new();
    private DateTimeOffset _rateWindow;
    private int _requests;

    public async Task<SmsApiResponse> ProcessAsync(string method, string rawUrl, string? authorization, string? origin,
        IPAddress localAddress, IPAddress remoteAddress, CancellationToken ct = default)
    {
        if (!settings.Enabled) return Error(503, "disabled");
        var now = _clock.GetUtcNow();
        lock (_rateLock)
        {
            if (now - _rateWindow >= TimeSpan.FromMinutes(1)) { _rateWindow = now; _requests = 0; }
            if (++_requests > 120) return new(429, new { error = "rate_limited" }, Math.Max(1, 60 - (int)(now - _rateWindow).TotalSeconds));
        }
        // Http.sys prefixes are not a substitute for checking the actual local socket address.
        if (localAddress.IsIPv4MappedToIPv6) localAddress = localAddress.MapToIPv4();
        if (remoteAddress.IsIPv4MappedToIPv6) remoteAddress = remoteAddress.MapToIPv4();
        if (!localAddress.Equals(settings.Validate()) || !IsPrivateClient(remoteAddress) || origin != null)
            return Error(403, "access_denied");
        if (settings.Token.Length < 32 || authorization == null || authorization.Length > 256 ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(_tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
            return Error(401, "unauthorized");
        if (method != "GET") return Error(405, "read_only_get_required");
        if (rawUrl.Length > 2048 || !rawUrl.StartsWith('/') || rawUrl.Contains('#') || rawUrl.Any(char.IsControl)) return Error(400, "invalid_query");
        var separator = rawUrl.IndexOf('?');
        var path = separator < 0 ? rawUrl : rawUrl[..separator];
        if (path is not ("/api/v1/sms" or "/api/v1/otp")) return Error(404, "not_found");
        SmsInboxQuery query;
        try { query = ParseQuery(separator < 0 ? "" : rawUrl[(separator + 1)..], path.EndsWith("/otp"), now); }
        catch (ArgumentException) { return Error(400, "invalid_query", "支持 limit(1–100)、keyword、sender、slotId、otpOnly、maxAgeSeconds(1–604800)、since(含时区 ISO 8601)。参数不可重复；Token 只放 Authorization 请求头。"); }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await database.QueryIncomingSmsAsync(query, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var messages = result.Messages.Select(m => new SmsApiMessage(m.Id, m.SlotId, m.SenderOrRecipient,
                TruncateText(m.Text), m.Timestamp, m.ExtractedOtpCode, m.Text.Length > 4096)).ToArray();
            return path.EndsWith("/otp") ? new(200, new { found = messages.Length > 0, code = messages.FirstOrDefault()?.Code,
                message = messages.FirstOrDefault(), scanLimited = result.ScanLimited, generatedAtUtc = now.UtcDateTime, codeExtraction = "heuristic" })
                : new(200, new { items = messages, count = messages.Length, scanLimited = result.ScanLimited, generatedAtUtc = now.UtcDateTime });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Error(503, "temporarily_unavailable"); } // Never echo SQL, paths, raw SMS or credential material.
    }

    internal static string TruncateText(string text) => text.Length <= 4096 ? text :
        text[..(char.IsHighSurrogate(text[4095]) && char.IsLowSurrogate(text[4096]) ? 4095 : 4096)];

    internal static SmsInboxQuery ParseQuery(string raw, bool latestOtp, DateTimeOffset now)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || Regex.IsMatch(pair, "%(?![0-9a-fA-F]{2})")) throw new ArgumentException();
            var name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = Uri.UnescapeDataString(parts[1].Replace('+', ' '));
            if (name is not ("limit" or "keyword" or "sender" or "slotId" or "otpOnly" or "maxAgeSeconds" or "since") ||
                value.Any(char.IsControl) || !values.TryAdd(name, value)) throw new ArgumentException();
        }
        int Number(string key, int fallback, int maximum) => !values.TryGetValue(key, out var value) ? fallback :
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= maximum ? n : throw new ArgumentException();
        string? Text(string key, int max) => !values.TryGetValue(key, out var value) || value.Length == 0 ? null :
            value.Length <= max ? value : throw new ArgumentException();
        var age = Number("maxAgeSeconds", latestOtp ? 300 : 86400, 604800);
        var limit = Number("limit", latestOtp ? 1 : 20, 100);
        if (latestOtp && limit != 1) throw new ArgumentException();
        var otp = latestOtp;
        if (values.TryGetValue("otpOnly", out var flag))
        {
            if (!bool.TryParse(flag, out var enabled) || latestOtp && !enabled) throw new ArgumentException();
            otp = enabled;
        }
        var since = now.AddSeconds(-age);
        if (values.TryGetValue("since", out var timestamp))
        {
            if (!Regex.IsMatch(timestamp, @"(?:[zZ]|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(timestamp,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed > now) throw new ArgumentException();
            if (parsed > since) since = parsed;
        }
        return new(since.UtcDateTime, now.UtcDateTime, limit, Text("keyword", 200), Text("sender", 100), Text("slotId", 128), otp);
    }

    private static bool IsPrivateClient(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            : (bytes[0] & 0xfe) == 0xfc;
    }
    private static SmsApiResponse Error(int status, string error, string? hint = null) => new(status, new { error, hint });
}
