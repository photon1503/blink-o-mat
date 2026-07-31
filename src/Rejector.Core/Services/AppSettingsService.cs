using System.Text.Json;
using Rejector.Core.Abstractions;
using Rejector.Core.Models;

namespace Rejector.Core.Services;

public sealed class AppSettingsService
{
    private const string DefaultApplicationName = "Rejector";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly IAppDataPathProvider _pathProvider;
    private readonly string _applicationName;

    public AppSettingsService(IAppDataPathProvider? pathProvider = null, string applicationName = DefaultApplicationName)
    {
        _pathProvider = pathProvider ?? new DefaultAppDataPathProvider();
        _applicationName = applicationName;
    }

    public string SettingsDirectoryPath => _pathProvider.GetApplicationDataDirectory(_applicationName);

    public string SettingsFilePath => Path.Combine(SettingsDirectoryPath, "settings.json");

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
            Directory.CreateDirectory(SettingsDirectoryPath);
            var json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
        }
    }

    public bool TryBackupPersistedSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return false;
            }

            Directory.CreateDirectory(SettingsDirectoryPath);
            var backupPath = Path.Combine(SettingsDirectoryPath, $"settings.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
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