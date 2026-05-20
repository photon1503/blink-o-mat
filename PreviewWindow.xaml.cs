using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using blink_o_mat.ViewModels;
using WpfPoint = System.Windows.Point;

namespace blink_o_mat;

public partial class PreviewWindow : Window
{
    private static readonly System.Windows.Media.Brush CachedFrameBrush;
    private static readonly System.Windows.Media.Brush ActiveFrameBrush;

    static PreviewWindow()
    {
        CachedFrameBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x39, 0xD3, 0x53));
        CachedFrameBrush.Freeze();
        ActiveFrameBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
        ActiveFrameBrush.Freeze();
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
    private int? _queuedKeyboardNavigationIndex;
    private const int LoupeSampleSize = 31;
    private const int LoupeZoomScale = 4;

    public PreviewWindow(FramePreviewViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
        _vm.PropertyChanged += Vm_PropertyChanged;
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Window_PreviewMouseUp), true);
        Loaded += (_, _) =>
        {
            FitToView();
            RedrawCacheIndicators();
            _hasInitializedView = true;
        };
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

    private void StfSlider_Loaded(object sender, RoutedEventArgs e)
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

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        if (e.Delta > 0)
        {
            _vm.Zoom = Math.Min(8.0, _vm.Zoom * 1.1);
        }
        else
        {
            _vm.Zoom = Math.Max(0.1, _vm.Zoom / 1.1);
        }
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
            var point = e.GetPosition(PreviewImage);
            if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
            {
                return;
            }

            var normalized = new WpfPoint(
                point.X / PreviewImage.ActualWidth,
                point.Y / PreviewImage.ActualHeight);

            _vm.SetManualRoi(normalized);
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

    private static bool IsVisualDescendantOf(DependencyObject? child, DependencyObject ancestor)
    {
        while (child is not null)
        {
            if (ReferenceEquals(child, ancestor))
            {
                return true;
            }

            child = VisualTreeHelper.GetParent(child);
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

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _isPanning || _isLoupeActive)
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
            or nameof(FramePreviewViewModel.CachedFrameIndices))
        {
            // Skip individual redraws while UpdateFramePosition is batching;
            // the final FramePositionBatchUpdated event will trigger a single redraw.
            if (!_vm.IsBatchingFramePosition)
            {
                RedrawCacheIndicators();
            }
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

        foreach (var cachedIndex in _vm.CachedFrameIndices)
        {
            if (cachedIndex < 0 || cachedIndex >= frameCount)
            {
                continue;
            }

            var y = frameCount == 1
                ? span * 0.5
                : (cachedIndex / (double)(frameCount - 1)) * span;

            var marker = new System.Windows.Shapes.Rectangle
            {
                Width = cachedIndex == currentIndex ? 8 : 6,
                Height = markerHeight,
                RadiusX = 1,
                RadiusY = 1,
                Fill = cachedIndex == currentIndex ? ActiveFrameBrush : CachedFrameBrush
            };

            Canvas.SetTop(marker, Math.Clamp(y - (markerHeight / 2.0), 0.0, Math.Max(0.0, height - markerHeight)));
            Canvas.SetLeft(marker, cachedIndex == currentIndex ? 0.0 : 1.0);
            CacheIndicatorCanvas.Children.Add(marker);
        }
    }

    protected override async void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

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
