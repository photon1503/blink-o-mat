using Rejector.Core.Models;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class FrameRejectionServiceTests
{
    [Fact]
    public void RevalidateAll_AppliesThresholdsAcrossDifferentFilters()
    {
        var service = new FrameRejectionService();
        var frames = new[]
        {
            CreateFrame("red.fit", "R", 5.0),
            CreateFrame("green.fit", "G", 4.0),
        };
        frames[1].SetAutomaticRejected(true);

        service.RevalidateAll(frames, new Thresholds { MaxFwhm = 4.5 });

        Assert.True(frames[0].AutomaticRejected);
        Assert.False(frames[1].AutomaticRejected);
    }

    [Fact]
    public void RevalidateAll_UsesFilterThresholdAndGlobalFallbackForEveryFrame()
    {
        var service = new FrameRejectionService();
        var frames = new[]
        {
            CreateFrame("red.fit", "R", 5.0),
            CreateFrame("green.fit", "G", 5.0),
            CreateFrame("unfiltered.fit", string.Empty, 5.0),
        };
        var filterThresholds = new Dictionary<string, Thresholds>(StringComparer.OrdinalIgnoreCase)
        {
            ["R"] = new Thresholds { MaxFwhm = 4.0 },
            ["(no filter)"] = new Thresholds { MaxFwhm = 4.0 },
        };

        service.RevalidateAll(frames, new Thresholds { MaxFwhm = 6.0 }, filterThresholds);

        Assert.True(frames[0].AutomaticRejected);
        Assert.False(frames[1].AutomaticRejected);
        Assert.True(frames[2].AutomaticRejected);
    }

    [Fact]
    public void CreatePermissive_UsesObservedWorstValuesAndAcceptsEveryFrame()
    {
        var service = new FrameRejectionService();
        var frames = new[]
        {
            new ProcessedFrame
            {
                FilePath = "best.fit",
                FileName = "best.fit",
                Metrics = new AstroMetrics
                {
                    Fwhm = 2,
                    FwhmArcsec = 1.5,
                    Sqm = 21,
                    SkyTemp = -10,
                    Hfr = 1,
                    Eccentricity = 0.2,
                    MeanBackground = 100,
                    StarCount = 500,
                    SatelliteTrailConfidence = 0,
                },
                OverallScore = 4.5,
            },
            new ProcessedFrame
            {
                FilePath = "worst.fit",
                FileName = "worst.fit",
                Metrics = new AstroMetrics
                {
                    Fwhm = 12,
                    FwhmArcsec = 8.5,
                    Sqm = 17,
                    SkyTemp = 12,
                    Hfr = 7,
                    Eccentricity = 0.9,
                    MeanBackground = 8_000,
                    StarCount = 20,
                    SatelliteTrailConfidence = 100,
                },
                OverallScore = 1.2,
            },
        };

        var thresholds = Thresholds.CreatePermissive(frames);

        Assert.Equal(12, thresholds.MaxFwhm);
        Assert.Equal(8.5, thresholds.MaxFwhmArcsec);
        Assert.Equal(17, thresholds.MinSqm);
        Assert.Equal(12, thresholds.MaxSkyTemp);
        Assert.Equal(7, thresholds.MaxHfr);
        Assert.Equal(0.9, thresholds.MaxEccentricity);
        Assert.Equal(8_000, thresholds.MaxMeanBackground);
        Assert.Equal(20, thresholds.MinStars);
        Assert.Equal(0, thresholds.MinSatelliteConfidence);
        Assert.Equal(1.2, thresholds.MinScore);
        Assert.All(frames, frame => Assert.False(service.ShouldReject(frame, thresholds)));
    }

    private static ProcessedFrame CreateFrame(string fileName, string filterName, double fwhm)
    {
        return new ProcessedFrame
        {
            FilePath = fileName,
            FileName = fileName,
            FilterName = filterName,
            Metrics = new AstroMetrics
            {
                Fwhm = fwhm,
                Hfr = 1.0,
                Eccentricity = 0.2,
                MeanBackground = 100,
                StarCount = 100,
            },
        };
    }
}