using Avalonia;
using Avalonia.Controls;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Specialized;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Rejector.Avalonia.ViewModels;
using System.Text.Json;
using System.IO;
using System.Linq;

namespace Rejector.Avalonia.Views;

public sealed class FramePreviewWindow : Window
{
    private readonly Image _previewImage;
    private readonly Canvas _previewSurface;
    private readonly Grid _previewLayer;
    private readonly ScaleTransform _previewScale = new(1.0, 1.0);
    private readonly TextBlock _zoomText;
    private readonly TextBlock _intervalText;
    private readonly TextBlock _cacheText;
    private readonly TextBlock _framePositionText;
    private readonly Slider _frameSlider;
    private readonly Border _filterChipContainer;
    private readonly WrapPanel _filterChipWrap;
    private readonly ToggleButton _acceptedToggle;
    private readonly ToggleButton _rejectedToggle;
    private readonly DispatcherTimer _playTimer = new();
    private readonly Canvas _overlayCanvas;
    private readonly Canvas _starTrailOverlayCanvas;
    private readonly Canvas _orientationOverlayCanvas;
    private readonly Canvas _curvatureOverlayCanvas;
    private readonly Rectangle _roiRect;
    private readonly Rectangle _roiDragRect;
    private readonly Rectangle[] _roiHandles = new Rectangle[4];
    private readonly Canvas _cacheIndicatorCanvas;
    private readonly Canvas _cacheDotCanvas;
    private readonly Button _playButton;
    private readonly Border _loupeBorder;
    private readonly Image _loupeImage;
    private readonly TextBlock _loupeXText;
    private readonly TextBlock _loupeYText;
    private readonly TextBlock _loupeKText;
    private readonly TextBlock _loupeMinText;
    private readonly TextBlock _loupeMaxText;
    private readonly TextBlock _loupeMeanText;
    private readonly Border _curvatureTooltip;
    private readonly TextBlock _curvatureTooltipText;
    private double[]? _curvatureGrid;
    private int _curvatureGridWidth;
    private int _curvatureGridHeight;
    private double _curvatureArcsecPerPixel;
    private Rect _curvatureImageRect;
    private bool _isLoupeActive;
    private bool _isPanning;
    private PanMouseButton _activePanButton = PanMouseButton.None;
    private bool _isKeyboardNavigationInProgress;
    private int? _activeKeyboardNavigationIndex;
    private int? _queuedKeyboardNavigationIndex;
    private bool _isRoiDragging;
    private Point _roiDragOriginImage;
    private IPointer? _roiDragPointer;
    private Point _panStartPoint;
    private Vector _panStartOffset;
    private double _imageLeft;
    private double _imageTop;
    private RoiEditMode _roiEditMode;
    private IPointer? _roiEditPointer;
    private int _roiActiveHandleIndex = -1;
    private Point _roiEditStartPointer;
    private (double Left, double Top, double Width, double Height) _roiEditStartRect;
    private const double DefaultRoiSize = 0.3;
        private const int LoupeSampleSize = 31;
    private double _roiLeft = 0.35;
    private double _roiTop = 0.35;
    private double _roiWidth = DefaultRoiSize;
    private double _roiHeight = DefaultRoiSize;
    private static readonly double[] PlaybackIntervals = [0.1, 0.2, 0.5, 1.0, 2.0, 3.0, 5.0, 10.0];
    private int _playbackIntervalIndex = 3;
    private MainWindowViewModel? _attachedVm;
    private bool _hasInitializedView;
    private bool _isWindowPlacementReady;
    private bool _isApplyingWindowPlacement;
    private WindowPlacement? _restoredWindowPlacement;
    private bool _overlayRedrawQueued;
    private ViewState? _pendingViewState;

    private enum PanMouseButton
    {
        None,
        Left,
        Middle,
    }

    private enum RoiEditMode
    {
        None,
        Move,
        Resize,
    }

    // Enlarges the default (fully-functional) Fluent Slider's Thumb/track so it's easy to grab, without
    // replacing the control template - a from-scratch FuncControlTemplate risks breaking Slider's internal
    // NameScope-based wiring (PART_Track lookup, drag handling) in subtle ways.
    private static Style[] CreatePreviewSliderStyles() =>
    [
        new Style(x => x.OfType<Slider>().Class("PreviewSlider").Template().OfType<Thumb>())
        {
            Setters =
            {
                new Setter(WidthProperty, 22.0),
                new Setter(HeightProperty, 22.0),
                new Setter(TemplatedControl.BackgroundProperty, SolidColorBrush.Parse("#4FB3FF")),
                new Setter(TemplatedControl.BorderBrushProperty, SolidColorBrush.Parse("#0D6FC8")),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(11)),
            },
        },
        new Style(x => x.OfType<Slider>().Class("VerticalPreviewSlider").Template().OfType<Thumb>())
        {
            Setters =
            {
                new Setter(WidthProperty, 16.0),
                new Setter(HeightProperty, 16.0),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
            },
        },
    ];

    private void RestoreWindowPlacement()
    {
        if (!File.Exists(WindowPlacementPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(WindowPlacementPath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, WindowPlacement>>(json);
            if (settings is null || !settings.TryGetValue("PreviewWindow", out var placement))
            {
                return;
            }

            if (placement.Width <= 0 || placement.Height <= 0)
            {
                return;
            }

            var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
            if (!IsOnScreen(bounds))
            {
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)Math.Round(placement.Left), (int)Math.Round(placement.Top));
            Width = placement.Width;
            Height = placement.Height;
            WindowState = placement.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            _restoredWindowPlacement = placement;
        }
        catch
        {
        }
    }

    private void ApplyRestoredWindowPlacementOrSaveCurrent()
    {
        if (_restoredWindowPlacement is not { } placement)
        {
            _isWindowPlacementReady = true;
            SaveWindowPlacement();
            return;
        }

        _isApplyingWindowPlacement = true;
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                if (!MacWindowPlacement.TrySetFrame(this, new Rect(placement.Left, placement.Top, placement.Width, placement.Height)))
                {
                    Position = new PixelPoint((int)Math.Round(placement.Left), (int)Math.Round(placement.Top));
                    Width = placement.Width;
                    Height = placement.Height;
                }
                WindowState = placement.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            }
            finally
            {
                _isApplyingWindowPlacement = false;
                _isWindowPlacementReady = true;
                SaveWindowPlacement();
            }
        }, TimeSpan.FromMilliseconds(100), DispatcherPriority.Background);
    }

    private void SaveWindowPlacementIfReady()
    {
        if (_isWindowPlacementReady && !_isApplyingWindowPlacement)
        {
            SaveWindowPlacement();
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            if (_isApplyingWindowPlacement)
            {
                return;
            }

            if (WindowState == WindowState.Minimized)
            {
                return;
            }

            var settings = File.Exists(WindowPlacementPath)
                ? JsonSerializer.Deserialize<Dictionary<string, WindowPlacement>>(File.ReadAllText(WindowPlacementPath)) ?? new Dictionary<string, WindowPlacement>()
                : new Dictionary<string, WindowPlacement>();

            settings.TryGetValue("PreviewWindow", out var previous);
            var saveBounds = WindowState == WindowState.Normal || previous is null;
            var nativeFrame = MacWindowPlacement.TryGetFrame(this, out var frame);
            if (saveBounds && OperatingSystem.IsMacOS() && !nativeFrame)
            {
                return;
            }

            var placement = new WindowPlacement
            {
                Left = saveBounds ? (nativeFrame ? frame.X : Position.X) : previous!.Left,
                Top = saveBounds ? (nativeFrame ? frame.Y : Position.Y) : previous!.Top,
                Width = saveBounds ? (nativeFrame ? frame.Width : Bounds.Width) : previous!.Width,
                Height = saveBounds ? (nativeFrame ? frame.Height : Bounds.Height) : previous!.Height,
                WindowState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal,
            };

            settings["PreviewWindow"] = placement;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WindowPlacementPath)!);
            File.WriteAllText(WindowPlacementPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private bool IsOnScreen(Rect bounds)
    {
        var screenCount = Screens.ScreenCount;
        if (screenCount == 0)
        {
            return true;
        }

        foreach (var screen in Screens.All)
        {
            var placementRect = new PixelRect(
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.Width),
                (int)Math.Round(bounds.Height));

            if (screen.Bounds.Intersects(placementRect))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public WindowState WindowState { get; set; }
    }

    private static void ApplyPreviewSliderTemplate(Slider slider)
    {
        slider.Classes.Add("PreviewSlider");
        if (slider.Orientation == Orientation.Vertical)
        {
            slider.Classes.Add("VerticalPreviewSlider");
            slider.Width = 26;
            slider.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        slider.Height = 28;
        slider.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void ApplyChipTemplate(ToggleButton chip)
    {
        chip.Template = new FuncControlTemplate<ToggleButton>((control, _) =>
            new Border
            {
                [!Border.BackgroundProperty] = control[!TemplatedControl.BackgroundProperty],
                [!Border.BorderBrushProperty] = control[!TemplatedControl.BorderBrushProperty],
                [!Border.BorderThicknessProperty] = control[!TemplatedControl.BorderThicknessProperty],
                [!Border.CornerRadiusProperty] = control[!TemplatedControl.CornerRadiusProperty],
                [!Border.PaddingProperty] = control[!TemplatedControl.PaddingProperty],
                Child = new ContentPresenter
                {
                    [!ContentPresenter.ContentProperty] = control[!ContentControl.ContentProperty],
                    [!ContentPresenter.HorizontalContentAlignmentProperty] = control[!ContentControl.HorizontalContentAlignmentProperty],
                    [!ContentPresenter.VerticalContentAlignmentProperty] = control[!ContentControl.VerticalContentAlignmentProperty],
                },
            });
    }

    private const string WindowPlacementDirectoryName = "Rejector";
    private const string WindowPlacementFileName = "window-placement.json";
    private static readonly string WindowPlacementPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        WindowPlacementDirectoryName,
        WindowPlacementFileName);

    public FramePreviewWindow()
    {
        Width = 1200;
        Height = 900;
        MinWidth = 640;
        MinHeight = 480;
        Background = SolidColorBrush.Parse("#111315");
        Styles.AddRange(CreatePreviewSliderStyles());

        RestoreWindowPlacement();
        Opened += (_, _) =>
        {
            ApplyRestoredWindowPlacementOrSaveCurrent();
        };
        Closing += (_, _) => SaveWindowPlacement();
        PositionChanged += (_, _) => SaveWindowPlacementIfReady();
        SizeChanged += (_, _) => SaveWindowPlacementIfReady();
        Closed += (_, _) => SaveWindowPlacement();

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
            Margin = new Thickness(12),
        };

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var prevButton = new Button { Content = "Prev", MinWidth = 58, Height = 30 };
        prevButton.Click += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectPreviousResult();
            }
        };
        Grid.SetColumn(prevButton, 0);
        toolbar.Children.Add(prevButton);

        var nextButton = new Button { Content = "Next", MinWidth = 58, Height = 30 };
        nextButton.Click += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectNextResult();
            }
        };
        Grid.SetColumn(nextButton, 1);
        toolbar.Children.Add(nextButton);

        _playButton = new Button { Content = "▶", Width = 36, Height = 30 };
        _playButton.Click += (_, _) =>
        {
            if (_playTimer.IsEnabled)
            {
                _playTimer.Stop();
                _playButton.Content = "▶";
            }
            else
            {
                _playTimer.Start();
                _playButton.Content = "⏸";
            }
        };
        Grid.SetColumn(_playButton, 2);
        toolbar.Children.Add(_playButton);

        var intervalDown = new Button { Content = "-", Width = 24, Height = 30 };
        intervalDown.Click += (_, _) => SetPlaybackInterval(_playbackIntervalIndex - 1);
        Grid.SetColumn(intervalDown, 3);
        toolbar.Children.Add(intervalDown);

        _intervalText = new TextBlock
        {
            Width = 56,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Text = "1.0 s",
        };
        Grid.SetColumn(_intervalText, 4);
        toolbar.Children.Add(_intervalText);

        var intervalUp = new Button { Content = "+", Width = 24, Height = 30 };
        intervalUp.Click += (_, _) => SetPlaybackInterval(_playbackIntervalIndex + 1);
        Grid.SetColumn(intervalUp, 5);
        toolbar.Children.Add(intervalUp);

        var zoomOutButton = new Button { Content = "-", Width = 34, Height = 30 };
        zoomOutButton.Click += (_, _) => ZoomAroundViewportCenter(1.0 / 1.25);
        Grid.SetColumn(zoomOutButton, 6);
        toolbar.Children.Add(zoomOutButton);

        var fitButton = new Button { Content = "Fit", Width = 44, Height = 30 };
        fitButton.Click += (_, _) => FitToView();
        Grid.SetColumn(fitButton, 7);
        toolbar.Children.Add(fitButton);

        var zoomInButton = new Button { Content = "+", Width = 34, Height = 30 };
        zoomInButton.Click += (_, _) => ZoomAroundViewportCenter(1.25);
        Grid.SetColumn(zoomInButton, 8);
        toolbar.Children.Add(zoomInButton);

        var oneToOneButton = new Button { Content = "1:1", Width = 46, Height = 30 };
        oneToOneButton.Click += (_, _) => SetZoomAroundViewerPoint(GetViewportCenter(), 1.0);
        Grid.SetColumn(oneToOneButton, 9);
        toolbar.Children.Add(oneToOneButton);

        var roiToggle = new ToggleButton { Content = "ROI", Width = 50, Height = 30 };
        roiToggle.Bind(ToggleButton.IsCheckedProperty, new Binding("IsRoiOverlayVisible") { Mode = BindingMode.TwoWay });
        Grid.SetColumn(roiToggle, 10);
        toolbar.Children.Add(roiToggle);

        var openButton = new Button { Content = "Open", Width = 58, Height = 30 };
        openButton.Click += (_, _) => OpenCurrentFrameInFileManager();
        Grid.SetColumn(openButton, 11);
        toolbar.Children.Add(openButton);

        static void ApplyChipVisual(
            ToggleButton chip,
            bool isChecked,
            string activeBackground,
            string activeBorder,
            string activeForeground)
        {
            chip.Background = SolidColorBrush.Parse(isChecked ? activeBackground : "#161616");
            chip.BorderBrush = SolidColorBrush.Parse(isChecked ? activeBorder : "#444");
            chip.Foreground = SolidColorBrush.Parse(isChecked ? activeForeground : "#CCBDBDBD");
            if (chip.Content is TextBlock text)
            {
                text.Foreground = SolidColorBrush.Parse(isChecked ? activeForeground : "#CCBDBDBD");
            }
        }

        static void ApplyNeutralToggleVisual(ToggleButton toggle, bool isChecked)
        {
            toggle.Background = SolidColorBrush.Parse(isChecked ? "#334F78D1" : "#1E2227");
            toggle.BorderBrush = SolidColorBrush.Parse(isChecked ? "#8FB3FF" : "#4A515A");
            toggle.Foreground = SolidColorBrush.Parse(isChecked ? "#F3F8FF" : "#D3DAE3");
        }

        var acceptedToggle = new ToggleButton
        {
            Content = "Accepted",
            Width = 84,
            Height = 30,
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 0),
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ApplyChipTemplate(acceptedToggle);
        acceptedToggle.Bind(ToggleButton.IsCheckedProperty, new Binding("ShowAccepted") { Mode = BindingMode.TwoWay });
        acceptedToggle.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty)
            {
                ApplyChipVisual(acceptedToggle, acceptedToggle.IsChecked == true, "#2F3FAE63", "#7AD68E", "#FFE9F8EE");
            }
        };
        acceptedToggle.AttachedToVisualTree += (_, _) =>
            ApplyChipVisual(acceptedToggle, acceptedToggle.IsChecked == true, "#2F3FAE63", "#7AD68E", "#FFE9F8EE");
        _acceptedToggle = acceptedToggle;

        var rejectedToggle = new ToggleButton
        {
            Content = "Rejected",
            Width = 84,
            Height = 30,
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 0),
            Margin = new Thickness(0, 0, 12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ApplyChipTemplate(rejectedToggle);
        rejectedToggle.Bind(ToggleButton.IsCheckedProperty, new Binding("ShowRejected") { Mode = BindingMode.TwoWay });
        rejectedToggle.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty)
            {
                ApplyChipVisual(rejectedToggle, rejectedToggle.IsChecked == true, "#33C45F5F", "#E07A7A", "#FFF9EAEA");
            }
        };
        rejectedToggle.AttachedToVisualTree += (_, _) =>
            ApplyChipVisual(rejectedToggle, rejectedToggle.IsChecked == true, "#33C45F5F", "#E07A7A", "#FFF9EAEA");
        _rejectedToggle = rejectedToggle;

        var curvatureToggle = new ToggleButton
        {
            Content = "Curvature",
            Width = 90,
            Height = 30,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0),
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ApplyChipTemplate(curvatureToggle);
        curvatureToggle.Bind(ToggleButton.IsCheckedProperty, new Binding("IsCurvatureViewVisible") { Mode = BindingMode.TwoWay });
        curvatureToggle.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty)
            {
                ApplyNeutralToggleVisual(curvatureToggle, curvatureToggle.IsChecked == true);
            }
        };
        curvatureToggle.AttachedToVisualTree += (_, _) => ApplyNeutralToggleVisual(curvatureToggle, curvatureToggle.IsChecked == true);
        Grid.SetColumn(curvatureToggle, 12);
        toolbar.Children.Add(curvatureToggle);

        _zoomText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SolidColorBrush.Parse("#B8BCC0"),
            Text = "Zoom: 1.00x",
        };
        Grid.SetColumn(_zoomText, 14);
        toolbar.Children.Add(_zoomText);
        root.Children.Add(toolbar);

        _filterChipWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
        };

        _filterChipContainer = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8, 4, 8, 6),
            BorderBrush = SolidColorBrush.Parse("#2D3136"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = "VISIBILITY / FILTERS",
                        FontSize = 9,
                        FontWeight = FontWeight.Bold,
                        Foreground = SolidColorBrush.Parse("#666"),
                    },
                    _filterChipWrap,
                },
            },
        };
        Grid.SetRow(_filterChipContainer, 1);
        root.Children.Add(_filterChipContainer);

        var previewStateRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _framePositionText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SolidColorBrush.Parse("#B8BCC0"),
            Width = 80,
            TextAlignment = TextAlignment.Right,
        };
        _framePositionText.Bind(TextBlock.TextProperty, new Binding("PreviewFramePositionText"));
        Grid.SetColumn(_framePositionText, 0);
        previewStateRow.Children.Add(_framePositionText);

        _cacheText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SolidColorBrush.Parse("#7F95A8"),
            Width = 140,
            TextAlignment = TextAlignment.Right,
            Text = "cache: 0",
        };
        Grid.SetColumn(_cacheText, 1);
        previewStateRow.Children.Add(_cacheText);
        Grid.SetRow(previewStateRow, 2);
        root.Children.Add(previewStateRow);

        var caption = new TextBlock
        {
            FontSize = 12,
            Foreground = SolidColorBrush.Parse("#B8BCC0"),
            Margin = new Thickness(0, 0, 0, 10),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        caption.Bind(TextBlock.TextProperty, new Binding("SelectedPreviewCaption"));
        Grid.SetRow(caption, 3);
        root.Children.Add(caption);

        var imageGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,26,14,*,300") };
        Grid.SetRow(imageGrid, 4);

        _cacheIndicatorCanvas = new Canvas
        {
            Width = 12,
            Background = SolidColorBrush.Parse("#181B1E"),
        };
        imageGrid.Children.Add(_cacheIndicatorCanvas);

        _frameSlider = new Slider
        {
            Minimum = 0,
            Maximum = 0,
            Orientation = Orientation.Vertical,
            IsDirectionReversed = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ApplyPreviewSliderTemplate(_frameSlider);
        _frameSlider.Bind(Slider.ValueProperty, new Binding("PreviewFrameSliderValue") { Mode = BindingMode.TwoWay });
        _frameSlider.Bind(Slider.MaximumProperty, new Binding("PreviewFrameSliderMaximum"));
        Grid.SetColumn(_frameSlider, 1);
        imageGrid.Children.Add(_frameSlider);

        _cacheDotCanvas = new Canvas
        {
            Width = 12,
            Background = SolidColorBrush.Parse("#181B1E"),
            IsHitTestVisible = false,
        };
        Grid.SetColumn(_cacheDotCanvas, 2);
        imageGrid.Children.Add(_cacheDotCanvas);

        var imageBorder = new Border
        {
            Background = SolidColorBrush.Parse("#17191B"),
            BorderBrush = SolidColorBrush.Parse("#2D3136"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
        };
        Grid.SetColumn(imageBorder, 3);

        _previewImage = new Image
        {
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapInterpolationMode(_previewImage, BitmapInterpolationMode.None);

        _previewImage.Bind(Image.SourceProperty, new Binding("SelectedPreviewImage"));

        _previewSurface = new Canvas
        {
            ClipToBounds = true,
        };
        _previewSurface.Children.Add(_previewImage);
        _previewImage.PointerPressed += PreviewImageOnPointerPressed;
        _previewSurface.PointerMoved += PreviewScrollOnPointerMoved;
        _previewSurface.PointerReleased += PreviewScrollOnPointerReleased;

        _overlayCanvas = new Canvas
        {
            IsHitTestVisible = true,
        };

        _starTrailOverlayCanvas = new Canvas { IsHitTestVisible = false };
        _orientationOverlayCanvas = new Canvas { IsHitTestVisible = false };
        _curvatureOverlayCanvas = new Canvas { IsHitTestVisible = false };
        _overlayCanvas.Children.Add(_curvatureOverlayCanvas);
        _overlayCanvas.Children.Add(_orientationOverlayCanvas);
        _overlayCanvas.Children.Add(_starTrailOverlayCanvas);

        _curvatureTooltipText = new TextBlock
        {
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
        };
        _curvatureTooltip = new Border
        {
            Background = SolidColorBrush.Parse("#CC101014"),
            BorderBrush = SolidColorBrush.Parse("#80FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = _curvatureTooltipText,
        };

        _roiRect = new Rectangle
        {
            Stroke = SolidColorBrush.Parse("#FFD700"),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = Brushes.Transparent,
            IsVisible = false,
        };
        _roiRect.PointerPressed += RoiBodyOnPointerPressed;
        _overlayCanvas.Children.Add(_roiRect);

        _roiDragRect = new Rectangle
        {
            Stroke = SolidColorBrush.Parse("#FFA500"),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 2],
            Fill = SolidColorBrush.Parse("#1EFFA500"),
            IsVisible = false,
            IsHitTestVisible = false,
        };
        _overlayCanvas.Children.Add(_roiDragRect);

        for (var index = 0; index < _roiHandles.Length; index++)
        {
            var handle = new Rectangle
            {
                Width = 10,
                Height = 10,
                Fill = SolidColorBrush.Parse("#202020"),
                Stroke = SolidColorBrush.Parse("#FFD700"),
                StrokeThickness = 1,
                IsVisible = false,
                Tag = index,
            };
            handle.PointerPressed += RoiHandleOnPointerPressed;
            _roiHandles[index] = handle;
            _overlayCanvas.Children.Add(handle);
        }

        _loupeImage = new Image { Width = 124, Height = 140, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(_loupeImage, BitmapInterpolationMode.None);
        _loupeXText = CreateLoupeText("X    0");
        _loupeYText = CreateLoupeText("Y    0");
        _loupeKText = CreateLoupeText("K    0");
        _loupeMinText = CreateLoupeText("Min  0");
        _loupeMinText.Margin = new Thickness(0, 6, 0, 0);
        _loupeMaxText = CreateLoupeText("Max  0");
        _loupeMeanText = CreateLoupeText("Mean 0.00");

        var loupeValues = new StackPanel { Margin = new Thickness(8, 6), Spacing = 0 };
        loupeValues.Children.Add(_loupeXText);
        loupeValues.Children.Add(_loupeYText);
        loupeValues.Children.Add(_loupeKText);
        loupeValues.Children.Add(_loupeMinText);
        loupeValues.Children.Add(_loupeMaxText);
        loupeValues.Children.Add(_loupeMeanText);

        var loupeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("124,*") };
        loupeGrid.Children.Add(new Border
        {
            Background = SolidColorBrush.Parse("#1A1A1A"),
            BorderBrush = SolidColorBrush.Parse("#33000000"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _loupeImage,
        });
        Grid.SetColumn(loupeValues, 1);
        loupeGrid.Children.Add(loupeValues);

        _loupeBorder = new Border
        {
            Width = 204,
            Height = 140,
            Background = SolidColorBrush.Parse("#24323C"),
            BorderBrush = SolidColorBrush.Parse("#3A4148"),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = loupeGrid,
        };
        _overlayCanvas.Children.Add(_loupeBorder);

        var starOverlayBadge = new Border
        {
            Background = SolidColorBrush.Parse("#99203E6D"),
            BorderBrush = SolidColorBrush.Parse("#7AB3FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(10),
            Child = new TextBlock { Text = "Star Debug", Foreground = SolidColorBrush.Parse("#DDEBFF"), FontSize = 11 },
        };
        starOverlayBadge.Bind(IsVisibleProperty, new Binding("IsStarDebugOverlayVisible"));
        _overlayCanvas.Children.Add(starOverlayBadge);

        var orientationOverlayBadge = new Border
        {
            Background = SolidColorBrush.Parse("#9948285F"),
            BorderBrush = SolidColorBrush.Parse("#C9A8FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(10, 40, 10, 10),
            Child = new TextBlock { Text = "Orientation Debug", Foreground = SolidColorBrush.Parse("#F1E6FF"), FontSize = 11 },
        };
        orientationOverlayBadge.Bind(IsVisibleProperty, new Binding("IsOrientationDebugOverlayVisible"));
        _overlayCanvas.Children.Add(orientationOverlayBadge);

        var curvatureOverlayBadge = new Border
        {
            Background = SolidColorBrush.Parse("#995F3E1F"),
            BorderBrush = SolidColorBrush.Parse("#FFC57A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(10, 70, 10, 10),
            Child = new TextBlock { Text = "Curvature View", Foreground = SolidColorBrush.Parse("#FFE8C7"), FontSize = 11 },
        };
        curvatureOverlayBadge.Bind(IsVisibleProperty, new Binding("IsCurvatureViewVisible"));
        _overlayCanvas.Children.Add(curvatureOverlayBadge);

        _overlayCanvas.PointerPressed += OverlayCanvasOnPointerPressed;
        _overlayCanvas.PointerMoved += OverlayCanvasOnPointerMoved;
        _overlayCanvas.PointerReleased += OverlayCanvasOnPointerReleased;
        _overlayCanvas.PointerExited += (_, _) => _curvatureTooltip.IsVisible = false;

        _previewLayer = new Grid();
        _previewLayer.Children.Add(_previewSurface);
        _previewLayer.Children.Add(_overlayCanvas);
        _previewLayer.PointerWheelChanged += PreviewLayerOnPointerWheelChanged;
        _previewLayer.AddHandler(PointerPressedEvent, PreviewLayerOnPointerPressed, RoutingStrategies.Tunnel);

        imageBorder.Child = _previewLayer;
        imageGrid.Children.Add(imageBorder);

        var scoreValueText = new TextBlock { FontSize = 40, FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#E67575") };
        scoreValueText.Bind(TextBlock.TextProperty, new Binding("SelectedResult.ScoreValueText"));
        scoreValueText.Bind(TextBlock.ForegroundProperty, new Binding("SelectedResult.QualityColor"));

        var scoreQualityText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = SolidColorBrush.Parse("#AAB3BC"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
        };
        scoreQualityText.Bind(TextBlock.TextProperty, new Binding("SelectedResult.QualityLabel"));
        scoreQualityText.Bind(TextBlock.ForegroundProperty, new Binding("SelectedResult.QualityColor"));

        var scoreProgressBar = new ProgressBar
        {
            Height = 10,
            Margin = new Thickness(0, 2, 0, 0),
            Minimum = 0,
            Maximum = 100,
            Background = SolidColorBrush.Parse("#303338"),
        };
        scoreProgressBar.Bind(RangeBase.ValueProperty, new Binding("SelectedResult.ScoreProgressPercent"));
        scoreProgressBar.Bind(ProgressBar.ForegroundProperty, new Binding("SelectedResult.QualityColor"));

        var scoreToggleButton = new Button
        {
            Height = 30,
            MinWidth = 118,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 4),
            FontWeight = FontWeight.SemiBold,
            Foreground = SolidColorBrush.Parse("#F5F5F5"),
            BorderBrush = SolidColorBrush.Parse("#4B4F54"),
            BorderThickness = new Thickness(1),
            Content = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = SolidColorBrush.Parse("#F5F5F5") },
        };
        scoreToggleButton.Bind(Button.CommandProperty, new Binding("ToggleRejectCommand"));
        scoreToggleButton.Bind(Button.CommandParameterProperty, new Binding("SelectedResult"));
        scoreToggleButton.Bind(Button.BackgroundProperty, new Binding("SelectedResult.RejectionStateColor"));
        if (scoreToggleButton.Content is TextBlock scoreToggleText)
        {
            scoreToggleText.Bind(TextBlock.TextProperty, new Binding("SelectedResult.RejectionStateLabel"));
            scoreToggleText.TextAlignment = TextAlignment.Center;
        }
        scoreToggleButton.Margin = new Thickness(8, 0, 0, 0);
        scoreToggleButton.VerticalAlignment = VerticalAlignment.Top;

        var scoreHeaderText = new TextBlock
        {
            Text = "SCORE",
            Foreground = SolidColorBrush.Parse("#8E9AA6"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };

        var scoreValueRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 2),
            Children =
            {
                scoreValueText,
                new TextBlock
                {
                    Text = "/ 5",
                    Foreground = SolidColorBrush.Parse("#C8C8C8"),
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 4),
                },
            },
        };

        var scorePanelGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowSpacing = 6,
        };
        scorePanelGrid.Children.Add(scoreHeaderText);
        Grid.SetRow(scoreValueRow, 1);
        scorePanelGrid.Children.Add(scoreValueRow);
        Grid.SetRow(scoreQualityText, 1);
        Grid.SetColumn(scoreQualityText, 1);
        scorePanelGrid.Children.Add(scoreQualityText);
        Grid.SetColumn(scoreToggleButton, 1);
        Grid.SetRow(scoreToggleButton, 0);
        Grid.SetRowSpan(scoreToggleButton, 2);
        scorePanelGrid.Children.Add(scoreToggleButton);
        Grid.SetRow(scoreProgressBar, 2);
        Grid.SetColumnSpan(scoreProgressBar, 2);
        scorePanelGrid.Children.Add(scoreProgressBar);

        var stfTargetSlider = new Slider { Minimum = 0.01, Maximum = 0.5, Width = 220 };
        ApplyPreviewSliderTemplate(stfTargetSlider);
        stfTargetSlider.Bind(Slider.ValueProperty, new Binding("StfTargetBackground") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        var stfShadowsSlider = new Slider { Minimum = 0.0, Maximum = 1.0, Width = 170 };
        ApplyPreviewSliderTemplate(stfShadowsSlider);
        stfShadowsSlider.Bind(Slider.ValueProperty, new Binding("StfShadows") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        var stfMidtonesSlider = new Slider { Minimum = 0.0, Maximum = 1.0, Width = 170 };
        ApplyPreviewSliderTemplate(stfMidtonesSlider);
        stfMidtonesSlider.Bind(Slider.ValueProperty, new Binding("StfMidtones") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        var stfHighlightsSlider = new Slider { Minimum = 0.0, Maximum = 1.0, Width = 170 };
        ApplyPreviewSliderTemplate(stfHighlightsSlider);
        stfHighlightsSlider.Bind(Slider.ValueProperty, new Binding("StfHighlights") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        var autoStretchButton = new Button
        {
            Height = 28,
            Content = "Auto Stretch",
        };
        autoStretchButton.Click += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ApplyAutoStretchToSelectedPreview();
            }
        };

        var roiButton = new Button
        {
            Height = 28,
            Content = "Show / edit ROI",
        };
        roiButton.Click += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.IsRoiOverlayVisible = !vm.IsRoiOverlayVisible;
            }
        };

        Control MetricRow(string label, string path, string? indicatorPath = null)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
                ColumnSpacing = 6,
            };

            if (!string.IsNullOrWhiteSpace(indicatorPath))
            {
                var marker = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                marker.Bind(Shape.FillProperty, new Binding(indicatorPath));
                row.Children.Add(marker);
            }

            var labelText = new TextBlock
            {
                Foreground = SolidColorBrush.Parse("#DDE4EA"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Text = label,
            };
            Grid.SetColumn(labelText, 1);
            row.Children.Add(labelText);

            var valueText = new TextBlock
            {
                Foreground = SolidColorBrush.Parse("#FFE7B0"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            valueText.Bind(TextBlock.TextProperty, new Binding(path));
            if (!string.IsNullOrWhiteSpace(indicatorPath))
            {
                valueText.Bind(TextBlock.ForegroundProperty, new Binding(indicatorPath));
            }
            Grid.SetColumn(valueText, 3);
            row.Children.Add(valueText);
            return row;
        }

        Control StfSliderRow(string label, Slider slider, string valuePath)
        {
            var value = new TextBlock
            {
                Foreground = SolidColorBrush.Parse("#F2C56E"),
                FontFamily = new FontFamily("Consolas"),
                Width = 58,
                TextAlignment = TextAlignment.Right,
            };
            value.Bind(TextBlock.TextProperty, new Binding(valuePath) { StringFormat = "{0:F4}" });

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 6,
            };
            row.Children.Add(new TextBlock { Text = label, Foreground = SolidColorBrush.Parse("#AAB3BC"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            return row;
        }

        var inspectionChevron = new TextBlock
        {
            Text = "▸",
            Foreground = SolidColorBrush.Parse("#9BA6B2"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var inspectionBody = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Spacing = 8,
            IsVisible = false,
            Children =
            {
                new TextBlock { Text = "Stretch (STF)", Foreground = SolidColorBrush.Parse("#E0E6ED") },
                autoStretchButton,
                StfSliderRow("Shadows", stfShadowsSlider, "StfShadows"),
                StfSliderRow("Midtones", stfMidtonesSlider, "StfMidtones"),
                StfSliderRow("Highlights", stfHighlightsSlider, "StfHighlights"),
                StfSliderRow("Target Background", stfTargetSlider, "StfTargetBackground"),
                roiButton,
            },
        };

        var inspectionHeader = new ToggleButton
        {
            IsChecked = false,
            Background = SolidColorBrush.Parse("#14181C"),
            BorderBrush = SolidColorBrush.Parse("#2E353D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ApplyChipTemplate(inspectionHeader);
        inspectionHeader.Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = "INSPECTION",
                    Foreground = SolidColorBrush.Parse("#D0D8E0"),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                inspectionChevron,
            },
        };
        Grid.SetColumn(inspectionChevron, 1);

        void UpdateInspectionCardVisual(bool expanded)
        {
            inspectionBody.IsVisible = expanded;
            inspectionChevron.Text = expanded ? "▾" : "▸";
            inspectionHeader.Background = SolidColorBrush.Parse(expanded ? "#1B2127" : "#14181C");
            inspectionHeader.BorderBrush = SolidColorBrush.Parse(expanded ? "#3A4450" : "#2E353D");
        }

        inspectionHeader.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty)
            {
                UpdateInspectionCardVisual(inspectionHeader.IsChecked == true);
            }
        };
        UpdateInspectionCardVisual(false);

        var sidePanel = new Border
        {
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(10),
            Background = SolidColorBrush.Parse("#1A1D20"),
            BorderBrush = SolidColorBrush.Parse("#2D3136"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Border
                    {
                        BorderBrush = SolidColorBrush.Parse("#2D3136"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10),
                        Child = scorePanelGrid,
                    },
                    new Border
                    {
                        BorderBrush = SolidColorBrush.Parse("#2D3136"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10),
                        Child = new StackPanel
                        {
                            Spacing = 0,
                            Children =
                            {
                                inspectionHeader,
                                inspectionBody,
                            },
                        },
                    },
                    new Border
                    {
                        BorderBrush = SolidColorBrush.Parse("#2D3136"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10),
                        Child = new StackPanel
                        {
                            Spacing = 5,
                            Children =
                            {
                                new TextBlock { Text = "METRICS", Foreground = SolidColorBrush.Parse("#D0D8E0"), FontSize = 11, FontWeight = FontWeight.SemiBold },
                                MetricRow("Date/time", "SelectedResult.TimestampDisplay"),
                                MetricRow("Filter", "SelectedResult.FilterDisplay"),
                                MetricRow("Exposure", "SelectedResult.ExposureDisplay"),
                                MetricRow("FWHM", "SelectedResult.FwhmPixelDisplay", "SelectedResult.FwhmIndicatorColor"),
                                MetricRow("FWHM sky", "SelectedResult.FwhmArcsecDisplay", "SelectedResult.FwhmIndicatorColor"),
                                MetricRow("HFR", "SelectedResult.HfrDisplay", "SelectedResult.HfrIndicatorColor"),
                                MetricRow("Stars", "SelectedResult.StarCount", "SelectedResult.StarsIndicatorColor"),
                                MetricRow("SQM", "SelectedResult.SqmDisplay"),
                                MetricRow("Sky temp", "SelectedResult.SkyTempDisplay"),
                                MetricRow("Eccentricity", "SelectedResult.EccentricityDisplay", "SelectedResult.EccentricityIndicatorColor"),
                                MetricRow("Mean BG", "SelectedResult.MeanBackgroundDisplay", "SelectedResult.MeanBackgroundIndicatorColor"),
                                MetricRow("Median", "SelectedResult.MedianDisplay"),
                                MetricRow("MAD", "SelectedResult.MadDisplay"),
                                MetricRow("Min", "SelectedResult.MinDisplay"),
                                MetricRow("Max", "SelectedResult.MaxDisplay"),
                                MetricRow("Trail conf.", "SelectedResult.TrailText", "SelectedResult.TrailIndicatorColor"),
                                MetricRow("Clouds", "SelectedResult.CloudText", "SelectedResult.CloudIndicatorColor"),
                            },
                        },
                    },
                    new Border
                    {
                        BorderBrush = SolidColorBrush.Parse("#2D3136"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10),
                        Child = new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                new TextBlock { Text = "KEYBOARD SHORTCUTS", Foreground = SolidColorBrush.Parse("#D0D8E0"), FontSize = 11, FontWeight = FontWeight.SemiBold },
                                new TextBlock { Text = "Left / Right: previous or next frame", Foreground = SolidColorBrush.Parse("#AAB3BC"), FontSize = 11 },
                                new TextBlock { Text = "R: reject or unreject current frame", Foreground = SolidColorBrush.Parse("#AAB3BC"), FontSize = 11 },
                                new TextBlock { Text = "Mouse Wheel: zoom at cursor position", Foreground = SolidColorBrush.Parse("#AAB3BC"), FontSize = 11 },
                                new TextBlock { Text = "Ctrl + Drag: draw square ROI", Foreground = SolidColorBrush.Parse("#AAB3BC"), FontSize = 11 },
                                new TextBlock { Text = "Escape: cancel ROI drag", Foreground = SolidColorBrush.Parse("#AAB3BC"), FontSize = 11 },
                            },
                        },
                    },
                },
            },
        };
        Grid.SetColumn(sidePanel, 4);
        imageGrid.Children.Add(sidePanel);
        root.Children.Add(imageGrid);

        Content = root;

        _playTimer.Interval = TimeSpan.FromSeconds(PlaybackIntervals[_playbackIntervalIndex]);
        _playTimer.Tick += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            var moved = vm.SelectNextResult();
            if (!moved)
            {
                _playTimer.Stop();
                _playButton.Content = "▶";
            }
        };

        _previewImage.PropertyChanged += (_, e) =>
        {
            if (e.Property == Image.SourceProperty)
            {
                HideLoupe();

                if (_previewImage.Source is null)
                {
                    if (DataContext is MainWindowViewModel vm && vm.SelectedResult is null)
                    {
                        _pendingViewState = null;
                    }

                    ScheduleOverlayRedraw();
                    return;
                }

                var viewState = _pendingViewState;
                _pendingViewState = null;
                if (!_hasInitializedView || viewState is null)
                {
                    Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Background);
                }
                else
                {
                    Dispatcher.UIThread.Post(() => RestoreViewState(viewState), DispatcherPriority.Background);
                }
                Dispatcher.UIThread.Post(UpdateCacheIndicators, DispatcherPriority.Background);
                ScheduleOverlayRedraw();
            }
        };

        _overlayCanvas.SizeChanged += (_, _) => UpdateRoiRect();
        _overlayCanvas.SizeChanged += (_, _) => ScheduleOverlayRedraw();
        _previewSurface.SizeChanged += (_, _) => ApplyImageLayout();

        DataContextChanged += (_, _) => AttachVmSubscriptions();
        AttachVmSubscriptions();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            _hasInitializedView = true;
            Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Background);
            Dispatcher.UIThread.Post(UpdateCacheIndicators, DispatcherPriority.Background);
            ScheduleOverlayRedraw();
        };

        SetPlaybackInterval(_playbackIntervalIndex);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
                if (e.Key == Key.Escape && (_isRoiDragging || _roiEditMode != RoiEditMode.None))
                {
                    if (_isRoiDragging)
                    {
                        CancelRoiDrag();
                    }
                    else
                    {
                        CancelRoiEdit();
                    }
                    e.Handled = true;
                    return;
                }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            QueueKeyboardNavigation(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            QueueKeyboardNavigation(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            _playTimer.IsEnabled = !_playTimer.IsEnabled;
            _playButton.Content = _playTimer.IsEnabled ? "⏸" : "▶";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.R)
        {
            vm.ToggleSelectedReject();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.IsStarDebugOverlayVisible = !vm.IsStarDebugOverlayVisible;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.IsOrientationDebugOverlayVisible = !vm.IsOrientationDebugOverlayVisible;
            e.Handled = true;
            return;
        }

    }

    private void AttachVmSubscriptions()
    {
        if (_attachedVm is not null)
        {
            _attachedVm.PropertyChanged -= VmOnPropertyChanged;
            _attachedVm.FilterChips.CollectionChanged -= FilterChipsOnCollectionChanged;
        }

        _attachedVm = DataContext as MainWindowViewModel;
        if (_attachedVm is null)
        {
            return;
        }

        _attachedVm.PropertyChanged += VmOnPropertyChanged;
        _attachedVm.FilterChips.CollectionChanged += FilterChipsOnCollectionChanged;
        _cacheText.Text = $"cache: {_attachedVm.CachedPreviewCount}";
        SyncRoiFromViewModel();
        UpdateRoiRect();
        UpdateCacheIndicators();
        RebuildFilterChipRow();
        ScheduleOverlayRedraw();
    }

    private void FilterChipsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildFilterChipRow();
    }

    private void RebuildFilterChipRow()
    {
        _filterChipWrap.Children.Clear();
        _filterChipContainer.IsVisible = true;
        _filterChipWrap.Children.Add(_acceptedToggle);
        _filterChipWrap.Children.Add(_rejectedToggle);

        if (DataContext is not MainWindowViewModel vm || !vm.HasFilterChips)
        {
            return;
        }

        foreach (var chip in vm.FilterChips)
        {
            var (checkedBackground, checkedBorder, checkedForeground) = Rejector.Core.Services.FilterClassifier.GetColors(chip.Category);
            var toggle = new ToggleButton
            {
                Height = 30,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(10, 0),
                FontWeight = FontWeight.SemiBold,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = chip.DisplayName },
            };
            ToolTip.SetTip(toggle, "Left-click toggles this filter. Right-click shows only this filter.");
            ApplyChipTemplate(toggle);

            toggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(FilterChipViewModel.IsSelected))
            {
                Source = chip,
                Mode = BindingMode.TwoWay,
            });
            toggle.PointerPressed += (_, args) =>
            {
                var point = args.GetCurrentPoint(this);
                if (!point.Properties.IsRightButtonPressed || DataContext is not MainWindowViewModel viewModel)
                {
                    return;
                }

                viewModel.ToggleFilterExclusively(chip);
                args.Handled = true;
            };

            toggle.PropertyChanged += (_, args) =>
            {
                if (args.Property == ToggleButton.IsCheckedProperty)
                {
                    var isChecked = toggle.IsChecked == true;
                    toggle.Background = SolidColorBrush.Parse(isChecked ? checkedBackground : "#0A0A0A");
                    toggle.BorderBrush = SolidColorBrush.Parse(isChecked ? checkedBorder : "#222222");
                    toggle.Foreground = SolidColorBrush.Parse(isChecked ? checkedForeground : "#55777777");
                    if (toggle.Content is TextBlock text)
                    {
                        text.Foreground = SolidColorBrush.Parse(isChecked ? checkedForeground : "#55777777");
                    }
                }
            };
            var isSelected = chip.IsSelected;
            toggle.Background = SolidColorBrush.Parse(isSelected ? checkedBackground : "#0A0A0A");
            toggle.BorderBrush = SolidColorBrush.Parse(isSelected ? checkedBorder : "#222222");
            toggle.Foreground = SolidColorBrush.Parse(isSelected ? checkedForeground : "#55777777");
            if (toggle.Content is TextBlock contentText)
            {
                contentText.Foreground = SolidColorBrush.Parse(isSelected ? checkedForeground : "#55777777");
            }

            _filterChipWrap.Children.Add(toggle);
        }
    }

    private void VmOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_attachedVm is null)
        {
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CachedPreviewCount))
        {
            _cacheText.Text = $"cache: {_attachedVm.CachedPreviewCount}";
            UpdateCacheIndicators();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedResult)
                 || e.PropertyName == nameof(MainWindowViewModel.PreviewFrameSliderValue)
                 || e.PropertyName == nameof(MainWindowViewModel.PreviewFrameSliderMaximum))
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedResult))
            {
                _pendingViewState = CaptureViewState();
            }
            UpdateCacheIndicators();
            ScheduleOverlayRedraw();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsRoiOverlayVisible))
        {
            UpdateRoiRect();
            ScheduleOverlayRedraw();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.CurrentManualRoi))
        {
            SyncRoiFromViewModel();
            UpdateRoiRect();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsStarDebugOverlayVisible)
                 || e.PropertyName == nameof(MainWindowViewModel.IsOrientationDebugOverlayVisible)
                 || e.PropertyName == nameof(MainWindowViewModel.IsCurvatureViewVisible))
        {
            ScheduleOverlayRedraw();
        }
    }

    private void SetPlaybackInterval(int newIndex)
    {
        _playbackIntervalIndex = Math.Clamp(newIndex, 0, PlaybackIntervals.Length - 1);
        var value = PlaybackIntervals[_playbackIntervalIndex];
        _playTimer.Interval = TimeSpan.FromSeconds(value);
        _intervalText.Text = value < 1.0 ? $"{value * 1000:0} ms" : $"{value:0.#} s";
    }

    private void QueueKeyboardNavigation(int direction)
    {
        if (direction == 0 || DataContext is not MainWindowViewModel vm || vm.Results.Count == 0)
        {
            return;
        }

        var currentIndex = vm.SelectedResult is null
            ? 0
            : Math.Max(0, vm.Results.IndexOf(vm.SelectedResult));
        var baseIndex = _queuedKeyboardNavigationIndex ?? _activeKeyboardNavigationIndex ?? currentIndex;
        var targetIndex = Math.Clamp(baseIndex + direction, 0, vm.Results.Count - 1);
        if (targetIndex == baseIndex && targetIndex == currentIndex)
        {
            return;
        }

        _queuedKeyboardNavigationIndex = targetIndex;
        if (_isKeyboardNavigationInProgress)
        {
            return;
        }

        _ = ProcessQueuedKeyboardNavigationAsync();
    }

    private async Task ProcessQueuedKeyboardNavigationAsync()
    {
        if (_isKeyboardNavigationInProgress)
        {
            return;
        }

        _isKeyboardNavigationInProgress = true;
        try
        {
            while (_queuedKeyboardNavigationIndex is int targetIndex)
            {
                _queuedKeyboardNavigationIndex = null;
                _activeKeyboardNavigationIndex = targetIndex;

                if (DataContext is MainWindowViewModel vm)
                {
                    vm.SelectResultAtIndex(targetIndex);
                }

                await Task.Yield();
            }
        }
        finally
        {
            _activeKeyboardNavigationIndex = null;
            _isKeyboardNavigationInProgress = false;
        }
    }

    private void FitToView()
    {
        if (_previewImage.Source is not IImage image)
        {
            return;
        }

        var viewportWidth = Math.Max(0, _previewSurface.Bounds.Width - 12);
        var viewportHeight = Math.Max(0, _previewSurface.Bounds.Height - 12);
        if (viewportWidth <= 0 || viewportHeight <= 0 || image.Size.Width <= 0 || image.Size.Height <= 0)
        {
            return;
        }

        var scaleX = viewportWidth / image.Size.Width;
        var scaleY = viewportHeight / image.Size.Height;
        SetZoom(Math.Min(scaleX, scaleY));
        _imageLeft = (_previewSurface.Bounds.Width - (image.Size.Width * _previewScale.ScaleX)) / 2.0;
        _imageTop = (_previewSurface.Bounds.Height - (image.Size.Height * _previewScale.ScaleY)) / 2.0;
        ApplyImageLayout();
    }

    private void PreviewLayerOnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_previewImage.Source is null || e.Delta.Y == 0)
        {
            return;
        }

        var factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        SetZoomAroundViewerPoint(e.GetPosition(_previewSurface), _previewScale.ScaleX * factor);
        e.Handled = true;
    }

    private void PreviewLayerOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_previewImage.Source is null || !e.GetCurrentPoint(_previewLayer).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        BeginPanning(e, PanMouseButton.Middle);
    }

    private void PreviewImageOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_previewImage.Source is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_previewImage);
        if (point.Properties.IsRightButtonPressed)
        {
            _isLoupeActive = true;
            e.Pointer.Capture(_previewSurface);
            ShowLoupeAt(e.GetPosition(_previewImage));
            e.Handled = true;
            return;
        }

        if (!(point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed))
        {
            return;
        }

        if (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _isRoiDragging = true;
            _roiDragOriginImage = e.GetPosition(_previewImage);
            _roiDragPointer = e.Pointer;
            _previewImage.Cursor = new Cursor(StandardCursorType.Cross);
            e.Pointer.Capture(_previewSurface);
            UpdateRoiDrag(_roiDragOriginImage);
            e.Handled = true;
            return;
        }

        BeginPanning(e, point.Properties.IsMiddleButtonPressed ? PanMouseButton.Middle : PanMouseButton.Left);
    }

    private void BeginPanning(PointerPressedEventArgs e, PanMouseButton button)
    {
        if (_isRoiDragging || _roiEditMode != RoiEditMode.None)
        {
            return;
        }

        _isPanning = true;
        _activePanButton = button;
        _panStartPoint = e.GetPosition(_previewSurface);
        _panStartOffset = new Vector(_imageLeft, _imageTop);
        _previewImage.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(_previewSurface);
        e.Handled = true;
    }

    private void PreviewScrollOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isRoiDragging)
        {
            if (!e.GetCurrentPoint(_previewSurface).Properties.IsLeftButtonPressed
                || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                CancelRoiDrag();
                e.Pointer.Capture(null);
                return;
            }

            UpdateRoiDrag(e.GetPosition(_previewImage));
            e.Handled = true;
            return;
        }

        if (_isLoupeActive)
        {
            if (!e.GetCurrentPoint(_previewSurface).Properties.IsRightButtonPressed)
            {
                HideLoupe();
                e.Pointer.Capture(null);
                return;
            }

            ShowLoupeAt(e.GetPosition(_previewImage));
            e.Handled = true;
            return;
        }

        if (!_isPanning)
        {
            return;
        }

        var panStillPressed = _activePanButton switch
        {
            PanMouseButton.Middle => e.GetCurrentPoint(_previewSurface).Properties.IsMiddleButtonPressed,
            PanMouseButton.Left => e.GetCurrentPoint(_previewSurface).Properties.IsLeftButtonPressed,
            _ => false,
        };

        if (!panStillPressed)
        {
            StopPanning(e.Pointer);
            return;
        }

        var point = e.GetPosition(_previewSurface);
        var delta = point - _panStartPoint;
        _imageLeft = _panStartOffset.X + delta.X;
        _imageTop = _panStartOffset.Y + delta.Y;
        ApplyImageLayout();
        e.Handled = true;
    }

    private void PreviewScrollOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isRoiDragging)
        {
            CommitRoiDrag(e.GetPosition(_previewImage));
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_isLoupeActive)
        {
            HideLoupe();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_isPanning)
        {
            return;
        }

        StopPanning(e.Pointer);
        e.Handled = true;
    }

    private void StopPanning(IPointer pointer)
    {
        _isPanning = false;
        _activePanButton = PanMouseButton.None;
        _previewImage.Cursor = null;
        pointer.Capture(null);
    }

    private void UpdateRoiDrag(Point currentImage)
    {
        if (_previewImage.Bounds.Width <= 0 || _previewImage.Bounds.Height <= 0 || !TryGetImageDisplayRect(out var imageRect))
        {
            return;
        }

        var deltaX = currentImage.X - _roiDragOriginImage.X;
        var deltaY = currentImage.Y - _roiDragOriginImage.Y;
        var side = Math.Min(Math.Abs(deltaX), Math.Abs(deltaY));
        var endX = Math.Clamp(_roiDragOriginImage.X + (deltaX >= 0 ? side : -side), 0, _previewImage.Bounds.Width);
        var endY = Math.Clamp(_roiDragOriginImage.Y + (deltaY >= 0 ? side : -side), 0, _previewImage.Bounds.Height);
        var left = Math.Min(Math.Clamp(_roiDragOriginImage.X, 0, _previewImage.Bounds.Width), endX);
        var top = Math.Min(Math.Clamp(_roiDragOriginImage.Y, 0, _previewImage.Bounds.Height), endY);
        var width = Math.Abs(endX - _roiDragOriginImage.X);
        var height = Math.Abs(endY - _roiDragOriginImage.Y);

        _roiDragRect.Width = width / _previewImage.Bounds.Width * imageRect.Width;
        _roiDragRect.Height = height / _previewImage.Bounds.Height * imageRect.Height;
        Canvas.SetLeft(_roiDragRect, imageRect.Left + (left / _previewImage.Bounds.Width * imageRect.Width));
        Canvas.SetTop(_roiDragRect, imageRect.Top + (top / _previewImage.Bounds.Height * imageRect.Height));
        _roiDragRect.IsVisible = true;
    }

    private void CommitRoiDrag(Point currentImage)
    {
        var deltaX = currentImage.X - _roiDragOriginImage.X;
        var deltaY = currentImage.Y - _roiDragOriginImage.Y;
        var side = Math.Min(Math.Abs(deltaX), Math.Abs(deltaY));
        if (side >= 4 && _previewImage.Bounds.Width > 0 && _previewImage.Bounds.Height > 0 && DataContext is MainWindowViewModel vm)
        {
            var originX = Math.Clamp(_roiDragOriginImage.X, 0, _previewImage.Bounds.Width);
            var originY = Math.Clamp(_roiDragOriginImage.Y, 0, _previewImage.Bounds.Height);
            var endX = Math.Clamp(originX + (deltaX >= 0 ? side : -side), 0, _previewImage.Bounds.Width);
            var endY = Math.Clamp(originY + (deltaY >= 0 ? side : -side), 0, _previewImage.Bounds.Height);
            vm.SetManualRoi((
                Math.Min(originX, endX) / _previewImage.Bounds.Width,
                Math.Min(originY, endY) / _previewImage.Bounds.Height,
                Math.Abs(endX - originX) / _previewImage.Bounds.Width,
                Math.Abs(endY - originY) / _previewImage.Bounds.Height));
        }

        CancelRoiDrag();
    }

    private void CancelRoiDrag()
    {
        _isRoiDragging = false;
        _roiDragPointer?.Capture(null);
        _roiDragPointer = null;
        _roiDragRect.IsVisible = false;
        _previewImage.Cursor = null;
    }

    private static TextBlock CreateLoupeText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = SolidColorBrush.Parse("#EEFFFFFF"),
            };
        }

    private void ShowLoupeAt(Point imagePoint)
        {
            if (DataContext is not MainWindowViewModel vm
                || _previewImage.Bounds.Width <= 0
                || _previewImage.Bounds.Height <= 0
                || !TryGetImageDisplayRect(out var imageRect))
            {
                return;
            }

            var source = vm.SelectedPreviewImage;
            if (source is null || source.PixelSize.Width <= 0 || source.PixelSize.Height <= 0)
            {
                return;
            }

            var pixelX = Math.Clamp(
                (int)Math.Round((imagePoint.X / _previewImage.Bounds.Width) * Math.Max(0, source.PixelSize.Width - 1)),
                0,
                Math.Max(0, source.PixelSize.Width - 1));
            var pixelY = Math.Clamp(
                (int)Math.Round((imagePoint.Y / _previewImage.Bounds.Height) * Math.Max(0, source.PixelSize.Height - 1)),
                0,
                Math.Max(0, source.PixelSize.Height - 1));
            var sample = vm.BuildPreviewLoupeSample(pixelX, pixelY, LoupeSampleSize);
            if (sample is null)
            {
                return;
            }

            if (_loupeImage.Source is Bitmap oldBitmap)
            {
                oldBitmap.Dispose();
            }

            var bitmap = new WriteableBitmap(
                new PixelSize(sample.Width, sample.Height),
                new Vector(96, 96),
                PixelFormats.Gray8,
                AlphaFormat.Opaque);
            using (var locked = bitmap.Lock())
            {
                for (var row = 0; row < sample.Height; row++)
                {
                    Marshal.Copy(
                        sample.Pixels,
                        row * sample.Width,
                        IntPtr.Add(locked.Address, row * locked.RowBytes),
                        sample.Width);
                }
            }

            _loupeImage.Source = bitmap;
            _loupeXText.Text = $"X    {sample.PixelX}";
            _loupeYText.Text = $"Y    {sample.PixelY}";
            _loupeKText.Text = $"K    {sample.CenterValue}";
            _loupeMinText.Text = $"Min  {sample.MinValue}";
            _loupeMaxText.Text = $"Max  {sample.MaxValue}";
            _loupeMeanText.Text = $"Mean {sample.MeanValue:F2}";

            var hostX = imageRect.Left + ((imagePoint.X / _previewImage.Bounds.Width) * imageRect.Width);
            var hostY = imageRect.Top + ((imagePoint.Y / _previewImage.Bounds.Height) * imageRect.Height);
            Canvas.SetLeft(_loupeBorder, Math.Clamp(hostX + 16, 0, Math.Max(0, _overlayCanvas.Bounds.Width - _loupeBorder.Width)));
            Canvas.SetTop(_loupeBorder, Math.Clamp(hostY + 16, 0, Math.Max(0, _overlayCanvas.Bounds.Height - _loupeBorder.Height)));
            _loupeBorder.IsVisible = true;
        }

    private void HideLoupe()
        {
            _isLoupeActive = false;
            _loupeBorder.IsVisible = false;
            if (_loupeImage.Source is Bitmap bitmap)
            {
                _loupeImage.Source = null;
                bitmap.Dispose();
            }
    }

    private Point GetViewportCenter()
    {
        return new Point(_previewSurface.Bounds.Width / 2.0, _previewSurface.Bounds.Height / 2.0);
    }

    private void ZoomAroundViewportCenter(double factor)
    {
        SetZoomAroundViewerPoint(GetViewportCenter(), _previewScale.ScaleX * factor);
    }

    private void SetZoomAroundViewerPoint(Point viewerPoint, double targetZoom)
    {
        if (_previewImage.Source is null || _previewSurface.Bounds.Width <= 0 || _previewSurface.Bounds.Height <= 0)
        {
            return;
        }

        var oldZoom = _previewScale.ScaleX;
        var newZoom = Math.Clamp(targetZoom, 0.1, 8.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            return;
        }

        var imageX = (viewerPoint.X - _imageLeft) / oldZoom;
        var imageY = (viewerPoint.Y - _imageTop) / oldZoom;
        SetZoom(newZoom);
        _imageLeft = viewerPoint.X - (imageX * newZoom);
        _imageTop = viewerPoint.Y - (imageY * newZoom);
        ApplyImageLayout();
    }

    private void SetZoom(double value)
    {
        var clamped = Math.Clamp(value, 0.1, 8.0);
        _previewScale.ScaleX = clamped;
        _previewScale.ScaleY = clamped;
        _zoomText.Text = $"Zoom: {clamped:F2}x";
    }

    private void OpenCurrentFrameInFileManager()
    {
        if (DataContext is not MainWindowViewModel vm
            || vm.SelectedResult is null
            || string.IsNullOrWhiteSpace(vm.SelectedResult.FilePath)
            || !File.Exists(vm.SelectedResult.FilePath))
        {
            return;
        }

        try
        {
            var filePath = vm.SelectedResult.FilePath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true,
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-R \"{filePath}\"",
                    UseShellExecute = false,
                });
            }
            else
            {
                var folder = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{folder}\"",
                        UseShellExecute = false,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open file manager: {ex.Message}");
        }
    }

    private void ApplyImageLayout()
    {
        if (_previewImage.Source is not IImage image)
        {
            return;
        }

        _previewImage.Width = image.Size.Width * _previewScale.ScaleX;
        _previewImage.Height = image.Size.Height * _previewScale.ScaleY;
        Canvas.SetLeft(_previewImage, _imageLeft);
        Canvas.SetTop(_previewImage, _imageTop);
        ScheduleOverlayRedraw();
    }

    private ViewState? CaptureViewState()
    {
        if (_previewImage.Source is not IImage image || _previewSurface.Bounds.Width <= 0 || _previewSurface.Bounds.Height <= 0)
        {
            return null;
        }

        var centerX = ((_previewSurface.Bounds.Width / 2.0) - _imageLeft) / _previewScale.ScaleX;
        var centerY = ((_previewSurface.Bounds.Height / 2.0) - _imageTop) / _previewScale.ScaleY;

        return new ViewState(
            _previewScale.ScaleX,
            image.Size.Width > 0 ? centerX / image.Size.Width : 0.5,
            image.Size.Height > 0 ? centerY / image.Size.Height : 0.5);
    }

    private void RestoreViewState(ViewState viewState)
    {
        SetZoom(viewState.Zoom);

        Dispatcher.UIThread.Post(() =>
        {
            if (_previewImage.Source is not IImage image)
            {
                return;
            }

            _imageLeft = (_previewSurface.Bounds.Width / 2.0)
                - (image.Size.Width * viewState.CenterXRatio * _previewScale.ScaleX);
            _imageTop = (_previewSurface.Bounds.Height / 2.0)
                - (image.Size.Height * viewState.CenterYRatio * _previewScale.ScaleY);
            ApplyImageLayout();
        }, DispatcherPriority.Background);
    }

    private sealed record ViewState(double Zoom, double CenterXRatio, double CenterYRatio);

    private void ScheduleOverlayRedraw()
    {
        if (_overlayRedrawQueued)
        {
            return;
        }

        _overlayRedrawQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _overlayRedrawQueued = false;
            RedrawOverlays();
        }, DispatcherPriority.Render);
    }

    private void UpdateCacheIndicators()
    {
        _cacheIndicatorCanvas.Children.Clear();
        _cacheDotCanvas.Children.Clear();

        if (DataContext is not MainWindowViewModel vm || vm.Results.Count == 0)
        {
            return;
        }

        var availableHeight = Math.Max(20.0, _cacheIndicatorCanvas.Bounds.Height - 8.0);
        var gap = availableHeight / vm.Results.Count;
        var activeIndex = vm.SelectedResult is null ? -1 : vm.Results.IndexOf(vm.SelectedResult);
        var positiveScores = vm.Results.Where(item => item.OverallScore > 0).Select(item => item.OverallScore).ToArray();
        var scoreMin = positiveScores.Length > 0 ? positiveScores.Min() : 0.0;
        var scoreMax = positiveScores.Length > 0 ? positiveScores.Max() : 0.0;
        var scoreRange = scoreMax > scoreMin ? scoreMax - scoreMin : 1.0;

        for (var index = 0; index < vm.Results.Count; index++)
        {
            var item = vm.Results[index];
            var isActive = index == activeIndex;
            var isCached = vm.IsPreviewCached(item.FilePath);
            var top = 4 + (index * gap);
            var markerHeight = Math.Clamp(availableHeight / vm.Results.Count, 2.0, 6.0);
            var normalizedScore = item.OverallScore > 0
                ? Math.Clamp((item.OverallScore - scoreMin) / scoreRange, 0.0, 1.0)
                : 0.5;
            var scoreColor = normalizedScore >= 0.5
                ? InterpolateColor(Color.FromRgb(0xFF, 0xD7, 0x00), Color.FromRgb(0x39, 0xD3, 0x53), (normalizedScore - 0.5) * 2.0)
                : InterpolateColor(Color.FromRgb(0xE5, 0x3E, 0x3E), Color.FromRgb(0xFF, 0xD7, 0x00), normalizedScore * 2.0);

            var bar = new Rectangle
            {
                Width = 6,
                Height = markerHeight,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(scoreColor),
            };
            Canvas.SetLeft(bar, 4);
            Canvas.SetTop(bar, top);
            _cacheIndicatorCanvas.Children.Add(bar);

            if (item.IsRejected)
            {
                _cacheIndicatorCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(3, top + (markerHeight / 2.0)),
                    EndPoint = new Point(11, top + (markerHeight / 2.0)),
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                });
            }

            if (isActive)
            {
                var activeMarker = new Polygon
                {
                    Fill = SolidColorBrush.Parse("#FFD87A"),
                    Stroke = SolidColorBrush.Parse("#C9A752"),
                    StrokeThickness = 1,
                    Points = [new Point(4, top + (markerHeight / 2.0)), new Point(0, top - 0.5), new Point(0, top + markerHeight + 0.5)],
                };
                _cacheIndicatorCanvas.Children.Add(activeMarker);
            }

            if (isCached)
            {
                var cacheDot = new Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Fill = SolidColorBrush.Parse("#4E7796"),
                };
                Canvas.SetLeft(cacheDot, 5);
                Canvas.SetTop(cacheDot, top + (markerHeight / 2.0) - 1.5);
                _cacheDotCanvas.Children.Add(cacheDot);
            }

            var hitArea = new Rectangle
            {
                Width = 14,
                Height = markerHeight + 4,
                Fill = Brushes.Transparent,
            };
            ToolTip.SetTip(hitArea, $"Frame {index + 1}: {item.FileName}");
            var capturedIndex = index;
            hitArea.PointerPressed += (_, e) =>
            {
                vm.SelectResultAtIndex(capturedIndex);
                e.Handled = true;
            };
            Canvas.SetLeft(hitArea, 0);
            Canvas.SetTop(hitArea, top - 2);
            _cacheIndicatorCanvas.Children.Add(hitArea);
        }
    }

    private static Color InterpolateColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(
            (byte)(from.R + ((to.R - from.R) * amount)),
            (byte)(from.G + ((to.G - from.G) * amount)),
            (byte)(from.B + ((to.B - from.B) * amount)));
    }

    private void OverlayCanvasOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsRoiOverlayVisible || _roiEditMode != RoiEditMode.None)
        {
            return;
        }

        e.Handled = false;
    }

    private void RoiBodyOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsRoiOverlayVisible)
        {
            return;
        }

        BeginRoiEdit(e, RoiEditMode.Move, -1, vm.CurrentManualRoi);
    }

    private void RoiHandleOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsRoiOverlayVisible || sender is not Rectangle { Tag: int handleIndex })
        {
            return;
        }

        BeginRoiEdit(e, RoiEditMode.Resize, handleIndex, vm.CurrentManualRoi);
    }

    private void BeginRoiEdit(
        PointerPressedEventArgs e,
        RoiEditMode mode,
        int handleIndex,
        (double Left, double Top, double Width, double Height) startRect)
    {
        _roiEditMode = mode;
        _roiEditPointer = e.Pointer;
        _roiActiveHandleIndex = handleIndex;
        _roiEditStartPointer = e.GetPosition(_overlayCanvas);
        _roiEditStartRect = startRect;
        e.Pointer.Capture(_overlayCanvas);
        e.Handled = true;
    }

    private void OverlayCanvasOnPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateCurvatureTooltip(e.GetPosition(_overlayCanvas));

        if (_roiEditMode == RoiEditMode.None || DataContext is not MainWindowViewModel vm || !vm.IsRoiOverlayVisible)
        {
            return;
        }

        if (!TryGetImageDisplayRect(out var imageRect))
        {
            return;
        }

        var current = e.GetPosition(_overlayCanvas);
        var start = _roiEditStartRect;

        if (_roiEditMode == RoiEditMode.Move)
        {
            var dxNorm = (current.X - _roiEditStartPointer.X) / imageRect.Width;
            var dyNorm = (current.Y - _roiEditStartPointer.Y) / imageRect.Height;
            _roiLeft = Math.Clamp(start.Left + dxNorm, 0.0, Math.Max(0.0, 1.0 - start.Width));
            _roiTop = Math.Clamp(start.Top + dyNorm, 0.0, Math.Max(0.0, 1.0 - start.Height));
            _roiWidth = start.Width;
            _roiHeight = start.Height;
        }
        else
        {
            ResizeRoiFromHandle(current, imageRect, start);
        }

        UpdateRoiRect();
        ScheduleOverlayRedraw();
        e.Handled = true;
    }

    private void OverlayCanvasOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_roiEditMode == RoiEditMode.None)
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.SetManualRoi((_roiLeft, _roiTop, _roiWidth, _roiHeight));
        }

        _roiEditMode = RoiEditMode.None;
        _roiActiveHandleIndex = -1;
        _roiEditPointer = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void CancelRoiEdit()
    {
        _roiLeft = _roiEditStartRect.Left;
        _roiTop = _roiEditStartRect.Top;
        _roiWidth = _roiEditStartRect.Width;
        _roiHeight = _roiEditStartRect.Height;
        _roiEditMode = RoiEditMode.None;
        _roiActiveHandleIndex = -1;
        _roiEditPointer?.Capture(null);
        _roiEditPointer = null;
        UpdateRoiRect();
    }

    private void ResizeRoiFromHandle(
        Point current,
        Rect imageRect,
        (double Left, double Top, double Width, double Height) start)
    {
        var left = start.Left * imageRect.Width;
        var top = start.Top * imageRect.Height;
        var right = (start.Left + start.Width) * imageRect.Width;
        var bottom = (start.Top + start.Height) * imageRect.Height;

        double anchorX;
        double anchorY;
        double movingX;
        double movingY;
        switch (_roiActiveHandleIndex)
        {
            case 0:
                anchorX = right; anchorY = bottom; movingX = left; movingY = top;
                break;
            case 1:
                anchorX = left; anchorY = bottom; movingX = right; movingY = top;
                break;
            case 2:
                anchorX = right; anchorY = top; movingX = left; movingY = bottom;
                break;
            default:
                anchorX = left; anchorY = top; movingX = right; movingY = bottom;
                break;
        }

        var deltaX = current.X - _roiEditStartPointer.X;
        var deltaY = current.Y - _roiEditStartPointer.Y;
        var relativeX = (movingX + deltaX) - anchorX;
        var relativeY = (movingY + deltaY) - anchorY;
        var minSide = Math.Max(4.0, 0.005 * Math.Min(imageRect.Width, imageRect.Height));
        var side = Math.Max(minSide, Math.Max(Math.Abs(relativeX), Math.Abs(relativeY)));
        var signX = relativeX >= 0 ? 1.0 : -1.0;
        var signY = relativeY >= 0 ? 1.0 : -1.0;

        var movingTargetX = Math.Clamp(anchorX + (signX * side), 0.0, imageRect.Width);
        var movingTargetY = Math.Clamp(anchorY + (signY * side), 0.0, imageRect.Height);
        side = Math.Max(minSide, Math.Min(Math.Abs(movingTargetX - anchorX), Math.Abs(movingTargetY - anchorY)));

        var finalMovingX = anchorX + (signX * side);
        var finalMovingY = anchorY + (signY * side);
        var nextLeftPx = Math.Min(anchorX, finalMovingX);
        var nextTopPx = Math.Min(anchorY, finalMovingY);

        _roiLeft = Math.Clamp(nextLeftPx / imageRect.Width, 0.0, 1.0);
        _roiTop = Math.Clamp(nextTopPx / imageRect.Height, 0.0, 1.0);
        _roiWidth = Math.Clamp(side / imageRect.Width, 0.005, 1.0);
        _roiHeight = Math.Clamp(side / imageRect.Height, 0.005, 1.0);
        _roiLeft = Math.Clamp(_roiLeft, 0.0, Math.Max(0.0, 1.0 - _roiWidth));
        _roiTop = Math.Clamp(_roiTop, 0.0, Math.Max(0.0, 1.0 - _roiHeight));
    }

    private void UpdateRoiRect()
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsRoiOverlayVisible)
        {
            SetRoiControlsVisible(false);
            return;
        }

        if (!TryGetImageDisplayRect(out var imageRect))
        {
            SetRoiControlsVisible(false);
            return;
        }

        SetRoiControlsVisible(true);
        _roiRect.Width = Math.Max(20, imageRect.Width * _roiWidth);
        _roiRect.Height = Math.Max(20, imageRect.Height * _roiHeight);
        var left = imageRect.Left + (imageRect.Width * _roiLeft);
        var top = imageRect.Top + (imageRect.Height * _roiTop);
        Canvas.SetLeft(_roiRect, left);
        Canvas.SetTop(_roiRect, top);
        PositionRoiHandle(0, left, top);
        PositionRoiHandle(1, left + _roiRect.Width, top);
        PositionRoiHandle(2, left, top + _roiRect.Height);
        PositionRoiHandle(3, left + _roiRect.Width, top + _roiRect.Height);
    }

    private void SyncRoiFromViewModel()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var roi = vm.CurrentManualRoi;
        _roiLeft = roi.Left;
        _roiTop = roi.Top;
        _roiWidth = roi.Width;
        _roiHeight = roi.Height;
    }

    private void SetRoiControlsVisible(bool visible)
    {
        _roiRect.IsVisible = visible;
        foreach (var handle in _roiHandles)
        {
            handle.IsVisible = visible;
        }
    }

    private void PositionRoiHandle(int index, double x, double y)
    {
        var handle = _roiHandles[index];
        handle.IsVisible = true;
        Canvas.SetLeft(handle, x - (handle.Width / 2.0));
        Canvas.SetTop(handle, y - (handle.Height / 2.0));
    }

    private void RedrawOverlays()
    {
        _starTrailOverlayCanvas.Children.Clear();
        _orientationOverlayCanvas.Children.Clear();
        _curvatureOverlayCanvas.Children.Clear();
        _curvatureGrid = null;
        _curvatureTooltip.IsVisible = false;

        if (DataContext is not MainWindowViewModel vm || vm.SelectedResult is null)
        {
            _previewSurface.IsVisible = true;
            return;
        }

        _previewSurface.IsVisible = !vm.IsCurvatureViewVisible;
        if (vm.IsCurvatureViewVisible)
        {
            SetRoiControlsVisible(false);
            RedrawCurvatureView(vm.SelectedResult);
            return;
        }

        UpdateRoiRect();

        if (!TryGetImageDisplayRect(out var imageRect))
        {
            return;
        }

        var selected = vm.SelectedResult;
        var frameWidth = Math.Max(1, selected.FrameWidth);
        var frameHeight = Math.Max(1, selected.FrameHeight);

        Point MapToCanvas(double x, double y)
        {
            var nx = Math.Clamp(x / Math.Max(1, frameWidth - 1), 0.0, 1.0);
            var ny = Math.Clamp(y / Math.Max(1, frameHeight - 1), 0.0, 1.0);
            return new Point(
                imageRect.Left + (nx * imageRect.Width),
                imageRect.Top + (ny * imageRect.Height));
        }

        if (vm.IsStarDebugOverlayVisible)
        {
            var stars = selected.Stars
                .OrderByDescending(star => star.Peak)
                .Take(300)
                .ToArray();

            foreach (var star in stars)
            {
                var p = MapToCanvas(star.X, star.Y);
                var radius = Math.Clamp(star.Fwhm * 0.35, 1.5, 5.0);
                var marker = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Stroke = SolidColorBrush.Parse("#A8D3FF"),
                    StrokeThickness = 1,
                    Fill = SolidColorBrush.Parse("#224A6A8A"),
                };
                Canvas.SetLeft(marker, p.X - radius);
                Canvas.SetTop(marker, p.Y - radius);
                _starTrailOverlayCanvas.Children.Add(marker);
            }

            if (selected.HasTrailLine)
            {
                var p1 = MapToCanvas(selected.TrailX1!.Value, selected.TrailY1!.Value);
                var p2 = MapToCanvas(selected.TrailX2!.Value, selected.TrailY2!.Value);
                var trail = new Line
                {
                    StartPoint = p1,
                    EndPoint = p2,
                    Stroke = SolidColorBrush.Parse("#FF6A6A"),
                    StrokeThickness = 2,
                };
                _starTrailOverlayCanvas.Children.Add(trail);
            }
        }

        if (vm.IsOrientationDebugOverlayVisible && selected.OrientationDebug is { Stars.Count: > 0 } debug)
        {
            var starBrush = SolidColorBrush.Parse("#D066CCFF");
            var triangleBrush = SolidColorBrush.Parse("#E0FFC833");
            for (var index = 0; index < debug.Stars.Count; index++)
            {
                var point = MapToCanvas(debug.Stars[index].X, debug.Stars[index].Y);
                var marker = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Stroke = starBrush,
                    StrokeThickness = 1.5,
                    Fill = Brushes.Transparent,
                };
                Canvas.SetLeft(marker, point.X - 4);
                Canvas.SetTop(marker, point.Y - 4);
                _orientationOverlayCanvas.Children.Add(marker);

                var label = new TextBlock
                {
                    Text = $"{index + 1}",
                    Foreground = starBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                };
                Canvas.SetLeft(label, point.X + 5);
                Canvas.SetTop(label, point.Y - 8);
                _orientationOverlayCanvas.Children.Add(label);
            }

            if (debug.TriangleIndices.Count == 3)
            {
                var trianglePoints = debug.TriangleIndices
                    .Where(index => index >= 0 && index < debug.Stars.Count)
                    .Select(index => MapToCanvas(debug.Stars[index].X, debug.Stars[index].Y))
                    .ToArray();
                if (trianglePoints.Length == 3)
                {
                    for (var index = 0; index < trianglePoints.Length; index++)
                    {
                        _orientationOverlayCanvas.Children.Add(new Line
                        {
                            StartPoint = trianglePoints[index],
                            EndPoint = trianglePoints[(index + 1) % trianglePoints.Length],
                            Stroke = triangleBrush,
                            StrokeThickness = 2,
                        });
                    }
                }
            }

            var summary = new Border
            {
                Background = SolidColorBrush.Parse("#B0000000"),
                Padding = new Thickness(6, 3),
                Child = new TextBlock
                {
                    Text = $"Orientation: {(debug.Rotate180 ? "flipped 180°" : "not flipped")}   stars: {debug.Stars.Count}   {debug.StatusText}",
                    Foreground = triangleBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                },
            };
            Canvas.SetLeft(summary, 8);
            Canvas.SetTop(summary, 8);
            _orientationOverlayCanvas.Children.Add(summary);
        }

    }

    private void RedrawCurvatureView(FrameSummaryViewModel selected)
    {
        var viewportWidth = _overlayCanvas.Bounds.Width;
        var viewportHeight = _overlayCanvas.Bounds.Height;
        if (viewportWidth <= 1 || viewportHeight <= 1)
        {
            return;
        }

        var frameWidth = Math.Max(1, selected.FrameWidth);
        var frameHeight = Math.Max(1, selected.FrameHeight);
        var frameAspect = frameWidth / (double)frameHeight;
        var viewportAspect = viewportWidth / viewportHeight;
        _curvatureImageRect = frameAspect > viewportAspect
            ? new Rect(0, (viewportHeight - (viewportWidth / frameAspect)) / 2.0, viewportWidth, viewportWidth / frameAspect)
            : new Rect((viewportWidth - (viewportHeight * frameAspect)) / 2.0, 0, viewportHeight * frameAspect, viewportHeight);

        var stars = selected.Stars.Where(star => star.Fwhm > 0 && !double.IsNaN(star.Fwhm)).ToArray();
        if (stars.Length == 0)
        {
            AddCurvatureStats("No FWHM data");
            return;
        }

        const int gridShortSide = 96;
        var gridWidth = frameWidth >= frameHeight
            ? gridShortSide
            : Math.Max(8, (int)Math.Round(gridShortSide * frameWidth / (double)frameHeight));
        var gridHeight = frameWidth >= frameHeight
            ? Math.Max(8, (int)Math.Round(gridShortSide * frameHeight / (double)frameWidth))
            : gridShortSide;
        var values = new double[gridWidth * gridHeight];
        var fwhms = stars.Select(star => star.Fwhm).Order().ToArray();
        var minFwhm = fwhms[0];
        var maxFwhm = fwhms[^1];
        var rampMin = Percentile(fwhms, 0.02);
        var rampMax = Percentile(fwhms, 0.98);
        if (rampMax - rampMin < 1e-6)
        {
            rampMin = minFwhm;
            rampMax = maxFwhm;
        }

        var cellWidth = frameWidth / (double)gridWidth;
        var cellHeight = frameHeight / (double)gridHeight;
        var diagonal = Math.Sqrt((double)frameWidth * frameWidth + (double)frameHeight * frameHeight);
        var twoSigmaSquared = 2.0 * Math.Pow(diagonal / 6.0, 2);
        for (var gridY = 0; gridY < gridHeight; gridY++)
        {
            var pixelY = (gridY + 0.5) * cellHeight;
            for (var gridX = 0; gridX < gridWidth; gridX++)
            {
                var pixelX = (gridX + 0.5) * cellWidth;
                var weightSum = 0.0;
                var valueSum = 0.0;
                foreach (var star in stars)
                {
                    var deltaX = star.X - pixelX;
                    var deltaY = star.Y - pixelY;
                    var weight = Math.Exp(-((deltaX * deltaX) + (deltaY * deltaY)) / twoSigmaSquared);
                    weightSum += weight;
                    valueSum += weight * star.Fwhm;
                }

                values[(gridY * gridWidth) + gridX] = weightSum > 0 ? valueSum / weightSum : minFwhm;
            }
        }

        var range = Math.Max(1e-9, rampMax - rampMin);
        var pixels = new byte[gridWidth * gridHeight * 4];
        for (var index = 0; index < values.Length; index++)
        {
            CurvatureColorRamp(Math.Clamp((values[index] - rampMin) / range, 0.0, 1.0), out var red, out var green, out var blue);
            var pixelIndex = index * 4;
            pixels[pixelIndex] = blue;
            pixels[pixelIndex + 1] = green;
            pixels[pixelIndex + 2] = red;
            pixels[pixelIndex + 3] = 255;
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(gridWidth, gridHeight),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Opaque);
        using (var locked = bitmap.Lock())
        {
            Marshal.Copy(pixels, 0, locked.Address, pixels.Length);
        }

        var heatmap = new Image
        {
            Source = bitmap,
            Width = _curvatureImageRect.Width,
            Height = _curvatureImageRect.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapInterpolationMode(heatmap, BitmapInterpolationMode.HighQuality);
        Canvas.SetLeft(heatmap, _curvatureImageRect.Left);
        Canvas.SetTop(heatmap, _curvatureImageRect.Top);
        _curvatureOverlayCanvas.Children.Add(heatmap);

        _curvatureGrid = values;
        _curvatureGridWidth = gridWidth;
        _curvatureGridHeight = gridHeight;
        _curvatureArcsecPerPixel = selected.Fwhm > 0 && selected.FwhmArcsec is > 0
            ? selected.FwhmArcsec.Value / selected.Fwhm
            : 0.0;

        var meanFwhm = fwhms.Average();
        var (cornerAverage, centerAverage) = CalculateCurvatureAverages(values, gridWidth, gridHeight, minFwhm, maxFwhm);
        var curvature = centerAverage > 0 ? ((cornerAverage - centerAverage) / centerAverage) * 100.0 : 0.0;
        string FormatFwhm(double value) => _curvatureArcsecPerPixel > 0
            ? FormattableString.Invariant($"{value:F2} px / {value * _curvatureArcsecPerPixel:F2}\"")
            : FormattableString.Invariant($"{value:F2} px");

        AddCurvatureStats(
            $"Min FWHM:   {FormatFwhm(minFwhm)}\n" +
            $"Max FWHM:   {FormatFwhm(maxFwhm)}\n" +
            $"Mean FWHM:  {FormatFwhm(meanFwhm)}\n" +
            $"Curvature:  {curvature:F1}%\n" +
            $"Stars Used: {stars.Length}");
        DrawCurvatureTarget();
        _curvatureOverlayCanvas.Children.Add(_curvatureTooltip);
    }

    private void AddCurvatureStats(string text)
    {
        var stats = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        Canvas.SetLeft(stats, _curvatureImageRect.Left + 12);
        Canvas.SetTop(stats, _curvatureImageRect.Top + 10);
        _curvatureOverlayCanvas.Children.Add(stats);
    }

    private void DrawCurvatureTarget()
    {
        var center = _curvatureImageRect.Center;
        var radius = Math.Min(_curvatureImageRect.Width, _curvatureImageRect.Height) * 0.04;
        var brush = SolidColorBrush.Parse("#E6FFFFFF");
        var ring = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = [3, 3],
        };
        Canvas.SetLeft(ring, center.X - radius);
        Canvas.SetTop(ring, center.Y - radius);
        _curvatureOverlayCanvas.Children.Add(ring);

        var armLength = radius * 1.6;
        _curvatureOverlayCanvas.Children.Add(new Line
        {
            StartPoint = new Point(center.X - armLength, center.Y),
            EndPoint = new Point(center.X + armLength, center.Y),
            Stroke = brush,
            StrokeThickness = 1,
        });
        _curvatureOverlayCanvas.Children.Add(new Line
        {
            StartPoint = new Point(center.X, center.Y - armLength),
            EndPoint = new Point(center.X, center.Y + armLength),
            Stroke = brush,
            StrokeThickness = 1,
        });

        const double bulletRadius = 2.5;
        var bullet = new Ellipse { Width = bulletRadius * 2, Height = bulletRadius * 2, Fill = brush };
        Canvas.SetLeft(bullet, center.X - bulletRadius);
        Canvas.SetTop(bullet, center.Y - bulletRadius);
        _curvatureOverlayCanvas.Children.Add(bullet);
    }

    private void UpdateCurvatureTooltip(Point position)
    {
        if (DataContext is not MainWindowViewModel { IsCurvatureViewVisible: true }
            || _curvatureGrid is null
            || !_curvatureImageRect.Contains(position))
        {
            _curvatureTooltip.IsVisible = false;
            return;
        }

        var normalizedX = (position.X - _curvatureImageRect.Left) / _curvatureImageRect.Width;
        var normalizedY = (position.Y - _curvatureImageRect.Top) / _curvatureImageRect.Height;
        var gridX = Math.Clamp((int)(normalizedX * _curvatureGridWidth), 0, _curvatureGridWidth - 1);
        var gridY = Math.Clamp((int)(normalizedY * _curvatureGridHeight), 0, _curvatureGridHeight - 1);
        var fwhm = _curvatureGrid[(gridY * _curvatureGridWidth) + gridX];
        _curvatureTooltipText.Text = _curvatureArcsecPerPixel > 0
            ? FormattableString.Invariant($"{fwhm:F2} px / {fwhm * _curvatureArcsecPerPixel:F2}\"")
            : FormattableString.Invariant($"{fwhm:F2} px");
        Canvas.SetLeft(_curvatureTooltip, Math.Min(position.X + 14, _overlayCanvas.Bounds.Width - 150));
        Canvas.SetTop(_curvatureTooltip, Math.Min(position.Y + 14, _overlayCanvas.Bounds.Height - 35));
        _curvatureTooltip.IsVisible = true;
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        var index = Math.Clamp((int)Math.Round((sortedValues.Count - 1) * percentile), 0, sortedValues.Count - 1);
        return sortedValues[index];
    }

    private static (double CornerAverage, double CenterAverage) CalculateCurvatureAverages(
        IReadOnlyList<double> values,
        int gridWidth,
        int gridHeight,
        double minFwhm,
        double maxFwhm)
    {
        var cornerSum = 0.0;
        var cornerWeightSum = 0.0;
        var centerSum = 0.0;
        var centerWeightSum = 0.0;
        for (var gridY = 0; gridY < gridHeight; gridY++)
        {
            var radialY = Math.Abs(((gridY + 0.5) / gridHeight) - 0.5) * 2.0;
            for (var gridX = 0; gridX < gridWidth; gridX++)
            {
                var radialX = Math.Abs(((gridX + 0.5) / gridWidth) - 0.5) * 2.0;
                var value = values[(gridY * gridWidth) + gridX];
                var cornerWeight = Math.Max(0, Math.Min(radialX, radialY) - 0.6) / 0.4;
                cornerSum += cornerWeight * value;
                cornerWeightSum += cornerWeight;
                var centerWeight = Math.Max(0, 1 - (Math.Sqrt((radialX * radialX) + (radialY * radialY)) / 0.3));
                centerSum += centerWeight * value;
                centerWeightSum += centerWeight;
            }
        }

        return (
            cornerWeightSum > 0 ? cornerSum / cornerWeightSum : maxFwhm,
            centerWeightSum > 0 ? centerSum / centerWeightSum : minFwhm);
    }

    private static void CurvatureColorRamp(double value, out byte red, out byte green, out byte blue)
    {
        ReadOnlySpan<(double Position, double Red, double Green, double Blue)> stops =
        [
            (0.00, 0.125, 0.125, 0.149),
            (0.10, 0.05, 0.05, 0.55),
            (0.20, 0.00, 0.20, 1.00),
            (0.35, 0.00, 0.85, 1.00),
            (0.50, 0.00, 0.85, 0.10),
            (0.68, 1.00, 1.00, 0.00),
            (0.82, 1.00, 0.55, 0.00),
            (0.93, 1.00, 0.10, 0.10),
            (1.00, 1.00, 0.55, 0.85),
        ];

        var upperIndex = 1;
        while (upperIndex < stops.Length - 1 && value > stops[upperIndex].Position)
        {
            upperIndex++;
        }

        var lower = stops[upperIndex - 1];
        var upper = stops[upperIndex];
        var interpolation = (value - lower.Position) / (upper.Position - lower.Position);
        red = (byte)Math.Round((lower.Red + ((upper.Red - lower.Red) * interpolation)) * 255);
        green = (byte)Math.Round((lower.Green + ((upper.Green - lower.Green) * interpolation)) * 255);
        blue = (byte)Math.Round((lower.Blue + ((upper.Blue - lower.Blue) * interpolation)) * 255);
    }

    private bool TryGetImageDisplayRect(out Rect rect)
    {
        rect = default;
        if (_previewImage.Source is not IImage image)
        {
            return false;
        }

        var viewportWidth = _overlayCanvas.Bounds.Width;
        var viewportHeight = _overlayCanvas.Bounds.Height;
        if (viewportWidth <= 1 || viewportHeight <= 1 || image.Size.Width <= 0 || image.Size.Height <= 0)
        {
            return false;
        }

        rect = new Rect(
            _imageLeft,
            _imageTop,
            image.Size.Width * _previewScale.ScaleX,
            image.Size.Height * _previewScale.ScaleY);
        return true;
    }
}