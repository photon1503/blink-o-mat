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