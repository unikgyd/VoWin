using System.Net;
using System.Security.Cryptography;
using qmiSharp.Client;
using qmiSharp.Services;
using qmiSharp.Transport;
using VoSharp.Common.Aka;
using VoSharp.Modem.At;
using VoSharp.Sim;

namespace VoSharp.Modem;

/// <summary>Read-only QMI operations used by the QMI-first / AT-fallback modem stack.</summary>
public interface IQmiModemReader : IAsyncDisposable
{
    Task<bool?> GetSimInsertedAsync(CancellationToken ct = default);
    Task<string> GetImeiAsync(CancellationToken ct = default);
    Task<string> GetImsiAsync(CancellationToken ct = default);
    Task<string> GetIccidAsync(CancellationToken ct = default);
    Task<QmiImsPdn?> GetActiveImsPdnAsync(CancellationToken ct = default);
    Task<SignalQuality?> GetSignalAsync(CancellationToken ct = default);
    Task<NetworkRegistration?> GetRegistrationAsync(CancellationToken ct = default);
}

/// <summary>Optional QMI UIM logical-channel support for ISIM files and AKA.</summary>
public interface IQmiIsimReader
{
    Task<IsimIdentityProbeResult> ReadIsimIdentityAsync(CancellationToken ct = default);
    Task<AkaResult> AuthenticateIsimAsync(AkaChallenge challenge, CancellationToken ct = default);
}

/// <summary>Optional QMI UIM logical-channel USIM AKA support.</summary>
public interface IQmiUsimReader
{
    Task<bool> CanAuthenticateUsimAsync(CancellationToken ct = default);
    Task<AkaResult> AuthenticateUsimAsync(AkaChallenge challenge, CancellationToken ct = default);
}

/// <summary>Read-only WDS profile preflight; does not require an active SIM or start a bearer.</summary>
public interface IQmiImsProfileReader
{
    Task<IReadOnlyList<QmiImsProfile>> GetConfiguredImsProfilesAsync(CancellationToken ct = default);
}

public sealed record QmiImsProfile(byte ProfileIndex, string PdpType, string Apn);

/// <summary>An already-connected WDS IMS context, not a request to start one.</summary>
public sealed record QmiImsPdn(byte? ProfileIndex, string Apn,
    IPAddress LocalAddress, IReadOnlyList<IPAddress> PcscfServers);

/// <summary>
/// A per-AT-port QMI endpoint. The standard Windows WWAN/AT ports are never
/// treated as raw QMUX devices; an actual device path or local QMI proxy must
/// be configured explicitly with VOWIN_QMI_ENDPOINT_COMx.
/// </summary>
public sealed class QmiModemReader : IQmiModemReader, IQmiIsimReader, IQmiUsimReader, IQmiImsProfileReader
{
    private readonly Func<IQmiTransport> _openTransport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private QmiClient? _client;
    private bool _disposed;

    internal QmiModemReader(Func<IQmiTransport> openTransport) => _openTransport = openTransport;

    public static QmiModemReader? FromEnvironment(string atPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(atPort);
        var key = $"VOWIN_QMI_ENDPOINT_{atPort.ToUpperInvariant()}";
        var endpoint = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (endpoint.StartsWith("device:", StringComparison.OrdinalIgnoreCase))
        {
            var path = endpoint[7..];
            if (!OperatingSystem.IsWindows() || !path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException($"{key} must name a Win32 raw-QMI device path.");
            return new QmiModemReader(() => new Win32DeviceTransport(path));
        }
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase) &&
            IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address) &&
            uri.Port is > 0 and <= 65535 && uri.AbsolutePath == "/" &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query))
            return new QmiModemReader(() => new SocketQmiTransport(address.ToString(), uri.Port));
        throw new ArgumentException($"{key} must be device:\\\\.\\<raw-qmi-path> or tcp://127.0.0.1:<port>.");
    }

    private async Task<T> QueryAsync<T>(Func<QmiClient, CancellationToken, Task<T>> query, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is null)
            {
                var transport = _openTransport();
                _client = await QmiClient.CreateAsync(transport,
                    new QmiClientOptions { DefaultTimeout = TimeSpan.FromSeconds(3) }, ct).ConfigureAwait(false);
            }
            return await query(_client, ct).ConfigureAwait(false);
        }
        catch
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public Task<bool?> GetSimInsertedAsync(CancellationToken ct = default) =>
        QueryAsync<bool?>(async (client, token) =>
        {
            await using var uim = new UimService(client);
            var status = await uim.GetCardStatusAsync(token).ConfigureAwait(false);
            return status.State switch
            {
                UimCardState.Present when status.HasSubscriptionApplication => true,
                UimCardState.Absent => false,
                _ => null
            };
        }, ct);

    public Task<string> GetImeiAsync(CancellationToken ct = default) =>
        QueryAsync(async (client, token) =>
        {
            await using var dms = new DmsService(client);
            var ids = await dms.GetSerialNumbersAsync(token).ConfigureAwait(false);
            return ids.IMEI ?? string.Empty;
        }, ct);

    public Task<string> GetImsiAsync(CancellationToken ct = default) =>
        QueryAsync(async (client, token) =>
        {
            await using var dms = new DmsService(client);
            return await dms.GetImsiAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<string> GetIccidAsync(CancellationToken ct = default) =>
        QueryAsync(async (client, token) =>
        {
            await using var dms = new DmsService(client);
            return await dms.GetIccidAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<IsimIdentityProbeResult> ReadIsimIdentityAsync(CancellationToken ct = default) =>
        QueryAsync(async (client, token) =>
        {
            await using var uim = new UimService(client);
            var transport = await QmiIsimApduTransport.CreateAsync(uim, token).ConfigureAwait(false);
            return await new IsimIdentityReader(transport).ReadAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<AkaResult> AuthenticateIsimAsync(AkaChallenge challenge, CancellationToken ct = default) =>
        QueryAsync(async (client, token) =>
        {
            await using var uim = new UimService(client);
            var transport = await QmiIsimApduTransport.CreateAsync(uim, token).ConfigureAwait(false);
            return await new IsimIdentityReader(transport).AuthenticateAsync(challenge, token)
                .ConfigureAwait(false);
        }, ct);

    public Task<bool> CanAuthenticateUsimAsync(CancellationToken ct = default) =>
        QueryAsync(async (client, token) =>
        {
            await using var uim = new UimService(client);
            var transport = await QmiIsimApduTransport.CreateAsync(uim, token).ConfigureAwait(false);
            var aid = transport.GetReadySelectedUsimAid();
            if (aid is null) return false;
            var channel = await transport.OpenApplicationAsync(aid, token).ConfigureAwait(false);
            if (channel == 0) return false;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await transport.CloseApplicationAsync(channel, cleanup.Token).ConfigureAwait(false);
            return true;
        }, ct);

    public Task<AkaResult> AuthenticateUsimAsync(AkaChallenge challenge, CancellationToken ct = default) =>
        QueryAsync(async (client, token) =>
        {
            ArgumentNullException.ThrowIfNull(challenge);
            await using var uim = new UimService(client);
            var transport = await QmiIsimApduTransport.CreateAsync(uim, token).ConfigureAwait(false);
            var aid = transport.GetReadySelectedUsimAid()
                ?? throw new InvalidOperationException("QMI selected GW application is not a ready USIM.");
            var channel = await transport.OpenApplicationAsync(aid, token).ConfigureAwait(false);
            if (channel == 0)
                throw new InvalidOperationException("QMI UIM could not open the selected USIM application.");
            try
            {
                var apdu = HardwareAka.BuildAuthenticateApdu(challenge.Rand, challenge.Autn);
                var cla = channel <= 3 ? checked((byte)channel) : checked((byte)(0x40 | (channel - 4)));
                apdu[0] = cla;
                byte[]? response;
                try
                {
                    response = await HardwareAka.TransmitWithChainingAsync(
                        (command, apduToken) => transport.TransmitAsync(channel, command, apduToken),
                        apdu, cla, token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(apdu); }
                if (response is null) return AkaResult.Failed("QMI USIM AUTHENTICATE returned no APDU response.");
                try { return HardwareAka.ParseAuthenticateResponse(response).ToAkaResult(); }
                finally { CryptographicOperations.ZeroMemory(response); }
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await transport.CloseApplicationAsync(channel, cleanup.Token).ConfigureAwait(false); }
                catch { /* Do not hide the AKA result or its original failure. */ }
            }
        }, ct);

    public Task<QmiImsPdn?> GetActiveImsPdnAsync(CancellationToken ct = default) =>
        QueryAsync<QmiImsPdn?>(async (client, token) =>
        {
            await using var wds = new WdsService(client);
            if (await wds.GetPacketServiceStatusAsync(token).ConfigureAwait(false) !=
                WdsPacketServiceStatus.Connected)
                return null;
            var settings = await wds.GetCurrentSettingsAsync(token).ConfigureAwait(false);
            var apn = settings.ApnName;
            if (string.IsNullOrWhiteSpace(apn) &&
                settings.ProfileType is { } profileType && settings.ProfileIndex is { } profileIndex)
            {
                var profile = await wds.GetProfileSettingsAsync(profileType, profileIndex,
                    token).ConfigureAwait(false);
                apn = profile.ApnName;
            }
            if (string.IsNullOrWhiteSpace(apn) || !HostImsPdnParser.IsImsApn(apn) ||
                settings.Ipv4Address is null || settings.Ipv4Address.Equals(IPAddress.Any))
                return null;
            var pcscf = settings.PcscfServers
                .Where(address => !address.Equals(IPAddress.Any) &&
                    address.AddressFamily == settings.Ipv4Address.AddressFamily)
                .Distinct().ToArray();
            if (pcscf.Length == 0) return null;
            return new QmiImsPdn(settings.ProfileIndex, apn, settings.Ipv4Address, pcscf);
        }, ct);

    public Task<IReadOnlyList<QmiImsProfile>> GetConfiguredImsProfilesAsync(CancellationToken ct = default) =>
        QueryAsync<IReadOnlyList<QmiImsProfile>>(async (client, token) =>
        {
            await using var wds = new WdsService(client);
            var profiles = await wds.GetProfileListAsync(profileType: 0, token).ConfigureAwait(false);
            var ims = new List<QmiImsProfile>();
            foreach (var profile in profiles.Where(profile => profile.ProfileType == 0))
            {
                var settings = await wds.GetProfileSettingsAsync(profile.ProfileType,
                    profile.ProfileIndex, token).ConfigureAwait(false);
                if (!HostImsPdnParser.IsImsApn(settings.ApnName)) continue;
                var pdpType = settings.PdpType switch
                {
                    0 => "IP",
                    1 => "PPP",
                    2 => "IPV6",
                    3 => "IPV4V6",
                    4 => "NON-IP",
                    _ => $"QMI PDP {settings.PdpType}"
                };
                ims.Add(new QmiImsProfile(profile.ProfileIndex, pdpType, settings.ApnName));
            }
            return ims;
        }, ct);

    public Task<SignalQuality?> GetSignalAsync(CancellationToken ct = default) =>
        QueryAsync<SignalQuality?>(async (client, token) =>
        {
            await using var nas = new NasService(client);
            var signal = await nas.GetSignalStrengthAsync(token).ConfigureAwait(false);
            if (signal.Rssi is < -130 or > -20) return null;
            var raw = Math.Clamp((signal.Rssi + 113) / 2, 0, 31);
            var bars = raw switch
            {
                >= 19 => 5,
                >= 14 => 4,
                >= 9 => 3,
                >= 4 => 2,
                >= 1 => 1,
                _ => 0
            };
            return new SignalQuality(raw, signal.Rssi, bars, RadioTechnology(signal.RadioInterface));
        }, ct);

    public Task<NetworkRegistration?> GetRegistrationAsync(CancellationToken ct = default) =>
        QueryAsync<NetworkRegistration?>(async (client, token) =>
        {
            await using var nas = new NasService(client);
            var system = await nas.GetServingSystemAsync(token).ConfigureAwait(false);
            var status = system.RegistrationState switch
            {
                NasRegistrationState.Registered when system.Roaming => NetworkRegStatus.Roaming,
                NasRegistrationState.Registered => NetworkRegStatus.Home,
                NasRegistrationState.NotRegistered => NetworkRegStatus.NotRegistered,
                NasRegistrationState.Searching => NetworkRegStatus.Searching,
                NasRegistrationState.RegistrationDenied => NetworkRegStatus.Denied,
                _ => NetworkRegStatus.Unknown
            };
            return status == NetworkRegStatus.Unknown ? null :
                new NetworkRegistration(status, system.PlmnName,
                    RadioTechnology(system.RadioInterface), null);
        }, ct);

    private static string RadioTechnology(byte radioInterface) => radioInterface switch
    {
        0x08 => "LTE",
        0x0C => "NR5G",
        0x05 => "UMTS",
        0x04 => "GSM",
        0x01 => "CDMA",
        0x02 => "EVDO",
        _ => "Unknown"
    };

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
