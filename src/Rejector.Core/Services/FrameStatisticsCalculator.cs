using Rejector.Core.Models;

namespace Rejector.Core.Services;

public sealed record FilterFrameStatistics(
    string FilterName,
    int Total,
    int Accepted,
    int Rejected,
    double AcceptedRatio,
    double TotalExposureSeconds,
    double AcceptedExposureSeconds);

public sealed record FrameStatistics(
    int Total,
    int Accepted,
    int Rejected,
    double AcceptedRatio,
    double TotalExposureSeconds,
    double AcceptedExposureSeconds,
    IReadOnlyList<FilterFrameStatistics> Filters);

public static class FrameStatisticsCalculator
{
    public static FrameStatistics Calculate(IEnumerable<ProcessedFrame> frames)
    {
        var scopedFrames = frames.ToList();
        var total = scopedFrames.Count;
        var accepted = scopedFrames.Count(frame => !frame.IsRejected);
        var totalExposureSeconds = scopedFrames.Sum(frame => frame.ExposureSeconds ?? 0);
        var acceptedExposureSeconds = scopedFrames
            .Where(frame => !frame.IsRejected)
            .Sum(frame => frame.ExposureSeconds ?? 0);

        var filters = scopedFrames
            .GroupBy(
                frame => string.IsNullOrWhiteSpace(frame.FilterName) ? "(none)" : frame.FilterName.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var filterFrames = group.ToList();
                var filterAccepted = filterFrames.Count(frame => !frame.IsRejected);
                return new FilterFrameStatistics(
                    group.Key,
                    filterFrames.Count,
                    filterAccepted,
                    filterFrames.Count - filterAccepted,
                    filterFrames.Count == 0 ? 0 : filterAccepted / (double)filterFrames.Count,
                    filterFrames.Sum(frame => frame.ExposureSeconds ?? 0),
                    filterFrames.Where(frame => !frame.IsRejected).Sum(frame => frame.ExposureSeconds ?? 0));
            })
            .ToList();

        return new FrameStatistics(
            total,
            accepted,
            total - accepted,
            total == 0 ? 0 : accepted / (double)total,
            totalExposureSeconds,
            acceptedExposureSeconds,
            filters);
    }
}