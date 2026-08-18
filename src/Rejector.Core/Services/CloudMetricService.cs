using Rejector.Core.Models;

namespace Rejector.Core.Services;

/// <summary>
/// Estimates per-frame cloud obscurance (0–100) from sky-background invariance,
/// corroborated by a star-count deficit. Combines a time-local signal (brief
/// events under nightly drift) with a clear-sky-baseline signal (sustained
/// cloud banks). Requires at least 3 frames per filter group.
/// </summary>
public static class CloudMetricService
{
    public static void Compute(IEnumerable<ProcessedFrame> frames)
    {
        foreach (var group in frames.GroupBy(frame => FrameGroupKey.Create(frame), StringComparer.OrdinalIgnoreCase))
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

            // Clear-sky reference: the darkest fifth of the night approximates the
            // cloud-free background; the best-populated frames anchor the star count.
            var clearBackground = Math.Max(Percentile(backgrounds, 0.2), 1e-6);
            var clearStars = Percentile(stars, 0.8);

            for (var index = 0; index < members.Length; index++)
            {
                // One-sided baselines: a frame at a clear/cloud regime boundary agrees
                // with its own side, while a genuine brief event deviates from both.
                var (deviation, localBackground) = MinSidedDeviation(backgrounds, index, radius: 4);
                var sigma = Math.Max(noise, Math.Max(0.02 * Math.Abs(localBackground), 1e-6));

                var backgroundZ = deviation / sigma;
                var backgroundSignal = Math.Clamp((backgroundZ - 2.5) / 7.5, 0.0, 1.0);

                var localStars = LocalMedian(stars, index, radius: 4);
                var starDeficit = localStars >= 10
                    ? Math.Clamp((((localStars - stars[index]) / localStars) - 0.3) / 0.5, 0.0, 1.0)
                    : 0.0;

                // Star deficit only corroborates a background deviation; alone it is
                // ambiguous (tracking/wind elongation also suppresses detected stars).
                var localConfidence = (int)Math.Round(100.0 * backgroundSignal * (0.7 + (0.3 * starDeficit)));

                // Sustained cloud banks defeat the local baseline (their neighbours are
                // cloudy too), so also compare against the clear-sky reference. The star
                // deficit is REQUIRED here: moonrise can lift the background alone, but
                // clouds always take the stars with it.
                var elevationSignal = Math.Clamp(((backgrounds[index] / clearBackground) - 1.3) / 1.2, 0.0, 1.0);
                var globalDeficit = clearStars >= 10
                    ? Math.Clamp((((clearStars - stars[index]) / clearStars) - 0.3) / 0.5, 0.0, 1.0)
                    : 0.0;
                var globalConfidence = (int)Math.Round(100.0 * elevationSignal * globalDeficit);

                members[index].CloudConfidence = Math.Max(localConfidence, globalConfidence);
            }
        }
    }

    private static double Percentile(double[] values, double percentile)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    private static (double Deviation, double Baseline) MinSidedDeviation(double[] values, int center, int radius)
    {
        var value = values[center];
        double? best = null;
        var baseline = value;

        Span<(int Start, int End)> sides =
        [
            (Math.Max(0, center - radius), center - 1),
            (center + 1, Math.Min(values.Length - 1, center + radius)),
        ];

        foreach (var (start, end) in sides)
        {
            if (end < start)
            {
                continue;
            }

            var sideMedian = Median(values[start..(end + 1)]);
            var deviation = Math.Abs(value - sideMedian);
            if (best is null || deviation < best.Value)
            {
                best = deviation;
                baseline = sideMedian;
            }
        }

        return (best ?? 0.0, baseline);
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
