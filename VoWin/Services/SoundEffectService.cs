using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace VoWin.Services
{
    public sealed class SoundEffectService : IDisposable
    {
        private static readonly Lazy<SoundEffectService> _lazy = new(() => new SoundEffectService());
        public static SoundEffectService Instance => _lazy.Value;

        private const int SampleRate = 44100;
        private readonly WaveFormat _waveFormat = new(SampleRate, 16, 1);

        // Pre-rendered DTMF PCM samples
        private readonly Dictionary<char, byte[]> _dtmfSamples = new();

        // DTMF playback pipeline
        private WaveOut? _dtmfWaveOut;
        private BufferedWaveProvider? _dtmfBuffer;
        private readonly object _dtmfLock = new();

        // Ringtone playback pipeline
        private CancellationTokenSource? _ringtoneCts;
        private WaveOut? _ringtoneWaveOut;
        private byte[]? _cachedRingtoneCycle;
        private readonly object _ringtoneLock = new();

        public SoundEffectService()
        {
            PrecomputeDtmfTones();
            PrecomputeRingtoneCycle();
            PrecomputeSmsChime();
        }

        #region DTMF Synthesis

        private void PrecomputeDtmfTones()
        {
            // ITU-T standard DTMF frequencies
            // Rows: 697, 770, 852, 941
            // Cols: 1209, 1336, 1477
            var dtmfFreqs = new Dictionary<char, (double f1, double f2)>
            {
                { '1', (697, 1209) },
                { '2', (697, 1336) },
                { '3', (697, 1477) },
                { '4', (770, 1209) },
                { '5', (770, 1336) },
                { '6', (770, 1477) },
                { '7', (852, 1209) },
                { '8', (852, 1336) },
                { '9', (852, 1477) },
                { '*', (941, 1209) },
                { '0', (941, 1336) },
                { '#', (941, 1477) }
            };

            const double durationSec = 0.11; // 110 ms
            int totalSamples = (int)(SampleRate * durationSec);
            int fadeSamples = (int)(SampleRate * 0.008); // 8ms fade in / fade out to avoid clicks

            foreach (var kvp in dtmfFreqs)
            {
                char key = kvp.Key;
                var (f1, f2) = kvp.Value;

                var buffer = new byte[totalSamples * 2];
                for (int i = 0; i < totalSamples; i++)
                {
                    double t = (double)i / SampleRate;
                    double s1 = Math.Sin(2 * Math.PI * f1 * t);
                    double s2 = Math.Sin(2 * Math.PI * f2 * t);
                    double mixed = (s1 + s2) * 0.42;

                    // Envelope: soft cosine fade in / fade out
                    double envelope = 1.0;
                    if (i < fadeSamples)
                    {
                        envelope = 0.5 * (1.0 - Math.Cos(Math.PI * i / fadeSamples));
                    }
                    else if (i > totalSamples - fadeSamples)
                    {
                        envelope = 0.5 * (1.0 - Math.Cos(Math.PI * (totalSamples - i) / fadeSamples));
                    }

                    short sample = (short)(mixed * envelope * short.MaxValue);
                    buffer[i * 2] = (byte)(sample & 0xFF);
                    buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }

                _dtmfSamples[key] = buffer;
            }
        }

        public void PlayDtmfTone(char digit)
        {
            if (!_dtmfSamples.TryGetValue(digit, out var pcmData))
            {
                return;
            }

            try
            {
                lock (_dtmfLock)
                {
                    EnsureDtmfPlayer();
                    if (_dtmfBuffer != null)
                    {
                        _dtmfBuffer.ClearBuffer();
                        _dtmfBuffer.AddSamples(pcmData, 0, pcmData.Length);
                    }
                }
            }
            catch
            {
                // Fallback graceful degradation
            }
        }

        private void EnsureDtmfPlayer()
        {
            if (_dtmfWaveOut == null)
            {
                _dtmfBuffer = new BufferedWaveProvider(_waveFormat)
                {
                    DiscardOnBufferOverflow = true
                };

                _dtmfWaveOut = new WaveOut();
                _dtmfWaveOut.Init(_dtmfBuffer);
                _dtmfWaveOut.Play();
            }
        }

        #endregion

        #region Melodious Ringtone Synthesis

        private void PrecomputeRingtoneCycle()
        {
            // Synthesize a modern 2-phrase marimba / bell chime motif
            // Note sequence: E5 (659.25Hz), G#5 (830.61Hz), B5 (987.77Hz), E6 (1318.51Hz)
            var notes = new[]
            {
                (pitch: 659.25, dur: 0.12, gap: 0.04),
                (pitch: 830.61, dur: 0.12, gap: 0.04),
                (pitch: 987.77, dur: 0.12, gap: 0.04),
                (pitch: 1318.51, dur: 0.45, gap: 0.18),
                (pitch: 659.25, dur: 0.12, gap: 0.04),
                (pitch: 830.61, dur: 0.12, gap: 0.04),
                (pitch: 987.77, dur: 0.12, gap: 0.04),
                (pitch: 1318.51, dur: 0.65, gap: 0.50)
            };

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            foreach (var (pitch, dur, gap) in notes)
            {
                int noteSamples = (int)(SampleRate * dur);
                for (int i = 0; i < noteSamples; i++)
                {
                    double t = (double)i / SampleRate;

                    // Bell harmonic spectrum: fundamental + 2nd harmonic + 3.8th inharmonic
                    double wave = 0.65 * Math.Sin(2 * Math.PI * pitch * t)
                                + 0.25 * Math.Sin(2 * Math.PI * pitch * 2 * t)
                                + 0.10 * Math.Sin(2 * Math.PI * pitch * 3.8 * t);

                    // Exponential decay envelope with quick 4ms attack
                    double attack = Math.Min(1.0, (double)i / (SampleRate * 0.004));
                    double decay = Math.Exp(-4.2 * t);
                    double val = wave * attack * decay * 0.65;

                    short s = (short)(Math.Clamp(val, -1.0, 1.0) * short.MaxValue);
                    writer.Write(s);
                }

                int gapSamples = (int)(SampleRate * gap);
                for (int g = 0; g < gapSamples; g++)
                {
                    writer.Write((short)0);
                }
            }

            _cachedRingtoneCycle = ms.ToArray();
        }

        public void StartRingtone()
        {
            StopRingtone();

            lock (_ringtoneLock)
            {
                var cts = _ringtoneCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            if (_cachedRingtoneCycle != null && _cachedRingtoneCycle.Length > 0)
                            {
                                await PlayPcmAsync(_cachedRingtoneCycle, cts.Token);
                            }

                            // 1.4s silence cadence before repeating
                            await Task.Delay(1400, cts.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }, cts.Token);
            }
        }

        public void StopRingtone()
        {
            lock (_ringtoneLock)
            {
                var cts = Interlocked.Exchange(ref _ringtoneCts, null);
                cts?.Cancel();
                cts?.Dispose();

                try
                {
                    _ringtoneWaveOut?.Stop();
                    _ringtoneWaveOut?.Dispose();
                    _ringtoneWaveOut = null;
                }
                catch { }
            }
        }

        private async Task PlayPcmAsync(byte[] pcmData, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>();
            using var reg = token.Register(() => tcs.TrySetCanceled());

            WaveOut? player = null;
            RawSourceWaveStream? stream = null;
            try
            {
                stream = new RawSourceWaveStream(new MemoryStream(pcmData), _waveFormat);
                player = new WaveOut();
                lock (_ringtoneLock)
                {
                    if (token.IsCancellationRequested) return;
                    _ringtoneWaveOut = player;
                }

                player.PlaybackStopped += (s, e) => tcs.TrySetResult(true);
                player.Init(stream);
                player.Play();

                await tcs.Task;
            }
            catch (OperationCanceledException) { }
            finally
            {
                try
                {
                    player?.Stop();
                    player?.Dispose();
                    stream?.Dispose();
                }
                catch { }
                lock (_ringtoneLock)
                {
                    if (_ringtoneWaveOut == player)
                    {
                        _ringtoneWaveOut = null;
                    }
                }
            }
        }

        #endregion

        #region SMS / OTP Notification Chime

        private byte[]? _cachedSmsChime;

        private void PrecomputeSmsChime()
        {
            // Crisp, pleasant 2-tone melodic chime (E5 659.25Hz -> A5 880Hz with natural decay)
            const double durationSec = 0.45;
            int totalSamples = (int)(SampleRate * durationSec);
            var buffer = new byte[totalSamples * 2];

            int note1Samples = (int)(SampleRate * 0.12);

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / SampleRate;
                double sampleVal;

                if (i < note1Samples)
                {
                    // Tone 1: E5 (659.25 Hz) + subtle harmonic
                    double t1 = t;
                    double env1 = Math.Exp(-6.0 * t1);
                    sampleVal = (Math.Sin(2 * Math.PI * 659.25 * t1) * 0.65 + Math.Sin(2 * Math.PI * 1318.5 * t1) * 0.2) * env1;
                }
                else
                {
                    // Tone 2: A5 (880.0 Hz) + octave shimmer
                    double t2 = (double)(i - note1Samples) / SampleRate;
                    double env2 = Math.Exp(-7.5 * t2);
                    sampleVal = (Math.Sin(2 * Math.PI * 880.0 * t2) * 0.75 + Math.Sin(2 * Math.PI * 1760.0 * t2) * 0.25) * env2;
                }

                short sample = (short)(sampleVal * short.MaxValue * 0.6);
                buffer[i * 2] = (byte)(sample & 0xFF);
                buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            _cachedSmsChime = buffer;
        }

        public void PlaySmsChime()
        {
            Task.Run(async () =>
            {
                try
                {
                    // Native Windows Notification Sound
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }

                    if (_cachedSmsChime != null)
                    {
                        using var stream = new RawSourceWaveStream(new MemoryStream(_cachedSmsChime), _waveFormat);
                        using var player = new WaveOut();
                        var tcs = new TaskCompletionSource<bool>();
                        player.PlaybackStopped += (s, e) => tcs.TrySetResult(true);
                        player.Init(stream);
                        player.Play();
                        await Task.WhenAny(tcs.Task, Task.Delay(600));
                    }
                }
                catch { }
            });
        }

        #endregion

        public void Dispose()
        {
            StopRingtone();

            lock (_dtmfLock)
            {
                try
                {
                    _dtmfWaveOut?.Stop();
                    _dtmfWaveOut?.Dispose();
                    _dtmfWaveOut = null;
                }
                catch { }
            }
        }
    }
}
