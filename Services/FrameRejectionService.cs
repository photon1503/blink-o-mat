using blink_o_mat.Models;

namespace blink_o_mat.Services;

public sealed class FrameRejectionService
{
    public bool ShouldReject(FrameItem frame, Thresholds thresholds)
    {
        var m = frame.Metrics;

        return m.Fwhm > thresholds.MaxFwhm
               || (m.Sqm.HasValue && m.Sqm.Value < thresholds.MinSqm)
               || (m.SkyTemp.HasValue && m.SkyTemp.Value > thresholds.MaxSkyTemp)
               || m.Hfr > thresholds.MaxHfr
               || m.Eccentricity > thresholds.MaxEccentricity
               || m.MeanBackground > thresholds.MaxMeanBackground
               || m.StarCount < thresholds.MinStars
               || (thresholds.MinSatelliteConfidence > 0 && m.SatelliteTrailConfidence >= thresholds.MinSatelliteConfidence);
    }

    public List<string> GetRejectionReasons(FrameItem frame, Thresholds thresholds)
    {
        var m = frame.Metrics;
        var reasons = new List<string>();

        if (m.Fwhm > thresholds.MaxFwhm)
            reasons.Add($"FWHM {m.Fwhm:F2} px  >  limit {thresholds.MaxFwhm:F2} px");

        if (m.Sqm.HasValue && m.Sqm.Value < thresholds.MinSqm)
            reasons.Add($"SQM {m.Sqm.Value:F3}  <  limit {thresholds.MinSqm:F3}");

        if (m.SkyTemp.HasValue && m.SkyTemp.Value > thresholds.MaxSkyTemp)
            reasons.Add($"Sky temp {m.SkyTemp.Value:F1}°  >  limit {thresholds.MaxSkyTemp:F1}°");

        if (m.Hfr > thresholds.MaxHfr)
            reasons.Add($"HFR {m.Hfr:F2} px  >  limit {thresholds.MaxHfr:F2} px");

        if (m.Eccentricity > thresholds.MaxEccentricity)
            reasons.Add($"Eccentricity {m.Eccentricity:F3}  >  limit {thresholds.MaxEccentricity:F3}");

        if (m.MeanBackground > thresholds.MaxMeanBackground)
            reasons.Add($"Mean BG {m.MeanBackground:F0}  >  limit {thresholds.MaxMeanBackground:F0}");

        if (m.StarCount < thresholds.MinStars)
            reasons.Add($"Stars {m.StarCount}  <  limit {thresholds.MinStars:F0}");

        if (thresholds.MinSatelliteConfidence > 0 && m.SatelliteTrailConfidence >= thresholds.MinSatelliteConfidence)
            reasons.Add($"Satellite trail confidence {m.SatelliteTrailConfidence}%  ≥  limit {thresholds.MinSatelliteConfidence}%");

        return reasons;
    }
}
