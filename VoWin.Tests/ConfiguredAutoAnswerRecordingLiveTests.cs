using System.IO;
using System.Net;
using System.Text.Json;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class ConfiguredAutoAnswerRecordingLiveTests
{
    [ConfiguredRecordingTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentRecordingCanBeDecodedAndEncodedForTheFarEndWithoutOpeningAudioDevices(bool octetAligned)
    {
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "call-experience.json");
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        var path = settings.RootElement.GetProperty("AutoAnswerMessagePath").GetString()!;
        CallMessagePlayback.Validate(path);
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        var packets = new List<byte[]>();
        rtp.CustomSender = bytes => { packets.Add(bytes); return Task.CompletedTask; };
        rtp.SetRemoteEndpoint(IPAddress.Loopback, 25000, 98, "AMR", amrOctetAligned: octetAligned, startSenders: false);
        var session = new DesktopMicrophoneSession("offline-file-verification", "offline-slot", "configured recording",
            pcm => { rtp.SendAudioPcm(pcm); return true; }, isMessagePlayback: true);
        session.Started();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await CallMessagePlayback.SendAsync(path, (bytes, count) =>
            {
                session.Process(bytes, count);
                if (packets.Count >= 100) cancellation.Cancel(); // Inspect at most two seconds of PCM, in memory only.
            }, cancellation.Token, token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        session.ObserveTransport(rtp.GetMediaStats());
        var status = session.Snapshot();
        Assert.True(status.InputPeak > 0); Assert.True(status.AudioPacketsSent > 0);
        Assert.Equal(0, status.ProcessingErrors); Assert.Equal(0UL, status.EncodeErrors); Assert.Equal(0UL, status.SendErrors);
        using var decoder = new AmrNativeCodec(initEncoder: false);
        var decodedPeak = packets.SelectMany(packet => decoder.DecodeAmrNbFrame(
            AmrNbDecoder.RtpPayloadToAmrFrame(packet[12..], forceOctetAligned: octetAligned))).Max(sample => Math.Abs((int)sample));
        Assert.True(decodedPeak > 0);
        Assert.All(packets, packet => Assert.Equal(octetAligned ? 33 : 32, packet.Length - 12));
        Console.WriteLine($"Configured recording verified: format={Path.GetExtension(path)}; octetAligned={octetAligned}; PCM/RTP frames={status.AudioPacketsSent}; peak={status.InputPeak}; decodedPeak={decodedPeak}; microphoneOpened=false; speakerOpened=false; carrierTraffic=false; audioSaved=false");
    }
}

public sealed class ConfiguredRecordingTheoryAttribute : TheoryAttribute
{
    public ConfiguredRecordingTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_RUN_AUTOANSWER_RECORDING_TEST") != "1")
            Skip = "Explicit opt-in required to read the configured local recording; no audio devices, carrier traffic or saved audio.";
    }
}
