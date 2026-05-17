namespace blink_o_mat.Models;

public sealed record AstroMetrics
{
    public double Fwhm { get; init; }
    public double? FwhmArcsec { get; init; }
    public double Hfr { get; init; }
    public int StarCount { get; init; }
    public double Eccentricity { get; init; }
    public double MeanBackground { get; init; }
    public double? FocalLengthMm { get; init; }
    public double? PixelSizeUm { get; init; }
    public bool PossibleSatelliteTrail { get; init; }
}
