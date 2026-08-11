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
    public void OverrideThresholds_ReplacesProfileThresholdsAndClearsFilterOverrides()
    {
        var profile = new SettingsProfile
        {
            Thresholds = new Thresholds { MaxFwhm = 3.5 },
            FilterThresholds =
            [
                new ProfileFilterThresholds
                {
                    Key = "Ha",
                    Thresholds = new Thresholds { MaxFwhm = 2.5 },
                },
            ],
        };
        var replacement = new Thresholds { MaxFwhm = 6.5 };

        profile.OverrideThresholds(replacement);
        replacement.MaxFwhm = 9.5;

        Assert.Equal(6.5, profile.Thresholds.MaxFwhm);
        Assert.Empty(profile.FilterThresholds);
    }

    [Fact]
    public void GetOrCreateFilterThresholds_ClonesGlobalAndResolvesCaseInsensitively()
    {
        var profile = new SettingsProfile
        {
            Thresholds = new Thresholds { MaxFwhm = 6.5 },
        };

        var filterThresholds = profile.GetOrCreateFilterThresholds(" Ha ");
        filterThresholds.MaxFwhm = 3.25;

        Assert.Equal(6.5, profile.Thresholds.MaxFwhm);
        Assert.Equal(3.25, profile.GetThresholdsForFilter("ha").MaxFwhm);
        Assert.Equal(6.5, profile.GetThresholdsForFilter("OIII").MaxFwhm);
        Assert.Single(profile.FilterThresholds);
    }

    [Fact]
    public void SaveAndLoad_PreservesProfileToggleThresholdAndScoreState()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var service = new AppSettingsService(new FixedPathProvider(tempRoot), "Rejector.Tests");
            service.Save(new AppSettings
            {
                Profiles =
                [
                    new SettingsProfile
                    {
                        Name = "Imaging",
                        Thresholds = new Thresholds
                        {
                            MaxFwhm = 4.25,
                            AutoCalcFwhmThreshold = false,
                        },
                        FilterThresholds =
                        [
                            new ProfileFilterThresholds
                            {
                                Key = "Ha",
                                Thresholds = new Thresholds
                                {
                                    MaxFwhm = 2.75,
                                    AutoCalcFwhmThreshold = false,
                                },
                            },
                        ],
                        ShowFwhmMetric = false,
                        ShowTrailMetric = false,
                        ShowSkyTempMetric = false,
                        ShowMeanBackgroundMetric = false,
                        ShowScoreMetric = false,
                        UseScoreFwhm = false,
                        ScoreWeightTrail = 4.25,
                    },
                ],
                DefaultProfileName = "Imaging",
            });

            var loaded = service.Load().Profiles.Single();

            Assert.Equal(4.25, loaded.Thresholds.MaxFwhm);
            Assert.False(loaded.Thresholds.AutoCalcFwhmThreshold);
            Assert.Equal(2.75, loaded.GetThresholdsForFilter("ha").MaxFwhm);
            Assert.False(loaded.GetThresholdsForFilter("ha").AutoCalcFwhmThreshold);
            Assert.False(loaded.ShowFwhmMetric);
            Assert.False(loaded.ShowTrailMetric);
            Assert.False(loaded.ShowSkyTempMetric);
            Assert.False(loaded.ShowMeanBackgroundMetric);
            Assert.False(loaded.ShowScoreMetric);
            Assert.False(loaded.UseScoreFwhm);
            Assert.Equal(4.25, loaded.ScoreWeightTrail);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void SaveAndLoad_PreservesSliderVisibilityAndPreviewOverlayState()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var service = new AppSettingsService(new FixedPathProvider(tempRoot), "Rejector.Tests");
            service.Save(new AppSettings
            {
                Profiles =
                [
                    new SettingsProfile
                    {
                        Name = "Imaging",
                        ShowTrailSlider = false,
                        ShowFwhmSlider = false,
                        ShowMeanBackgroundSlider = false,
                        ShowScoreSlider = false,
                        IsRoiOverlayVisible = false,
                        IsStarDebugOverlayVisible = true,
                        IsOrientationDebugOverlayVisible = true,
                        IsCurvatureViewVisible = true,
                    },
                ],
            });

            var loaded = service.Load().Profiles.Single();

            Assert.False(loaded.ShowTrailSlider);
            Assert.False(loaded.ShowFwhmSlider);
            Assert.False(loaded.ShowMeanBackgroundSlider);
            Assert.False(loaded.ShowScoreSlider);
            Assert.False(loaded.IsRoiOverlayVisible);
            Assert.True(loaded.IsStarDebugOverlayVisible);
            Assert.True(loaded.IsOrientationDebugOverlayVisible);
            Assert.True(loaded.IsCurvatureViewVisible);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
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