using VoSharp.Telephony.Calls;

namespace VoSharp.Kernel.SipGateway;

/// <summary>
/// Optional application-owned adapter for a baseband call's USB Audio/PCM
/// endpoint. The kernel reserves it before ATA/ATD so the desktop speaker path
/// does not race the SIP media path for the same UAC device.
/// </summary>
public interface ICellularSipMediaProvider
{
    void Reserve(string slotId);
    bool IsReserved(string? slotId);
    Task<ICallPcmMedia> OpenAsync(string slotId, CancellationToken ct);
    Task ReleaseAsync(string? slotId);
}
