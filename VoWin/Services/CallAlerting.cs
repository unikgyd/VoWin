using System.Media;
using NAudio.Wave;

namespace VoWin.Services;

/// <summary>Pre-opens local capture/playback and provides an audible incoming-call alert.</summary>
internal sealed class CallAlerting : IDisposable
{
    private WaveIn? _prewarmMicrophone;
    private WaveOut? _prewarmSpeaker;
    private CancellationTokenSource? _ringCts;

    public void PrewarmHostAudio()
    {
        if (_prewarmMicrophone != null || _prewarmSpeaker != null) return;
        try
        {
            var mic = FindInput();
            var speaker = FindOutput();
            if (mic >= 0)
            {
                _prewarmMicrophone = new WaveIn { DeviceNumber = mic, WaveFormat = new WaveFormat(8000, 16, 1) };
                _prewarmMicrophone.StartRecording();
            }
            if (speaker >= 0)
            {
                _prewarmSpeaker = new WaveOut { DeviceNumber = speaker };
                _prewarmSpeaker.Init(new SilenceProvider(new WaveFormat(8000, 16, 1)));
                _prewarmSpeaker.Play();
            }
        }
        catch { ReleaseHostAudio(); }
    }

    public void ReleaseHostAudio()
    {
        if (_prewarmMicrophone != null)
        {
            try { _prewarmMicrophone.StopRecording(); _prewarmMicrophone.Dispose(); } catch { }
            _prewarmMicrophone = null;
        }
        if (_prewarmSpeaker != null)
        {
            try { _prewarmSpeaker.Stop(); _prewarmSpeaker.Dispose(); } catch { }
            _prewarmSpeaker = null;
        }
    }

    public void StartRinging()
    {
        StopRinging();
        SoundEffectService.Instance.StartRingtone();
    }

    public void StopRinging()
    {
        SoundEffectService.Instance.StopRingtone();
        var cts = Interlocked.Exchange(ref _ringCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    private static int FindInput()
    {
        for (var i = 0; i < WaveIn.DeviceCount; i++)
            if (!IsModem(WaveIn.GetCapabilities(i).ProductName)) return i;
        return -1;
    }

    private static int FindOutput()
    {
        for (var i = 0; i < WaveOut.DeviceCount; i++)
            if (!IsModem(WaveOut.GetCapabilities(i).ProductName)) return i;
        return -1;
    }

    private static bool IsModem(string name) => name.Contains("AC Interface", StringComparison.OrdinalIgnoreCase) || name.Contains("Quectel", StringComparison.OrdinalIgnoreCase);
    public void Dispose() { StopRinging(); ReleaseHostAudio(); }
}
