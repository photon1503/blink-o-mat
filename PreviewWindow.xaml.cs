using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using blink_o_mat.ViewModels;
using WpfPoint = System.Windows.Point;

namespace blink_o_mat;

public partial class PreviewWindow : Window
{
    private readonly FramePreviewViewModel _vm;
    private bool _hasInitializedView;

    public PreviewWindow(FramePreviewViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
        _vm.PropertyChanged += Vm_PropertyChanged;
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
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || PreviewImage.Source is null)
        {
            return;
        }

        var point = e.GetPosition(PreviewImage);
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            return;
        }

        var normalized = new WpfPoint(
            point.X / PreviewImage.ActualWidth,
            point.Y / PreviewImage.ActualHeight);

        _vm.SetManualRoi(normalized);
    }

    private void FrameSlider_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawCacheIndicators();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FramePreviewViewModel.FrameSliderValue)
            or nameof(FramePreviewViewModel.FrameCount)
            or nameof(FramePreviewViewModel.CachedFrameIndices))
        {
            RedrawCacheIndicators();
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
                Fill = cachedIndex == currentIndex
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x39, 0xD3, 0x53))
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
            await _vm.NavigateAsync(-1);
            return;
        }

        if (e.Key == Key.Right)
        {
            e.Handled = true;
            await _vm.NavigateAsync(1);
            return;
        }

        if (e.Key == Key.R)
        {
            e.Handled = true;
            _vm.ToggleReject();
        }
    }
}
