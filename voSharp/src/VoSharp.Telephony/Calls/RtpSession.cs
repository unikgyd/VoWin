using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VoSharp.Common.Events;
using VoSharp.Sip;
using VoSharp.Telephony.Audio;

namespace VoSharp.Telephony.Calls;

public class RtpSession : IDisposable
{
    private readonly UdpClient _udp;
    private readonly UdpClient _rtcpUdp;
    private readonly WindowsAudioDevice? _audio;
    private readonly AsyncEventBus? _eventBus;
    private readonly AmrNativeCodec _amrCodec = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _rtcpLock = new();
    private IPEndPoint? _remoteEndpoint;
    private IPEndPoint? _remoteRtcpEndpoint;
    private byte _currentPayloadType = 8; // 8 = PCMA, 0 = PCMU
    private string _currentCodec = "PCMA";
    private bool _amrOctetAligned;
    private bool _sendersStarted;
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

    public int LocalPort { get; }
    public int LocalRtcpPort { get; }
    public int? BoundInterfaceIndex { get; }
    public IPAddress LocalAddress { get; }
    public IPEndPoint? RemoteRtcpEndPoint => _remoteRtcpEndpoint;
    public byte PayloadType => _currentPayloadType;
    public string Codec => _currentCodec;
    public bool AmrOctetAligned => _amrOctetAligned;
    public ulong TotalPacketsReceived { get; private set; }
    public Func<byte[], Task>? CustomSender { get; set; }
    public Func<byte[], Task>? CustomRtcpSender { get; set; }
    public Action<short[]>? OnAudioDecoded { get; set; }
    public Action<char, int>? OnDtmfReceived { get; set; }

    public RtpSession(
        WindowsAudioDevice? audio,
        AsyncEventBus? eventBus = null,
        int localPort = 0,
        IPAddress? localAddress = null,
        int? outgoingInterfaceIndex = null)
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
        IPAddress? remoteRtcpAddress = null)
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
        _remoteEndpoint     = new IPEndPoint(remoteIp, remotePort);
        _remoteRtcpEndpoint = new IPEndPoint(rtcpAddress, rtcpPort);
        _currentPayloadType = payloadType;
        _currentCodec = codec?.ToUpperInvariant() ?? payloadType switch
        {
            0 => "PCMU",
            8 => "PCMA",
            98 or 100 or 102 => "AMR",
            104 => "AMR-WB",
            _ => "PCMA"
        };
        _amrOctetAligned = amrOctetAligned ?? payloadType is 98 or 100;
        if (!_sendersStarted)
        {
            _sendersStarted = true;
            StartUplinkSender();
            StartRtcpSender();
        }
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
                    ProcessRtcpPacket(result.Buffer);
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

    private void StartUplinkSender()
    {
        Task.Run(async () =>
        {
            // Genuine RFC 4867 bandwidth-efficient AMR-NB MR475 comfort silence frame (14 bytes)
            var silenceAmr = Convert.FromHexString("F058CF31FC18C10E7FF800000000");
            var silencePcma = new byte[160];
            Array.Fill<byte>(silencePcma, 0xD5); // G.711a silence

            while (!_cts.IsCancellationRequested && !_isDisposed)
            {
                try
                {
                    await Task.Delay(800, _cts.Token).ConfigureAwait(false);
                    // If no real audio packet was sent by the microphone in the last 600ms, send comfort silence
                    if (DateTime.UtcNow - _lastUplinkSend >= TimeSpan.FromMilliseconds(600))
                    {
                        var payload = _currentCodec.StartsWith("AMR", StringComparison.Ordinal) ? silenceAmr : silencePcma;
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
        if (_remoteEndpoint == null || _isDisposed || pcmSamples == null || pcmSamples.Length == 0) return;
        
        byte[] payload;
        uint tsIncrement;

        if (_currentCodec.StartsWith("AMR", StringComparison.Ordinal))
        {
            var amrFrame = _amrCodec.EncodeAmrNbFrame(pcmSamples, mode: 7);
            if (amrFrame.Length == 0) return;
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
        SendRtpPayload(payload, tsIncrement);
    }

    private void SendRtpPayload(byte[] payload, uint tsIncrement = 160)
    {
        if (_remoteEndpoint == null || _isDisposed) return;
        _lastUplinkSend = DateTime.UtcNow;
        try
        {
            var packet = new byte[12 + payload.Length];
            packet[0] = 0x80;
            packet[1] = _currentPayloadType;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), _uplinkSeq++);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), _uplinkTs);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), _uplinkSsrc);
            payload.CopyTo(packet.AsSpan(12));

            _uplinkTs += tsIncrement;

            if (CustomSender != null)
            {
                _ = CustomSender(packet);
            }
            else
            {
                _udp.SendAsync(packet, packet.Length, _remoteEndpoint);
            }
        }
        catch { }
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

    private readonly SortedList<uint, byte[]> _recordedAmrFrames = new();
    private readonly object _amrLock = new();
    private uint _amrSeqBase;
    private bool _amrSeqBaseSet;

    public void ProcessRtpPacket(byte[] raw, IPEndPoint? source = null)
    {
        if (raw.Length < 12 || (raw[0] >> 6) != 2) return;
        if (source != null && (_remoteEndpoint == null || !_remoteEndpoint.Equals(source))) return;

        byte pt = (byte)(raw[1] & 0x7F);
        if (pt != _currentPayloadType && pt != 101) return;

        TotalPacketsReceived++;

        // RTP Header: V(2) P X CC(4) | M PT(7) | Seq(16) | TS(32) | SSRC(32)
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2, 2));
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4, 4));
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8, 4));
        RecordRtpArrival(seq, timestamp, ssrc, pt);

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
        if (headerLen >= raw.Length) return;

        if (raw.Length > headerLen)
        {
            var payload = raw.AsSpan(headerLen).ToArray();

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
                var amrFrame = AmrNbDecoder.RtpPayloadToAmrFrame(payload, forceOctetAligned: octetAligned);
                if (amrFrame.Length > 0)
                {
                    lock (_amrLock)
                    {
                        // Use extended sequence number to handle 16-bit wraparound
                        if (!_amrSeqBaseSet)
                        {
                            _amrSeqBase = seq;
                            _amrSeqBaseSet = true;
                        }
                        // Compute relative position handling 16-bit wrap
                        uint relSeq = (uint)((ushort)(seq - (ushort)_amrSeqBase));
                        _recordedAmrFrames[relSeq] = amrFrame;
                    }

                    // Real-time AMR decoding and speaker playback
                    var pcm = (negotiatedAmr && _currentCodec == "AMR-WB") || pt == 104
                        ? _amrCodec.DecodeAmrWbFrame(amrFrame)
                        : _amrCodec.DecodeAmrNbFrame(amrFrame);

                    if (pcm != null && pcm.Length > 0)
                    {
                        _audio?.PlayPcmSamples(pcm);
                        OnAudioDecoded?.Invoke(pcm);
                    }
                }
            }
            else if (pt == 0) // PCMU / G.711u
            {
                var samples = G711Codec.DecodeUlaw(payload);
                _audio?.PlayPcmSamples(samples);
                OnAudioDecoded?.Invoke(samples);
            }
            else // PCMA / G.711a (default PT=8)
            {
                var samples = G711Codec.DecodeAlaw(payload);
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
    public void ProcessRtcpPacket(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
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

    public void SaveAudioRecording(string wavPath)
    {
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
            _audio?.SaveToWavFile(wavPath);
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
            _amrCodec.Dispose();
        }
    }
}
