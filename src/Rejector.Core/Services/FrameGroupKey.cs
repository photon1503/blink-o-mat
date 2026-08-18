using System.Globalization;
using Rejector.Core.Models;

namespace Rejector.Core.Services;

/// <summary>
/// Builds the key frames are grouped by for per-channel scoring, cloud detection,
/// rejection thresholds and chip selection. Frames share a group only when both the
/// filter name AND the exposure time match, so mixed sub-lengths within the same
/// filter (e.g. 30s and 120s Ha subs) are never scored or thresholded together.
/// </summary>
public static class FrameGroupKey
{
    public static string Create(string? filterName, double? exposureSeconds)
    {
        var name = string.IsNullOrWhiteSpace(filterName) ? "(no filter)" : filterName.Trim();
        return name + FormatExposureSuffix(exposureSeconds);
    }

    public static string Create(ProcessedFrame frame) => Create(frame.FilterName, frame.ExposureSeconds);

    private static string FormatExposureSuffix(double? exposureSeconds)
    {
        if (exposureSeconds is not double value || value <= 0 || !double.IsFinite(value))
        {
            return string.Empty;
        }

        // Round to the nearest tenth of a second so trivial EXPTIME header rounding
        // (e.g. 29.998 vs 30.0) doesn't split the same imaging run into separate groups.
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        var text = rounded % 1 == 0
            ? rounded.ToString("0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.#", CultureInfo.InvariantCulture);
        return $"@{text}s";
    }
}
