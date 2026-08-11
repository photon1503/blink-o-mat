using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Rejector.Core.Services;

public sealed class UpdateCheckService(HttpClient? httpClient = null)
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/photon1503/blink-o-mat/releases/latest";

    public const string ReleasesPageUrl = "https://github.com/photon1503/blink-o-mat/releases/latest";

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();

    public async Task<string?> GetInstallerDownloadUrlAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConfigureHttpClient();
            var release = await _httpClient.GetFromJsonAsync<GithubReleaseWithAssets>(ReleasesApiUrl, cancellationToken);
            return release?.Assets?.FirstOrDefault(asset => IsInstallerAsset(asset.Name))?.BrowserDownloadUrl;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetLatestUpdateAsync(cancellationToken);
        return info?.Version;
    }

    public async Task<UpdateInfo?> GetLatestUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConfigureHttpClient();
            var release = await _httpClient.GetFromJsonAsync<GithubReleaseWithAssets>(ReleasesApiUrl, cancellationToken);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return null;
            }

            var tagVersion = release.TagName.TrimStart('v');
            if (!Version.TryParse(tagVersion, out var latestVersion))
            {
                return null;
            }

            var currentVersion = GetCurrentVersion();
            if (currentVersion is null || latestVersion <= currentVersion)
            {
                return null;
            }

            var installerUrl = release.Assets?.FirstOrDefault(asset => IsInstallerAsset(asset.Name))?.BrowserDownloadUrl;
            if (string.IsNullOrWhiteSpace(installerUrl))
            {
                return null;
            }

            return new UpdateInfo(tagVersion, release.Body ?? string.Empty, installerUrl);
        }
        catch
        {
            return null;
        }
    }

    public async Task<UpdateInfo?> GetLatestReleaseInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConfigureHttpClient();
            var release = await _httpClient.GetFromJsonAsync<GithubReleaseWithAssets>(ReleasesApiUrl, cancellationToken);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return null;
            }

            var tagVersion = release.TagName.TrimStart('v');
            var installerUrl = release.Assets?.FirstOrDefault(asset => IsInstallerAsset(asset.Name))?.BrowserDownloadUrl
                ?? ReleasesPageUrl;

            return new UpdateInfo(tagVersion, release.Body ?? string.Empty, installerUrl);
        }
        catch
        {
            return null;
        }
    }

    private void ConfigureHttpClient()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Rejector-UpdateCheck/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(3);
    }

    private static bool IsInstallerAsset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.ToLowerInvariant();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static Version? GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        if (version is null || version == new Version(1, 0, 0, 0))
        {
            return null;
        }

        return version;
    }

    private sealed class GithubReleaseWithAssets
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public List<GithubAsset>? Assets { get; init; }
    }

    private sealed class GithubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}

public sealed record UpdateInfo(string Version, string ReleaseNotesMarkdown, string InstallerUrl);
