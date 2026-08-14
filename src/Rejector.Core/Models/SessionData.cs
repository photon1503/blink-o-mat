namespace Rejector.Core.Models;

public sealed class SessionData
{
    public int Version { get; set; } = 1;
    public DateTimeOffset SavedAt { get; set; }
    public string? InputFolder { get; set; }
    public string? RejectedFolder { get; set; }
    public bool IncludeSubfolders { get; set; }
    public double MaxFwhm { get; set; }
    public double MaxFwhmArcsec { get; set; }
    public double MaxHfr { get; set; }
    public double MaxEccentricity { get; set; }
    public double MaxMeanBackground { get; set; }
    public double MinStars { get; set; }
    public double MinSqm { get; set; }
    public double MaxSkyTemp { get; set; }
    public int MinSatelliteConfidence { get; set; }
    public int MinCloudConfidence { get; set; }
    public bool RejectSatelliteTrail { get; set; }
    public double MinScore { get; set; }
    public double StfShadows { get; set; }
    public double StfMidtones { get; set; }
    public double StfHighlights { get; set; }
    public double StfTargetBackground { get; set; }
    public bool AutoStretchPerFrame { get; set; }
    public SessionRoiRect? ManualRoi { get; set; }
    public List<SessionSortRule> SortRules { get; set; } = [];
    public List<SessionFilterChip> FilterChips { get; set; } = [];
    public List<SessionFilterThresholds> FilterThresholds { get; set; } = [];
    public bool ShowAccepted { get; set; } = true;
    public bool ShowRejected { get; set; } = true;
    public List<SessionFrameEntry> Frames { get; set; } = [];
}

public sealed class SessionRoiRect
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class SessionSortRule
{
    public string Field { get; set; } = string.Empty;
    public string Direction { get; set; } = "Ascending";
}

public sealed class SessionFilterChip
{
    public string Key { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
}

public sealed class SessionFilterThresholds
{
    public string Key { get; set; } = string.Empty;
    public double MaxFwhm { get; set; }
    public double MaxFwhmArcsec { get; set; }
    public double MaxHfr { get; set; }
    public double MaxEccentricity { get; set; }
    public double MaxMeanBackground { get; set; }
    public double MinStars { get; set; }
    public double MinSqm { get; set; }
    public double MaxSkyTemp { get; set; }
    public int MinSatelliteConfidence { get; set; }
    public double MinScore { get; set; }
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

public sealed class SessionFrameEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? RelativePath { get; set; }
    public bool AutoRejected { get; set; }
    public bool? ManualRejectedOverride { get; set; }
    public double OverallScore { get; set; }
    public double Fwhm { get; set; }
    public double? FwhmArcsec { get; set; }
    public double? Sqm { get; set; }
    public double? SkyTemp { get; set; }
    public double Hfr { get; set; }
    public int StarCount { get; set; }
    public double Eccentricity { get; set; }
    public double MeanBackground { get; set; }
    public double Median { get; set; }
    public double Mad { get; set; }
    public double Min { get; set; }
    public int MinCount { get; set; }
    public double Max { get; set; }
    public int MaxCount { get; set; }
    public int SatelliteTrailConfidence { get; set; }
    public int CloudConfidence { get; set; }
    public double? TrailX1 { get; set; }
    public double? TrailY1 { get; set; }
    public double? TrailX2 { get; set; }
    public double? TrailY2 { get; set; }
    public DateTimeOffset? ExposureDateTime { get; set; }
    public double? ExposureSeconds { get; set; }
    public string? FilterName { get; set; }
    public double? FocalLengthMm { get; set; }
    public double? PixelSizeUm { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Rotate180 { get; set; }
    public int ShiftX { get; set; }
    public int ShiftY { get; set; }
    public double NormalizationMax { get; set; }
    public string? ThumbnailPng { get; set; }
    public string? RoiPng { get; set; }
}