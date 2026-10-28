using System.Net;
using qmiSharp.Client;
using qmiSharp.Services;

namespace VoSharp.Modem;

/// <summary>
/// Owns one independent QMI WDS IMS call. This proves only modem-side PDP
/// settings; the caller must separately verify Windows NIC/address/route
/// ownership before using it for Host IMS SIP.
/// </summary>
public sealed class QmiImsBearerLease : IAsyncDisposable
{
    private readonly WdsService _wds;

    public QmiImsBearerLease(QmiClient client) =>
        _wds = new WdsService(client, dedicatedClientId: true);

    public QmiImsPdn? CurrentPdn { get; private set; }
    public uint? PacketDataHandle => _wds.OwnedPacketDataHandle;
    public bool IsBearerStateIndeterminate => _wds.IsBearerStateIndeterminate;

    public Task<QmiImsPdn> StartAsync(string apn, CancellationToken ct = default) =>
        StartAsync(apn, 4, ct);

    public async Task<QmiImsPdn> StartAsync(string apn, byte ipFamily, CancellationToken ct = default)
    {
        if (!HostImsPdnParser.IsImsApn(apn))
            throw new ArgumentException("An IMS APN is required for a QMI IMS bearer.", nameof(apn));
        if (ipFamily is not (4 or 6))
            throw new ArgumentOutOfRangeException(nameof(ipFamily), "Only IPv4 or IPv6 QMI bearers are supported.");
        if (PacketDataHandle is not null || CurrentPdn is not null)
            throw new InvalidOperationException("This QMI IMS bearer lease is already active.");

        await _wds.SetIpFamilyAsync(ipFamily, ct).ConfigureAwait(false);
        await _wds.StartNetworkInterfaceAsync(apn, ipFamily, ct).ConfigureAwait(false);
        try
        {
            var settings = await _wds.GetCurrentSettingsAsync(ct).ConfigureAwait(false);
            var reportedApn = settings.ApnName;
            if (string.IsNullOrWhiteSpace(reportedApn) &&
                settings.ProfileType is { } profileType && settings.ProfileIndex is { } profileIndex)
            {
                var profile = await _wds.GetProfileSettingsAsync(profileType, profileIndex, ct)
                    .ConfigureAwait(false);
                reportedApn = profile.ApnName;
            }
            var local = ipFamily == 6 ? settings.Ipv6Address : settings.Ipv4Address;
            if (!HostImsPdnParser.IsImsApn(reportedApn) || local is null ||
                !HostImsPdnParser.IsUsableEndpointAddress(local))
                throw new InvalidOperationException($"QMI WDS did not return a valid IMS APN and IPv{ipFamily} address.");
            var pcscf = settings.PcscfServers
                .Where(address => address.AddressFamily == local.AddressFamily &&
                    HostImsPdnParser.IsUsableEndpointAddress(address) && !address.Equals(local))
                .Distinct().ToArray();
            if (pcscf.Length == 0)
                throw new InvalidOperationException("QMI WDS did not return an IMS P-CSCF address.");

            CurrentPdn = new QmiImsPdn(settings.ProfileIndex, reportedApn!,
                local, pcscf);
            return CurrentPdn;
        }
        catch (Exception original)
        {
            // A successful StartNetwork must never be left active after a
            // settings/PCO validation failure. If Stop fails, retain the handle
            // in WdsService and surface both errors for an explicit retry.
            try
            {
                if (_wds.OwnedPacketDataHandle is { } handle)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _wds.StopNetworkInterfaceAsync(handle, cleanup.Token).ConfigureAwait(false);
                }
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("QMI IMS bearer validation and rollback both failed; the bearer may remain active.",
                    original, cleanupError);
            }
            throw;
        }
    }

    /// <summary>Read-only Windows address and P-CSCF route check; never grants SIP access by itself.</summary>
    public HostImsProbeResult ProbeWindowsRoute(bool? simInserted)
    {
        var pdn = CurrentPdn ?? throw new InvalidOperationException("Start the QMI IMS bearer first.");
        return ModemDriver.BuildQmiHostImsProbe(simInserted, pdn, "QMI dedicated WDS bearer");
    }

    public async ValueTask DisposeAsync()
    {
        await _wds.DisposeAsync().ConfigureAwait(false);
        CurrentPdn = null;
    }
}
