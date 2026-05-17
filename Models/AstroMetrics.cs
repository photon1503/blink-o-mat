namespace blink_o_mat.Models;

public sealed record AstroMetrics
{
    public double Fwhm { get; init; }
    public double Hfr { get; init; }
    public double Eccentricity { get; init; }
    public double MeanBackground { get; init; }
    public bool PossibleSatelliteTrail { get; init; }
}
