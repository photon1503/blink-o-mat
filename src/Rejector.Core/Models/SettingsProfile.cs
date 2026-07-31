namespace Rejector.Core.Models;

public sealed class SettingsProfile
{
    public string Name { get; set; } = "Default";

    public Thresholds Thresholds { get; set; } = new();

    public List<ProfileFilterThresholds> FilterThresholds { get; set; } = [];

    public bool IncludeSubfoldersDefault { get; set; }

    public bool WatchFolderDefault { get; set; }

    public double StfTargetBackgroundDefault { get; set; } = 0.15;

    public bool ShowTrailSlider { get; set; } = true;
    public bool ShowFwhmSlider { get; set; } = true;
    public bool ShowFwhmArcsecSlider { get; set; } = true;
    public bool ShowSqmSlider { get; set; } = true;
    public bool ShowSkyTempSlider { get; set; } = true;
    public bool ShowHfrSlider { get; set; } = true;
    public bool ShowEccentricitySlider { get; set; } = true;
    public bool ShowMeanBackgroundSlider { get; set; } = true;
    public bool ShowStarsSlider { get; set; } = true;
    public bool ShowScoreSlider { get; set; } = true;

    public bool ShowTrailMetric { get; set; } = true;
    public bool ShowFwhmMetric { get; set; } = true;
    public bool ShowFwhmArcsecMetric { get; set; } = true;
    public bool ShowSqmMetric { get; set; } = true;
    public bool ShowSkyTempMetric { get; set; } = true;
    public bool ShowHfrMetric { get; set; } = true;
    public bool ShowEccentricityMetric { get; set; } = true;
    public bool ShowMeanBackgroundMetric { get; set; } = true;
    public bool ShowStarsMetric { get; set; } = true;
    public bool ShowScoreMetric { get; set; } = true;

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

    public bool UseScoreFwhm { get; set; } = true;
    public bool UseScoreEccentricity { get; set; } = true;
    public bool UseScoreTrail { get; set; } = true;
    public bool UseScoreHfr { get; set; } = true;
    public bool UseScoreStars { get; set; } = true;
    public bool UseScoreMeanBackground { get; set; } = true;

    public double ScoreWeightFwhm { get; set; } = 3.0;
    public double ScoreWeightEccentricity { get; set; } = 2.5;
    public double ScoreWeightTrail { get; set; } = 2.0;
    public double ScoreWeightHfr { get; set; } = 1.5;
    public double ScoreWeightStars { get; set; } = 1.5;
    public double ScoreWeightMeanBackground { get; set; } = 0.5;

    public static string NormalizeName(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? "Default" : value;
    }
}

public sealed class ProfileFilterThresholds
{
    public string Key { get; set; } = string.Empty;

    public Thresholds Thresholds { get; set; } = new();
}