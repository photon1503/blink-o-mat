using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace blink_o_mat.Converters;

/// <summary>
/// Converts a ratio (0.0–1.0) to a star-proportioned GridLength.
/// Used to drive accepted/rejected bar proportions in the frame summary.
/// </summary>
[ValueConversion(typeof(double), typeof(GridLength))]
public sealed class RatioToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ratio = value is double d ? Math.Clamp(d, 0.0, 1.0) : 0.0;
        var invert = parameter is string s && s == "invert";
        var starValue = invert ? 1.0 - ratio : ratio;
        // avoid a zero-width column causing a layout error when ratio is exactly 0 or 1
        return new GridLength(Math.Max(starValue, 0.0), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
