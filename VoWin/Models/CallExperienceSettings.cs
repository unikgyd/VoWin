namespace VoWin.Models;

public sealed class CallExperienceSettings
{
    public bool VolteAudioPrewarmEnabled { get; set; } = true;
    public bool SaveCallRecordings { get; set; } = true;
    public string? RecordingDirectory { get; set; } = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "VoWin",
        "Recordings");
    public bool AutoAnswerEnabled { get; set; }
    public int AutoAnswerDelaySeconds { get; set; } = 30;
    public string? AutoAnswerMessagePath { get; set; }
}
