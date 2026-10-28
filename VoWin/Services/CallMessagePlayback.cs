using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoWin.Services;

/// <summary>File to paced 20-ms PCM frames. Never opens a speaker or microphone.</summary>
internal static class CallMessagePlayback
{
    private static ISampleProvider Normalize(AudioFileReader reader)
    {
        ISampleProvider samples = reader;
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
        if (samples.WaveFormat.Channels != 1) throw new NotSupportedException("Auto-answer audio must be mono or stereo.");
        if (samples.WaveFormat.SampleRate != 8000) samples = new WdlResamplingSampleProvider(samples, 8000);
        return samples;
    }

    public static void Validate(string path)
    {
        using var reader = new AudioFileReader(path);
        if (Normalize(reader).Read(new float[160]) == 0)
            throw new InvalidDataException("Auto-answer audio is empty.");
    }

    public static async Task<int> SendAsync(string path, Action<byte[], int> send, CancellationToken token,
        Func<CancellationToken, Task>? waitForNextFrame = null)
    {
        token.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(path);
        var source = Normalize(reader);
        using var timer = waitForNextFrame == null ? new PeriodicTimer(TimeSpan.FromMilliseconds(20)) : null;
        var samples = new float[160];
        var frames = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = 0;
            while (count < samples.Length)
            {
                var read = source.Read(samples.AsSpan(count));
                if (read == 0) break;
                count += read;
            }
            if (count == 0) break;
            var pcm = new byte[320]; // Final partial frame is zero-padded.
            for (var index = 0; index < count; index++)
            {
                var value = float.IsFinite(samples[index]) ? samples[index] : 0;
                var sample = (short)Math.Clamp((int)Math.Round(Math.Clamp(value, -1, 1) * 32768d), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2, 2), sample);
            }
            token.ThrowIfCancellationRequested();
            send(pcm, pcm.Length);
            frames++;
            if (waitForNextFrame != null) await waitForNextFrame(token).ConfigureAwait(false);
            else await timer!.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        if (frames == 0) throw new InvalidDataException("Auto-answer audio is empty.");
        return frames;
    }
}
