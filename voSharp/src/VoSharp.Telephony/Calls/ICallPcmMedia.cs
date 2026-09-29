namespace VoSharp.Telephony.Calls;

/// <summary>
/// Transport-neutral 8 kHz, 16-bit, mono PCM endpoint used by call bridges.
/// Implementations may be an IMS RTP session or a modem USB Audio Class stream.
/// </summary>
public interface ICallPcmMedia
{
    event Action<short[]>? RemotePcmReceived;
    void SendExternalPcm(short[] samples);
}
