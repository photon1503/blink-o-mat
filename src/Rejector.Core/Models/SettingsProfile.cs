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
    public bool ShowCloudSlider { get; set; } = true;
    public bool ShowFwhmSlider { get; set; } = true;
    public bool ShowFwhmArcsecSlider { get; set; } = true;
    public bool ShowSqmSlider { get; set; } = true;
    public bool ShowSkyTempSlider { get; set; } = true;
    public bool ShowHfrSlider { get; set; } = true;
    public bool ShowEccentricitySlider { get; set; } = true;
    public bool ShowMeanBackgroundSlider { get; set; } = true;
    public bool ShowStarsSlider { get; set; } = true;
    public bool ShowScoreSlider { get; set; } = true;

    public bool IsRoiOverlayVisible { get; set; }
    public bool IsStarDebugOverlayVisible { get; set; }
    public bool IsOrientationDebugOverlayVisible { get; set; }
    public bool IsCurvatureViewVisible { get; set; }

    public bool ShowTrailMetric { get; set; } = true;
    public bool ShowCloudMetric { get; set; } = true;
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
    public bool AutoCalcCloudThreshold { get; set; } = true;
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
    public bool UseScoreCloud { get; set; } = true;
    public bool UseScoreHfr { get; set; } = true;
    public bool UseScoreStars { get; set; } = true;
    public bool UseScoreMeanBackground { get; set; } = true;

    public double ScoreWeightFwhm { get; set; } = 3.0;
    public double ScoreWeightEccentricity { get; set; } = 2.5;
    public double ScoreWeightTrail { get; set; } = 2.0;
    public double ScoreWeightCloud { get; set; } = 2.0;
    public double ScoreWeightHfr { get; set; } = 1.5;
    public double ScoreWeightStars { get; set; } = 1.5;
    public double ScoreWeightMeanBackground { get; set; } = 0.5;

    public void OverrideThresholds(Thresholds thresholds)
    {
        Thresholds = thresholds.Clone();
        FilterThresholds.Clear();
        AutoCalcTrailThreshold = Thresholds.AutoCalcTrailThreshold;
        AutoCalcCloudThreshold = Thresholds.AutoCalcCloudThreshold;
        AutoCalcFwhmThreshold = Thresholds.AutoCalcFwhmThreshold;
        AutoCalcFwhmArcsecThreshold = Thresholds.AutoCalcFwhmArcsecThreshold;
        AutoCalcSqmThreshold = Thresholds.AutoCalcSqmThreshold;
        AutoCalcSkyTempThreshold = Thresholds.AutoCalcSkyTempThreshold;
        AutoCalcHfrThreshold = Thresholds.AutoCalcHfrThreshold;
        AutoCalcEccentricityThreshold = Thresholds.AutoCalcEccentricityThreshold;
        AutoCalcMeanBackgroundThreshold = Thresholds.AutoCalcMeanBackgroundThreshold;
        AutoCalcStarsThreshold = Thresholds.AutoCalcStarsThreshold;
        AutoCalcScoreThreshold = Thresholds.AutoCalcScoreThreshold;
    }

    public Thresholds GetThresholdsForFilter(string? filterKey)
    {
        var normalizedKey = NormalizeFilterKey(filterKey);
        if (normalizedKey.Length == 0)
        {
            return Thresholds;
        }

        return FilterThresholds.FirstOrDefault(item =>
                   string.Equals(NormalizeFilterKey(item.Key), normalizedKey, StringComparison.OrdinalIgnoreCase))?.Thresholds
               ?? Thresholds;
    }

    public Thresholds GetOrCreateFilterThresholds(string? filterKey)
    {
        var normalizedKey = NormalizeFilterKey(filterKey);
        if (normalizedKey.Length == 0)
        {
            return Thresholds;
        }

        var existing = FilterThresholds.FirstOrDefault(item =>
            string.Equals(NormalizeFilterKey(item.Key), normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Thresholds;
        }

        var created = new ProfileFilterThresholds
        {
            Key = normalizedKey,
            Thresholds = Thresholds.Clone(),
        };
        FilterThresholds.Add(created);
        return created.Thresholds;
    }

    public SettingsProfile Clone()
    {
        return new SettingsProfile
        {
            Name = NormalizeName(Name),
            Thresholds = Thresholds?.Clone() ?? new Thresholds(),
            FilterThresholds = FilterThresholds.Select(item => new ProfileFilterThresholds
            {
                Key = item.Key,
                Thresholds = item.Thresholds?.Clone() ?? new Thresholds(),
            }).ToList(),
            IncludeSubfoldersDefault = IncludeSubfoldersDefault,
            WatchFolderDefault = WatchFolderDefault,
            StfTargetBackgroundDefault = StfTargetBackgroundDefault,
            ShowTrailSlider = ShowTrailSlider,
            ShowFwhmSlider = ShowFwhmSlider,
            ShowFwhmArcsecSlider = ShowFwhmArcsecSlider,
            ShowSqmSlider = ShowSqmSlider,
            ShowSkyTempSlider = ShowSkyTempSlider,
            ShowHfrSlider = ShowHfrSlider,
            ShowEccentricitySlider = ShowEccentricitySlider,
            ShowMeanBackgroundSlider = ShowMeanBackgroundSlider,
            ShowStarsSlider = ShowStarsSlider,
            ShowScoreSlider = ShowScoreSlider,
            IsRoiOverlayVisible = IsRoiOverlayVisible,
            IsStarDebugOverlayVisible = IsStarDebugOverlayVisible,
            IsOrientationDebugOverlayVisible = IsOrientationDebugOverlayVisible,
            IsCurvatureViewVisible = IsCurvatureViewVisible,
            ShowTrailMetric = ShowTrailMetric,
            ShowCloudMetric = ShowCloudMetric,
            ShowFwhmMetric = ShowFwhmMetric,
            ShowFwhmArcsecMetric = ShowFwhmArcsecMetric,
            ShowSqmMetric = ShowSqmMetric,
            ShowSkyTempMetric = ShowSkyTempMetric,
            ShowHfrMetric = ShowHfrMetric,
            ShowEccentricityMetric = ShowEccentricityMetric,
            ShowMeanBackgroundMetric = ShowMeanBackgroundMetric,
            ShowStarsMetric = ShowStarsMetric,
            ShowScoreMetric = ShowScoreMetric,
            AutoCalcTrailThreshold = AutoCalcTrailThreshold,
            AutoCalcCloudThreshold = AutoCalcCloudThreshold,
            AutoCalcFwhmThreshold = AutoCalcFwhmThreshold,
            AutoCalcFwhmArcsecThreshold = AutoCalcFwhmArcsecThreshold,
            AutoCalcSqmThreshold = AutoCalcSqmThreshold,
            AutoCalcSkyTempThreshold = AutoCalcSkyTempThreshold,
            AutoCalcHfrThreshold = AutoCalcHfrThreshold,
            AutoCalcEccentricityThreshold = AutoCalcEccentricityThreshold,
            AutoCalcMeanBackgroundThreshold = AutoCalcMeanBackgroundThreshold,
            AutoCalcStarsThreshold = AutoCalcStarsThreshold,
            AutoCalcScoreThreshold = AutoCalcScoreThreshold,
            UseScoreFwhm = UseScoreFwhm,
            UseScoreEccentricity = UseScoreEccentricity,
            UseScoreTrail = UseScoreTrail,
            UseScoreCloud = UseScoreCloud,
            UseScoreHfr = UseScoreHfr,
            UseScoreStars = UseScoreStars,
            UseScoreMeanBackground = UseScoreMeanBackground,
            ScoreWeightFwhm = ScoreWeightFwhm,
            ScoreWeightEccentricity = ScoreWeightEccentricity,
            ScoreWeightTrail = ScoreWeightTrail,
            ScoreWeightCloud = ScoreWeightCloud,
            ScoreWeightHfr = ScoreWeightHfr,
            ScoreWeightStars = ScoreWeightStars,
            ScoreWeightMeanBackground = ScoreWeightMeanBackground,
        };
    }

    public static string NormalizeName(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? "Default" : value;
    }

    private static string NormalizeFilterKey(string? raw)
    {
        return raw?.Trim() ?? string.Empty;
    }
}

public sealed class ProfileFilterThresholds
{
    public string Key { get; set; } = string.Empty;

    public Thresholds Thresholds { get; set; } = new();
}