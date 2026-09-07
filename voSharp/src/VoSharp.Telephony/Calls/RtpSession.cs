using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VoSharp.Common.Events;
using VoSharp.Telephony.Audio;

namespace VoSharp.Telephony.Calls;

public class RtpSession : IDisposable
{
    private readonly UdpClient _udp;
    private readonly WindowsAudioDevice _audio;
    private readonly AsyncEventBus? _eventBus;
    private readonly AmrNativeCodec _amrCodec = new();
    private readonly CancellationTokenSource _cts = new();
    private IPEndPoint? _remoteEndpoint;
    private byte _currentPayloadType = 8; // 8 = PCMA, 0 = PCMU
    private bool _isDisposed;

    public int LocalPort { get; }
    public ulong TotalPacketsReceived { get; private set; }
    public Func<byte[], Task>? CustomSender { get; set; }
    public Action<short[]>? OnAudioDecoded { get; set; }

    public RtpSession(WindowsAudioDevice audio, AsyncEventBus? eventBus = null, int localPort = 0)
    {
        _audio     = audio;
        _eventBus  = eventBus;
        _udp       = new UdpClient(localPort > 0 ? localPort : 0);
        LocalPort  = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        StartReceiver();
    }

    public void SetRemoteEndpoint(IPAddress remoteIp, int remotePort, byte payloadType = 8)
    {
        _remoteEndpoint     = new IPEndPoint(remoteIp, remotePort);
        _currentPayloadType = payloadType;
        StartUplinkSender();
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
                        var payload = (_currentPayloadType is 98 or 100 or 102 or 104) ? silenceAmr : silencePcma;
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

        if (_currentPayloadType is 98 or 100 or 102 or 104) // AMR-NB / AMR-WB
        {
            var amrFrame = _amrCodec.EncodeAmrNbFrame(pcmSamples, mode: 7);
            if (amrFrame.Length == 0) return;
            bool octetAligned = _currentPayloadType is 98 or 100;
            payload = AmrNativeCodec.AmrFrameToRtpPayload(amrFrame, octetAligned);
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
                        ProcessRtpPacket(result.Buffer);
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

    public void ProcessRtpPacket(byte[] raw)
    {
        if (raw.Length < 12) return;

        TotalPacketsReceived++;

        // RTP Header: V(2) P X CC(4) | M PT(7) | Seq(16) | TS(32) | SSRC(32)
        byte pt = (byte)(raw[1] & 0x7F);
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2, 2));

        // BUG-10 FIX: account for CSRC list AND optional Extension header
        int headerLen = 12 + (raw[0] & 0x0F) * 4;
        if ((raw[0] & 0x10) != 0 && raw.Length >= headerLen + 4)
        {
            // Extension header: defined-by-profile(2B) | length(2B in 32-bit words)
            int extWordLen = (raw[headerLen + 2] << 8 | raw[headerLen + 3]);
            headerLen += 4 + extWordLen * 4;
        }

        if (raw.Length > headerLen)
        {
            var payload = raw.AsSpan(headerLen).ToArray();

            if (pt is 102 or 100 or 98 or 104) // AMR-NB / AMR-WB
            {
                // PT 98/100 = octet-aligned (per SDP fmtp), PT 102/104 = bandwidth-efficient
                bool octetAligned = pt is 98 or 100;
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
                    var pcm = (pt == 104)
                        ? _amrCodec.DecodeAmrWbFrame(amrFrame)
                        : _amrCodec.DecodeAmrNbFrame(amrFrame);

                    if (pcm != null && pcm.Length > 0)
                    {
                        _audio.PlayPcmSamples(pcm);
                        OnAudioDecoded?.Invoke(pcm);
                    }
                }
            }
            else if (pt == 0) // PCMU / G.711u
            {
                var samples = G711Codec.DecodeUlaw(payload);
                _audio.PlayPcmSamples(samples);
            }
            else // PCMA / G.711a (default PT=8)
            {
                var samples = G711Codec.DecodeAlaw(payload);
                _audio.PlayPcmSamples(samples);
            }

            _eventBus?.Publish("rtp.audio.frame", "RtpSession",
                new { PT = pt, Total = TotalPacketsReceived });
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
            _audio.SaveToWavFile(wavPath);
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
            _cts.Dispose();
            _amrCodec.Dispose();
        }
    }
}
