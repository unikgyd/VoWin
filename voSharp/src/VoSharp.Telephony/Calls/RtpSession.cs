using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VoSharp.Common.Events;
using VoSharp.Sip;
using VoSharp.Telephony.Audio;

namespace VoSharp.Telephony.Calls;

public sealed record RtpMediaStats(
    ulong PacketsSent, ulong PacketsReceived, ulong AudioFramesDecoded, ulong SendErrors)
{
    public AudioPlaybackStats? Playback { get; init; }
    public ulong PcmSamplesReceived { get; init; }
    public ulong AudioFramesEncoded { get; init; }
    public ulong AudioPacketsSent { get; init; }
    public ulong AudioEncodeErrors { get; init; }
}

public sealed record RtpMediaConfiguration(string Codec, byte PayloadType, bool AmrOctetAligned,
    int AmrEncodeMode, string? AmrModeSet, IPAddress LocalAddress, int LocalPort,
    IPAddress? RemoteAddress, int? RemotePort, int? BoundInterfaceIndex, bool MediaEnabled);

public class RtpSession : IDisposable
{
    private readonly UdpClient _udp;
    private readonly UdpClient _rtcpUdp;
    private readonly WindowsAudioDevice? _audio;
    private readonly AudioPlaybackStats? _playbackAtStart;
    private readonly bool _recordAudio;
    private readonly AsyncEventBus? _eventBus;
    private readonly object _codecGate = new();
    private AmrNativeCodec? _amrCodec;
    // G.711-only SIP calls must not require a native AMR library or extract a
    // Windows DLL on Linux. Codec initialization is scoped to actual AMR use.
    private AmrNativeCodec AmrCodec
    {
        get
        {
            lock (_codecGate)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                return _amrCodec ??= new();
            }
        }
    }
    internal bool IsAmrCodecInitialized { get { lock (_codecGate) return _amrCodec != null; } }
    private readonly CancellationTokenSource _cts = new();
    private readonly object _rtcpLock = new();
    private IPEndPoint? _remoteEndpoint;
    private IPEndPoint? _remoteRtcpEndpoint;
    private byte _currentPayloadType = 8; // 8 = PCMA, 0 = PCMU
    private string _currentCodec = "PCMA";
    private bool _amrOctetAligned;
    private int _amrEncodeMode = 7;
    private bool _amrAllowsModeZero = true;
    private string? _amrModeSet;
    private bool _sendersStarted;
    private readonly object _senderStartGate = new();
    private readonly object _pcmFrameGate = new();
    private readonly object _packetSendGate = new();
    private readonly short[] _pcmFrame = new short[160];
    private int _pcmSamplesBuffered;
    private volatile bool _mediaEnabled = true;
    public bool MediaEnabled { get => _mediaEnabled; set => _mediaEnabled = value; }
    private uint _remoteSsrc;
    private ushort _baseSequence;
    private ushort _maxSequence;
    private uint _sequenceCycles;
    private uint _receivedForRtcp;
    private uint _expectedPrior;
    private uint _receivedPrior;
    private uint _jitter;
    private long? _lastTransit;
    private uint _lastSenderReport;
    private DateTimeOffset? _lastSenderReportAt;
    private bool _isDisposed;
    private int _lastDtmfEvent = -1;
    private uint _lastDtmfTimestamp;
    private long _packetsSent;
    private long _audioFramesDecoded;
    private long _sendErrors;
    private long _pcmSamplesReceived, _audioFramesEncoded, _audioPacketsSent, _audioEncodeErrors;

    public int LocalPort { get; }
    public int LocalRtcpPort { get; }
    public int? BoundInterfaceIndex { get; }
    public IPAddress LocalAddress { get; }
    public IPEndPoint? RemoteRtcpEndPoint => _remoteRtcpEndpoint;
    public byte PayloadType => _currentPayloadType;
    public string Codec => _currentCodec;
    public bool AmrOctetAligned => _amrOctetAligned;
    public string? AmrModeSet => _amrModeSet;
    public RtpMediaConfiguration GetMediaConfiguration()
    {
        lock (_pcmFrameGate)
            return new(_currentCodec, _currentPayloadType, _amrOctetAligned, _amrEncodeMode,
                _amrModeSet, LocalAddress, LocalPort, _remoteEndpoint?.Address,
                _remoteEndpoint?.Port, BoundInterfaceIndex, MediaEnabled);
    }
    public ulong TotalPacketsReceived { get; private set; }
    public RtpMediaStats GetMediaStats()
    {
        var playback = _audio?.GetPlaybackStats();
        if (playback is not null && _playbackAtStart is { } start)
            playback = playback with
            {
                FramesQueued = Math.Max(0, playback.FramesQueued - start.FramesQueued),
                FramesWritten = Math.Max(0, playback.FramesWritten - start.FramesWritten),
                FramesDropped = Math.Max(0, playback.FramesDropped - start.FramesDropped),
                WriteErrors = Math.Max(0, playback.WriteErrors - start.WriteErrors)
            };
        return new RtpMediaStats(
            (ulong)Interlocked.Read(ref _packetsSent), TotalPacketsReceived,
            (ulong)Interlocked.Read(ref _audioFramesDecoded),
            (ulong)Interlocked.Read(ref _sendErrors))
        {
            Playback = playback, PcmSamplesReceived = (ulong)Interlocked.Read(ref _pcmSamplesReceived),
            AudioFramesEncoded = (ulong)Interlocked.Read(ref _audioFramesEncoded),
            AudioPacketsSent = (ulong)Interlocked.Read(ref _audioPacketsSent),
            AudioEncodeErrors = (ulong)Interlocked.Read(ref _audioEncodeErrors)
        };
    }
    public Func<byte[], Task>? CustomSender { get; set; }
    public Func<byte[], Task>? CustomRtcpSender { get; set; }
    public Action<short[]>? OnAudioDecoded { get; set; }
    public Action<char, int>? OnDtmfReceived { get; set; }

    public RtpSession(
        WindowsAudioDevice? audio,
        AsyncEventBus? eventBus = null,
        int localPort = 0,
        IPAddress? localAddress = null,
        int? outgoingInterfaceIndex = null,
        bool recordAudio = false)
    {
        if (outgoingInterfaceIndex is not null &&
            (localAddress is null || localAddress.Equals(IPAddress.Any) || localAddress.Equals(IPAddress.IPv6Any)))
            throw new ArgumentException("Host IMS media requires an explicit IMS bearer address.", nameof(localAddress));
        if (outgoingInterfaceIndex is { } index)
        {
            if (index <= 0) throw new ArgumentOutOfRangeException(nameof(outgoingInterfaceIndex));
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows interface pinning is required for Host IMS media.");
        }
        _audio     = audio;
        _playbackAtStart = audio?.GetPlaybackStats();
        _recordAudio = recordAudio;
        _eventBus  = eventBus;
        var bindAddress = localAddress ?? IPAddress.Any;
        var rtp = new UdpClient(new IPEndPoint(bindAddress, localPort > 0 ? localPort : 0));
        UdpClient? rtcp = null;
        try
        {
            var rtpPort = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
            try
            {
                rtcp = new UdpClient(new IPEndPoint(bindAddress, rtpPort < ushort.MaxValue ? rtpPort + 1 : 0));
            }
            catch (SocketException)
            {
                rtcp = new UdpClient(new IPEndPoint(bindAddress, 0));
            }
            if (outgoingInterfaceIndex is { } interfaceIndex)
            {
                WindowsUnicastInterface.Pin(rtp.Client, interfaceIndex);
                WindowsUnicastInterface.Pin(rtcp.Client, interfaceIndex);
            }
            _udp = rtp;
            _rtcpUdp = rtcp;
            LocalPort = rtpPort;
            LocalRtcpPort = ((IPEndPoint)rtcp.Client.LocalEndPoint!).Port;
            LocalAddress = bindAddress;
            BoundInterfaceIndex = outgoingInterfaceIndex;
        }
        catch
        {
            rtcp?.Dispose();
            rtp.Dispose();
            throw;
        }

        StartReceiver();
        StartRtcpReceiver();
    }

    public void SetRemoteEndpoint(
        IPAddress remoteIp,
        int remotePort,
        byte payloadType = 8,
        string? codec = null,
        bool? amrOctetAligned = null,
        int? remoteRtcpPort = null,
        IPAddress? remoteRtcpAddress = null,
        string? amrModeSet = null,
        Func<byte[], Task>? protectedRtpSender = null,
        Func<byte[], Task>? protectedRtcpSender = null,
        bool startSenders = true)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);
        if (remotePort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(remotePort));
        var rtcpPort = remoteRtcpPort ?? (remotePort < ushort.MaxValue ? remotePort + 1 : 0);
        if (rtcpPort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(remoteRtcpPort));
        var rtcpAddress = remoteRtcpAddress ?? remoteIp;
        if (rtcpAddress.AddressFamily != remoteIp.AddressFamily ||
            (BoundInterfaceIndex is not null && LocalAddress.AddressFamily != remoteIp.AddressFamily))
            throw new ArgumentException("RTP, RTCP and the pinned IMS bearer must use the same address family.", nameof(remoteIp));
        if ((protectedRtpSender is null) != (protectedRtcpSender is null))
            throw new ArgumentException("Protected RTP and RTCP senders must be installed together.", nameof(protectedRtpSender));
        var selectedCodec = codec?.ToUpperInvariant() ?? payloadType switch
        {
            0 => "PCMU",
            8 => "PCMA",
            98 or 100 or 102 => "AMR",
            104 => "AMR-WB",
            _ => "PCMA"
        };
        var amrEncodeMode = 7;
        var amrAllowsModeZero = true;
        if (amrModeSet is not null)
        {
            if (selectedCodec != "AMR")
                throw new ArgumentException("AMR mode-set requires an AMR-NB codec.", nameof(amrModeSet));
            var modes = amrModeSet.Split(',', StringSplitOptions.TrimEntries);
            if (modes.Length == 0 || modes.Any(mode => mode.Length != 1 || mode[0] is < '0' or > '7') ||
                modes.Distinct().Count() != modes.Length)
                throw new ArgumentException("AMR mode-set must contain distinct modes 0 through 7.", nameof(amrModeSet));
            amrEncodeMode = modes.Max(mode => mode[0] - '0');
            amrAllowsModeZero = modes.Contains("0");
            amrModeSet = amrModeSet.Trim();
        }
        // Install ESP senders before the endpoint makes the uplink sender live.
        if (protectedRtpSender is not null)
        {
            CustomSender = protectedRtpSender;
            CustomRtcpSender = protectedRtcpSender;
        }
        lock (_pcmFrameGate)
        {
            // A re-INVITE can change codec or media target. Do not combine a
            // partial PCM frame from the previous media configuration with it.
            _pcmSamplesBuffered = 0;
            _remoteEndpoint = new IPEndPoint(remoteIp, remotePort);
            _remoteRtcpEndpoint = new IPEndPoint(rtcpAddress, rtcpPort);
            _currentPayloadType = payloadType;
            _currentCodec = selectedCodec;
            _amrOctetAligned = amrOctetAligned ?? payloadType is 98 or 100;
            _amrEncodeMode = amrEncodeMode;
            _amrAllowsModeZero = amrAllowsModeZero;
            _amrModeSet = amrModeSet;
        }
        if (startSenders)
            StartSenders();
    }

    public void StartSenders()
    {
        if (_remoteEndpoint is null || _remoteRtcpEndpoint is null)
            throw new InvalidOperationException("RTP peer must be configured before starting media senders.");
        lock (_senderStartGate)
        {
            if (_sendersStarted) return;
            _sendersStarted = true;
        }
        StartUplinkSender();
        StartRtcpSender();
    }

    private void StartRtcpSender()
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested && !_isDisposed)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                    var report = CreateReceiverReport();
                    if (report == null || _remoteRtcpEndpoint == null) continue;

                    if (CustomRtcpSender != null)
                        await CustomRtcpSender(report).ConfigureAwait(false);
                    else
                        await _rtcpUdp.SendAsync(report, report.Length, _remoteRtcpEndpoint).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _eventBus?.Publish("rtcp.report.error", "RtpSession", ex.Message);
                }
            }
        });
    }

    private void StartRtcpReceiver()
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested && !_isDisposed)
            {
                try
                {
                    var result = await _rtcpUdp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    ProcessRtcpPacket(result.Buffer, result.RemoteEndPoint);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!_isDisposed)
                        _eventBus?.Publish("rtcp.packet.error", "RtpSession", ex.Message);
                }
            }
        });
    }

    private ushort _uplinkSeq = (ushort)Random.Shared.Next(1, 10000);
    private uint _uplinkTs = (uint)Random.Shared.Next(1, 100000);
    private readonly uint _uplinkSsrc = (uint)Random.Shared.Next();
    private DateTime _lastUplinkSend = DateTime.UtcNow;
    private bool _audioTalkspurtPending = true;

    private void StartUplinkSender()
    {
        Task.Run(async () =>
        {
            // Genuine RFC 4867 bandwidth-efficient AMR-NB MR475 comfort
            // silence frame. Repack it for an octet-aligned peer rather than
            // sending bandwidth-efficient bits under an OA negotiation.
            var silenceAmrBe = Convert.FromHexString("F058CF31FC18C10E7FF800000000");
            var silenceAmrOa = AmrNativeCodec.AmrFrameToRtpPayload(
                AmrNbDecoder.RtpPayloadToAmrFrame(silenceAmrBe, forceOctetAligned: false),
                octetAligned: true);
            var silencePcma = new byte[160];
            Array.Fill<byte>(silencePcma, 0xD5); // G.711a silence
            var silencePcmu = G711Codec.EncodeUlaw(new short[160]);

            while (!_cts.IsCancellationRequested && !_isDisposed)
            {
                try
                {
                    await Task.Delay(800, _cts.Token).ConfigureAwait(false);
                    // If no real audio packet was sent by the microphone in the last 600ms, send comfort silence
                    if (DateTime.UtcNow - _lastUplinkSend >= TimeSpan.FromMilliseconds(600))
                    {
                        byte[] payload;
                        if (_currentCodec == "AMR" && !_amrAllowsModeZero)
                        {
                            var frame = AmrCodec.EncodeAmrNbFrame(new short[160], _amrEncodeMode);
                            if (frame.Length == 0 || (frame[0] >> 3 & 0x0f) != _amrEncodeMode)
                                continue;
                            payload = AmrNativeCodec.AmrFrameToRtpPayload(frame, _amrOctetAligned);
                        }
                        else
                            payload = _currentCodec.StartsWith("AMR", StringComparison.Ordinal)
                                ? (_amrOctetAligned ? silenceAmrOa : silenceAmrBe)
                                : _currentPayloadType == 0 ? silencePcmu : silencePcma;
                        SendRtpPayload(payload, tsIncrement: 160);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });
    }

    public void SendAudioPcm(short[] pcmSamples)
    {
        if (!MediaEnabled) return;
        if (_remoteEndpoint == null || _isDisposed || pcmSamples == null || pcmSamples.Length == 0) return;
        Interlocked.Add(ref _pcmSamplesReceived, pcmSamples.Length);

        lock (_pcmFrameGate)
        {
            var offset = 0;
            while (offset < pcmSamples.Length && MediaEnabled && !_isDisposed)
            {
                var copyCount = Math.Min(_pcmFrame.Length - _pcmSamplesBuffered, pcmSamples.Length - offset);
                Array.Copy(pcmSamples, offset, _pcmFrame, _pcmSamplesBuffered, copyCount);
                _pcmSamplesBuffered += copyCount;
                offset += copyCount;
                if (_pcmSamplesBuffered != _pcmFrame.Length) continue;
                _pcmSamplesBuffered = 0;
                SendAudioFrame((short[])_pcmFrame.Clone());
            }
        }
    }

    private void SendAudioFrame(short[] pcmSamples)
    {
        try
        {
            byte[] payload;
            uint tsIncrement;

            if (_currentCodec.StartsWith("AMR", StringComparison.Ordinal))
            {
                var amrFrame = AmrCodec.EncodeAmrNbFrame(pcmSamples, mode: _amrEncodeMode);
                if (amrFrame.Length == 0 || (amrFrame[0] >> 3 & 0x0f) != _amrEncodeMode)
                {
                    Interlocked.Increment(ref _audioEncodeErrors);
                    return;
                }
                payload = AmrNativeCodec.AmrFrameToRtpPayload(amrFrame, _amrOctetAligned);
                tsIncrement = 160; // 20ms @ 8000Hz = 160 samples
            }
            else if (_currentPayloadType == 0) // PCMU
            {
                payload = G711Codec.EncodeUlaw(pcmSamples);
                tsIncrement = (uint)payload.Length;
            }
            else // Default PCMA (8)
            {
                _currentPayloadType = 8;
                payload = G711Codec.EncodeAlaw(pcmSamples);
                tsIncrement = (uint)payload.Length;
            }
            Interlocked.Increment(ref _audioFramesEncoded);
            SendRtpPayload(payload, tsIncrement, audioFrame: true);
        }
        catch
        {
            // Never let a codec failure tear down the microphone callback.
            Interlocked.Increment(ref _audioEncodeErrors);
        }
    }

    private void SendRtpPayload(byte[] payload, uint tsIncrement = 160, bool audioFrame = false)
    {
        if (_remoteEndpoint == null || _isDisposed) return;
        try
        {
            Task send;
            lock (_packetSendGate)
            {
                if (_remoteEndpoint == null || _isDisposed) return;
                var now = DateTime.UtcNow;
                // RFC 4867 section 4.1: mark the first speech packet of a
                // talkspurt, including audio after an idle/comfort interval.
                var marker = audioFrame && (_audioTalkspurtPending ||
                    now - _lastUplinkSend >= TimeSpan.FromMilliseconds(600));
                _audioTalkspurtPending = !audioFrame;
                _lastUplinkSend = now;
                var packet = new byte[12 + payload.Length];
                packet[0] = 0x80;
                packet[1] = (byte)(_currentPayloadType | (marker ? 0x80 : 0));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), _uplinkSeq++);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), _uplinkTs);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), _uplinkSsrc);
                payload.CopyTo(packet.AsSpan(12));
                _uplinkTs += tsIncrement;
                send = CustomSender is { } sender
                    ? sender(packet)
                    : _udp.SendAsync(packet, packet.Length, _remoteEndpoint);
            }
            _ = ObserveRtpSendAsync(send, audioFrame);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendErrors);
            if (!_isDisposed) _eventBus?.Publish("rtp.send.error", "RtpSession", ex.Message);
        }
    }

    private async Task ObserveRtpSendAsync(Task send, bool audioFrame)
    {
        try
        {
            await send.ConfigureAwait(false);
            Interlocked.Increment(ref _packetsSent);
            if (audioFrame) Interlocked.Increment(ref _audioPacketsSent);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendErrors);
            if (!_isDisposed) _eventBus?.Publish("rtp.send.error", "RtpSession", ex.Message);
        }
    }

    private void StartReceiver()
    {
        Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested && !_isDisposed)
                {
                    // BUG-19 FIX: try-catch INSIDE the while loop so a single bad packet
                    // does not kill the entire receive loop for the duration of the call.
                    try
                    {
                        var result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                        ProcessRtpPacket(result.Buffer, result.RemoteEndPoint);
                    }
                    catch (OperationCanceledException) { throw; } // propagate cancellation
                    catch (Exception ex)
                    {
                        // BUG-19 FIX: log and continue — don't let one malformed packet kill the loop
                        _eventBus?.Publish("rtp.packet.error", "RtpSession", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on dispose */ }
            catch { /* outer catch: socket disposed etc. */ }
        });
    }

    private readonly SortedList<long, byte[]> _recordedAmrFrames = new();
    private readonly object _amrLock = new();
    private long _highestAmrSequence = -1;

    public void ProcessRtpPacket(byte[] raw, IPEndPoint? source = null)
    {
        if (!MediaEnabled) return;
        if (raw.Length < 12 || (raw[0] >> 6) != 2) return;
        if (source != null && (_remoteEndpoint == null || !_remoteEndpoint.Equals(source))) return;

        byte pt = (byte)(raw[1] & 0x7F);
        if (pt != _currentPayloadType && pt != 101) return;

        // RTP Header: V(2) P X CC(4) | M PT(7) | Seq(16) | TS(32) | SSRC(32)
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2, 2));
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4, 4));
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8, 4));
        // BUG-10 FIX: account for CSRC list AND optional Extension header
        int headerLen = 12 + (raw[0] & 0x0F) * 4;
        if (raw.Length < headerLen) return;
        if ((raw[0] & 0x10) != 0)
        {
            if (raw.Length < headerLen + 4) return;
            // Extension header: defined-by-profile(2B) | length(2B in 32-bit words)
            int extWordLen = (raw[headerLen + 2] << 8 | raw[headerLen + 3]);
            headerLen += 4 + extWordLen * 4;
        }
        var payloadEnd = raw.Length;
        if ((raw[0] & 0x20) != 0)
        {
            var paddingLength = raw[^1];
            if (paddingLength == 0 || paddingLength > payloadEnd - headerLen) return;
            payloadEnd -= paddingLength;
        }
        if (headerLen > payloadEnd) return;

        TotalPacketsReceived++;
        RecordRtpArrival(seq, timestamp, ssrc, pt);

        if (payloadEnd > headerLen)
        {
            var payload = raw.AsSpan(headerLen, payloadEnd - headerLen).ToArray();

            // RFC 4733 telephone-event. End packets are normally repeated, so
            // de-duplicate by RTP timestamp and event number before forwarding.
            if (pt == 101 && payload.Length >= 4)
            {
                var eventNumber = payload[0];
                var ended = (payload[1] & 0x80) != 0;
                var duration = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2));
                if (ended && (_lastDtmfTimestamp != timestamp || _lastDtmfEvent != eventNumber) &&
                    TryMapDtmf(eventNumber, out var digit))
                {
                    _lastDtmfTimestamp = timestamp;
                    _lastDtmfEvent = eventNumber;
                    OnDtmfReceived?.Invoke(digit, Math.Max(40, duration / 8));
                }
                return;
            }

            var negotiatedAmr = pt == _currentPayloadType &&
                                _currentCodec.StartsWith("AMR", StringComparison.Ordinal);
            if (negotiatedAmr || pt is 102 or 100 or 98 or 104) // AMR-NB / AMR-WB
            {
                var octetAligned = negotiatedAmr ? _amrOctetAligned : pt is 98 or 100;
                var wideband = (negotiatedAmr && _currentCodec == "AMR-WB") || pt == 104;
                var frames = AmrNbDecoder.RtpPayloadToAmrFrames(payload, forceOctetAligned: octetAligned, isWideband: wideband);
                for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                {
                    var amrFrame = frames[frameIndex];
                    if (_recordAudio) lock (_amrLock)
                    {
                        // Extend the 16-bit RTP sequence across wraps; the old modulo key
                        // overwrote earlier AMR frames after roughly 22 minutes of audio.
                        long extendedSeq = _highestAmrSequence < 0
                            ? seq
                            : (_highestAmrSequence & ~0xFFFFL) | seq;
                        if (_highestAmrSequence >= 0)
                        {
                            if (extendedSeq < _highestAmrSequence - 32768) extendedSeq += 65536;
                            else if (extendedSeq > _highestAmrSequence + 32768) extendedSeq -= 65536;
                        }
                        if (extendedSeq > _highestAmrSequence) _highestAmrSequence = extendedSeq;
                        _recordedAmrFrames[extendedSeq * 16 + frameIndex] = amrFrame;
                    }

                    // Real-time AMR decoding and speaker playback
                    var pcm = wideband
                        ? AmrCodec.DecodeAmrWbFrame(amrFrame)
                        : AmrCodec.DecodeAmrNbFrame(amrFrame);

                    if (pcm != null && pcm.Length > 0)
                    {
                        Interlocked.Increment(ref _audioFramesDecoded);
                        _audio?.PlayPcmSamples(pcm);
                        OnAudioDecoded?.Invoke(pcm);
                    }
                }
            }
            else if (pt == 0) // PCMU / G.711u
            {
                var samples = G711Codec.DecodeUlaw(payload);
                Interlocked.Increment(ref _audioFramesDecoded);
                _audio?.PlayPcmSamples(samples);
                OnAudioDecoded?.Invoke(samples);
            }
            else // PCMA / G.711a (default PT=8)
            {
                var samples = G711Codec.DecodeAlaw(payload);
                Interlocked.Increment(ref _audioFramesDecoded);
                _audio?.PlayPcmSamples(samples);
                OnAudioDecoded?.Invoke(samples);
            }

            _eventBus?.Publish("rtp.audio.frame", "RtpSession",
                new { PT = pt, Total = TotalPacketsReceived });
        }
    }

    private static bool TryMapDtmf(byte eventNumber, out char digit)
    {
        digit = eventNumber switch
        {
            <= 9 => (char)('0' + eventNumber),
            10 => '*',
            11 => '#',
            12 => 'A',
            13 => 'B',
            14 => 'C',
            15 => 'D',
            _ => '\0'
        };
        return digit != '\0';
    }

    private void RecordRtpArrival(ushort sequence, uint timestamp, uint ssrc, byte payloadType)
    {
        lock (_rtcpLock)
        {
            if (_receivedForRtcp == 0 || _remoteSsrc != ssrc)
            {
                _remoteSsrc = ssrc;
                _baseSequence = sequence;
                _maxSequence = sequence;
                _sequenceCycles = 0;
                _receivedForRtcp = 0;
                _expectedPrior = 0;
                _receivedPrior = 0;
                _jitter = 0;
                _lastTransit = null;
            }
            else
            {
                if (sequence < _maxSequence && _maxSequence - sequence > 0x8000)
                    _sequenceCycles += 1u << 16;
                if ((ushort)(sequence - _maxSequence) < 0x8000)
                    _maxSequence = sequence;
            }

            _receivedForRtcp++;
            var clockRate = payloadType == 104 ? 16000L : 8000L;
            var arrival = DateTime.UtcNow.Ticks * clockRate / TimeSpan.TicksPerSecond;
            var transit = arrival - timestamp;
            if (_lastTransit is long previous)
            {
                var delta = Math.Abs(transit - previous);
                _jitter = (uint)Math.Max(0, (long)_jitter + ((delta - _jitter) / 16));
            }
            _lastTransit = transit;
        }
    }

    /// <summary>Consumes RTCP control traffic, including Sender Reports used by RR feedback.</summary>
    public void ProcessRtcpPacket(byte[] raw, IPEndPoint? source = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (source != null && (_remoteRtcpEndpoint == null || !_remoteRtcpEndpoint.Equals(source))) return;
        var offset = 0;
        while (offset + 4 <= raw.Length)
        {
            if ((raw[offset] >> 6) != 2) return;
            var packetType = raw[offset + 1];
            var words = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offset + 2, 2));
            var packetLength = checked((words + 1) * 4);
            if (packetLength < 4 || offset + packetLength > raw.Length) return;

            if (packetType == 200 && packetLength >= 28) // Sender Report
            {
                lock (_rtcpLock)
                {
                    _remoteSsrc = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset + 4, 4));
                    var ntpSeconds = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset + 8, 4));
                    var ntpFraction = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset + 12, 4));
                    _lastSenderReport = (ntpSeconds << 16) | (ntpFraction >> 16);
                    _lastSenderReportAt = DateTimeOffset.UtcNow;
                }
            }
            else if (packetType == 203) // BYE
            {
                _eventBus?.Publish("rtcp.bye", "RtpSession", new { RemoteSsrc = _remoteSsrc });
            }
            offset += packetLength;
        }
    }

    /// <summary>Builds an RFC 3550 Receiver Report for the current RTP source.</summary>
    public byte[]? CreateReceiverReport()
    {
        lock (_rtcpLock)
        {
            if (_receivedForRtcp == 0) return null;

            var extendedHighest = _sequenceCycles + _maxSequence;
            var expected = extendedHighest - _baseSequence + 1;
            var lost = (long)expected - _receivedForRtcp;
            var expectedInterval = expected - _expectedPrior;
            var receivedInterval = _receivedForRtcp - _receivedPrior;
            var lostInterval = (long)expectedInterval - receivedInterval;
            var fractionLost = expectedInterval == 0 || lostInterval <= 0
                ? 0
                : (int)Math.Min(255, (lostInterval << 8) / expectedInterval);
            _expectedPrior = expected;
            _receivedPrior = _receivedForRtcp;

            var cumulativeLost = (int)Math.Clamp(lost, -0x800000L, 0x7fffffL);
            var report = new byte[32];
            report[0] = 0x81; // V=2, one report block
            report[1] = 201;  // Receiver Report
            BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(2, 2), 7);
            BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(4, 4), _uplinkSsrc);
            BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(8, 4), _remoteSsrc);
            report[12] = (byte)fractionLost;
            report[13] = (byte)(cumulativeLost >> 16);
            report[14] = (byte)(cumulativeLost >> 8);
            report[15] = (byte)cumulativeLost;
            BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(16, 4), extendedHighest);
            BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(20, 4), _jitter);
            BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(24, 4), _lastSenderReport);
            if (_lastSenderReportAt is { } senderReportAt)
            {
                var delay = Math.Max(0, (DateTimeOffset.UtcNow - senderReportAt).TotalSeconds);
                BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(28, 4),
                    (uint)Math.Min(uint.MaxValue, delay * 65536));
            }
            return report;
        }
    }

    public void SaveAudioRecording(string wavPath, byte[] pcmRecording)
    {
        ArgumentNullException.ThrowIfNull(pcmRecording);
        lock (_amrLock)
        {
            if (_recordedAmrFrames.Count > 0)
            {
                var amrPath = Path.ChangeExtension(wavPath, ".amr");
                try
                {
                    using (var fs = new FileStream(amrPath, FileMode.Create, FileAccess.Write))
                    {
                        // RFC 4867 §5.1 Magic number: "#!AMR\n"
                        fs.Write(System.Text.Encoding.ASCII.GetBytes("#!AMR\n"));
                        // SortedList.Values are already in sequence-number order
                        foreach (var frame in _recordedAmrFrames.Values)
                        {
                            fs.Write(frame);
                        }
                    }
                    Console.WriteLine($"[RtpSession] Saved {_recordedAmrFrames.Count} AMR frames to {amrPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RtpSession] Warning during AMR save: {ex.Message}");
                }
            }
        }

        // Save recorded PCM directly via _audio (standard RIFF/WAV without external process)
        try
        {
            _audio?.SavePcmToWavFile(wavPath, pcmRecording);
        }
        catch { }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _cts.Cancel();
            _udp.Dispose();
            _rtcpUdp.Dispose();
            _cts.Dispose();
            lock (_codecGate) _amrCodec?.Dispose();
        }
    }
}
