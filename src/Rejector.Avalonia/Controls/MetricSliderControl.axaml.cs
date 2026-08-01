using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Rejector.Avalonia.Controls;

public partial class MetricSliderControl : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<MetricSliderControl, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<MetricSliderControl, double>(nameof(Value), 0.0, defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<MetricSliderControl, double>(nameof(Minimum), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<MetricSliderControl, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<double> TickFrequencyProperty =
        AvaloniaProperty.Register<MetricSliderControl, double>(nameof(TickFrequency), 1.0);

    public static readonly StyledProperty<double> LargeChangeProperty =
        AvaloniaProperty.Register<MetricSliderControl, double>(nameof(LargeChange), 10.0);

    public static readonly StyledProperty<int> DecimalsProperty =
        AvaloniaProperty.Register<MetricSliderControl, int>(nameof(Decimals), 0);

    public static readonly StyledProperty<string> SuffixProperty =
        AvaloniaProperty.Register<MetricSliderControl, string>(nameof(Suffix), string.Empty);

    public static readonly StyledProperty<bool> ShowRejectCountProperty =
        AvaloniaProperty.Register<MetricSliderControl, bool>(nameof(ShowRejectCount), false);

    public static readonly StyledProperty<int> RejectCountProperty =
        AvaloniaProperty.Register<MetricSliderControl, int>(nameof(RejectCount), 0);

    public static readonly DirectProperty<MetricSliderControl, string> DisplayTextProperty =
        AvaloniaProperty.RegisterDirect<MetricSliderControl, string>(nameof(DisplayText), control => control.DisplayText);

    private string _displayText = string.Empty;

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double TickFrequency
    {
        get => GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    public double LargeChange
    {
        get => GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    public int Decimals
    {
        get => GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    public string Suffix
    {
        get => GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public bool ShowRejectCount
    {
        get => GetValue(ShowRejectCountProperty);
        set => SetValue(ShowRejectCountProperty, value);
    }

    public int RejectCount
    {
        get => GetValue(RejectCountProperty);
        set => SetValue(RejectCountProperty, value);
    }

    public string DisplayText
    {
        get => _displayText;
        private set => SetAndRaise(DisplayTextProperty, ref _displayText, value);
    }

    public MetricSliderControl()
    {
        InitializeComponent();
        UpdateDisplayText();

        ValueProperty.Changed.AddClassHandler<MetricSliderControl>((control, _) => control.UpdateDisplayText());
        DecimalsProperty.Changed.AddClassHandler<MetricSliderControl>((control, _) => control.UpdateDisplayText());
        SuffixProperty.Changed.AddClassHandler<MetricSliderControl>((control, _) => control.UpdateDisplayText());
    }

    private void ValueBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginEdit();
        e.Handled = true;
    }

    private void BeginEdit()
    {
        ValueDisplay.IsVisible = false;
        ValueEditor.IsVisible = true;
        ValueEditor.Text = Value.ToString($"F{Decimals}", CultureInfo.InvariantCulture);
        ValueEditor.SelectAll();
        ValueEditor.Focus();
    }

    private void CommitEdit()
    {
        if (!ValueEditor.IsVisible)
        {
            return;
        }

        var text = ValueEditor.Text?.Trim() ?? string.Empty;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            Value = Math.Clamp(parsed, Minimum, Maximum);
        }

        EndEdit();
    }

    private void CancelEdit()
    {
        EndEdit();
    }

    private void EndEdit()
    {
        ValueEditor.IsVisible = false;
        ValueDisplay.IsVisible = true;
        UpdateDisplayText();
    }

    private void ValueEditor_LostFocus(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        CommitEdit();
    }

    private void ValueEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    private void UpdateDisplayText()
    {
        var formatted = Value.ToString($"F{Decimals}", CultureInfo.InvariantCulture);
        DisplayText = string.IsNullOrEmpty(Suffix) ? formatted : formatted + Suffix;
    }
}
