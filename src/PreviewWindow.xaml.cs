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
            UpdateRoiOverlay();
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
