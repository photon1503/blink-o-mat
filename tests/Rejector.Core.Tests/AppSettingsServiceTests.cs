using Rejector.Core.Abstractions;
using Rejector.Core.Models;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_NormalizesProfilesAndDefaultProfile()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var service = new AppSettingsService(new FixedPathProvider(tempRoot), "Rejector.Tests");
            var settings = new AppSettings
            {
                DefaultProfileName = "Missing",
                Profiles =
                [
                    new SettingsProfile { Name = "  " },
                    new SettingsProfile { Name = "default" },
                ],
            };

            service.Save(settings);

            var loaded = service.Load();

            Assert.Single(loaded.Profiles);
            Assert.Equal("Default", loaded.Profiles[0].Name);
            Assert.Equal("Default", loaded.DefaultProfileName);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Clone_CreatesIndependentSnapshotOfProfileState()
    {
        var original = new SettingsProfile
        {
            Name = "Custom",
            Thresholds = new Thresholds
            {
                MaxFwhm = 3.5,
                MaxFwhmArcsec = 2.2,
                MinSqm = 18.5,
                AutoCalcFwhmThreshold = false,
            },
            IncludeSubfoldersDefault = true,
            WatchFolderDefault = true,
            StfTargetBackgroundDefault = 0.33,
            ShowTrailMetric = false,
            UseScoreFwhm = false,
            ScoreWeightFwhm = 4.5,
        };

        var clone = original.Clone();

        clone.Name = "Clone";
        clone.Thresholds.MaxFwhm = 9.9;
        clone.IncludeSubfoldersDefault = false;
        clone.ShowTrailMetric = true;
        clone.UseScoreFwhm = true;
        clone.ScoreWeightFwhm = 1.2;

        Assert.Equal("Custom", original.Name);
        Assert.Equal(3.5, original.Thresholds.MaxFwhm);
        Assert.True(original.IncludeSubfoldersDefault);
        Assert.False(original.ShowTrailMetric);
        Assert.False(original.UseScoreFwhm);
        Assert.Equal(4.5, original.ScoreWeightFwhm);
        Assert.Equal(0.33, original.StfTargetBackgroundDefault);
    }

    [Fact]
    public void TryBackupPersistedSettings_MovesExistingSettingsFile()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var service = new AppSettingsService(new FixedPathProvider(tempRoot), "Rejector.Tests");
            service.Save(new AppSettings());

            var backedUp = service.TryBackupPersistedSettings();

            Assert.True(backedUp);
            Assert.False(File.Exists(service.SettingsFilePath));
            Assert.Single(Directory.GetFiles(service.SettingsDirectoryPath, "settings.corrupt-*.json"));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Rejector.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedPathProvider(string rootPath) : IAppDataPathProvider
    {
        public string GetApplicationDataDirectory(string applicationName)
        {
            return Path.Combine(rootPath, applicationName);
        }
    }
}