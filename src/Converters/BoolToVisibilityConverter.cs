using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace blink_o_mat.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isVisible = value is true;
        if (parameter is string p && string.Equals(p, "invert", StringComparison.OrdinalIgnoreCase))
        {
            isVisible = !isVisible;
        }

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var result = value is Visibility.Visible;
        if (parameter is string p && string.Equals(p, "invert", StringComparison.OrdinalIgnoreCase))
        {
            result = !result;
        }

        return result;
    }
}
