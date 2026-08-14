using Rejector.Core.Models;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class FrameSortComparerTests
{
    [Fact]
    public void Compare_UsesSecondaryRule_WhenPrimaryRuleTies()
    {
        var frames = new List<ProcessedFrame>
        {
            CreateFrame("alpha", fwhm: 2.0, hfr: 3.0),
            CreateFrame("bravo", fwhm: 1.0, hfr: 2.5),
            CreateFrame("charlie", fwhm: 1.0, hfr: 3.5),
        };

        var comparer = new FrameSortComparer([
            new FrameSortRule("FWHM", IsAscending: true),
            new FrameSortRule("HFR", IsAscending: false)
        ]);

        var ordered = frames.OrderBy(frame => frame, comparer).ToList();

        Assert.Collection(ordered,
            frame => Assert.Equal("charlie", frame.FileName),
            frame => Assert.Equal("bravo", frame.FileName),
            frame => Assert.Equal("alpha", frame.FileName));
    }

    [Fact]
    public void Compare_FallsBackToFileName_WhenEveryRuleTies()
    {
        var frames = new List<ProcessedFrame>
        {
            CreateFrame("zeta", fwhm: 1.0, hfr: 2.0),
            CreateFrame("alpha", fwhm: 1.0, hfr: 2.0),
            CreateFrame("mike", fwhm: 1.0, hfr: 2.0),
        };

        var comparer = new FrameSortComparer([
            new FrameSortRule("FWHM", IsAscending: true),
            new FrameSortRule("HFR", IsAscending: true)
        ]);

        var ordered = frames.OrderBy(frame => frame, comparer).ToList();

        Assert.Collection(ordered,
            frame => Assert.Equal("alpha", frame.FileName),
            frame => Assert.Equal("mike", frame.FileName),
            frame => Assert.Equal("zeta", frame.FileName));
    }

    [Fact]
    public void Compare_KeepsMissingNullableValuesLast_WhenDescending()
    {
        var frames = new List<ProcessedFrame>
        {
            CreateFrame("missing", fwhm: 1.0, hfr: 2.0),
            CreateFrame("newer", fwhm: 1.0, hfr: 2.0, observationDate: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            CreateFrame("older", fwhm: 1.0, hfr: 2.0, observationDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        var comparer = new FrameSortComparer([
            new FrameSortRule("Observation date", IsAscending: false)
        ]);

        var ordered = frames.OrderBy(frame => frame, comparer).ToList();

        Assert.Collection(ordered,
            frame => Assert.Equal("newer", frame.FileName),
            frame => Assert.Equal("older", frame.FileName),
            frame => Assert.Equal("missing", frame.FileName));
    }

    [Fact]
    public void Compare_SortsCloudConfidenceAndClarifiedPixelValueFields()
    {
        var frames = new List<ProcessedFrame>
        {
            CreateFrame("clear", fwhm: 1.0, hfr: 2.0, min: 500),
            CreateFrame("cloudy", fwhm: 1.0, hfr: 2.0, min: 100),
        };
        frames[0].CloudConfidence = 10;
        frames[1].CloudConfidence = 85;

        var cloudComparer = new FrameSortComparer([new FrameSortRule("Cloud confidence", IsAscending: false)]);
        var cloudOrdered = frames.OrderBy(frame => frame, cloudComparer).ToList();
        Assert.Equal("cloudy", cloudOrdered[0].FileName);

        var minimumComparer = new FrameSortComparer([new FrameSortRule("Minimum pixel value", IsAscending: true)]);
        var minimumOrdered = frames.OrderBy(frame => frame, minimumComparer).ToList();
        Assert.Equal("cloudy", minimumOrdered[0].FileName);
    }

    private static ProcessedFrame CreateFrame(
        string fileName,
        double fwhm,
        double hfr,
        DateTimeOffset? observationDate = null,
        double min = 0)
    {
        return new ProcessedFrame
        {
            FilePath = $"/tmp/{fileName}.fits",
            FileName = fileName,
            ExposureDateTime = observationDate,
            Metrics = new AstroMetrics
            {
                Fwhm = fwhm,
                Hfr = hfr,
                Min = min,
            }
        };
    }
}
