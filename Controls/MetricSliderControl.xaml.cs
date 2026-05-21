using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace blink_o_mat.Controls;

public partial class MetricSliderControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(MetricSliderControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(MetricSliderControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(MetricSliderControl), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(MetricSliderControl), new PropertyMetadata(100.0));

    public static readonly DependencyProperty TickFrequencyProperty =
        DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(MetricSliderControl), new PropertyMetadata(1.0));

    public static readonly DependencyProperty LargeChangeProperty =
        DependencyProperty.Register(nameof(LargeChange), typeof(double), typeof(MetricSliderControl), new PropertyMetadata(10.0));

    public static readonly DependencyProperty DecimalsProperty =
        DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(MetricSliderControl),
            new PropertyMetadata(0, OnDecimalsChanged));

    public static readonly DependencyProperty SuffixProperty =
        DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(MetricSliderControl),
            new PropertyMetadata(string.Empty, OnSuffixChanged));

    public static readonly DependencyProperty ShowRejectCountProperty =
        DependencyProperty.Register(nameof(ShowRejectCount), typeof(bool), typeof(MetricSliderControl), new PropertyMetadata(false));

    public static readonly DependencyProperty RejectCountProperty =
        DependencyProperty.Register(nameof(RejectCount), typeof(int), typeof(MetricSliderControl), new PropertyMetadata(0));

    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(MetricSliderControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SliderConverterProperty =
        DependencyProperty.Register(nameof(SliderConverter), typeof(IValueConverter), typeof(MetricSliderControl),
            new PropertyMetadata(null, OnSliderConverterChanged));

    public static readonly DependencyProperty SliderConverterParameterProperty =
        DependencyProperty.Register(nameof(SliderConverterParameter), typeof(object), typeof(MetricSliderControl),
            new PropertyMetadata(null, OnSliderConverterChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double TickFrequency
    {
        get => (double)GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    public double LargeChange
    {
        get => (double)GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public bool ShowRejectCount
    {
        get => (bool)GetValue(ShowRejectCountProperty);
        set => SetValue(ShowRejectCountProperty, value);
    }

    public int RejectCount
    {
        get => (int)GetValue(RejectCountProperty);
        set => SetValue(RejectCountProperty, value);
    }

    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        private set => SetValue(DisplayTextProperty, value);
    }

    public IValueConverter? SliderConverter
    {
        get => (IValueConverter?)GetValue(SliderConverterProperty);
        set => SetValue(SliderConverterProperty, value);
    }

    public object? SliderConverterParameter
    {
        get => GetValue(SliderConverterParameterProperty);
        set => SetValue(SliderConverterParameterProperty, value);
    }

    /// <summary>Raised after Loaded when the internal <see cref="Slider"/> is available. Sender is the internal Slider.</summary>
    public event EventHandler? InternalSliderLoaded;

    public MetricSliderControl()
    {
        InitializeComponent();
        UpdateDisplayText();
        Loaded += (_, _) =>
        {
            ApplySliderBinding();
            InternalSliderLoaded?.Invoke(TheSlider, EventArgs.Empty);
        };
    }

    private void ApplySliderBinding()
    {
        var binding = new System.Windows.Data.Binding(nameof(Value))
        {
            Source = this,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        };

        if (SliderConverter is not null)
        {
            binding.Converter = SliderConverter;
            binding.ConverterParameter = SliderConverterParameter;
        }

        (TheSlider as System.Windows.FrameworkElement)?.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, binding);
    }

    private static void OnSliderConverterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (MetricSliderControl)d;
        if (ctrl.IsLoaded)
        {
            ctrl.ApplySliderBinding();
        }
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MetricSliderControl)d).UpdateDisplayText();
    }

    private static void OnDecimalsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MetricSliderControl)d).UpdateDisplayText();
    }

    private static void OnSuffixChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MetricSliderControl)d).UpdateDisplayText();
    }

    private void UpdateDisplayText()
    {
        var formatted = Value.ToString("F" + Decimals, CultureInfo.InvariantCulture);
        DisplayText = string.IsNullOrEmpty(Suffix) ? formatted : formatted + Suffix;
    }

    private void ValueBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginEdit();
        e.Handled = true;
    }

    private void BeginEdit()
    {
        ValueDisplay.Visibility = Visibility.Collapsed;
        ValueEditor.Visibility = Visibility.Visible;
        ValueEditor.Text = Value.ToString("F" + Decimals, CultureInfo.InvariantCulture);
        ValueEditor.SelectAll();
        ValueEditor.Focus();
    }

    private void CommitEdit()
    {
        if (ValueEditor.Visibility != Visibility.Visible)
        {
            return;
        }

        var text = ValueEditor.Text.Trim();
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
        ValueEditor.Visibility = Visibility.Collapsed;
        ValueDisplay.Visibility = Visibility.Visible;
        UpdateDisplayText();
    }

    private void ValueEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitEdit();
    }

    private void ValueEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }
}
