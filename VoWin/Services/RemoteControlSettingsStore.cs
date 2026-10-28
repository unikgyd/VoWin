using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoWin.Models;

namespace VoWin.Services;

internal sealed class RemoteControlSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoWin.RemoteControl.v1");
    private readonly string _path;

    public RemoteControlSettingsStore(string? path = null)
    {
        if (path != null) { _path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(_path)!); return; }
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "remote-control.json");
    }

    public async Task<RemoteControlSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new RemoteControlSettings();
        try
        {
            await using var stream = File.OpenRead(_path);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedSettings>(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            return persisted?.ToSettings() ?? new RemoteControlSettings();
        }
        catch
        {
            return new RemoteControlSettings();
        }
    }

    public async Task SaveAsync(RemoteControlSettings value, CancellationToken ct = default)
    {
        var tmp = _path + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(stream, PersistedSettings.FromSettings(value),
                new JsonSerializerOptions { WriteIndented = true }, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        File.Move(tmp, _path, true);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return string.Empty; }
    }

    private sealed class PersistedSettings
    {
        public bool WeixinEnabled { get; set; }
        public string WeixinBaseUrl { get; set; } = string.Empty;
        public string WeixinTokenProtected { get; set; } = string.Empty;
        public string WeixinBotId { get; set; } = string.Empty;
        public string WeixinUserId { get; set; } = string.Empty;
        public string WeixinAllowedUsers { get; set; } = string.Empty;
        public bool QqEnabled { get; set; }
        public bool TelegramEnabled { get; set; }
        public string TelegramApiBaseUrl { get; set; } = "https://api.telegram.org";
        public string TelegramBotTokenProtected { get; set; } = string.Empty;
        public string TelegramAllowedUsers { get; set; } = string.Empty;
        public bool BarkEnabled { get; set; }
        public string BarkServerUrl { get; set; } = "https://api.day.app";
        public string BarkDeviceKeyProtected { get; set; } = string.Empty;
        public bool BarkIncludeSmsContent { get; set; }
        public bool AllowRemoteCalls { get; set; }
        public bool AllowRemoteSms { get; set; }
        public bool AllowRemoteLineChanges { get; set; }
        public bool AllowRemoteReadSms { get; set; }
        public string QqAppId { get; set; } = string.Empty;
        public string QqClientSecretProtected { get; set; } = string.Empty;
        public string QqAllowedUsers { get; set; } = string.Empty;
        public bool NotifyIncomingSms { get; set; } = true;
        public bool NotifyOtpOnly { get; set; }
        public bool NotifyIncomingCalls { get; set; } = true;

        public static PersistedSettings FromSettings(RemoteControlSettings s) => new()
        {
            WeixinEnabled = s.WeixinEnabled,
            WeixinBaseUrl = s.WeixinBaseUrl,
            WeixinTokenProtected = Protect(s.WeixinToken),
            WeixinBotId = s.WeixinBotId,
            WeixinUserId = s.WeixinUserId,
            WeixinAllowedUsers = s.WeixinAllowedUsers,
            QqEnabled = s.QqEnabled,
            QqAppId = s.QqAppId,
            QqClientSecretProtected = Protect(s.QqClientSecret),
            QqAllowedUsers = s.QqAllowedUsers,
            TelegramEnabled = s.TelegramEnabled,
            TelegramApiBaseUrl = s.TelegramApiBaseUrl,
            TelegramBotTokenProtected = Protect(s.TelegramBotToken),
            TelegramAllowedUsers = s.TelegramAllowedUsers,
            BarkEnabled = s.BarkEnabled,
            BarkServerUrl = s.BarkServerUrl,
            BarkDeviceKeyProtected = Protect(s.BarkDeviceKey),
            BarkIncludeSmsContent = s.BarkIncludeSmsContent,
            AllowRemoteCalls = s.AllowRemoteCalls,
            AllowRemoteSms = s.AllowRemoteSms,
            AllowRemoteLineChanges = s.AllowRemoteLineChanges,
            AllowRemoteReadSms = s.AllowRemoteReadSms,
            NotifyIncomingSms = s.NotifyIncomingSms,
            NotifyOtpOnly = s.NotifyOtpOnly,
            NotifyIncomingCalls = s.NotifyIncomingCalls
        };

        public RemoteControlSettings ToSettings() => new()
        {
            WeixinEnabled = WeixinEnabled,
            WeixinBaseUrl = string.IsNullOrWhiteSpace(WeixinBaseUrl) ? "https://ilinkai.weixin.qq.com" : WeixinBaseUrl,
            WeixinToken = Unprotect(WeixinTokenProtected),
            WeixinBotId = WeixinBotId,
            WeixinUserId = WeixinUserId,
            WeixinAllowedUsers = WeixinAllowedUsers,
            QqEnabled = QqEnabled,
            QqAppId = QqAppId,
            QqClientSecret = Unprotect(QqClientSecretProtected),
            QqAllowedUsers = QqAllowedUsers,
            TelegramEnabled = TelegramEnabled,
            TelegramApiBaseUrl = TelegramApiBaseUrl,
            TelegramBotToken = Unprotect(TelegramBotTokenProtected),
            TelegramAllowedUsers = TelegramAllowedUsers,
            BarkEnabled = BarkEnabled,
            BarkServerUrl = BarkServerUrl,
            BarkDeviceKey = Unprotect(BarkDeviceKeyProtected),
            BarkIncludeSmsContent = BarkIncludeSmsContent,
            AllowRemoteCalls = AllowRemoteCalls,
            AllowRemoteSms = AllowRemoteSms,
            AllowRemoteLineChanges = AllowRemoteLineChanges,
            AllowRemoteReadSms = AllowRemoteReadSms,
            NotifyIncomingSms = NotifyIncomingSms,
            NotifyOtpOnly = NotifyOtpOnly,
            NotifyIncomingCalls = NotifyIncomingCalls
        };
    }
}
