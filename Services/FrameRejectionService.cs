using blink_o_mat.Models;

namespace blink_o_mat.Services;

public sealed class FrameRejectionService
{
    public bool ShouldReject(FrameItem frame, Thresholds thresholds)
    {
        var m = frame.Metrics;

        return m.Fwhm > thresholds.MaxFwhm
               || m.Hfr > thresholds.MaxHfr
               || m.Eccentricity > thresholds.MaxEccentricity
               || m.MeanBackground > thresholds.MaxMeanBackground
               || (thresholds.RejectSatelliteTrail && m.PossibleSatelliteTrail);
    }
}
