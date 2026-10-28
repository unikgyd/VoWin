using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoWin.Models;

namespace VoWin.Services;

public sealed class TelephonySettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public TelephonySettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "telephony.json");

    private async Task<TelephonySettings> ReadCoreAsync()
    {
        if (!File.Exists(_path)) return new();
        await using var stream = File.OpenRead(_path);
        var settings = await JsonSerializer.DeserializeAsync<TelephonySettings>(stream, Options) ?? new();
        settings.Sims ??= new(StringComparer.Ordinal);
        return settings;
    }

    public async Task<TelephonySettings> LoadAsync()
    {
        await _gate.WaitAsync();
        try { return await ReadCoreAsync(); }
        finally { _gate.Release(); }
    }

    public async Task UpdateAsync(Action<TelephonySettings> update)
    {
        await _gate.WaitAsync();
        var temporary = _path + ".tmp";
        try
        {
            var settings = await ReadCoreAsync();
            update(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, settings, Options);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
}
