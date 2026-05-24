using System;
using System.Globalization;
using System.Windows.Data;

namespace blink_o_mat.Converters;

public sealed class StfSliderValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var actual = Clamp01(System.Convert.ToDouble(value, culture));
        var (mode, exponent) = ParseParameter(parameter);

        return mode switch
        {
            SliderCurveMode.LowRange => Math.Pow(actual, 1.0 / exponent),
            SliderCurveMode.HighRange => 1.0 - Math.Pow(1.0 - actual, 1.0 / exponent),
            _ => actual
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var slider = Clamp01(System.Convert.ToDouble(value, culture));
        var (mode, exponent) = ParseParameter(parameter);

        return mode switch
        {
            SliderCurveMode.LowRange => Math.Pow(slider, exponent),
            SliderCurveMode.HighRange => 1.0 - Math.Pow(1.0 - slider, exponent),
            _ => slider
        };
    }

    private static (SliderCurveMode Mode, double Exponent) ParseParameter(object? parameter)
    {
        var text = parameter as string;
        if (string.IsNullOrWhiteSpace(text))
        {
            return (SliderCurveMode.LowRange, 3.0);
        }

        var parts = text.Split(':', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var mode = parts[0].Equals("high", StringComparison.OrdinalIgnoreCase)
            ? SliderCurveMode.HighRange
            : SliderCurveMode.LowRange;

        var exponent = 3.0;
        if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0.0)
        {
            exponent = parsed;
        }

        return (mode, exponent);
    }

    private static double Clamp01(double value) => Math.Clamp(double.IsFinite(value) ? value : 0.0, 0.0, 1.0);

    private enum SliderCurveMode
    {
        LowRange,
        HighRange
    }
}
