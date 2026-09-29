using qmiSharp.Services;

namespace VoSharp.Modem;

/// <summary>
/// ISIM APDUs on the single, unambiguously selected physical UICC. Multi-slot
/// mapping is deliberately deferred to AT until the target hardware verifies it.
/// </summary>
internal sealed class QmiIsimApduTransport : IIsimApduTransport
{
    private readonly UimService _uim;
    private readonly byte _slot;
    private readonly UimCardStatus _status;

    private QmiIsimApduTransport(UimService uim, byte slot, UimCardStatus status)
    {
        _uim = uim;
        _slot = slot;
        _status = status;
    }

    public bool IsOpen => true;

    public string? GetReadySelectedUsimAid()
    {
        var selected = _status.SelectedCardApplications[_status.SelectedApplicationIndex!.Value];
        var aid = Convert.ToHexString(selected.Aid);
        return selected.Type == 2 && selected.State == 7 &&
            aid.StartsWith("A0000000871002", StringComparison.Ordinal)
            ? aid : null;
    }

    public static async Task<QmiIsimApduTransport> CreateAsync(UimService uim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uim);
        var status = await uim.GetCardStatusAsync(ct).ConfigureAwait(false);
        if (status.NumCards != 1 || status.SelectedCardIndex != 0 ||
            status.SelectedApplicationIndex is null || !status.HasSubscriptionApplication)
            throw new NotSupportedException("QMI ISIM APDUs require one unambiguous selected GW subscription.");
        var slots = await uim.GetSlotStatusAsync(ct).ConfigureAwait(false);
        if (slots.Count != 1 || !slots[0].IsActive || !slots[0].CardPresent ||
            slots[0].PhysicalSlotNumber != 1)
            throw new NotSupportedException("QMI UIM physical-slot mapping is not unambiguous.");
        return new QmiIsimApduTransport(uim, slots[0].PhysicalSlotNumber, status);
    }

    public Task<(bool Ready, bool Absent)> CheckReadyAsync(CancellationToken ct)
    {
        var selected = _status.SelectedCardApplications[_status.SelectedApplicationIndex!.Value];
        return Task.FromResult((selected.State == 7, _status.State == UimCardState.Absent));
    }

    public Task<IsimApplicationDirectory> GetApplicationsAsync(CancellationToken ct)
    {
        var aids = _status.SelectedCardApplications
            .Where(app => app.Aid.Length >= 7 && app.Type is 1 or 2 or 5)
            .Select(app => Convert.ToHexString(app.Aid))
            .Where(aid => aid.StartsWith("A00000008710", StringComparison.Ordinal))
            .ToArray();
        return Task.FromResult(new IsimApplicationDirectory(aids.Length > 0, aids));
    }

    public async Task<int> OpenApplicationAsync(string aid, CancellationToken ct)
    {
        if (!_status.SelectedCardApplications.Any(app =>
            Convert.ToHexString(app.Aid).Equals(aid, StringComparison.OrdinalIgnoreCase)))
            return 0;
        return await _uim.OpenLogicalChannelAsync(_slot, Convert.FromHexString(aid), ct)
            .ConfigureAwait(false);
    }

    public async Task<byte[]?> TransmitAsync(int channelId, byte[] command, CancellationToken ct) =>
        await _uim.SendApduAsync(_slot, checked((byte)channelId), command, ct).ConfigureAwait(false);

    public Task CloseApplicationAsync(int channelId, CancellationToken ct) =>
        _uim.CloseLogicalChannelAsync(_slot, checked((byte)channelId), ct);
}
