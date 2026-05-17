using System.Windows;
using System.Windows.Input;
using blink_o_mat.ViewModels;

namespace blink_o_mat;

public partial class PreviewWindow : Window
{
    private readonly FramePreviewViewModel _vm;

    public PreviewWindow(FramePreviewViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
    }

    public void RefreshImagePath(string path)
    {
        _vm.ImagePath = string.Empty;
        _vm.ImagePath = path;
    }

    private void Smaller_Click(object sender, RoutedEventArgs e)
    {
        _vm.Zoom = Math.Max(0.1, _vm.Zoom / 1.25);
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
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
}
