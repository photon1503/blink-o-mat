using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using blink_o_mat.ViewModels;
using blink_o_mat.Services;
using WpfPoint = System.Windows.Point;

namespace blink_o_mat;

public partial class PreviewWindow : Window
{
    private static readonly System.Windows.Media.Brush ActiveFrameBrush;
    private static readonly System.Windows.Media.Color ScoreHighColor  = System.Windows.Media.Color.FromRgb(0x39, 0xD3, 0x53); // green
    private static readonly System.Windows.Media.Color ScoreMidColor   = System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00); // yellow
    private static readonly System.Windows.Media.Color ScoreLowColor   = System.Windows.Media.Color.FromRgb(0xE5, 0x3E, 0x3E); // red
    private static readonly System.Windows.Media.Brush CacheBorderBrush;

    static PreviewWindow()
    {
        ActiveFrameBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
        ActiveFrameBrush.Freeze();
        CacheBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x9E, 0xFF));
        CacheBorderBrush.Freeze();
    }

    private readonly FramePreviewViewModel _vm;
    private bool _hasInitializedView;
    private bool _isKeyboardNavigationInProgress;
    private bool _isLoupeActive;
    private bool _isPanning;
    private int? _activeKeyboardNavigationIndex;
    private WpfPoint _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private bool _isRoiDragging;
    private WpfPoint _roiDragOriginImage;
    private System.Windows.Shapes.Rectangle? _roiDragOverlay;

    // Persistent ROI overlay (toggled from the side panel)
    private System.Windows.Shapes.Rectangle? _roiPersistentRect;
    private readonly System.Windows.Shapes.Rectangle?[] _roiHandles = new System.Windows.Shapes.Rectangle?[4];
    private bool _isRoiOverlayEditing;
    private RoiEditMode _roiEditMode;
    private int _roiActiveHandleIndex = -1;
    private WpfPoint _roiEditStartMouseImage;
    private (double Left, double Top, double Width, double Height) _roiEditStartRect;
    private int? _queuedKeyboardNavigationIndex;
    private const int LoupeSampleSize = 31;
    private const int LoupeZoomScale = 4;

    private enum RoiEditMode { None, Move, Resize }

    // Playback
    private readonly DispatcherTimer _playTimer;
    private double _playIntervalSeconds = 1.0;
    private static readonly double[] PlayIntervalSteps = [0.1, 0.2, 0.5, 1.0, 2.0, 3.0, 5.0, 10.0];

    // Curvature view state (used by the live mouse-position tooltip).
    private double[]? _curvatureGrid;
    private int _curvatureGridW;
    private int _curvatureGridH;
    private int _curvatureImgW;
    private int _curvatureImgH;
    private double _curvatureArcsecPerPixel;

    public PreviewWindow(FramePreviewViewModel vm)
    {
        InitializeComponent();
        WindowPlacementService.RestorePreviewWindow(this);
        Closing += PreviewWindow_Closing;

        _vm = vm;
        DataContext = _vm;
        _vm.PropertyChanged += Vm_PropertyChanged;
        SourceInitialized += (_, _) => WindowTitleBarStyler.Apply(this);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Window_PreviewMouseUp), true);
        Loaded += (_, _) =>
        {
            FitToView();
            RedrawCacheIndicators();
            ImageScrollViewer.ScrollChanged += (_, _) => UpdateRoiOverlay();
            ImageScrollViewer.ScrollChanged += (_, _) => UpdateStarDebugOverlay();
            UpdateRoiOverlay();
            UpdateStarDebugOverlay();
            UpdateCurvatureView();
            _hasInitializedView = true;
        };

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_playIntervalSeconds) };
        _playTimer.Tick += async (_, _) =>
        {
            // Stop at the last frame
            if (_vm.CurrentFrameIndex >= _vm.FrameCount - 1)
            {
                StopPlayback();
                return;
            }
            await _vm.NavigateAsync(1);
        };
    }

    private void StopPlayback()
    {
        _playTimer.Stop();
        PlayButton.IsChecked = false;
        PlayButtonIcon.Text = "▶";
    }

    private void PlayButton_Checked(object sender, RoutedEventArgs e)
    {
        PlayButtonIcon.Text = "⏸";
        _playTimer.Interval = TimeSpan.FromSeconds(_playIntervalSeconds);
        _playTimer.Start();
    }

    private void PlayButton_Unchecked(object sender, RoutedEventArgs e)
    {
        _playTimer.Stop();
        PlayButtonIcon.Text = "▶";
    }

    private void IntervalDown_Click(object sender, RoutedEventArgs e)
    {
        var idx = Array.BinarySearch(PlayIntervalSteps, _playIntervalSeconds);
        if (idx < 0) idx = ~idx;
        idx = Math.Max(0, idx - 1);
        _playIntervalSeconds = PlayIntervalSteps[idx];
        _playTimer.Interval = TimeSpan.FromSeconds(_playIntervalSeconds);
        UpdateIntervalText();
    }

    private void IntervalUp_Click(object sender, RoutedEventArgs e)
    {
        var idx = Array.BinarySearch(PlayIntervalSteps, _playIntervalSeconds);
        if (idx < 0) idx = ~idx - 1;
        idx = Math.Min(PlayIntervalSteps.Length - 1, idx + 1);
        _playIntervalSeconds = PlayIntervalSteps[idx];
        _playTimer.Interval = TimeSpan.FromSeconds(_playIntervalSeconds);
        UpdateIntervalText();
    }

    private void UpdateIntervalText()
    {
        IntervalText.Text = _playIntervalSeconds < 1.0
            ? $"{_playIntervalSeconds * 1000:0} ms"
            : $"{_playIntervalSeconds:0.#} s";
    }

    private void PreviewWindow_Closing(object? sender, CancelEventArgs e)
    {
        _playTimer.Stop();
        WindowPlacementService.SavePreviewWindow(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= Vm_PropertyChanged;
        base.OnClosed(e);
    }

    public void RefreshImage(BitmapSource image)
    {
        var viewState = CaptureViewState();
        _vm.Image = null;
        _vm.Image = image;
        HideLoupe();
        Dispatcher.BeginInvoke(UpdateRoiOverlay, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(UpdateStarDebugOverlay, DispatcherPriority.Loaded);

        if (!_hasInitializedView)
        {
            return;
        }

        if (viewState is null)
        {
            FitToView();
            return;
        }

        Dispatcher.BeginInvoke(() => RestoreViewState(viewState), DispatcherPriority.Loaded);
    }

    private void Smaller_Click(object sender, RoutedEventArgs e)
    {
        _vm.Zoom = Math.Max(0.1, _vm.Zoom / 1.25);
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        FitToView();
    }

    private void FitToView()
    {
        if (PreviewImage.Source is null)
        {
            return;
        }

        var source = PreviewImage.Source;
        if (source.Width <= 0 || source.Height <= 0 || ImageScrollViewer.ViewportWidth <= 0 || ImageScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var zx = ImageScrollViewer.ViewportWidth / source.Width;
        var zy = ImageScrollViewer.ViewportHeight / source.Height;
        _vm.Zoom = Math.Max(0.1, Math.Min(zx, zy));
        ImageScrollViewer.ScrollToHorizontalOffset(0);
        ImageScrollViewer.ScrollToVerticalOffset(0);
    }

    private ViewState? CaptureViewState()
    {
        if (PreviewImage.Source is null || ImageScrollViewer.ViewportWidth <= 0 || ImageScrollViewer.ViewportHeight <= 0)
        {
            return null;
        }

        var extentWidth = ImageScrollViewer.ExtentWidth;
        var extentHeight = ImageScrollViewer.ExtentHeight;
        var centerX = ImageScrollViewer.HorizontalOffset + (ImageScrollViewer.ViewportWidth / 2.0);
        var centerY = ImageScrollViewer.VerticalOffset + (ImageScrollViewer.ViewportHeight / 2.0);

        return new ViewState(
            _vm.Zoom,
            extentWidth > 0 ? centerX / extentWidth : 0.5,
            extentHeight > 0 ? centerY / extentHeight : 0.5);
    }

    private void RestoreViewState(ViewState viewState)
    {
        _vm.Zoom = viewState.Zoom;

        Dispatcher.BeginInvoke(() =>
        {
            var targetCenterX = ImageScrollViewer.ExtentWidth * viewState.CenterXRatio;
            var targetCenterY = ImageScrollViewer.ExtentHeight * viewState.CenterYRatio;
            var horizontalOffset = Math.Max(0, targetCenterX - (ImageScrollViewer.ViewportWidth / 2.0));
            var verticalOffset = Math.Max(0, targetCenterY - (ImageScrollViewer.ViewportHeight / 2.0));

            ImageScrollViewer.ScrollToHorizontalOffset(horizontalOffset);
            ImageScrollViewer.ScrollToVerticalOffset(verticalOffset);
        }, DispatcherPriority.Background);
    }

    private sealed record ViewState(double Zoom, double CenterXRatio, double CenterYRatio);

    private void StfSlider_Loaded(object? sender, EventArgs e)
    {
        HookSliderThumbEvents(sender as Slider);
    }

    private void HookSliderThumbEvents(Slider? slider)
    {
        if (slider?.Template.FindName("PART_Track", slider) is not Track track || track.Thumb is null)
        {
            return;
        }

        track.Thumb.DragStarted -= StretchThumb_DragStarted;
        track.Thumb.DragCompleted -= StretchThumb_DragCompleted;
        track.Thumb.DragStarted += StretchThumb_DragStarted;
        track.Thumb.DragCompleted += StretchThumb_DragCompleted;
    }

    private void StretchThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _vm.BeginInteractiveStretch();
    }

    private void StretchThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _vm.EndInteractiveStretch();
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        await _vm.NavigateAsync(-1);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await _vm.NavigateAsync(1);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _vm.Zoom = Math.Min(8.0, _vm.Zoom * 1.25);
    }

    private void OneToOne_Click(object sender, RoutedEventArgs e)
    {
        _vm.Zoom = 1.0;
    }

    private void ToggleReject_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleReject();
    }

    private void OpenInFileExplorer_Click(object sender, RoutedEventArgs e)
    {
        var filePath = _vm.Item.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            System.Windows.MessageBox.Show(this, "The current file could not be found.", "Open in File Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Failed to open File Explorer.\n\n{ex.Message}", "Open in File Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewImage.Source is null)
        {
            return;
        }

        e.Handled = true;
        var zoomFactor = e.Delta > 0 ? 1.1 : (1.0 / 1.1);
        ZoomAroundViewerPoint(e.GetPosition(ImageScrollViewer), zoomFactor);
    }

    private void ZoomAroundViewerPoint(WpfPoint viewerPoint, double zoomFactor)
    {
        if (PreviewImage.Source is null
            || ImageScrollViewer.ViewportWidth <= 0
            || ImageScrollViewer.ViewportHeight <= 0
            || zoomFactor <= 0)
        {
            return;
        }

        var oldZoom = _vm.Zoom;
        var newZoom = Math.Clamp(oldZoom * zoomFactor, 0.1, 8.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            return;
        }

        var imageX = (ImageScrollViewer.HorizontalOffset + viewerPoint.X) / oldZoom;
        var imageY = (ImageScrollViewer.VerticalOffset + viewerPoint.Y) / oldZoom;

        _vm.Zoom = newZoom;

        Dispatcher.BeginInvoke(() =>
        {
            var targetHorizontalOffset = Math.Max(0, (imageX * newZoom) - viewerPoint.X);
            var targetVerticalOffset = Math.Max(0, (imageY * newZoom) - viewerPoint.Y);
            ImageScrollViewer.ScrollToHorizontalOffset(targetHorizontalOffset);
            ImageScrollViewer.ScrollToVerticalOffset(targetVerticalOffset);
        }, DispatcherPriority.Loaded);
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideLoupe();

        if (PreviewImage.Source is null)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _isRoiDragging = true;
            _roiDragOriginImage = e.GetPosition(PreviewImage);
            PreviewImage.CaptureMouse();
            Cursor = System.Windows.Input.Cursors.Cross;
            EnsureRoiDragOverlay();
            UpdateRoiDragOverlay(_roiDragOriginImage, _roiDragOriginImage);
            e.Handled = true;
            return;
        }

        _isPanning = true;
        _panStartPoint = e.GetPosition(ImageScrollViewer);
        _panStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ImageScrollViewer.VerticalOffset;
        PreviewImage.CaptureMouse();
        Cursor = System.Windows.Input.Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isRoiDragging)
        {
            CommitRoiDrag(e.GetPosition(PreviewImage));
            e.Handled = true;
            return;
        }

        if (!_isPanning)
        {
            return;
        }

        StopPanning();
        e.Handled = true;
    }

    private void ImageScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject dependencyObject || IsVisualDescendantOf(dependencyObject, PreviewImage))
        {
            return;
        }

        HideLoupe();
    }

    private void PreviewImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isLoupeActive = true;
        PreviewImage.CaptureMouse();
        ShowLoupeAt(e.GetPosition(PreviewImage));
    }

    private void PreviewImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isRoiDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                CancelRoiDrag();
                return;
            }

            UpdateRoiDragOverlay(_roiDragOriginImage, e.GetPosition(PreviewImage));
            e.Handled = true;
            return;
        }

        if (!_isLoupeActive)
        {
            if (!_isPanning)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                StopPanning();
                return;
            }

            var point = e.GetPosition(ImageScrollViewer);
            var deltaX = point.X - _panStartPoint.X;
            var deltaY = point.Y - _panStartPoint.Y;

            ImageScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartHorizontalOffset - deltaX));
            ImageScrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartVerticalOffset - deltaY));
            e.Handled = true;
            return;
        }

        ShowLoupeAt(e.GetPosition(PreviewImage));
    }

    private void PreviewImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isLoupeActive = false;
        if (Mouse.Captured == PreviewImage)
        {
            Mouse.Capture(null);
        }

        HideLoupe();
    }

    private void ShowLoupeAt(WpfPoint imagePoint)
    {
        if (PreviewImage.Source is not BitmapSource source || PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            return;
        }

        var pixelX = Math.Clamp((int)Math.Round((imagePoint.X / PreviewImage.ActualWidth) * Math.Max(0, source.PixelWidth - 1)), 0, Math.Max(0, source.PixelWidth - 1));
        var pixelY = Math.Clamp((int)Math.Round((imagePoint.Y / PreviewImage.ActualHeight) * Math.Max(0, source.PixelHeight - 1)), 0, Math.Max(0, source.PixelHeight - 1));

        var crop = BuildLoupeBitmap(source, pixelX, pixelY, out var centerValue, out var minValue, out var maxValue, out var meanValue);
        LoupeImage.Source = crop;

        LoupeXText.Text = $"X    {pixelX}";
        LoupeYText.Text = $"Y    {pixelY}";
        LoupeKText.Text = $"K    {centerValue}";
        LoupeMinText.Text = $"Min  {minValue}";
        LoupeMaxText.Text = $"Max  {maxValue}";
        LoupeMeanText.Text = $"Mean {meanValue:F2}";

        var hostPoint = PreviewImage.TranslatePoint(imagePoint, LoupeCanvas);
        var canvasWidth = Math.Max(0.0, LoupeCanvas.ActualWidth);
        var canvasHeight = Math.Max(0.0, LoupeCanvas.ActualHeight);
        var left = Math.Min(Math.Max(0, hostPoint.X + 16), Math.Max(0, canvasWidth - LoupeBorder.Width));
        var top = Math.Min(Math.Max(0, hostPoint.Y + 16), Math.Max(0, canvasHeight - LoupeBorder.Height));

        Canvas.SetLeft(LoupeBorder, left);
        Canvas.SetTop(LoupeBorder, top);
        LoupeBorder.Visibility = Visibility.Visible;
    }

    private static BitmapSource BuildLoupeBitmap(BitmapSource source, int pixelX, int pixelY, out byte centerValue, out byte minValue, out byte maxValue, out double meanValue)
    {
        BitmapSource graySource;
        if (source.Format == PixelFormats.Gray8)
        {
            graySource = source;
        }
        else
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = source;
            converted.DestinationFormat = PixelFormats.Gray8;
            converted.EndInit();
            converted.Freeze();
            graySource = converted;
        }

        var half = LoupeSampleSize / 2;
        var startX = Math.Clamp(pixelX - half, 0, Math.Max(0, graySource.PixelWidth - LoupeSampleSize));
        var startY = Math.Clamp(pixelY - half, 0, Math.Max(0, graySource.PixelHeight - LoupeSampleSize));
        var width = Math.Min(LoupeSampleSize, graySource.PixelWidth);
        var height = Math.Min(LoupeSampleSize, graySource.PixelHeight);
        var stride = width;
        var pixels = new byte[stride * height];
        graySource.CopyPixels(new Int32Rect(startX, startY, width, height), pixels, stride, 0);

        minValue = byte.MaxValue;
        maxValue = byte.MinValue;
        double sum = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var v = pixels[i];
            if (v < minValue) minValue = v;
            if (v > maxValue) maxValue = v;
            sum += v;
        }

        meanValue = sum / Math.Max(1, width * height);
        var centerLocalX = Math.Clamp(pixelX - startX, 0, width - 1);
        var centerLocalY = Math.Clamp(pixelY - startY, 0, height - 1);
        centerValue = pixels[(centerLocalY * stride) + centerLocalX];

        var loupeSource = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, stride);
        loupeSource.Freeze();
        var scaled = new TransformedBitmap(loupeSource, new ScaleTransform(LoupeZoomScale, LoupeZoomScale));
        scaled.Freeze();
        return scaled;
    }

    private void HideLoupe()
    {
        _isLoupeActive = false;
        LoupeBorder.Visibility = Visibility.Collapsed;
        LoupeImage.Source = null;
    }

    private void StopPanning()
    {
        _isPanning = false;
        if (Mouse.Captured == PreviewImage)
        {
            Mouse.Capture(null);
        }

        Cursor = null;
    }

    private void EnsureRoiDragOverlay()
    {
        if (_roiDragOverlay is not null)
        {
            return;
        }

        _roiDragOverlay = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00)),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 2],
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 0xFF, 0xA5, 0x00)),
            IsHitTestVisible = false
        };
        LoupeCanvas.Children.Add(_roiDragOverlay);
    }

    // origin and current are in PreviewImage logical coordinates (unscaled by zoom)
    private void UpdateRoiDragOverlay(WpfPoint origin, WpfPoint current)
    {
        if (_roiDragOverlay is null || PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            return;
        }

        // Compute square: side = min of |dx|, |dy| in image pixels
        var dx = current.X - origin.X;
        var dy = current.Y - origin.Y;
        var side = Math.Min(Math.Abs(dx), Math.Abs(dy));
        var signX = dx >= 0 ? 1 : -1;
        var signY = dy >= 0 ? 1 : -1;

        // Clamp so rect stays within image bounds
        var x1 = origin.X;
        var y1 = origin.Y;
        var x2 = Math.Clamp(x1 + signX * side, 0, PreviewImage.ActualWidth);
        var y2 = Math.Clamp(y1 + signY * side, 0, PreviewImage.ActualHeight);
        x1 = Math.Clamp(x1, 0, PreviewImage.ActualWidth);
        y1 = Math.Clamp(y1, 0, PreviewImage.ActualHeight);

        var rectX = Math.Min(x1, x2);
        var rectY = Math.Min(y1, y2);
        var rectW = Math.Abs(x2 - x1);
        var rectH = Math.Abs(y2 - y1);

        // Translate from PreviewImage coords to LoupeCanvas coords (accounts for zoom + scroll)
        var topLeft = PreviewImage.TranslatePoint(new WpfPoint(rectX, rectY), LoupeCanvas);

        // The rectangle is drawn in canvas space; width/height are in zoomed display pixels
        _roiDragOverlay.Width = rectW * _vm.Zoom;
        _roiDragOverlay.Height = rectH * _vm.Zoom;
        Canvas.SetLeft(_roiDragOverlay, topLeft.X);
        Canvas.SetTop(_roiDragOverlay, topLeft.Y);
        _roiDragOverlay.Visibility = Visibility.Visible;
    }

    private void CommitRoiDrag(WpfPoint current)
    {
        _isRoiDragging = false;
        if (Mouse.Captured == PreviewImage)
        {
            Mouse.Capture(null);
        }

        Cursor = null;

        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            RemoveRoiDragOverlay();
            return;
        }

        var dx = current.X - _roiDragOriginImage.X;
        var dy = current.Y - _roiDragOriginImage.Y;
        var side = Math.Min(Math.Abs(dx), Math.Abs(dy));

        if (side < 4)
        {
            // Too small — ignore
            RemoveRoiDragOverlay();
            return;
        }

        var signX = dx >= 0 ? 1 : -1;
        var signY = dy >= 0 ? 1 : -1;
        var x1 = Math.Clamp(_roiDragOriginImage.X, 0, PreviewImage.ActualWidth);
        var y1 = Math.Clamp(_roiDragOriginImage.Y, 0, PreviewImage.ActualHeight);
        var x2 = Math.Clamp(x1 + signX * side, 0, PreviewImage.ActualWidth);
        var y2 = Math.Clamp(y1 + signY * side, 0, PreviewImage.ActualHeight);

        var left = Math.Min(x1, x2) / PreviewImage.ActualWidth;
        var top = Math.Min(y1, y2) / PreviewImage.ActualHeight;
        var width = Math.Abs(x2 - x1) / PreviewImage.ActualWidth;
        var height = Math.Abs(y2 - y1) / PreviewImage.ActualHeight;

        RemoveRoiDragOverlay();
        _vm.SetManualRoi((left, top, width, height));
    }

    private void CancelRoiDrag()
    {
        _isRoiDragging = false;
        if (Mouse.Captured == PreviewImage)
        {
            Mouse.Capture(null);
        }

        Cursor = null;
        RemoveRoiDragOverlay();
    }

    private void RemoveRoiDragOverlay()
    {
        if (_roiDragOverlay is not null)
        {
            LoupeCanvas.Children.Remove(_roiDragOverlay);
            _roiDragOverlay = null;
        }
    }

    // ---------- Persistent ROI overlay (toggleable, drag-move / corner-resize / right-click-apply) ----------

    private void EnsureRoiOverlayShapes()
    {
        if (_roiPersistentRect is not null)
        {
            return;
        }

        var stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
        stroke.Freeze();

        _roiPersistentRect = new System.Windows.Shapes.Rectangle
        {
            Stroke = stroke,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeAll,
        };
        _roiPersistentRect.MouseLeftButtonDown += RoiBody_MouseLeftButtonDown;
        _roiPersistentRect.MouseMove += RoiOverlay_MouseMove;
        _roiPersistentRect.MouseLeftButtonUp += RoiOverlay_MouseLeftButtonUp;
        _roiPersistentRect.MouseRightButtonUp += RoiOverlay_MouseRightButtonUp;
        RoiOverlayCanvas.Children.Add(_roiPersistentRect);

        for (var i = 0; i < 4; i++)
        {
            var handle = new System.Windows.Shapes.Rectangle
            {
                Width = 10,
                Height = 10,
                Stroke = stroke,
                StrokeThickness = 1.0,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
                Cursor = (i == 0 || i == 3) ? System.Windows.Input.Cursors.SizeNWSE : System.Windows.Input.Cursors.SizeNESW,
                Tag = i,
            };
            handle.MouseLeftButtonDown += RoiHandle_MouseLeftButtonDown;
            handle.MouseMove += RoiOverlay_MouseMove;
            handle.MouseLeftButtonUp += RoiOverlay_MouseLeftButtonUp;
            handle.MouseRightButtonUp += RoiOverlay_MouseRightButtonUp;
            _roiHandles[i] = handle;
            RoiOverlayCanvas.Children.Add(handle);
        }
    }

    private void UpdateRoiOverlay()
    {
        if (!_vm.IsRoiOverlayVisible || _vm.CurrentManualRoi is not { } roi
            || PreviewImage.Source is not BitmapSource source
            || PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            RoiOverlayCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        EnsureRoiOverlayShapes();
        RoiOverlayCanvas.Visibility = Visibility.Visible;

        var imgLeft = roi.Left * PreviewImage.ActualWidth;
        var imgTop = roi.Top * PreviewImage.ActualHeight;
        var imgRight = (roi.Left + roi.Width) * PreviewImage.ActualWidth;
        var imgBottom = (roi.Top + roi.Height) * PreviewImage.ActualHeight;

        var topLeft = PreviewImage.TranslatePoint(new WpfPoint(imgLeft, imgTop), RoiOverlayCanvas);
        var bottomRight = PreviewImage.TranslatePoint(new WpfPoint(imgRight, imgBottom), RoiOverlayCanvas);

        var x = topLeft.X;
        var y = topLeft.Y;
        var w = Math.Max(2.0, bottomRight.X - topLeft.X);
        var h = Math.Max(2.0, bottomRight.Y - topLeft.Y);

        _roiPersistentRect!.Width = w;
        _roiPersistentRect.Height = h;
        Canvas.SetLeft(_roiPersistentRect, x);
        Canvas.SetTop(_roiPersistentRect, y);

        // Corners: 0=TL, 1=TR, 2=BL, 3=BR
        PositionHandle(0, x, y);
        PositionHandle(1, x + w, y);
        PositionHandle(2, x, y + h);
        PositionHandle(3, x + w, y + h);
    }

    private void PositionHandle(int index, double cx, double cy)
    {
        var handle = _roiHandles[index];
        if (handle is null) return;
        Canvas.SetLeft(handle, cx - handle.Width / 2.0);
        Canvas.SetTop(handle, cy - handle.Height / 2.0);
    }

    private void UpdateStarDebugOverlay()
    {
        if (StarDebugOverlayCanvas is null)
        {
            return;
        }

        if (!_vm.IsStarDebugOverlayVisible
            || PreviewImage.Source is not BitmapSource source
            || PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            StarDebugOverlayCanvas.Visibility = Visibility.Collapsed;
            StarDebugOverlayCanvas.Children.Clear();
            return;
        }

        var stars = _vm.Item?.Metrics.Stars;
        if (stars is null || stars.Count == 0)
        {
            StarDebugOverlayCanvas.Visibility = Visibility.Collapsed;
            StarDebugOverlayCanvas.Children.Clear();
            return;
        }

        StarDebugOverlayCanvas.Visibility = Visibility.Visible;
        StarDebugOverlayCanvas.Children.Clear();

        var pixelWidth = source.PixelWidth;
        var pixelHeight = source.PixelHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        var scaleX = PreviewImage.ActualWidth / pixelWidth;
        var scaleY = PreviewImage.ActualHeight / pixelHeight;
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x33, 0xFF, 0x66));
        brush.Freeze();

        // Per-pixel arcsec scale, derived from the frame's focal length / pixel size
        // via the already-computed FwhmArcsec / Fwhm ratio so labels match the metrics panel.
        var frameMetrics = _vm.Item?.Metrics;
        double arcsecPerPixel = 0;
        if (frameMetrics is not null && frameMetrics.Fwhm > 0 && frameMetrics.FwhmArcsec is > 0)
        {
            arcsecPerPixel = frameMetrics.FwhmArcsec.Value / frameMetrics.Fwhm;
        }

        foreach (var star in stars)
        {
            // Star FWHM is in image pixels; draw a ring at ~2*FWHM diameter for visibility.
            var radiusPx = Math.Max(2.0, star.Fwhm) * 1.5;
            var topLeftImg = new WpfPoint(
                (star.X - radiusPx) * scaleX,
                (star.Y - radiusPx) * scaleY);
            var bottomRightImg = new WpfPoint(
                (star.X + radiusPx) * scaleX,
                (star.Y + radiusPx) * scaleY);

            var topLeft = PreviewImage.TranslatePoint(topLeftImg, StarDebugOverlayCanvas);
            var bottomRight = PreviewImage.TranslatePoint(bottomRightImg, StarDebugOverlayCanvas);
            var w = bottomRight.X - topLeft.X;
            var h = bottomRight.Y - topLeft.Y;
            if (w < 3 || h < 3)
            {
                continue;
            }

            var ellipse = new Ellipse
            {
                Width = w,
                Height = h,
                Stroke = brush,
                StrokeThickness = 1.0,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ellipse, topLeft.X);
            Canvas.SetTop(ellipse, topLeft.Y);
            StarDebugOverlayCanvas.Children.Add(ellipse);

            var label = new TextBlock
            {
                Text = arcsecPerPixel > 0
                    ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} ({1:F2}\")", star.Fwhm, star.Fwhm * arcsecPerPixel)
                    : star.Fwhm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                Foreground = brush,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, bottomRight.X + 2);
            Canvas.SetTop(label, topLeft.Y - 2);
            StarDebugOverlayCanvas.Children.Add(label);
        }

        // Summary readout: count and median FWHM (px / arcsec when available).
        var summary = frameMetrics is null
            ? $"Stars: {stars.Count}"
            : frameMetrics.FwhmArcsec is > 0
                ? $"Stars: {stars.Count}   FWHM: {frameMetrics.Fwhm:F2} px / {frameMetrics.FwhmArcsec:F2}\""
                : $"Stars: {stars.Count}   FWHM: {frameMetrics.Fwhm:F2} px";

        var summaryBackground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0));
        summaryBackground.Freeze();
        var summaryBorder = new Border
        {
            Background = summaryBackground,
            Padding = new Thickness(6, 3, 6, 3),
            Child = new TextBlock
            {
                Text = summary,
                Foreground = brush,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12
            },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(summaryBorder, 8);
        Canvas.SetTop(summaryBorder, 8);
        StarDebugOverlayCanvas.Children.Add(summaryBorder);
    }

    private void UpdateCurvatureView()
    {
        if (CurvatureImage is null || CurvatureStatsText is null || ImageScrollViewer is null)
        {
            return;
        }

        if (!_vm.IsCurvatureViewVisible)
        {
            CurvatureImage.Visibility = Visibility.Collapsed;
            CurvatureImage.Source = null;
            CurvatureStatsText.Visibility = Visibility.Collapsed;
            if (CurvatureTargetCanvas is not null)
            {
                CurvatureTargetCanvas.Visibility = Visibility.Collapsed;
                CurvatureTargetCanvas.Children.Clear();
            }
            ImageScrollViewer.Visibility = Visibility.Visible;
            _curvatureGrid = null;
            if (CurvatureTooltip is not null) CurvatureTooltip.Visibility = Visibility.Collapsed;
            return;
        }

        ImageScrollViewer.Visibility = Visibility.Collapsed;

        var metrics = _vm.Item?.Metrics;
        var stars = metrics?.Stars;
        if (metrics is null || stars is null || stars.Count == 0 || PreviewImage.Source is not BitmapSource src)
        {
            CurvatureImage.Visibility = Visibility.Collapsed;
            CurvatureImage.Source = null;
            CurvatureStatsText.Text = "No FWHM data";
            CurvatureStatsText.Visibility = Visibility.Visible;
            return;
        }

        var imgW = src.PixelWidth;
        var imgH = src.PixelHeight;
        if (imgW <= 0 || imgH <= 0)
        {
            return;
        }

        // Coarse grid heatmap, scaled to image aspect.
        const int gridShort = 96;
        int gridW, gridH;
        if (imgW >= imgH)
        {
            gridW = gridShort;
            gridH = Math.Max(8, (int)Math.Round(gridShort * (double)imgH / imgW));
        }
        else
        {
            gridH = gridShort;
            gridW = Math.Max(8, (int)Math.Round(gridShort * (double)imgW / imgH));
        }

        var values = new double[gridW * gridH];
        var arcsecPerPixel = metrics.Fwhm > 0 && metrics.FwhmArcsec is > 0
            ? metrics.FwhmArcsec.Value / metrics.Fwhm
            : 0.0;

        double minF = double.PositiveInfinity, maxF = double.NegativeInfinity;
        var fwhms = new System.Collections.Generic.List<double>(stars.Count);
        foreach (var s in stars)
        {
            if (s.Fwhm <= 0 || double.IsNaN(s.Fwhm)) continue;
            fwhms.Add(s.Fwhm);
            if (s.Fwhm < minF) minF = s.Fwhm;
            if (s.Fwhm > maxF) maxF = s.Fwhm;
        }
        if (fwhms.Count == 0 || double.IsInfinity(minF) || double.IsInfinity(maxF))
        {
            CurvatureImage.Visibility = Visibility.Collapsed;
            return;
        }

        // Robust ramp bounds (2nd / 98th percentile) so one outlier doesn't compress the
        // color scale, while true min/max are still reported in the stats panel.
        fwhms.Sort();
        double Percentile(double p)
        {
            var idx = (int)Math.Round((fwhms.Count - 1) * p);
            if (idx < 0) idx = 0; else if (idx >= fwhms.Count) idx = fwhms.Count - 1;
            return fwhms[idx];
        }
        var rampMin = Percentile(0.02);
        var rampMax = Percentile(0.98);
        if (rampMax - rampMin < 1e-6) { rampMin = minF; rampMax = maxF; }

        // Gaussian-kernel interpolation on coarse grid for smooth CCDInspector-style maps.
        var cellW = imgW / (double)gridW;
        var cellH = imgH / (double)gridH;
        var diag = Math.Sqrt((double)imgW * imgW + (double)imgH * imgH);
        var bandwidth = diag / 6.0; // sigma in image pixels
        var twoSigma2 = 2.0 * bandwidth * bandwidth;
        for (int gy = 0; gy < gridH; gy++)
        {
            var py = (gy + 0.5) * cellH;
            for (int gx = 0; gx < gridW; gx++)
            {
                var px = (gx + 0.5) * cellW;
                double wsum = 0, vsum = 0;
                foreach (var s in stars)
                {
                    if (s.Fwhm <= 0) continue;
                    var dx = s.X - px;
                    var dy = s.Y - py;
                    var d2 = dx * dx + dy * dy;
                    var w = Math.Exp(-d2 / twoSigma2);
                    wsum += w;
                    vsum += w * s.Fwhm;
                }
                values[gy * gridW + gx] = wsum > 0 ? vsum / wsum : minF;
            }
        }

        var range = Math.Max(1e-9, rampMax - rampMin);
        var pixels = new byte[gridW * gridH * 4];
        for (int i = 0; i < values.Length; i++)
        {
            var t = (values[i] - rampMin) / range;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            ColorRamp(t, out var r, out var g, out var b);
            var pi = i * 4;
            pixels[pi + 0] = b;
            pixels[pi + 1] = g;
            pixels[pi + 2] = r;
            pixels[pi + 3] = 255;
        }

        var bmp = BitmapSource.Create(gridW, gridH, 96, 96, PixelFormats.Bgra32, null, pixels, gridW * 4);
        bmp.Freeze();
        CurvatureImage.Source = bmp;
        CurvatureImage.Visibility = Visibility.Visible;

        // Cache grid for the live tooltip.
        _curvatureGrid = values;
        _curvatureGridW = gridW;
        _curvatureGridH = gridH;
        _curvatureImgW = imgW;
        _curvatureImgH = imgH;
        _curvatureArcsecPerPixel = arcsecPerPixel;

        // Curvature stats.
        var meanF = 0.0;
        var n = 0;
        foreach (var s in stars)
        {
            if (s.Fwhm <= 0) continue;
            meanF += s.Fwhm;
            n++;
        }
        if (n > 0) meanF /= n;

        // CCDInspector-style spatial curvature: average FWHM in the four image corners
        // versus the central region, expressed as a percentage of the center.
        double cornerSum = 0, cornerW = 0, centerSum = 0, centerW = 0;
        for (int gy = 0; gy < gridH; gy++)
        {
            var ny = (gy + 0.5) / gridH;             // 0..1
            var ry = Math.Abs(ny - 0.5) * 2.0;       // 0 center .. 1 edge
            for (int gx = 0; gx < gridW; gx++)
            {
                var nx = (gx + 0.5) / gridW;
                var rx = Math.Abs(nx - 0.5) * 2.0;
                var v = values[gy * gridW + gx];
                if (v <= 0) continue;
                // Corner weight: high near (rx,ry)~1, falls to 0 inside.
                var cornerWeight = Math.Max(0, Math.Min(rx, ry) - 0.6) / 0.4; // ramp 0.6..1.0
                cornerSum += cornerWeight * v;
                cornerW   += cornerWeight;
                // Center weight: high near (rx,ry)~0.
                var d = Math.Sqrt(rx * rx + ry * ry);
                var centerWeight = Math.Max(0, 1 - d / 0.3); // within ~30% of center
                centerSum += centerWeight * v;
                centerW   += centerWeight;
            }
        }
        var cornerAvg = cornerW > 0 ? cornerSum / cornerW : maxF;
        var centerAvg = centerW > 0 ? centerSum / centerW : minF;
        var curvature = centerAvg > 0 ? (cornerAvg - centerAvg) / centerAvg * 100.0 : 0.0;

        string Fmt(double pxVal) => arcsecPerPixel > 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} px / {1:F2}\"", pxVal, pxVal * arcsecPerPixel)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} px", pxVal);

        CurvatureStatsText.Text =
            $"Min FWHM:   {Fmt(minF)}\n" +
            $"Max FWHM:   {Fmt(maxF)}\n" +
            $"Mean FWHM:  {Fmt(meanF)}\n" +
            $"Curvature:  {curvature:F1}%\n" +
            $"Stars Used: {n}";
        CurvatureStatsText.Visibility = Visibility.Visible;

        DrawCurvatureTarget();
    }

    private void CurvatureTargetCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm.IsCurvatureViewVisible) DrawCurvatureTarget();
    }

    private void CurvatureImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_curvatureGrid is null || _curvatureGridW <= 0 || _curvatureGridH <= 0)
        {
            CurvatureTooltip.Visibility = Visibility.Collapsed;
            return;
        }
        if (CurvatureImage.ActualWidth <= 0 || CurvatureImage.ActualHeight <= 0)
        {
            return;
        }

        // The Uniform-stretched image rectangle inside the Image control.
        var ctrlW = CurvatureImage.ActualWidth;
        var ctrlH = CurvatureImage.ActualHeight;
        var imgAspect = _curvatureImgW / (double)_curvatureImgH;
        var ctrlAspect = ctrlW / ctrlH;
        double areaW, areaH, areaX, areaY;
        if (imgAspect > ctrlAspect) { areaW = ctrlW; areaH = ctrlW / imgAspect; areaX = 0; areaY = (ctrlH - areaH) / 2; }
        else                        { areaH = ctrlH; areaW = ctrlH * imgAspect; areaY = 0; areaX = (ctrlW - areaW) / 2; }

        var pos = e.GetPosition(CurvatureImage);
        var u = (pos.X - areaX) / areaW;
        var v = (pos.Y - areaY) / areaH;
        if (u < 0 || u > 1 || v < 0 || v > 1)
        {
            CurvatureTooltip.Visibility = Visibility.Collapsed;
            return;
        }

        var gx = Math.Min(_curvatureGridW - 1, Math.Max(0, (int)(u * _curvatureGridW)));
        var gy = Math.Min(_curvatureGridH - 1, Math.Max(0, (int)(v * _curvatureGridH)));
        var fwhmPx = _curvatureGrid[gy * _curvatureGridW + gx];

        CurvatureTooltipText.Text = _curvatureArcsecPerPixel > 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} px / {1:F2}\"", fwhmPx, fwhmPx * _curvatureArcsecPerPixel)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} px", fwhmPx);

        var hostPos = e.GetPosition((IInputElement)CurvatureTooltip.Parent);
        CurvatureTooltip.Margin = new Thickness(hostPos.X + 14, hostPos.Y + 14, 0, 0);
        CurvatureTooltip.Visibility = Visibility.Visible;
    }

    private void CurvatureImage_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CurvatureTooltip.Visibility = Visibility.Collapsed;
    }

    private void DrawCurvatureTarget()
    {
        if (CurvatureTargetCanvas is null || CurvatureImage is null) return;
        CurvatureTargetCanvas.Children.Clear();

        var canvasW = CurvatureTargetCanvas.ActualWidth;
        var canvasH = CurvatureTargetCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0)
        {
            // First pass before layout; retry once layout completes.
            CurvatureTargetCanvas.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(DrawCurvatureTarget, DispatcherPriority.Loaded);
            return;
        }

        // Determine the on-screen rectangle of the Uniform-stretched heatmap image.
        var src = CurvatureImage.Source as BitmapSource;
        double areaW = canvasW, areaH = canvasH, areaX = 0, areaY = 0;
        if (src is not null && src.PixelWidth > 0 && src.PixelHeight > 0)
        {
            // CurvatureImage is sized by the grid bitmap aspect, which matches image aspect.
            var imgAspect = (double)src.PixelWidth / src.PixelHeight;
            var canvasAspect = canvasW / canvasH;
            if (imgAspect > canvasAspect)
            {
                areaW = canvasW;
                areaH = canvasW / imgAspect;
                areaX = 0;
                areaY = (canvasH - areaH) / 2;
            }
            else
            {
                areaH = canvasH;
                areaW = canvasH * imgAspect;
                areaY = 0;
                areaX = (canvasW - areaW) / 2;
            }
        }

        var cx = areaX + areaW / 2.0;
        var cy = areaY + areaH / 2.0;
        var white = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        white.Freeze();

        // Outer dashed ring.
        var ringR = Math.Min(areaW, areaH) * 0.04;
        var ring = new Ellipse
        {
            Width = ringR * 2,
            Height = ringR * 2,
            Stroke = white,
            StrokeThickness = 1.0,
            StrokeDashArray = new DoubleCollection { 3, 3 }
        };
        Canvas.SetLeft(ring, cx - ringR);
        Canvas.SetTop(ring, cy - ringR);
        CurvatureTargetCanvas.Children.Add(ring);

        // Crosshair.
        var armLen = ringR * 1.6;
        var hLine = new Line { X1 = cx - armLen, Y1 = cy, X2 = cx + armLen, Y2 = cy, Stroke = white, StrokeThickness = 1.0 };
        var vLine = new Line { X1 = cx, Y1 = cy - armLen, X2 = cx, Y2 = cy + armLen, Stroke = white, StrokeThickness = 1.0 };
        CurvatureTargetCanvas.Children.Add(hLine);
        CurvatureTargetCanvas.Children.Add(vLine);

        // Center bullet.
        const double bulletR = 2.5;
        var bullet = new Ellipse
        {
            Width = bulletR * 2,
            Height = bulletR * 2,
            Fill = white
        };
        Canvas.SetLeft(bullet, cx - bulletR);
        Canvas.SetTop(bullet, cy - bulletR);
        CurvatureTargetCanvas.Children.Add(bullet);

        CurvatureTargetCanvas.Visibility = Visibility.Visible;
    }

    private static void ColorRamp(double t, out byte r, out byte g, out byte b)
    {
        // CCDInspector-style palette: dark blue -> blue -> cyan -> green -> yellow -> orange -> red -> pink.
        // Anchor stops (t, R, G, B).
        ReadOnlySpan<(double t, double r, double g, double b)> stops = stackalloc (double, double, double, double)[]
        {
            (0.00, 0.125, 0.125, 0.149), // #202026 background
            (0.10, 0.05, 0.05, 0.55),    // dark blue
            (0.20, 0.00, 0.20, 1.00),    // blue
            (0.35, 0.00, 0.85, 1.00),    // cyan
            (0.50, 0.00, 0.85, 0.10),    // green
            (0.68, 1.00, 1.00, 0.00),    // yellow
            (0.82, 1.00, 0.55, 0.00),    // orange
            (0.93, 1.00, 0.10, 0.10),    // red
            (1.00, 1.00, 0.55, 0.85),    // pink
        };

        if (t <= stops[0].t) { r = (byte)Math.Round(stops[0].r * 255); g = (byte)Math.Round(stops[0].g * 255); b = (byte)Math.Round(stops[0].b * 255); return; }
        if (t >= stops[^1].t) { r = (byte)Math.Round(stops[^1].r * 255); g = (byte)Math.Round(stops[^1].g * 255); b = (byte)Math.Round(stops[^1].b * 255); return; }

        for (int i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].t)
            {
                var a = stops[i - 1];
                var c = stops[i];
                var u = (t - a.t) / (c.t - a.t);
                var rf = a.r + (c.r - a.r) * u;
                var gf = a.g + (c.g - a.g) * u;
                var bf = a.b + (c.b - a.b) * u;
                r = (byte)Math.Round(rf * 255);
                g = (byte)Math.Round(gf * 255);
                b = (byte)Math.Round(bf * 255);
                return;
            }
        }
        r = g = b = 0;
    }

    private void RoiBody_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is null) return;
        _isRoiOverlayEditing = true;
        _roiEditMode = RoiEditMode.Move;
        _roiActiveHandleIndex = -1;
        _roiEditStartMouseImage = e.GetPosition(PreviewImage);
        _roiEditStartRect = _vm.CurrentManualRoi ?? (0, 0, 0, 0);
        ((System.Windows.IInputElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void RoiHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is null) return;
        if (sender is not System.Windows.Shapes.Rectangle handle) return;
        _isRoiOverlayEditing = true;
        _roiEditMode = RoiEditMode.Resize;
        _roiActiveHandleIndex = handle.Tag is int idx ? idx : 0;
        _roiEditStartMouseImage = e.GetPosition(PreviewImage);
        _roiEditStartRect = _vm.CurrentManualRoi ?? (0, 0, 0, 0);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void RoiOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isRoiOverlayEditing || PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0) return;

        var current = e.GetPosition(PreviewImage);
        var dxNorm = (current.X - _roiEditStartMouseImage.X) / PreviewImage.ActualWidth;
        var dyNorm = (current.Y - _roiEditStartMouseImage.Y) / PreviewImage.ActualHeight;

        var start = _roiEditStartRect;
        (double Left, double Top, double Width, double Height) next = start;

        if (_roiEditMode == RoiEditMode.Move)
        {
            next.Left = Math.Clamp(start.Left + dxNorm, 0.0, Math.Max(0.0, 1.0 - start.Width));
            next.Top = Math.Clamp(start.Top + dyNorm, 0.0, Math.Max(0.0, 1.0 - start.Height));
        }
        else if (_roiEditMode == RoiEditMode.Resize)
        {
            // Square in IMAGE PIXELS: widthNorm * imgW == heightNorm * imgH.
            // Work in pixel space, then convert back to normalized.
            double imgW = PreviewImage.ActualWidth;
            double imgH = PreviewImage.ActualHeight;
            // 0=TL, 1=TR, 2=BL, 3=BR
            double l = start.Left * imgW, t = start.Top * imgH;
            double r = (start.Left + start.Width) * imgW, b = (start.Top + start.Height) * imgH;
            double ax, ay; // anchor (pixels)
            double mx, my; // moving corner start (pixels)
            switch (_roiActiveHandleIndex)
            {
                case 0: ax = r; ay = b; mx = l; my = t; break;
                case 1: ax = l; ay = b; mx = r; my = t; break;
                case 2: ax = r; ay = t; mx = l; my = b; break;
                default: ax = l; ay = t; mx = r; my = b; break;
            }
            var dxPx = current.X - _roiEditStartMouseImage.X;
            var dyPx = current.Y - _roiEditStartMouseImage.Y;
            var newMx = mx + dxPx;
            var newMy = my + dyPx;
            var sx = newMx - ax;
            var sy = newMy - ay;
            var sidePx = Math.Max(Math.Abs(sx), Math.Abs(sy));
            var minSidePx = Math.Max(4.0, 0.005 * Math.Min(imgW, imgH));
            if (sidePx < minSidePx) sidePx = minSidePx;
            var signX = sx >= 0 ? 1 : -1;
            var signY = sy >= 0 ? 1 : -1;
            newMx = ax + signX * sidePx;
            newMy = ay + signY * sidePx;

            // Clamp the moving corner to image bounds; if clamped, shrink side accordingly.
            var clampedMx = Math.Clamp(newMx, 0.0, imgW);
            var clampedMy = Math.Clamp(newMy, 0.0, imgH);
            sidePx = Math.Min(Math.Abs(clampedMx - ax), Math.Abs(clampedMy - ay));
            if (sidePx < minSidePx) sidePx = minSidePx;

            double nlPx = Math.Min(ax, ax + signX * sidePx);
            double ntPx = Math.Min(ay, ay + signY * sidePx);
            double sidePxFinal = sidePx;

            next = (
                Math.Clamp(nlPx / imgW, 0.0, 1.0),
                Math.Clamp(ntPx / imgH, 0.0, 1.0),
                Math.Clamp(sidePxFinal / imgW, 0.0, 1.0),
                Math.Clamp(sidePxFinal / imgH, 0.0, 1.0));
            next.Left = Math.Clamp(next.Left, 0.0, Math.Max(0.0, 1.0 - next.Width));
            next.Top = Math.Clamp(next.Top, 0.0, Math.Max(0.0, 1.0 - next.Height));

            DrawRoiPreview(next);
            e.Handled = true;
            return;
        }
        else
        {
            return;
        }

        DrawRoiPreview(next);
        e.Handled = true;
    }

    private (double Left, double Top, double Width, double Height)? _roiPreviewRect;

    private void DrawRoiPreview((double Left, double Top, double Width, double Height) rect)
    {
        _roiPreviewRect = rect;
        if (_roiPersistentRect is null) return;

        var imgLeft = rect.Left * PreviewImage.ActualWidth;
        var imgTop = rect.Top * PreviewImage.ActualHeight;
        var imgRight = (rect.Left + rect.Width) * PreviewImage.ActualWidth;
        var imgBottom = (rect.Top + rect.Height) * PreviewImage.ActualHeight;

        var topLeft = PreviewImage.TranslatePoint(new WpfPoint(imgLeft, imgTop), RoiOverlayCanvas);
        var bottomRight = PreviewImage.TranslatePoint(new WpfPoint(imgRight, imgBottom), RoiOverlayCanvas);

        var x = topLeft.X;
        var y = topLeft.Y;
        var w = Math.Max(2.0, bottomRight.X - topLeft.X);
        var h = Math.Max(2.0, bottomRight.Y - topLeft.Y);

        _roiPersistentRect.Width = w;
        _roiPersistentRect.Height = h;
        Canvas.SetLeft(_roiPersistentRect, x);
        Canvas.SetTop(_roiPersistentRect, y);
        PositionHandle(0, x, y);
        PositionHandle(1, x + w, y);
        PositionHandle(2, x, y + h);
        PositionHandle(3, x + w, y + h);
    }

    private void RoiOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRoiOverlayEditing) return;
        _isRoiOverlayEditing = false;
        _roiEditMode = RoiEditMode.None;
        ((System.Windows.IInputElement)sender).ReleaseMouseCapture();

        if (_roiPreviewRect is { } rect)
        {
            _vm.SetManualRoi(rect);
        }
        _roiPreviewRect = null;
        // ViewModel will refresh ROI thumbnails; re-sync overlay from authoritative source.
        UpdateRoiOverlay();
        e.Handled = true;
    }

    private void RoiOverlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CurrentManualRoi is { } rect)
        {
            _vm.SetManualRoi(rect);
            UpdateRoiOverlay();
        }
        e.Handled = true;
    }

    private static DependencyObject? GetParentObject(DependencyObject? child)
    {
        if (child is null)
        {
            return null;
        }

        if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(child);
        }

        if (child is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }

        return LogicalTreeHelper.GetParent(child);
    }

    private static bool IsVisualDescendantOf(DependencyObject? child, DependencyObject ancestor)
    {
        while (child is not null)
        {
            if (ReferenceEquals(child, ancestor))
            {
                return true;
            }

            child = GetParentObject(child);
        }

        return false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = GetParentObject(child);
        }

        return null;
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _isPanning || _isLoupeActive || _isRoiDragging)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject dependencyObject)
        {
            return;
        }

        if (IsVisualDescendantOf(dependencyObject, PreviewImage))
        {
            return;
        }

        if (FindVisualAncestor<System.Windows.Controls.Primitives.ButtonBase>(dependencyObject) is null
            && FindVisualAncestor<Slider>(dependencyObject) is null
            && FindVisualAncestor<System.Windows.Controls.ComboBox>(dependencyObject) is null
            && FindVisualAncestor<Expander>(dependencyObject) is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(RestoreKeyboardFocusToWindow, DispatcherPriority.Input);
    }

    private void RestoreKeyboardFocusToWindow()
    {
        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        Focus();
        Keyboard.Focus(this);
    }

    private void FrameSlider_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawCacheIndicators();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FramePreviewViewModel.FramePositionBatchUpdated))
        {
            // FramePosition batch: all position fields were updated together; redraw once.
            RedrawCacheIndicators();
            return;
        }

        if (e.PropertyName is nameof(FramePreviewViewModel.FrameSliderValue)
            or nameof(FramePreviewViewModel.FrameCount)
            or nameof(FramePreviewViewModel.CachedFrameIndices)
            or nameof(FramePreviewViewModel.FrameStateChanged))
        {
            // Skip individual redraws while UpdateFramePosition is batching;
            // the final FramePositionBatchUpdated event will trigger a single redraw.
            if (!_vm.IsBatchingFramePosition)
            {
                RedrawCacheIndicators();
            }
            return;
        }

        if (e.PropertyName is nameof(FramePreviewViewModel.IsRoiOverlayVisible)
            or nameof(FramePreviewViewModel.Zoom)
            or nameof(FramePreviewViewModel.CurrentManualRoi))
        {
            UpdateRoiOverlay();
        }

        if (e.PropertyName is nameof(FramePreviewViewModel.IsStarDebugOverlayVisible)
            or nameof(FramePreviewViewModel.Zoom)
            or nameof(FramePreviewViewModel.Image))
        {
            UpdateStarDebugOverlay();
        }

        if (e.PropertyName is nameof(FramePreviewViewModel.IsCurvatureViewVisible)
            or nameof(FramePreviewViewModel.Image))
        {
            UpdateCurvatureView();
        }
    }

    private void RedrawCacheIndicators()
    {
        if (!IsLoaded)
        {
            return;
        }

        CacheIndicatorCanvas.Children.Clear();

        var frameCount = _vm.FrameCount;
        var height = Math.Max(0.0, FrameSlider.ActualHeight - 8.0);
        if (frameCount <= 0 || height <= 0)
        {
            return;
        }

        CacheIndicatorCanvas.Height = height;
        var currentIndex = Math.Clamp((int)Math.Round(_vm.FrameSliderValue), 0, Math.Max(0, frameCount - 1));
        var span = Math.Max(1.0, height - 2.0);
        var markerHeight = Math.Clamp(height / Math.Max(1, frameCount), 2.0, 6.0);

        var cachedSet = new HashSet<int>(_vm.CachedFrameIndices);
        var frameData = _vm.GetVisibleFrameData();

        // Determine score range for normalization
        double scoreMin = double.MaxValue, scoreMax = double.MinValue;
        for (var i = 0; i < frameData.Count; i++)
        {
            if (frameData[i].Score > 0)
            {
                scoreMin = Math.Min(scoreMin, frameData[i].Score);
                scoreMax = Math.Max(scoreMax, frameData[i].Score);
            }
        }
        var scoreRange = (scoreMax > scoreMin) ? (scoreMax - scoreMin) : 1.0;

        for (var sliderIndex = 0; sliderIndex < frameCount; sliderIndex++)
        {
            var y = frameCount == 1
                ? span * 0.5
                : (sliderIndex / (double)(frameCount - 1)) * span;
            var top = Math.Clamp(y - (markerHeight / 2.0), 0.0, Math.Max(0.0, height - markerHeight));

            var isCurrent = sliderIndex == currentIndex;
            var isCached = cachedSet.Contains(sliderIndex);

            // Score-driven fill color (green → yellow → red)
            var fillColor = ScoreMidColor;
            if (sliderIndex < frameData.Count && frameData[sliderIndex].Score > 0)
            {
                var t = Math.Clamp((frameData[sliderIndex].Score - scoreMin) / scoreRange, 0.0, 1.0);
                fillColor = t >= 0.5
                    ? LerpColor(ScoreMidColor, ScoreHighColor, (t - 0.5) * 2.0)
                    : LerpColor(ScoreLowColor, ScoreMidColor, t * 2.0);
            }

            const double borderSize = 2.0;
            var markerWidth = isCurrent ? 8.0 : 6.0;
            var left = isCurrent ? 0.0 : 1.0;
            var capturedIndex = sliderIndex; // capture for lambda

            // Transparent hit-test overlay covering the full marker area (including border)
            // so clicks always register regardless of which child element is on top
            var hitArea = new System.Windows.Shapes.Rectangle
            {
                Width = markerWidth + borderSize * 2,
                Height = Math.Max(markerHeight + borderSize * 2, 8.0),
                Fill = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Frame {capturedIndex + 1}"
            };
            hitArea.MouseLeftButtonUp += (_, _) => _ = _vm.NavigateToIndexAsync(capturedIndex);
            Canvas.SetTop(hitArea, top - borderSize);
            Canvas.SetLeft(hitArea, left - borderSize);

            // Blue border drawn as a background rect that peeks out behind the fill rect
            if (isCached)
            {
                var border = new System.Windows.Shapes.Rectangle
                {
                    Width = markerWidth + borderSize * 2,
                    Height = markerHeight + borderSize * 2,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = CacheBorderBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetTop(border, top - borderSize);
                Canvas.SetLeft(border, left - borderSize);
                CacheIndicatorCanvas.Children.Add(border);
            }

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = markerWidth,
                Height = markerHeight,
                RadiusX = 1,
                RadiusY = 1,
                Fill = isCurrent
                    ? ActiveFrameBrush
                    : new SolidColorBrush(fillColor),
                IsHitTestVisible = false
            };

            Canvas.SetTop(rect, top);
            Canvas.SetLeft(rect, left);
            CacheIndicatorCanvas.Children.Add(rect);

            // Strike-through for rejected frames
            bool isRejected = sliderIndex < frameData.Count && frameData[sliderIndex].IsRejected;
            if (isRejected && markerHeight >= 2.0)
            {
                var midY = top + markerHeight / 2.0;
                var strike = new System.Windows.Shapes.Line
                {
                    X1 = left - 1.0,
                    Y1 = midY,
                    X2 = left + markerWidth + 1.0,
                    Y2 = midY,
                    Stroke = System.Windows.Media.Brushes.White,
                    StrokeThickness = 2.0,
                    Opacity = 1.0,
                    IsHitTestVisible = false
                };
                CacheIndicatorCanvas.Children.Add(strike);
            }

            // Add hit area last so it sits on top of all visual layers
            CacheIndicatorCanvas.Children.Add(hitArea);
        }
    }

    private static System.Windows.Media.Color LerpColor(
        System.Windows.Media.Color from,
        System.Windows.Media.Color to,
        double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return System.Windows.Media.Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
    }

    protected override async void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape && _isRoiDragging)
        {
            e.Handled = true;
            CancelRoiDrag();
            return;
        }

        if (e.Key == Key.Left)
        {
            e.Handled = true;
            QueueKeyboardNavigation(-1);
            return;
        }

        if (e.Key == Key.Right)
        {
            e.Handled = true;
            QueueKeyboardNavigation(1);
            return;
        }

        if (e.Key == Key.R)
        {
            e.Handled = true;
            _vm.ToggleReject();
        }

        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _vm.IsStarDebugOverlayVisible = !_vm.IsStarDebugOverlayVisible;
            return;
        }

        if (e.Key == Key.Space)
        {
            e.Handled = true;
            PlayButton.IsChecked = !PlayButton.IsChecked;
        }
    }

    private void QueueKeyboardNavigation(int direction)
    {
        if (direction == 0 || _vm.FrameCount <= 0)
        {
            return;
        }

        var baseIndex = _queuedKeyboardNavigationIndex ?? _activeKeyboardNavigationIndex ?? _vm.CurrentFrameIndex;
        var targetIndex = Math.Clamp(baseIndex + direction, 0, _vm.FrameCount - 1);
        if (targetIndex == baseIndex && targetIndex == _vm.CurrentFrameIndex)
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
                if (targetIndex == _vm.CurrentFrameIndex)
                {
                    continue;
                }

                _activeKeyboardNavigationIndex = targetIndex;
                try
                {
                    await _vm.NavigateToIndexAsync(targetIndex);
                }
                finally
                {
                    _activeKeyboardNavigationIndex = null;
                }
            }
        }
        finally
        {
            _isKeyboardNavigationInProgress = false;
        }
    }
}
