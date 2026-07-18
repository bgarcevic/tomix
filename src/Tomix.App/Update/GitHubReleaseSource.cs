using System.Text.Json;
using Tomix.Core.Update;

namespace Tomix.App.Update;

/// <summary>
/// Reads CLI releases from the GitHub Releases API. Metadata comes from
/// <c>api.github.com</c> (unauthenticated, 60 req/h — plenty at the 24h check TTL);
/// asset and checksum downloads use the unmetered <c>github.com/.../releases/download</c>
/// URLs, matching install.sh exactly.
/// </summary>
public sealed class GitHubReleaseSource : IReleaseSource
{
    private const string Owner = "bgarcevic";
    private const string Repo = "tomix-cli";
    private const string ApiBase = $"https://api.github.com/repos/{Owner}/{Repo}";
    private const string DownloadBase = $"https://github.com/{Owner}/{Repo}/releases/download";

    private readonly HttpClient _httpClient;

    public GitHubReleaseSource(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        // /releases/latest already excludes drafts and prereleases.
        using var document = await GetJsonAsync($"{ApiBase}/releases/latest", cancellationToken).ConfigureAwait(false);
        return document is null ? null : ParseRelease(document.RootElement);
    }

    public async Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"{ApiBase}/releases?per_page=100", cancellationToken).ConfigureAwait(false);
        if (document is null)
            return [];

        var releases = new List<ReleaseInfo>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;

            var release = ParseRelease(element);
            if (release is not null)
                releases.Add(release);
        }

        return releases;
    }

    public async Task<byte[]> DownloadAssetAsync(string version, string assetName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync($"{DownloadBase}/v{version}/{assetName}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadChecksumsAsync(string version, CancellationToken cancellationToken)
    {
        using var response = await SendAsync($"{DownloadBase}/v{version}/checksums.txt", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    private Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Per-request headers: the HttpClient is shared with the Power BI catalog, so
        // mutating DefaultRequestHeaders here would leak into unrelated calls.
        request.Headers.UserAgent.ParseAdd("tomix-cli");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return _httpClient.SendAsync(request, cancellationToken);
    }

    private static ReleaseInfo? ParseRelease(JsonElement element)
    {
        if (!element.TryGetProperty("tag_name", out var tagProperty))
            return null;

        var tag = tagProperty.GetString();
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var version = tag.StartsWith('v') ? tag[1..] : tag;
        var name = element.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        var body = element.TryGetProperty("body", out var bodyProperty) ? bodyProperty.GetString() : null;
        var prerelease = element.TryGetProperty("prerelease", out var preProperty) && preProperty.GetBoolean();
        DateTimeOffset? publishedAt = element.TryGetProperty("published_at", out var publishedProperty)
            && publishedProperty.ValueKind == JsonValueKind.String
            && publishedProperty.TryGetDateTimeOffset(out var published)
                ? published
                : null;

        return new ReleaseInfo(version, name, body, publishedAt, prerelease);
    }
}
