using Rejector.Core.Models;

namespace Rejector.Core.Services;

/// <summary>
/// Estimates per-frame cloud obscurance (0–100) by checking sky-background
/// invariance against the frame's own filter group, corroborated by a
/// star-count deficit. Requires at least 3 frames per filter group.
/// </summary>
public static class CloudMetricService
{
    public static void Compute(IEnumerable<ProcessedFrame> frames)
    {
        foreach (var group in frames.GroupBy(frame => NormalizeFilterKey(frame.FilterName), StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToArray();
            if (members.Length < 3)
            {
                foreach (var frame in members)
                {
                    frame.CloudConfidence = 0;
                }

                continue;
            }

            var backgrounds = members.Select(member => member.Metrics.MeanBackground).ToArray();
            var medianBackground = Median(backgrounds);
            var mad = Median(backgrounds.Select(value => Math.Abs(value - medianBackground)).ToArray());

            // Relative floor keeps natural night-sky drift from flagging stable sets.
            var sigma = Math.Max(1.4826 * mad, Math.Max(0.02 * Math.Abs(medianBackground), 1e-6));

            var medianStars = Median(members.Select(member => (double)member.Metrics.StarCount).ToArray());

            foreach (var frame in members)
            {
                var backgroundZ = Math.Abs(frame.Metrics.MeanBackground - medianBackground) / sigma;
                var backgroundSignal = Math.Clamp((backgroundZ - 2.5) / 7.5, 0.0, 1.0);

                var starDeficit = medianStars >= 10
                    ? Math.Clamp((((medianStars - frame.Metrics.StarCount) / medianStars) - 0.3) / 0.5, 0.0, 1.0)
                    : 0.0;

                frame.CloudConfidence = (int)Math.Round(100.0 * Math.Clamp((0.7 * backgroundSignal) + (0.3 * starDeficit), 0.0, 1.0));
            }
        }
    }

    private static string NormalizeFilterKey(string? filterName)
    {
        return string.IsNullOrWhiteSpace(filterName) ? "(no filter)" : filterName.Trim();
    }

    private static double Median(double[] values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) * 0.5;
    }
}
