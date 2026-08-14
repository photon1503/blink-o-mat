using Rejector.Core.Models;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class CloudMetricServiceTests
{
    [Fact]
    public void Compute_FlagsBackgroundOutlierWithinFilterGroup()
    {
        var frames = new[]
        {
            CreateFrame("l1.fit", "L", 400, 60),
            CreateFrame("l2.fit", "L", 405, 62),
            CreateFrame("l3.fit", "L", 398, 58),
            CreateFrame("l4.fit", "L", 402, 61),
            CreateFrame("l5.fit", "L", 396, 59),
            CreateFrame("cloudy.fit", "L", 1200, 12),
        };

        CloudMetricService.Compute(frames);

        Assert.True(frames[^1].CloudConfidence >= 60, $"Expected high cloud confidence for outlier, got {frames[^1].CloudConfidence}");
        Assert.All(frames[..^1], frame => Assert.True(frame.CloudConfidence <= 10, $"{frame.FileName} unexpectedly flagged with {frame.CloudConfidence}"));
    }

    [Fact]
    public void Compute_StableGroup_YieldsZeroConfidence()
    {
        var frames = new[]
        {
            CreateFrame("l1.fit", "L", 400, 60),
            CreateFrame("l2.fit", "L", 410, 63),
            CreateFrame("l3.fit", "L", 395, 57),
            CreateFrame("l4.fit", "L", 405, 61),
        };

        CloudMetricService.Compute(frames);

        Assert.All(frames, frame => Assert.Equal(0, frame.CloudConfidence));
    }

    [Fact]
    public void Compute_EvaluatesEachFilterGroupIndependently()
    {
        var frames = new[]
        {
            CreateFrame("ha1.fit", "Ha", 2000, 30),
            CreateFrame("ha2.fit", "Ha", 2004, 31),
            CreateFrame("ha3.fit", "Ha", 1996, 29),
            CreateFrame("l1.fit", "L", 400, 60),
            CreateFrame("l2.fit", "L", 404, 62),
            CreateFrame("l3.fit", "L", 396, 58),
        };

        CloudMetricService.Compute(frames);

        Assert.All(frames, frame => Assert.Equal(0, frame.CloudConfidence));
    }

    [Fact]
    public void Compute_GroupsSmallerThanThreeFrames_AreNeverFlagged()
    {
        var frames = new[]
        {
            CreateFrame("l1.fit", "L", 400, 60),
            CreateFrame("l2.fit", "L", 4000, 5),
        };

        CloudMetricService.Compute(frames);

        Assert.All(frames, frame => Assert.Equal(0, frame.CloudConfidence));
    }

    [Fact]
    public void Compute_StarLossWithStableBackground_IsNotFlaggedAsClouds()
    {
        // Tracking/wind elongation suppresses detected stars but leaves the sky background invariant.
        var frames = new[]
        {
            CreateFrame("l1.fit", "L", 400, 60),
            CreateFrame("l2.fit", "L", 405, 62),
            CreateFrame("l3.fit", "L", 398, 58),
            CreateFrame("l4.fit", "L", 402, 61),
            CreateFrame("elongated.fit", "L", 401, 8),
        };

        CloudMetricService.Compute(frames);

        Assert.Equal(0, frames[^1].CloudConfidence);
    }

    [Fact]
    public void ShouldReject_UsesCloudConfidenceThreshold()
    {
        var service = new FrameRejectionService();
        var frame = CreateFrame("cloudy.fit", "L", 1200, 10);
        frame.CloudConfidence = 85;

        var enabled = new Thresholds { MinCloudConfidence = 80 };
        var disabled = new Thresholds { MinCloudConfidence = 0 };

        Assert.True(service.ShouldReject(frame, enabled));
        Assert.Contains(service.GetRejectionReasons(frame, enabled), reason => reason.Contains("Cloud"));
        Assert.False(service.ShouldReject(frame, disabled));
    }

    private static ProcessedFrame CreateFrame(string fileName, string filterName, double meanBackground, int starCount)
    {
        return new ProcessedFrame
        {
            FilePath = fileName,
            FileName = fileName,
            FilterName = filterName,
            Metrics = new AstroMetrics
            {
                MeanBackground = meanBackground,
                StarCount = starCount,
            },
        };
    }
}
