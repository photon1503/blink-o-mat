namespace blink_o_mat.Models;

public sealed record MeasuredStar(double X, double Y, double Fwhm, double Hfr, double Peak);

public sealed record AstroMetrics
{
    public double Fwhm { get; init; }
    public double? FwhmArcsec { get; init; }
    public double? Sqm { get; init; }
    public double? SkyTemp { get; init; }
    public double Hfr { get; init; }
    public int StarCount { get; init; }
    public double Eccentricity { get; init; }
    public double MeanBackground { get; init; }
    public double Median { get; init; }
    public double Mad { get; init; }
    public double Min { get; init; }
    public int MinCount { get; init; }
    public double Max { get; init; }
    public int MaxCount { get; init; }
    public double? FocalLengthMm { get; init; }
    public double? PixelSizeUm { get; init; }
    public int SatelliteTrailConfidence { get; init; }
    public double? TrailX1 { get; init; }
    public double? TrailY1 { get; init; }
    public double? TrailX2 { get; init; }
    public double? TrailY2 { get; init; }

    /// <summary>
    /// Stars that contributed to the FWHM/HFR/eccentricity statistics, in native image
    /// pixel coordinates. Populated by <c>RustafitsService.ComputeMetrics</c> and used
    /// by the preview window's Ctrl+F debug overlay. Not persisted to the session file.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<MeasuredStar> Stars { get; init; } = System.Array.Empty<MeasuredStar>();
}
