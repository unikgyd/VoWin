namespace VoWin.Models;

public sealed class CallExperienceSettings
{
    public bool VolteAudioPrewarmEnabled { get; set; } = true;
    public bool AutoAnswerEnabled { get; set; }
    public int AutoAnswerDelaySeconds { get; set; } = 30;
    public string? AutoAnswerMessagePath { get; set; }
}
