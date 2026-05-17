namespace blink_o_mat.Models;

public sealed class Thresholds
{
    public double MaxFwhm { get; set; } = 8.0;
    public double MaxHfr { get; set; } = 4.5;
    public double MaxEccentricity { get; set; } = 0.6;
    public double MaxMeanBackground { get; set; } = 2000.0;
    public bool RejectSatelliteTrail { get; set; } = true;
}
