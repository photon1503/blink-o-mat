using Rejector.Core.Models;

namespace Rejector.Core.Services;

/// <summary>
/// Estimates per-frame cloud obscurance (0–100) by checking sky-background
/// invariance against time-adjacent frames of the same filter, corroborated by
/// a star-count deficit. Requires at least 3 frames per filter group.
/// </summary>
public static class CloudMetricService
{
    public static void Compute(IEnumerable<ProcessedFrame> frames)
    {
        foreach (var group in frames.GroupBy(frame => NormalizeFilterKey(frame.FilterName), StringComparer.OrdinalIgnoreCase))
        {
            var members = group
                .OrderBy(frame => frame.ExposureDateTime ?? DateTimeOffset.MaxValue)
                .ThenBy(frame => frame.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (members.Length < 3)
            {
                foreach (var frame in members)
                {
                    frame.CloudConfidence = 0;
                }

                continue;
            }

            var backgrounds = members.Select(member => member.Metrics.MeanBackground).ToArray();
            var stars = members.Select(member => (double)member.Metrics.StarCount).ToArray();

            // Robust frame-to-frame noise from consecutive differences: slow nightly
            // drift (moon, twilight) cancels out, so only sudden deviations register.
            var consecutiveDiffs = new double[backgrounds.Length - 1];
            for (var i = 0; i < consecutiveDiffs.Length; i++)
            {
                consecutiveDiffs[i] = Math.Abs(backgrounds[i + 1] - backgrounds[i]);
            }

            var noise = Median(consecutiveDiffs) * 1.4826 / Math.Sqrt(2.0);

            for (var index = 0; index < members.Length; index++)
            {
                var localBackground = LocalMedian(backgrounds, index, radius: 4);
                var sigma = Math.Max(noise, Math.Max(0.02 * Math.Abs(localBackground), 1e-6));

                var backgroundZ = Math.Abs(backgrounds[index] - localBackground) / sigma;
                var backgroundSignal = Math.Clamp((backgroundZ - 2.5) / 7.5, 0.0, 1.0);

                var localStars = LocalMedian(stars, index, radius: 4);
                var starDeficit = localStars >= 10
                    ? Math.Clamp((((localStars - stars[index]) / localStars) - 0.3) / 0.5, 0.0, 1.0)
                    : 0.0;

                // Star deficit only corroborates a background deviation; alone it is
                // ambiguous (tracking/wind elongation also suppresses detected stars).
                members[index].CloudConfidence = (int)Math.Round(100.0 * backgroundSignal * (0.7 + (0.3 * starDeficit)));
            }
        }
    }

    // Median of surrounding frames, excluding the frame itself so its own
    // deviation cannot drag the baseline toward it.
    private static double LocalMedian(double[] values, int center, int radius)
    {
        var window = new List<double>(radius * 2);
        for (var i = Math.Max(0, center - radius); i <= Math.Min(values.Length - 1, center + radius); i++)
        {
            if (i != center)
            {
                window.Add(values[i]);
            }
        }

        return Median(window.ToArray());
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
