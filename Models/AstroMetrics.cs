namespace blink_o_mat.Models;

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
}
