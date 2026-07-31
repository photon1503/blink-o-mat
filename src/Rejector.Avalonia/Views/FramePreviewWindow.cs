using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rejector.Avalonia.ViewModels;

namespace Rejector.Avalonia.Views;

public sealed class FramePreviewWindow : Window
{
    private readonly Image _previewImage;
    private readonly ScrollViewer _previewScroll;
    private readonly ScaleTransform _previewScale = new(1.0, 1.0);
    private readonly TextBlock _zoomText;
    private readonly TextBlock _intervalText;
    private readonly TextBlock _cacheText;
    private readonly TextBlock _framePositionText;
    private readonly Slider _frameSlider;
    private readonly DispatcherTimer _playTimer = new();
    private readonly Canvas _overlayCanvas;
    private readonly Canvas _starTrailOverlayCanvas;
    private readonly Canvas _orientationOverlayCanvas;
    private readonly Canvas _curvatureOverlayCanvas;
    private readonly Rectangle _roiRect;
    private readonly Rectangle[] _roiHandles = new Rectangle[4];
    private readonly Canvas _cacheIndicatorCanvas;
    private readonly Button _playButton;
    private bool _isPanning;
    private Point _panStartPoint;
    private Vector _panStartOffset;
    private RoiEditMode _roiEditMode;
    private int _roiActiveHandleIndex = -1;
    private Point _roiEditStartPointer;
    private (double Left, double Top, double Width, double Height) _roiEditStartRect;
    private const double DefaultRoiSize = 0.3;
    private double _roiLeft = 0.35;
    private double _roiTop = 0.35;
    private double _roiWidth = DefaultRoiSize;
    private double _roiHeight = DefaultRoiSize;
    private static readonly double[] PlaybackIntervals = [0.1, 0.2, 0.5, 1.0, 2.0, 3.0, 5.0, 10.0];
    private int _playbackIntervalIndex = 3;
    private MainWindowViewModel? _attachedVm;

    private enum RoiEditMode
    {
        None,
        Move,
        Resize,
    }

    public FramePreviewWindow()
    {
        Width = 1200;
        Height = 900;
        MinWidth = 640;
        MinHeight = 480;
        Background = SolidColorBrush.Parse("#111315");

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(12),
        };

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"),
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

        var toggleRejectButton = new Button { Content = "Toggle Reject", MinWidth = 108, Height = 30 };
        toggleRejectButton.Click += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ToggleSelectedReject();
            }
        };
        Grid.SetColumn(toggleRejectButton, 10);
        toolbar.Children.Add(toggleRejectButton);

        var roiToggle = new ToggleButton { Content = "ROI", Width = 50, Height = 30 };
        roiToggle.Bind(ToggleButton.IsCheckedProperty, new Binding("IsRoiOverlayVisible") { Mode = BindingMode.TwoWay });
        Grid.SetColumn(roiToggle, 11);
        toolbar.Children.Add(roiToggle);

        _zoomText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SolidColorBrush.Parse("#B8BCC0"),
            Text = "Zoom: 1.00x",
        };
        Grid.SetColumn(_zoomText, 12);
        toolbar.Children.Add(_zoomText);
        root.Children.Add(toolbar);

        var previewStateRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _frameSlider = new Slider { Minimum = 0, Maximum = 0 };
        _frameSlider.Bind(Slider.ValueProperty, new Binding("PreviewFrameSliderValue") { Mode = BindingMode.TwoWay });
        _frameSlider.Bind(Slider.MaximumProperty, new Binding("PreviewFrameSliderMaximum"));
        Grid.SetColumn(_frameSlider, 0);
        previewStateRow.Children.Add(_frameSlider);

        _framePositionText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SolidColorBrush.Parse("#B8BCC0"),
            Width = 80,
            TextAlignment = TextAlignment.Right,
        };
        _framePositionText.Bind(TextBlock.TextProperty, new Binding("PreviewFramePositionText"));
        Grid.SetColumn(_framePositionText, 1);
        previewStateRow.Children.Add(_framePositionText);

        _cacheText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SolidColorBrush.Parse("#7F95A8"),
            Width = 140,
            TextAlignment = TextAlignment.Right,
            Text = "cache: 0",
        };
        Grid.SetColumn(_cacheText, 2);
        previewStateRow.Children.Add(_cacheText);
        Grid.SetRow(previewStateRow, 1);
        root.Children.Add(previewStateRow);

        var caption = new TextBlock
        {
            FontSize = 12,
            Foreground = SolidColorBrush.Parse("#B8BCC0"),
            Margin = new Thickness(0, 0, 0, 10),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        caption.Bind(TextBlock.TextProperty, new Binding("SelectedPreviewCaption"));
        Grid.SetRow(caption, 2);
        root.Children.Add(caption);

        var imageGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*") };
        Grid.SetRow(imageGrid, 3);

        _cacheIndicatorCanvas = new Canvas
        {
            Width = 12,
            Background = SolidColorBrush.Parse("#181B1E"),
        };
        imageGrid.Children.Add(_cacheIndicatorCanvas);

        var imageBorder = new Border
        {
            Background = SolidColorBrush.Parse("#17191B"),
            BorderBrush = SolidColorBrush.Parse("#2D3136"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
        };
        Grid.SetColumn(imageBorder, 1);

        _previewImage = new Image
        {
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(_previewImage, BitmapInterpolationMode.None);

        _previewImage.Bind(Image.SourceProperty, new Binding("SelectedPreviewImage"));

        var previewTransform = new LayoutTransformControl
        {
            Child = _previewImage,
            LayoutTransform = _previewScale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _previewScroll = new ScrollViewer
        {
            Content = previewTransform,
        };
        _previewImage.PointerPressed += PreviewImageOnPointerPressed;
        _previewScroll.PointerMoved += PreviewScrollOnPointerMoved;
        _previewScroll.PointerReleased += PreviewScrollOnPointerReleased;

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

        var previewLayer = new Grid();
        previewLayer.Children.Add(_previewScroll);
        previewLayer.Children.Add(_overlayCanvas);
        previewLayer.PointerWheelChanged += PreviewLayerOnPointerWheelChanged;

        imageBorder.Child = previewLayer;
        imageGrid.Children.Add(imageBorder);
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
                Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Background);
                Dispatcher.UIThread.Post(UpdateCacheIndicators, DispatcherPriority.Background);
                Dispatcher.UIThread.Post(RedrawOverlays, DispatcherPriority.Background);
            }
        };

        _overlayCanvas.SizeChanged += (_, _) => UpdateRoiRect();
        _overlayCanvas.SizeChanged += (_, _) => RedrawOverlays();
        _previewScroll.ScrollChanged += (_, _) => RedrawOverlays();

        DataContextChanged += (_, _) => AttachVmSubscriptions();
        AttachVmSubscriptions();

        KeyDown += OnKeyDown;
        Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Background);
            Dispatcher.UIThread.Post(UpdateCacheIndicators, DispatcherPriority.Background);
            Dispatcher.UIThread.Post(RedrawOverlays, DispatcherPriority.Background);
        };

        SetPlaybackInterval(_playbackIntervalIndex);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            vm.SelectPreviousResult();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            vm.SelectNextResult();
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

        if (e.Key == Key.Add || e.Key == Key.OemPlus)
        {
            ZoomAroundViewportCenter(1.25);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
        {
            ZoomAroundViewportCenter(1.0 / 1.25);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D0)
        {
            FitToView();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.R)
        {
            vm.ToggleSelectedReject();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S)
        {
            vm.IsStarDebugOverlayVisible = !vm.IsStarDebugOverlayVisible;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O)
        {
            vm.IsOrientationDebugOverlayVisible = !vm.IsOrientationDebugOverlayVisible;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C)
        {
            vm.IsCurvatureViewVisible = !vm.IsCurvatureViewVisible;
            e.Handled = true;
        }
    }

    private void AttachVmSubscriptions()
    {
        if (_attachedVm is not null)
        {
            _attachedVm.PropertyChanged -= VmOnPropertyChanged;
        }

        _attachedVm = DataContext as MainWindowViewModel;
        if (_attachedVm is null)
        {
            return;
        }

        _attachedVm.PropertyChanged += VmOnPropertyChanged;
        _cacheText.Text = $"cache: {_attachedVm.CachedPreviewCount}";
        SyncRoiFromViewModel();
        UpdateRoiRect();
        UpdateCacheIndicators();
        RedrawOverlays();
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
            UpdateCacheIndicators();
            RedrawOverlays();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsRoiOverlayVisible))
        {
            UpdateRoiRect();
            RedrawOverlays();
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
            RedrawOverlays();
        }
    }

    private void SetPlaybackInterval(int newIndex)
    {
        _playbackIntervalIndex = Math.Clamp(newIndex, 0, PlaybackIntervals.Length - 1);
        var value = PlaybackIntervals[_playbackIntervalIndex];
        _playTimer.Interval = TimeSpan.FromSeconds(value);
        _intervalText.Text = value < 1.0 ? $"{value * 1000:0} ms" : $"{value:0.#} s";
    }

    private void FitToView()
    {
        if (_previewImage.Source is not IImage image)
        {
            return;
        }

        var viewportWidth = Math.Max(0, _previewScroll.Bounds.Width - 12);
        var viewportHeight = Math.Max(0, _previewScroll.Bounds.Height - 12);
        if (viewportWidth <= 0 || viewportHeight <= 0 || image.Size.Width <= 0 || image.Size.Height <= 0)
        {
            return;
        }

        var scaleX = viewportWidth / image.Size.Width;
        var scaleY = viewportHeight / image.Size.Height;
        SetZoom(Math.Min(scaleX, scaleY));
        _previewScroll.Offset = default;
    }

    private void PreviewLayerOnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_previewImage.Source is null || e.Delta.Y == 0)
        {
            return;
        }

        var factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        SetZoomAroundViewerPoint(e.GetPosition(_previewScroll), _previewScale.ScaleX * factor);
        e.Handled = true;
    }

    private void PreviewImageOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_previewImage.Source is null || !e.GetCurrentPoint(_previewImage).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panStartPoint = e.GetPosition(_previewScroll);
        _panStartOffset = _previewScroll.Offset;
        _previewImage.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(_previewScroll);
        e.Handled = true;
    }

    private void PreviewScrollOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        if (!e.GetCurrentPoint(_previewScroll).Properties.IsLeftButtonPressed)
        {
            StopPanning(e.Pointer);
            return;
        }

        var point = e.GetPosition(_previewScroll);
        var delta = point - _panStartPoint;
        _previewScroll.Offset = new Vector(
            Math.Max(0, _panStartOffset.X - delta.X),
            Math.Max(0, _panStartOffset.Y - delta.Y));
        e.Handled = true;
    }

    private void PreviewScrollOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
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
        _previewImage.Cursor = null;
        pointer.Capture(null);
    }

    private Point GetViewportCenter()
    {
        return new Point(_previewScroll.Bounds.Width / 2.0, _previewScroll.Bounds.Height / 2.0);
    }

    private void ZoomAroundViewportCenter(double factor)
    {
        SetZoomAroundViewerPoint(GetViewportCenter(), _previewScale.ScaleX * factor);
    }

    private void SetZoomAroundViewerPoint(Point viewerPoint, double targetZoom)
    {
        if (_previewImage.Source is null || _previewScroll.Bounds.Width <= 0 || _previewScroll.Bounds.Height <= 0)
        {
            return;
        }

        var oldZoom = _previewScale.ScaleX;
        var newZoom = Math.Clamp(targetZoom, 0.1, 8.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            return;
        }

        var imageX = (_previewScroll.Offset.X + viewerPoint.X) / oldZoom;
        var imageY = (_previewScroll.Offset.Y + viewerPoint.Y) / oldZoom;
        SetZoom(newZoom);

        Dispatcher.UIThread.Post(() =>
        {
            _previewScroll.Offset = new Vector(
                Math.Max(0, (imageX * newZoom) - viewerPoint.X),
                Math.Max(0, (imageY * newZoom) - viewerPoint.Y));
            RedrawOverlays();
        }, DispatcherPriority.Background);
    }

    private void SetZoom(double value)
    {
        var clamped = Math.Clamp(value, 0.1, 8.0);
        _previewScale.ScaleX = clamped;
        _previewScale.ScaleY = clamped;
        _zoomText.Text = $"Zoom: {clamped:F2}x";
    }

    private void UpdateCacheIndicators()
    {
        _cacheIndicatorCanvas.Children.Clear();

        if (DataContext is not MainWindowViewModel vm || vm.Results.Count == 0)
        {
            return;
        }

        var availableHeight = Math.Max(20.0, _cacheIndicatorCanvas.Bounds.Height - 8.0);
        var gap = availableHeight / vm.Results.Count;
        var activeIndex = vm.SelectedResult is null ? -1 : vm.Results.IndexOf(vm.SelectedResult);

        for (var index = 0; index < vm.Results.Count; index++)
        {
            var item = vm.Results[index];
            var isActive = index == activeIndex;
            var isCached = vm.IsPreviewCached(item.FilePath);
            var top = 4 + (index * gap);
            var markerHeight = Math.Max(6.0, gap - 2.0);

            var scoreColor = item.OverallScore switch
            {
                >= 4.0 => "#39D353",
                >= 2.0 => "#FFD700",
                _ => "#E53E3E",
            };

            var bar = new Border
            {
                Width = 10,
                Height = markerHeight,
                CornerRadius = new CornerRadius(2),
                Background = SolidColorBrush.Parse(scoreColor),
                Opacity = isCached ? 0.95 : 0.35,
            };
            Canvas.SetLeft(bar, 1);
            Canvas.SetTop(bar, top);
            _cacheIndicatorCanvas.Children.Add(bar);

            if (isActive)
            {
                var activeFrame = new Border
                {
                    Width = 12,
                    Height = markerHeight + 2,
                    BorderBrush = SolidColorBrush.Parse("#DDEBFF"),
                    BorderThickness = new Thickness(1),
                    Background = Brushes.Transparent,
                };
                Canvas.SetLeft(activeFrame, 0);
                Canvas.SetTop(activeFrame, top - 1);
                _cacheIndicatorCanvas.Children.Add(activeFrame);
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
                _cacheIndicatorCanvas.Children.Add(cacheDot);
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
        _roiActiveHandleIndex = handleIndex;
        _roiEditStartPointer = e.GetPosition(_overlayCanvas);
        _roiEditStartRect = startRect;
        e.Pointer.Capture(_overlayCanvas);
        e.Handled = true;
    }

    private void OverlayCanvasOnPointerMoved(object? sender, PointerEventArgs e)
    {
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
        RedrawOverlays();
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
        e.Pointer.Capture(null);
        e.Handled = true;
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

        if (DataContext is not MainWindowViewModel vm || vm.SelectedResult is null)
        {
            return;
        }

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

        if (vm.IsOrientationDebugOverlayVisible && selected.Stars.Count >= 3)
        {
            var anchors = selected.Stars.OrderByDescending(star => star.Peak).Take(3).ToArray();
            var points = anchors.Select(star => MapToCanvas(star.X, star.Y)).ToArray();

            for (var i = 0; i < points.Length; i++)
            {
                var next = points[(i + 1) % points.Length];
                var edge = new Line
                {
                    StartPoint = points[i],
                    EndPoint = next,
                    Stroke = SolidColorBrush.Parse("#C9A8FF"),
                    StrokeThickness = 1.5,
                };
                _orientationOverlayCanvas.Children.Add(edge);
            }
        }

        if (vm.IsCurvatureViewVisible)
        {
            const int gridX = 12;
            const int gridY = 8;
            for (var gy = 0; gy < gridY; gy++)
            {
                for (var gx = 0; gx < gridX; gx++)
                {
                    var nx = (gx + 0.5) / gridX;
                    var ny = (gy + 0.5) / gridY;
                    var px = imageRect.Left + (nx * imageRect.Width);
                    var py = imageRect.Top + (ny * imageRect.Height);

                    var dx = nx - 0.5;
                    var dy = ny - 0.5;
                    var r = Math.Sqrt((dx * dx) + (dy * dy));
                    var t = Math.Clamp(r / 0.72, 0.0, 1.0);
                    var color = Color.FromRgb(
                        (byte)(0x4A + ((0xFF - 0x4A) * t)),
                        (byte)(0xAE - (0x5E * t)),
                        (byte)(0x7C - (0x4C * t)));

                    var node = new Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = new SolidColorBrush(color),
                        Opacity = 0.55,
                    };
                    Canvas.SetLeft(node, px - 3);
                    Canvas.SetTop(node, py - 3);
                    _curvatureOverlayCanvas.Children.Add(node);
                }
            }
        }
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

        var zoom = _previewScale.ScaleX;
        var imageWidth = image.Size.Width * zoom;
        var imageHeight = image.Size.Height * zoom;
        var left = ((viewportWidth - imageWidth) / 2.0) - _previewScroll.Offset.X;
        var top = ((viewportHeight - imageHeight) / 2.0) - _previewScroll.Offset.Y;
        rect = new Rect(left, top, imageWidth, imageHeight);
        return true;
    }
}