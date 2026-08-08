namespace Rejector.Core.Models;

public sealed class Thresholds
{
    public double MaxFwhm { get; set; } = 8.0;
    public double MaxFwhmArcsec { get; set; } = 20.0;
    public double MinSqm { get; set; } = 0;
    public double MaxSkyTemp { get; set; } = 40.0;
    public double MaxHfr { get; set; } = 4.5;
    public double MaxEccentricity { get; set; } = 0.6;
    public double MaxMeanBackground { get; set; } = 2000.0;
    public double MinStars { get; set; } = 0;
    public int MinSatelliteConfidence { get; set; } = 80;
    public double MinScore { get; set; } = 0.0;

    public bool AutoCalcTrailThreshold { get; set; } = true;
    public bool AutoCalcFwhmThreshold { get; set; } = true;
    public bool AutoCalcFwhmArcsecThreshold { get; set; } = true;
    public bool AutoCalcSqmThreshold { get; set; } = true;
    public bool AutoCalcSkyTempThreshold { get; set; } = true;
    public bool AutoCalcHfrThreshold { get; set; } = true;
    public bool AutoCalcEccentricityThreshold { get; set; } = true;
    public bool AutoCalcMeanBackgroundThreshold { get; set; } = true;
    public bool AutoCalcStarsThreshold { get; set; } = true;
    public bool AutoCalcScoreThreshold { get; set; } = true;

    public static Thresholds CreatePermissive(IEnumerable<ProcessedFrame> frames)
    {
        var materialized = frames.ToList();
        var defaults = new Thresholds();

        static double MaxOrDefault(IEnumerable<double> values, double fallback)
        {
            var finite = values.Where(double.IsFinite).ToList();
            return finite.Count > 0 ? finite.Max() : fallback;
        }

        static double MinOrDefault(IEnumerable<double> values, double fallback)
        {
            var finite = values.Where(double.IsFinite).ToList();
            return finite.Count > 0 ? finite.Min() : fallback;
        }

        return new Thresholds
        {
            MaxFwhm = MaxOrDefault(materialized.Select(frame => frame.Metrics.Fwhm), defaults.MaxFwhm),
            MaxFwhmArcsec = MaxOrDefault(materialized.Select(frame => frame.Metrics.FwhmArcsec).OfType<double>(), defaults.MaxFwhmArcsec),
            MinSqm = MinOrDefault(materialized.Select(frame => frame.Metrics.Sqm).OfType<double>(), defaults.MinSqm),
            MaxSkyTemp = MaxOrDefault(materialized.Select(frame => frame.Metrics.SkyTemp).OfType<double>(), defaults.MaxSkyTemp),
            MaxHfr = MaxOrDefault(materialized.Select(frame => frame.Metrics.Hfr), defaults.MaxHfr),
            MaxEccentricity = MaxOrDefault(materialized.Select(frame => frame.Metrics.Eccentricity), defaults.MaxEccentricity),
            MaxMeanBackground = MaxOrDefault(materialized.Select(frame => frame.Metrics.MeanBackground), defaults.MaxMeanBackground),
            MinStars = materialized.Count > 0 ? materialized.Min(frame => (double)frame.Metrics.StarCount) : defaults.MinStars,
            MinSatelliteConfidence = 0,
            MinScore = MinOrDefault(materialized.Select(frame => frame.OverallScore), defaults.MinScore),
            AutoCalcTrailThreshold = false,
            AutoCalcFwhmThreshold = false,
            AutoCalcFwhmArcsecThreshold = false,
            AutoCalcSqmThreshold = false,
            AutoCalcSkyTempThreshold = false,
            AutoCalcHfrThreshold = false,
            AutoCalcEccentricityThreshold = false,
            AutoCalcMeanBackgroundThreshold = false,
            AutoCalcStarsThreshold = false,
            AutoCalcScoreThreshold = false,
        };
    }

    public Thresholds Clone()
    {
        return new Thresholds
        {
            MaxFwhm = MaxFwhm,
            MaxFwhmArcsec = MaxFwhmArcsec,
            MinSqm = MinSqm,
            MaxSkyTemp = MaxSkyTemp,
            MaxHfr = MaxHfr,
            MaxEccentricity = MaxEccentricity,
            MaxMeanBackground = MaxMeanBackground,
            MinStars = MinStars,
            MinSatelliteConfidence = MinSatelliteConfidence,
            MinScore = MinScore,
            AutoCalcTrailThreshold = AutoCalcTrailThreshold,
            AutoCalcFwhmThreshold = AutoCalcFwhmThreshold,
            AutoCalcFwhmArcsecThreshold = AutoCalcFwhmArcsecThreshold,
            AutoCalcSqmThreshold = AutoCalcSqmThreshold,
            AutoCalcSkyTempThreshold = AutoCalcSkyTempThreshold,
            AutoCalcHfrThreshold = AutoCalcHfrThreshold,
            AutoCalcEccentricityThreshold = AutoCalcEccentricityThreshold,
            AutoCalcMeanBackgroundThreshold = AutoCalcMeanBackgroundThreshold,
            AutoCalcStarsThreshold = AutoCalcStarsThreshold,
            AutoCalcScoreThreshold = AutoCalcScoreThreshold,
        };
    }
}