using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace blink_o_mat.Services;

public sealed class UpdateCheckService
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/photon1503/blink-o-mat/releases/latest";

    public const string ReleasesPageUrl =
        "https://github.com/photon1503/blink-o-mat/releases/latest";

    /// <summary>
    /// Returns the browser download URL of the first .exe asset in the latest release,
    /// or null if none is found or the request fails.
    /// </summary>
    public async Task<string?> GetInstallerDownloadUrlAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Rejector-UpdateCheck/1.0");
            http.Timeout = TimeSpan.FromSeconds(3);

            var release = await http.GetFromJsonAsync<GithubReleaseWithAssets>(
                ReleasesApiUrl, cancellationToken);

            return release?.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                ?.BrowserDownloadUrl;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the latest release tag (e.g. "1.0.10") when a newer version is available,
    /// or null when the current version is up to date or the check fails.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetLatestUpdateAsync(cancellationToken);
        return info?.Version;
    }

    /// <summary>
    /// Returns information about the latest release when a newer version with a
    /// ready-to-download installer is available. Returns null when:
    /// - the current version is up to date,
    /// - the release does not (yet) contain an .exe asset (build still in progress),
    /// - or the request fails.
    /// </summary>
    public async Task<UpdateInfo?> GetLatestUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Rejector-UpdateCheck/1.0");
            http.Timeout = TimeSpan.FromSeconds(3);

            var release = await http.GetFromJsonAsync<GithubReleaseWithAssets>(
                ReleasesApiUrl, cancellationToken);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            // Strip optional leading 'v' so "v1.0.10" and "1.0.10" both parse.
            var tagVersion = release.TagName.TrimStart('v');

            if (!Version.TryParse(tagVersion, out var latestVersion))
                return null;

            var currentVersion = GetCurrentVersion();
            if (currentVersion is null || latestVersion <= currentVersion)
                return null;

            // Don't surface the update until the installer asset is actually published.
            // GitHub creates the release as soon as the tag is pushed; the .exe is uploaded
            // after the CI build completes, which can take a few minutes.
            var installerUrl = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                ?.BrowserDownloadUrl;

            if (string.IsNullOrEmpty(installerUrl))
                return null;

            return new UpdateInfo(tagVersion, release.Body ?? string.Empty, installerUrl);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the latest GitHub release notes and version regardless of whether it is newer
    /// than the current app version. Intended for debug preview paths.
    /// </summary>
    public async Task<UpdateInfo?> GetLatestReleaseInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Rejector-UpdateCheck/1.0");
            http.Timeout = TimeSpan.FromSeconds(3);

            var release = await http.GetFromJsonAsync<GithubReleaseWithAssets>(
                ReleasesApiUrl, cancellationToken);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            var tagVersion = release.TagName.TrimStart('v');
            var installerUrl = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                ?.BrowserDownloadUrl
                ?? ReleasesPageUrl;

            return new UpdateInfo(tagVersion, release.Body ?? string.Empty, installerUrl);
        }
        catch
        {
            return null;
        }
    }

    private static Version? GetCurrentVersion()
    {
        // Version is stamped by the CI pipeline via /p:Version=x.y.z
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        // Default 1.0.0.0 means "not stamped" — treat as unknown, skip the check.
        if (v is null || v == new Version(1, 0, 0, 0))
            return null;
        return v;
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
