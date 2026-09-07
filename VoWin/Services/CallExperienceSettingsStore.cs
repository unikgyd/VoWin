using System.IO;
using System.Text.Json;
using VoWin.Models;
namespace VoWin.Services;

internal sealed class CallExperienceSettingsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "call-experience.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CallExperienceSettings> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return new CallExperienceSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<CallExperienceSettings>(stream) ?? new CallExperienceSettings();
        }
        catch { return new CallExperienceSettings(); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CallExperienceSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
        }
        finally { _gate.Release(); }
    }
}
