using Rejector.Core.Models;

namespace Rejector.Core.Services;

public sealed class FrameRejectionService
{
    public void RevalidateAll(IEnumerable<ProcessedFrame> frames, Thresholds thresholds)
    {
        foreach (var frame in frames)
        {
            frame.SetAutomaticRejected(ShouldReject(frame, thresholds));
        }
    }

    public void RevalidateAll(
        IEnumerable<ProcessedFrame> frames,
        Thresholds defaultThresholds,
        IReadOnlyDictionary<string, Thresholds> filterThresholds)
    {
        foreach (var frame in frames)
        {
            var groupKey = FrameGroupKey.Create(frame);
            var thresholds = filterThresholds.TryGetValue(groupKey, out var filterSpecific)
                ? filterSpecific
                : defaultThresholds;
            frame.SetAutomaticRejected(ShouldReject(frame, thresholds));
        }
    }

    public bool ShouldReject(ProcessedFrame frame, Thresholds thresholds)
    {
        var metrics = frame.Metrics;

        return metrics.Fwhm > thresholds.MaxFwhm
               || (metrics.FwhmArcsec.HasValue && metrics.FwhmArcsec.Value > thresholds.MaxFwhmArcsec)
               || (metrics.Sqm.HasValue && metrics.Sqm.Value < thresholds.MinSqm)
               || (metrics.SkyTemp.HasValue && metrics.SkyTemp.Value > thresholds.MaxSkyTemp)
               || metrics.Hfr > thresholds.MaxHfr
               || metrics.Eccentricity > thresholds.MaxEccentricity
               || metrics.MeanBackground > thresholds.MaxMeanBackground
               || metrics.StarCount < thresholds.MinStars
               || (thresholds.MinSatelliteConfidence > 0 && metrics.SatelliteTrailConfidence >= thresholds.MinSatelliteConfidence)
               || (thresholds.MinCloudConfidence > 0 && frame.CloudConfidence >= thresholds.MinCloudConfidence)
               || (thresholds.MinScore > 0 && frame.OverallScore < thresholds.MinScore);
    }

    public List<string> GetRejectionReasons(ProcessedFrame frame, Thresholds thresholds)
    {
        var metrics = frame.Metrics;
        var reasons = new List<string>();

        if (metrics.Fwhm > thresholds.MaxFwhm)
        {
            reasons.Add($"FWHM {metrics.Fwhm:F2} px  >  limit {thresholds.MaxFwhm:F2} px");
        }

        if (metrics.FwhmArcsec.HasValue && metrics.FwhmArcsec.Value > thresholds.MaxFwhmArcsec)
        {
            reasons.Add($"FWHM {metrics.FwhmArcsec.Value:F2} as  >  limit {thresholds.MaxFwhmArcsec:F2} as");
        }

        if (metrics.Sqm.HasValue && metrics.Sqm.Value < thresholds.MinSqm)
        {
            reasons.Add($"SQM {metrics.Sqm.Value:F3}  <  limit {thresholds.MinSqm:F3}");
        }

        if (metrics.SkyTemp.HasValue && metrics.SkyTemp.Value > thresholds.MaxSkyTemp)
        {
            reasons.Add($"Sky temp {metrics.SkyTemp.Value:F1}°  >  limit {thresholds.MaxSkyTemp:F1}°");
        }

        if (metrics.Hfr > thresholds.MaxHfr)
        {
            reasons.Add($"HFR {metrics.Hfr:F2} px  >  limit {thresholds.MaxHfr:F2} px");
        }

        if (metrics.Eccentricity > thresholds.MaxEccentricity)
        {
            reasons.Add($"Eccentricity {metrics.Eccentricity:F3}  >  limit {thresholds.MaxEccentricity:F3}");
        }

        if (metrics.MeanBackground > thresholds.MaxMeanBackground)
        {
            reasons.Add($"Mean BG {metrics.MeanBackground:F0}  >  limit {thresholds.MaxMeanBackground:F0}");
        }

        if (metrics.StarCount < thresholds.MinStars)
        {
            reasons.Add($"Stars {metrics.StarCount}  <  limit {thresholds.MinStars:F0}");
        }

        if (thresholds.MinSatelliteConfidence > 0 && metrics.SatelliteTrailConfidence >= thresholds.MinSatelliteConfidence)
        {
            reasons.Add($"Satellite trail confidence {metrics.SatelliteTrailConfidence}%  ≥  limit {thresholds.MinSatelliteConfidence}%");
        }

        if (thresholds.MinCloudConfidence > 0 && frame.CloudConfidence >= thresholds.MinCloudConfidence)
        {
            reasons.Add($"Cloud confidence {frame.CloudConfidence}%  ≥  limit {thresholds.MinCloudConfidence}%");
        }

        if (thresholds.MinScore > 0 && frame.OverallScore < thresholds.MinScore)
        {
            reasons.Add($"Score {frame.OverallScore:F1}  <  limit {thresholds.MinScore:F1}");
        }

        return reasons;
    }
}