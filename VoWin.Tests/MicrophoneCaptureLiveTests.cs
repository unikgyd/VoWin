using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class MicrophoneCaptureLiveTests
{
    [MicrophoneLiveFact]
    public async Task ActualHostMicrophonePassesThroughCallScopedPcmAmrAndLoopbackRtpWithoutSavingAudio()
    {
        var device = CellularAudioWorker.FindHostInputDevice();
        Assert.True(device >= 0, "No verified non-modem host microphone is available.");
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
        rtp.SetRemoteEndpoint(IPAddress.Loopback, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port, 98, "AMR");
        var session = new DesktopMicrophoneSession("offline-live-mic", "offline-loopback", WaveIn.GetCapabilities(device).ProductName,
            pcm => { rtp.SendAudioPcm(pcm); return true; });
        using var capture = new WaveIn { DeviceNumber = device, WaveFormat = new WaveFormat(8000, 16, 1), BufferMilliseconds = 20 };
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, e) => session.Process(e.Buffer, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
        try
        {
            capture.StartRecording(); session.Started();
            var packet = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(98, packet.Buffer[1] & 0x7f);
            var frame = AmrNbDecoder.RtpPayloadToAmrFrame(packet.Buffer[12..], forceOctetAligned: false);
            using var decoder = new AmrNativeCodec(initEncoder: false);
            Assert.Equal(160, decoder.DecodeAmrNbFrame(frame).Length);
            await Task.Delay(1500);
        }
        finally { session.Close(); capture.StopRecording(); }
        Assert.Null(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        var transport = rtp.GetMediaStats(); session.ObserveTransport(transport);
        var snapshot = session.Snapshot();
        Assert.True(snapshot.CapturedBuffers > 0); Assert.True(snapshot.ForwardedBuffers > 0);
        Assert.True(snapshot.InputPeak > 0); Assert.True(snapshot.AudioPacketsSent > 0);
        Assert.Equal(0, snapshot.ProcessingErrors); Assert.Equal(0UL, snapshot.EncodeErrors); Assert.Equal(0UL, snapshot.SendErrors);
        Console.WriteLine($"Live microphone verified: captured={snapshot.CapturedBuffers}; forwarded={snapshot.ForwardedBuffers}; rejected={snapshot.RejectedBuffers}; encoded={snapshot.EncodedFrames}; audioPackets={snapshot.AudioPacketsSent}; processingErrors={snapshot.ProcessingErrors}; encodeErrors={snapshot.EncodeErrors}; sendErrors={snapshot.SendErrors}; peak={snapshot.InputPeak}; audioSaved=false; modemAccessed=false; carrierTraffic=false");
    }
}

public sealed class MicrophoneLiveFactAttribute : FactAttribute
{
    public MicrophoneLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_RUN_MICROPHONE_TEST") != "1")
            Skip = "Explicit opt-in required: briefly opens the PC microphone and sends RTP to loopback only, without saving audio.";
    }
}
