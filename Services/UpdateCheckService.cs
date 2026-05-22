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
    /// Returns the latest release tag (e.g. "1.0.10") when a newer version is available,
    /// or null when the current version is up to date or the check fails.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Rejector-UpdateCheck/1.0");
            http.Timeout = TimeSpan.FromSeconds(10);

            var release = await http.GetFromJsonAsync<GithubRelease>(
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

            return tagVersion;
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

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }
    }
}
