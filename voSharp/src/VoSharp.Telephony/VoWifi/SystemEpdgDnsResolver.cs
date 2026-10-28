using System.Net;
using System.Net.Sockets;

namespace VoSharp.Telephony.VoWifi;

public sealed record EpdgDnsFamilyResult(string Family, IPAddress[] Addresses, string Status);

/// <summary>Independent bounded A/AAAA queries; one broken family cannot discard the other.</summary>
internal sealed class SystemEpdgDnsResolver : IEpdgDnsResolver
{
    private readonly Func<string, ushort, CancellationToken, Task<EpdgDnsFamilyResult>> _query;
    private readonly TimeSpan _familyTimeout;
    internal IReadOnlyList<EpdgDnsFamilyResult> Results { get; private set; } = [];
    internal string Name => OperatingSystem.IsWindows() ? "windows-configured-dns/fresh-A+AAAA" : "system-dns/A+AAAA";
    internal string Detail => string.Join(", ", Results.Select(result => $"{result.Family}={result.Status}/{result.Addresses.Length}"));

    internal SystemEpdgDnsResolver(
        Func<string, ushort, CancellationToken, Task<EpdgDnsFamilyResult>>? query = null, TimeSpan? familyTimeout = null)
    {
        _query = query ?? QueryAsync;
        _familyTimeout = familyTimeout ?? TimeSpan.FromSeconds(6);
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Results = await Task.WhenAll(QueryFamilyAsync(host, 1, ct), QueryFamilyAsync(host, 28, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var addresses = Results.SelectMany(result => result.Addresses).Distinct().ToArray();
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        return addresses;
    }

    private async Task<EpdgDnsFamilyResult> QueryFamilyAsync(string host, ushort type, CancellationToken ct)
    {
        var family = type == 1 ? "A" : "AAAA";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_familyTimeout);
        try { return await _query(host, type, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(family, [], "timeout"); }
        catch (SocketException ex) { return new(family, [], $"dns-{ex.SocketErrorCode}"); }
    }

    private static async Task<EpdgDnsFamilyResult> QueryAsync(string host, ushort type, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return await WindowsEpdgDnsQuery.QueryAsync(host, type, ct).ConfigureAwait(false);
        var addresses = await Dns.GetHostAddressesAsync(host,
            type == 1 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, ct).ConfigureAwait(false);
        return new(type == 1 ? "A" : "AAAA", addresses, addresses.Length > 0 ? "resolved" : "no-data");
    }
}
