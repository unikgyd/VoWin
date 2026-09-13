using System.Net.Http.Headers;
using System.Net.Http;
using System.Text.Json;

namespace VoWin.Services;

internal sealed class GitHubReleaseUpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/unikgyd/VoWin/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    public async Task<ReleaseUpdateInfo?> CheckAsync(Version installedVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = release.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || !TryParseVersion(tag.GetString(), out var latestVersion)) return null;
        if (latestVersion <= Normalize(installedVersion)) return null;

        var pageUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;
        return new ReleaseUpdateInfo(latestVersion, pageUrl);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoWin-Update-Check/1.0");
        return client;
    }

    private static bool TryParseVersion(string? tag, out Version version)
    {
        var normalized = (tag ?? string.Empty).Trim().TrimStart('v', 'V');
        if (!Version.TryParse(normalized, out var parsed))
        {
            version = new Version(0, 0, 0);
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));
}

internal sealed record ReleaseUpdateInfo(Version Version, string ReleasePageUrl);
