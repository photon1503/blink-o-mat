using System;

namespace blink_o_mat.Models;

public sealed class SettingsProfile
{
    public string Name { get; set; } = "Default";

    public Thresholds Thresholds { get; set; } = new();

    public bool IncludeSubfoldersDefault { get; set; } = false;

    public bool WatchFolderDefault { get; set; } = false;

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

    public static string NormalizeName(string? raw)
    {
        var n = (raw ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(n) ? "Default" : n;
    }
}
