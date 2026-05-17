using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using blink_o_mat.ViewModels;
using WpfPoint = System.Windows.Point;

namespace blink_o_mat;

public partial class PreviewWindow : Window
{
    private readonly FramePreviewViewModel _vm;

    public PreviewWindow(FramePreviewViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
        Loaded += (_, _) => FitToView();
    }

    public void RefreshImage(BitmapSource image)
    {
        _vm.Image = null;
        _vm.Image = image;
        FitToView();
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
