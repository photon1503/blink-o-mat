using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using blink_o_mat.Models;

namespace blink_o_mat.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rejector");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsFilePath);
            return Normalize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions));
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
        }
    }

    internal static bool TryBackupPersistedSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return false;
            }

            Directory.CreateDirectory(SettingsDirectory);
            var backupPath = Path.Combine(
                SettingsDirectory,
                $"settings.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
            File.Move(SettingsFilePath, backupPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AppSettings Normalize(AppSettings? settings)
    {
        var normalized = settings ?? new AppSettings();
        var profiles = (normalized.Profiles ?? [])
            .Select(NormalizeProfile)
            .GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (profiles.Count == 0)
        {
            profiles.Add(new SettingsProfile { Name = "Default" });
        }

        normalized.Profiles = profiles;
        normalized.DefaultProfileName = SettingsProfile.NormalizeName(normalized.DefaultProfileName);
        if (!normalized.Profiles.Any(profile => string.Equals(profile.Name, normalized.DefaultProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            normalized.DefaultProfileName = normalized.Profiles[0].Name;
        }

        return normalized;
    }

    private static SettingsProfile NormalizeProfile(SettingsProfile? profile)
    {
        var normalized = profile ?? new SettingsProfile();
        normalized.Name = SettingsProfile.NormalizeName(normalized.Name);
        normalized.Thresholds ??= new Thresholds();
        normalized.FilterThresholds = (normalized.FilterThresholds ?? [])
            .Select(NormalizeProfileFilterThresholds)
            .GroupBy(filterThresholds => filterThresholds.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return normalized;
    }

    private static ProfileFilterThresholds NormalizeProfileFilterThresholds(ProfileFilterThresholds? filterThresholds)
    {
        var normalized = filterThresholds ?? new ProfileFilterThresholds();
        normalized.Key = normalized.Key?.Trim() ?? string.Empty;
        normalized.Thresholds ??= new Thresholds();
        return normalized;
    }
}

public sealed class AppSettings
{
    public string? InputFolder { get; set; }

    public string? RejectedFolder { get; set; }

    public bool IncludeSubfolders { get; set; }

    public bool WatchFolder { get; set; }

    public string DefaultProfileName { get; set; } = "Default";

    public List<SettingsProfile> Profiles { get; set; } =
    [
        new SettingsProfile
        {
            Name = "Default"
        }
    ];
}
