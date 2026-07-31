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
}