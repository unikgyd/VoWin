using System.Buffers.Binary;
using System.IO;
using VoWin.Services;
using VoSharp.Telephony.Calls;

namespace VoWin.Tests;

public sealed class DesktopMicrophoneSessionTests
{
    private static byte[] Pcm(short value = 2000)
    {
        var bytes = new byte[320];
        for (var i = 0; i < 160; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), value);
        return bytes;
    }

    [Fact]
    public void CaptureAndForwardAreCountedSeparatelyAndFullScaleNegativeDoesNotOverflow()
    {
        short[]? sent = null;
        var session = new DesktopMicrophoneSession("call-a", "slot-a", "offline mic", pcm => { sent = pcm; return false; });
        session.Started(); session.Process(Pcm(short.MinValue), 320);
        var status = session.Snapshot();
        Assert.Equal(1, status.CapturedBuffers); Assert.Equal(0, status.ForwardedBuffers); Assert.Equal(1, status.RejectedBuffers);
        Assert.Equal(32768, status.InputPeak); Assert.Equal(100, status.InputLevel);
        Assert.Equal(short.MinValue, sent![0]); Assert.Contains("未接收 PCM", status.StatusText);
    }

    [Fact]
    public void MutingActuallySendsSilenceAndUnmutingRestoresPcm()
    {
        var sent = new List<short[]>();
        var session = new DesktopMicrophoneSession("call-a", "slot-a", "offline mic", pcm => { sent.Add(pcm); return true; });
        session.Started(); session.SetMuted(true); session.Process(Pcm(), 320);
        Assert.All(sent[0], sample => Assert.Equal(0, sample));
        Assert.Equal(0, session.Snapshot().InputLevel); Assert.Contains("已静音", session.Snapshot().StatusText);
        session.SetMuted(false); session.Process(Pcm(), 320);
        Assert.All(sent[1], sample => Assert.Equal(2000, sample)); Assert.Equal(2, session.Snapshot().ForwardedBuffers);
    }

    [Fact]
    public void AForwardingExceptionDoesNotKillTheCaptureCallback()
    {
        var attempts = 0;
        var session = new DesktopMicrophoneSession("call-a", "slot-a", "offline mic", _ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("offline encoder failure");
            return true;
        });
        session.Started(); session.Process(Pcm(), 320); session.Process(Pcm(), 320);
        var status = session.Snapshot();
        Assert.True(status.IsCapturing); Assert.Equal(2, status.CapturedBuffers);
        Assert.Equal(1, status.ProcessingErrors); Assert.Equal(1, status.ForwardedBuffers);
    }

    [Fact]
    public void ClosedSessionsIgnoreLateCallbacksInsteadOfFeedingTheNextCall()
    {
        var sends = 0;
        var old = new DesktopMicrophoneSession("old", "slot-a", "offline mic", _ => { sends++; return true; });
        old.Started(); old.Close(); old.Process(Pcm(), 320); old.CaptureStopped(new IOException());
        Assert.Equal(0, sends); Assert.False(old.Snapshot().IsCapturing); Assert.Null(old.Snapshot().Error);
    }

    [Fact]
    public void MissingDataAndUnexpectedCaptureStopsAreVisibleAndRetryable()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new DesktopMicrophoneSession("call-a", "slot-a", "offline mic", _ => true, () => now);
        session.Started(); now += TimeSpan.FromSeconds(3);
        Assert.Contains("未收到", session.Snapshot().StatusText); Assert.True(session.Snapshot().CanRestart);
        session.Process(Pcm(), 320);
        Assert.Contains("尚无上行音频包", session.Snapshot().StatusText);
        Assert.True(session.Snapshot().CanRestart);
        session.ObserveTransport(new(1, 0, 0, 0) { AudioFramesEncoded = 1, AudioPacketsSent = 1 });
        Assert.False(session.Snapshot().CanRestart);
        now += TimeSpan.FromSeconds(3); Assert.Contains("中断", session.Snapshot().StatusText);
        Assert.Equal(0, session.Snapshot().InputLevel);
        session.CaptureStopped(new IOException());
        session.CaptureStopped(null); // A later native disposal event must not hide the original failure.
        Assert.False(session.Snapshot().IsCapturing); Assert.Contains("IOException", session.Snapshot().StatusText);
    }

    [Fact]
    public void ReopeningCaptureDoesNotInheritOldRtpSuccessOrErrors()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new DesktopMicrophoneSession("call-a", "slot-a", "offline mic", _ => true, () => now);
        var previous = new RtpMediaStats(10, 0, 0, 2) { AudioFramesEncoded = 12, AudioPacketsSent = 10, AudioEncodeErrors = 1 };
        session.SetTransportBaseline(previous); session.ObserveTransport(previous);
        session.Started(); session.Process(Pcm(), 320); now += TimeSpan.FromSeconds(3);
        var status = session.Snapshot();
        Assert.Equal(0UL, status.AudioPacketsSent); Assert.Equal(0UL, status.EncodedFrames);
        Assert.Equal(0UL, status.SendErrors); Assert.Equal(0UL, status.EncodeErrors);
        session.Process(Pcm(), 320);
        Assert.Contains("尚无上行音频包", session.Snapshot().StatusText);
        session.ObserveTransport(previous with { AudioFramesEncoded = 13, AudioPacketsSent = 11 });
        Assert.Contains("已发出", session.Snapshot().StatusText);
        Assert.Equal(1UL, session.Snapshot().EncodedFrames); Assert.Equal(1UL, session.Snapshot().AudioPacketsSent);
    }

    [Theory]
    [InlineData(321)] [InlineData(322)]
    public void InvalidPcmLengthsDoNotEscapeTheCallback(int count)
    {
        var session = new DesktopMicrophoneSession("call-a", "slot-a", "offline mic", _ => throw new Exception());
        session.Process(Pcm(), count);
        Assert.Equal(1, session.Snapshot().ProcessingErrors); Assert.Equal(0, session.Snapshot().CapturedBuffers);
    }
}
