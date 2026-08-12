using Rejector.Core.Models;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class FrameStatisticsCalculatorTests
{
    [Fact]
    public void Calculate_ReportsAcceptedRejectedAndExposureTotals()
    {
        var accepted = CreateFrame("accepted", "R", 120);
        var rejected = CreateFrame("rejected", "R", 180);
        rejected.SetAutomaticRejected(true);

        var statistics = FrameStatisticsCalculator.Calculate([accepted, rejected]);

        Assert.Equal(2, statistics.Total);
        Assert.Equal(1, statistics.Accepted);
        Assert.Equal(1, statistics.Rejected);
        Assert.Equal(0.5, statistics.AcceptedRatio);
        Assert.Equal(300, statistics.TotalExposureSeconds);
        Assert.Equal(120, statistics.AcceptedExposureSeconds);
    }

    [Fact]
    public void Calculate_GroupsFiltersCaseInsensitivelyAndNormalizesBlankNames()
    {
        var statistics = FrameStatisticsCalculator.Calculate([
            CreateFrame("red-one", "R", 60),
            CreateFrame("red-two", "r", 90),
            CreateFrame("blank", " ", 30)
        ]);

        Assert.Collection(statistics.Filters,
            filter =>
            {
                Assert.Equal("(none)", filter.FilterName);
                Assert.Equal(1, filter.Total);
            },
            filter =>
            {
                Assert.Equal("R", filter.FilterName);
                Assert.Equal(2, filter.Total);
                Assert.Equal(150, filter.AcceptedExposureSeconds);
            });
    }

    [Fact]
    public void MoveRejectedSummary_GroupsRejectedFramesByFilterName()
    {
        var frames = new[]
        {
            CreateRejectedFrame("red-one", "R"),
            CreateRejectedFrame("red-two", "r"),
            CreateRejectedFrame("blank-one", " "),
            CreateRejectedFrame("blue-one", "B"),
            CreateAcceptedFrame("keep-one", "R")
        };

        var summary = new FrameMoveService().GetRejectedCountsByFilter(frames);

        Assert.Equal(2, summary["R"]);
        Assert.Equal(1, summary["B"]);
        Assert.Equal(1, summary["(no filter)"]);
    }

    private static ProcessedFrame CreateFrame(string fileName, string filterName, double exposureSeconds)
    {
        return new ProcessedFrame
        {
            FilePath = $"/tmp/{fileName}.fits",
            FileName = fileName,
            FilterName = filterName,
            ExposureSeconds = exposureSeconds,
            Metrics = new AstroMetrics()
        };
    }

    private static ProcessedFrame CreateRejectedFrame(string fileName, string filterName)
    {
        var frame = CreateFrame(fileName, filterName, 60);
        frame.SetAutomaticRejected(true);
        return frame;
    }

    private static ProcessedFrame CreateAcceptedFrame(string fileName, string filterName)
    {
        var frame = CreateFrame(fileName, filterName, 60);
        frame.SetAutomaticRejected(false);
        return frame;
    }
}