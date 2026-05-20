namespace blink_o_mat.Models;

public sealed class Thresholds
{
    public double MaxFwhm { get; set; } = 8.0;
    public double MinSqm { get; set; } = 0;
    public double MaxSkyTemp { get; set; } = 40.0;
    public double MaxHfr { get; set; } = 4.5;
    public double MaxEccentricity { get; set; } = 0.6;
    public double MaxMeanBackground { get; set; } = 2000.0;
    public double MinStars { get; set; } = 0;
    public int MinSatelliteConfidence { get; set; } = 80;
}
