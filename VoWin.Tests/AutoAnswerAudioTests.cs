using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Reflection;
using System.Windows.Threading;
using NAudio.Wave;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class AutoAnswerAudioTests
{
    private static string Wave(int rate, int channels, int samplesPerChannel)
    {
        var path = Path.Combine(Path.GetTempPath(), "VoWin-auto-answer-" + Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, channels));
        var samples = new short[samplesPerChannel * channels];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = (short)(Math.Sin(2 * Math.PI * 440 * (index / channels) / rate) * 12000);
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    [Fact]
    public void RecordingRequestsAreOneShotAndCannotCrossCallsOrSlots()
    {
        var requests = new AutoAnswerMessageRequests();
        requests.Queue("call-a", "slot-a", "first.wav");
        requests.Queue("call-a", "slot-b", "second.wav");
        Assert.Null(requests.Take("call-b", "slot-a"));
        Assert.Equal("second.wav", requests.Take("call-a", "slot-b"));
        Assert.Equal("first.wav", requests.Take("CALL-A", "SLOT-A"));
        Assert.Null(requests.Take("call-a", "slot-a"));
    }

    [Fact]
    public void HangupRemovalAndHotplugCannotLeaveARecordingForANewerCall()
    {
        var requests = new AutoAnswerMessageRequests();
        requests.Queue("old", "slot-a", "first.wav"); requests.Queue("other", "slot-b", "second.wav");
        requests.Remove("old", "slot-a"); Assert.Null(requests.Take("old", "slot-a"));
        requests.Queue("old", "slot-a", "first.wav"); requests.RemoveSlot("slot-a");
        Assert.Null(requests.Take("old", "slot-a")); Assert.Equal("second.wav", requests.Take("other", "slot-b"));
        requests.Queue("fallback", null, "fallback.wav"); requests.Clear(); Assert.Null(requests.Take("fallback", null));
    }

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(48000, 2)]
    public async Task RecordingIsNormalizedIntoAudibleTwentyMillisecondPcmWithoutAudioDevices(int rate, int channels)
    {
        var path = Wave(rate, channels, rate / 10);
        try
        {
            CallMessagePlayback.Validate(path);
            var frames = new List<byte[]>();
            var waits = 0;
            var count = await CallMessagePlayback.SendAsync(path, (bytes, size) =>
            {
                Assert.Equal(320, size); frames.Add(bytes);
            }, CancellationToken.None, token => { waits++; return Task.CompletedTask; });
            Assert.InRange(count, 5, 6); Assert.Equal(count, waits);
            Assert.All(frames, bytes => Assert.Equal(320, bytes.Length));
            Assert.Contains(frames.SelectMany(bytes => bytes), sample => sample != 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FinalPartialFrameIsPaddedInsteadOfTruncatedOrReplayingOldSamples()
    {
        var path = Wave(8000, 1, 161);
        try
        {
            var frames = new List<byte[]>();
            Assert.Equal(2, await CallMessagePlayback.SendAsync(path, (bytes, _) => frames.Add(bytes), CancellationToken.None, _ => Task.CompletedTask));
            Assert.All(frames[1][2..], value => Assert.Equal(0, value));
            Assert.NotEqual(0, BinaryPrimitives.ReadInt16LittleEndian(frames[1]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancellationStopsBeforeAnotherFrameCanReachTheCall()
    {
        var path = Wave(8000, 1, 800);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var frames = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CallMessagePlayback.SendAsync(path,
                (_, _) => { frames++; cancellation.Cancel(); }, cancellation.Token, token => Task.Delay(1, token)));
            Assert.Equal(1, frames);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptyOrMissingRecordingFailsValidationBeforeAutomaticPickup()
    {
        var path = Wave(8000, 1, 0);
        try { Assert.Throws<InvalidDataException>(() => CallMessagePlayback.Validate(path)); }
        finally { File.Delete(path); }
        Assert.Throws<FileNotFoundException>(() => CallMessagePlayback.Validate(path));
    }

    [Fact]
    public async Task DefaultPacingDoesNotDumpTheWholeRecordingIntoRtpAtOnce()
    {
        var path = Wave(8000, 1, 1600);
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.Equal(10, await CallMessagePlayback.SendAsync(path, (_, _) => { }, CancellationToken.None));
            Assert.InRange(clock.ElapsedMilliseconds, 150, 3000);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RecordingReachesAmrRtpAsAudioEvenWithNoMicrophone()
    {
        var path = Wave(8000, 1, 800);
        try
        {
            using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
            var packets = new List<byte[]>();
            rtp.CustomSender = packet => { packets.Add(packet); return Task.CompletedTask; };
            rtp.SetRemoteEndpoint(IPAddress.Loopback, 25000, 98, "AMR", amrOctetAligned: false, startSenders: false);
            var session = new DesktopMicrophoneSession("offline-answer", "offline-slot", "自动应答录音",
                pcm => { rtp.SendAudioPcm(pcm); return true; }, isMessagePlayback: true);
            session.Started();
            Assert.Equal(5, await CallMessagePlayback.SendAsync(path, session.Process, CancellationToken.None, _ => Task.CompletedTask));
            session.ObserveTransport(rtp.GetMediaStats());
            var status = session.Snapshot();
            Assert.True(status.IsMessagePlayback); Assert.Equal(5UL, status.AudioPacketsSent);
            Assert.Contains("自动应答录音", status.StatusText); Assert.True(status.CanRestart);
            using var decoder = new AmrNativeCodec(initEncoder: false);
            var pcm = packets.SelectMany(packet => decoder.DecodeAmrNbFrame(
                AmrNbDecoder.RtpPayloadToAmrFrame(packet[12..], forceOctetAligned: false))).ToArray();
            Assert.Contains(pcm, value => Math.Abs((int)value) > 100);
            session.SetMuted(true); Assert.Contains("录音已静音", session.Snapshot().StatusText);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(2, false, 16)] [InlineData(2, true, 17)]
    [InlineData(7, false, 32)] [InlineData(7, true, 33)]
    public async Task PacedRecordingCrossesARealUdpSocketWithValidAmrHeadersAndAudio(int mode, bool octetAligned, int payloadBytes)
    {
        var path = Wave(8000, 1, 1600);
        try
        {
            using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
            rtp.SetRemoteEndpoint(IPAddress.Loopback, ((IPEndPoint)peer.Client.LocalEndPoint!).Port,
                102, "AMR", octetAligned, remoteRtcpPort: 25001,
                amrModeSet: mode.ToString(CultureInfo.InvariantCulture), startSenders: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sending = CallMessagePlayback.SendAsync(path, (bytes, count) =>
            {
                var pcm = Enumerable.Range(0, count / 2)
                    .Select(index => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * 2, 2))).ToArray();
                rtp.SendAudioPcm(pcm);
            }, timeout.Token);
            using var decoder = new AmrNativeCodec(initEncoder: false);
            var decodedPeak = 0;
            ushort? sequence = null;
            uint? timestamp = null;
            for (var index = 0; index < 10; index++)
            {
                var packet = (await peer.ReceiveAsync(timeout.Token)).Buffer;
                Assert.Equal(12 + payloadBytes, packet.Length);
                Assert.Equal(102, packet[1] & 0x7f);
                Assert.Equal(index == 0 ? 0x80 : 0, packet[1] & 0x80);
                var nextSequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
                var nextTimestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
                if (sequence is { } previous) Assert.Equal(unchecked((ushort)(previous + 1)), nextSequence);
                if (timestamp is { } last) Assert.Equal(unchecked(last + 160), nextTimestamp);
                sequence = nextSequence; timestamp = nextTimestamp;
                var frame = AmrNbDecoder.RtpPayloadToAmrFrame(packet[12..], octetAligned);
                Assert.Equal(mode, frame[0] >> 3 & 15);
                decodedPeak = Math.Max(decodedPeak, decoder.DecodeAmrNbFrame(frame).Max(sample => Math.Abs((int)sample)));
            }
            Assert.Equal(10, await sending);
            Assert.True(decodedPeak > 100);
            Assert.Equal(0UL, rtp.GetMediaStats().SendErrors);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("new-call", "slot-a", CallState.Incoming)]
    [InlineData("expected-call", "slot-b", CallState.Incoming)]
    [InlineData("expected-call", "slot-a", CallState.Active)]
    public async Task DelayedAutomaticAnswerCannotTargetAnotherCallOrAnAlreadyAnsweredCall(string currentCall, string currentSlot, CallState state)
    {
        var folder = Path.Combine(Path.GetTempPath(), "VoWin-auto-answer-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var service = new VoKernelService(new PreferenceDatabaseService(Path.Combine(folder, "db.sqlite")),
            new TelephonySettingsStore(Path.Combine(folder, "telephony.json")), Path.Combine(folder, "test.log"));
        var kernel = (VoKernel)service.Kernel;
        Assert.True(kernel.Pool.TryAddSlot(new ModemSlot(currentSlot, "OFFLINE")));
        typeof(VoKernel).GetMethod("TrackCall", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(kernel, [currentSlot, currentCall, "offline", false, state, null]);
        var answer = (Task<CallInfo?>)typeof(VoKernelService).GetMethod("AnswerCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, ["slot-a", "expected-call", "offline-message.wav", CancellationToken.None])!;
        Assert.Null(await answer);
        var requests = (AutoAnswerMessageRequests)typeof(VoKernelService).GetField("_autoAnswerMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.Null(requests.Take("expected-call", "slot-a"));
        Assert.Equal(currentCall, kernel.ActiveCall!.CallId);
    }

    [Fact]
    public async Task ConnectedImsServiceActuallyConsumesTheRecordingAndStopsItOnCallRelease()
    {
        var path = Wave(8000, 1, 16000);
        var folder = Path.Combine(Path.GetTempPath(), "VoWin-auto-answer-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var service = new VoKernelService(new PreferenceDatabaseService(Path.Combine(folder, "db.sqlite")),
            new TelephonySettingsStore(Path.Combine(folder, "telephony.json")), Path.Combine(folder, "test.log"), uiDispatcher: Dispatcher.CurrentDispatcher);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var kernel = (VoKernel)service.Kernel;
            var slot = new ModemSlot("offline-slot", "OFFLINE");
            Assert.True(kernel.Pool.TryAddSlot(slot)); service.Slots.Add(slot);
            typeof(VoKernel).GetMethod("TrackCall", hidden)!.Invoke(kernel,
                [slot.Id, "offline-auto-call", "offline", false, CallState.Active, "AMR"]);
            var rtp = new RtpSession(null, localAddress: IPAddress.Loopback);
            var packets = new ConcurrentQueue<byte[]>();
            var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rtp.CustomSender = bytes => { packets.Enqueue(bytes); if (packets.Count >= 3) heard.TrySetResult(); return Task.CompletedTask; };
            rtp.SetRemoteEndpoint(IPAddress.Loopback, 25000, 98, "AMR", startSenders: false);
            typeof(ImsCallManager).GetField("_rtp", hidden)!.SetValue(slot.Calls, rtp);
            typeof(ImsCallManager).GetProperty(nameof(ImsCallManager.State))!.SetValue(slot.Calls, CallState.Active);
            typeof(ImsCallManager).GetProperty(nameof(ImsCallManager.ActiveCall))!.SetValue(slot.Calls,
                new CallInfo("offline-auto-call", "offline", CallState.Active, DateTime.UtcNow, DateTime.UtcNow, null, "AMR", null, false));
            var requests = (AutoAnswerMessageRequests)typeof(VoKernelService).GetField("_autoAnswerMessages", hidden)!.GetValue(service)!;
            requests.Queue("offline-auto-call", slot.Id, path);
            await (Task)typeof(VoKernelService).GetMethod("EnsureDesktopMediaForCallAsync", hidden)!.Invoke(service,
                [new CallConnectedEventArgs("offline-auto-call", "offline", "AMR", DateTime.UtcNow, slot.Id)])!;
            await heard.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(service.GetMicrophoneUplinkStatus().IsMessagePlayback);
            Assert.True(service.GetMicrophoneUplinkStatus().AudioPacketsSent > 0);
            Assert.Null(typeof(VoKernelService).GetField("_waveIn", hidden)!.GetValue(service));
            Assert.Null(requests.Take("offline-auto-call", slot.Id));
            typeof(VoKernelService).GetMethod("StopMicrophone", hidden)!.Invoke(service, null);
            await ((Task)typeof(VoKernelService).GetField("_autoAnswerPlaybackTask", hidden)!.GetValue(service)!).WaitAsync(TimeSpan.FromSeconds(2));
            var endedPackets = packets.Count;
            await Task.Delay(60); Assert.Equal(endedPackets, packets.Count);
            Assert.False(service.GetMicrophoneUplinkStatus().HasSession);
            Assert.Null(typeof(VoKernelService).GetField("_waveIn", hidden)!.GetValue(service));
        }
        finally { await service.DisposeAsync(); File.Delete(path); }
    }

    [Fact]
    public async Task UnavailableCallCannotLeaveARecordingRequestBehindAfterAnswerReturnsNull()
    {
        var folder = Path.Combine(Path.GetTempPath(), "VoWin-auto-answer-no-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var service = new VoKernelService(new PreferenceDatabaseService(Path.Combine(folder, "db.sqlite")),
            new TelephonySettingsStore(Path.Combine(folder, "telephony.json")), Path.Combine(folder, "test.log"));
        var kernel = (VoKernel)service.Kernel;
        Assert.True(kernel.Pool.TryAddSlot(new ModemSlot("slot-a", "OFFLINE")));
        typeof(VoKernel).GetMethod("TrackCall", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(kernel, ["slot-a", "expected-call", "offline", false, CallState.Incoming, null]);
        var answer = (Task<CallInfo?>)typeof(VoKernelService).GetMethod("AnswerCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, ["slot-a", "expected-call", "offline-message.wav", CancellationToken.None])!;
        Assert.Null(await answer);
        var requests = (AutoAnswerMessageRequests)typeof(VoKernelService).GetField("_autoAnswerMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.Null(requests.Take("expected-call", "slot-a"));
    }
}
