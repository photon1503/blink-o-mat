namespace Rejector.Core.Services;

public static class ScoreColorPalette
{
    private const string Red = "#CD5C5C";
    private const string Yellow = "#DAA520";
    private const string Green = "#5CA36E";

    public static string ForScore(double score)
    {
        var clamped = Math.Clamp(score, 0.0, 5.0);

        if (clamped <= 2.0)
        {
            return Interpolate(Red, Yellow, clamped / 2.0);
        }

        return Interpolate(Yellow, Green, (clamped - 2.0) / 3.0);
    }

    public static string ForBackground(double score)
    {
        return $"#33{ForScore(score).TrimStart('#') }";
    }

    private static string Interpolate(string startHex, string endHex, double amount)
    {
        var start = ParseHex(startHex);
        var end = ParseHex(endHex);
        var t = Math.Clamp(amount, 0.0, 1.0);

        var red = (byte)Math.Round(start.R + ((end.R - start.R) * t));
        var green = (byte)Math.Round(start.G + ((end.G - start.G) * t));
        var blue = (byte)Math.Round(start.B + ((end.B - start.B) * t));

        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    private static (byte R, byte G, byte B) ParseHex(string hex)
    {
        var normalized = hex.Trim();
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length != 6)
        {
            throw new ArgumentException($"Expected a six-digit hex color, got '{hex}'.", nameof(hex));
        }

        return (
            byte.Parse(normalized.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(normalized.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(normalized.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
    }
}