using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoWin.Models;

namespace VoWin.Services;

internal sealed class SmsApiSettingsStore
{
    private readonly string _path;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoWin.SmsApi.v1");
    public SmsApiSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "sms-api.json");
    private sealed record Persisted(bool Enabled, string BindAddress, int Port, bool AllowLanAccess, string ProtectedToken);

    public async Task<SmsApiSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new();
        try
        {
            await using var file = File.OpenRead(_path);
            var p = await JsonSerializer.DeserializeAsync<Persisted>(file, cancellationToken: ct).ConfigureAwait(false)
                ?? throw new InvalidDataException();
            var token = string.IsNullOrEmpty(p.ProtectedToken) ? "" : Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(p.ProtectedToken), Entropy, DataProtectionScope.CurrentUser));
            return new() { Enabled = p.Enabled, BindAddress = p.BindAddress ?? "127.0.0.1", Port = p.Port, AllowLanAccess = p.AllowLanAccess, Token = token };
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidDataException("短信 API 配置无法读取或 Token 无法解密；接口保持关闭，请重新保存配置。"); }
    }

    public async Task SaveAsync(SmsApiSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var token = string.IsNullOrEmpty(settings.Token) ? "" : Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(settings.Token), Entropy, DataProtectionScope.CurrentUser));
        var temp = _path + ".tmp";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(file, new Persisted(settings.Enabled, settings.BindAddress, settings.Port,
                settings.AllowLanAccess, token), cancellationToken: ct).ConfigureAwait(false);
            await file.FlushAsync(ct).ConfigureAwait(false);
        }
        File.Move(temp, _path, true);
    }
}
