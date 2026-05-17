using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using blink_o_mat.Models;

namespace blink_o_mat.ViewModels;

public sealed class FramePreviewViewModel : INotifyPropertyChanged
{
    private readonly Action<double> _setStretch;
    private readonly Func<double> _getStretch;
    private BitmapSource? _image;
    private double _zoom = 1.0;

    public FrameItem Item { get; }

    public BitmapSource? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value)) return;
            _image = value;
            OnPropertyChanged();
        }
    }

    public double Stretch
    {
        get => _getStretch();
        set
        {
            var clamped = Math.Clamp(value, 0.25, 3.0);
            if (Math.Abs(clamped - _getStretch()) < 0.0001) return;
            _setStretch(clamped);
            OnPropertyChanged();
        }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 8.0);
            if (Math.Abs(_zoom - clamped) < 0.0001) return;
            _zoom = clamped;
            OnPropertyChanged();
        }
    }

    public FramePreviewViewModel(FrameItem item, Func<double> getStretch, Action<double> setStretch)
    {
        Item = item;
        _getStretch = getStretch;
        _setStretch = setStretch;
        _image = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
