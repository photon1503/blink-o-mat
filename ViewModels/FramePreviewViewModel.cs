using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using blink_o_mat.Models;
using WpfPoint = System.Windows.Point;

namespace blink_o_mat.ViewModels;

public sealed class FramePreviewViewModel : INotifyPropertyChanged
{
    private readonly Action<double> _setStretch;
    private readonly Func<double> _getStretch;
    private readonly Action<WpfPoint> _setManualRoi;
    private readonly Func<RoiBias> _getRoiBias;
    private readonly Action<RoiBias> _setRoiBias;
    private readonly Func<StretchMode> _getStretchMode;
    private readonly Action<StretchMode> _setStretchMode;
    private readonly Func<int, Task> _navigate;
    private readonly Action _toggleReject;
    private readonly Func<bool> _getSkipRejected;
    private readonly Action<bool> _setSkipRejected;
    private FrameItem _item;
    private BitmapSource? _image;
    private double _zoom = 1.0;

    public FrameItem Item
    {
        get => _item;
        private set
        {
            if (ReferenceEquals(_item, value)) return;
            _item = value;
            OnPropertyChanged();
        }
    }

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
            var clamped = Math.Clamp(value, 0.25, 5.0);
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

    public RoiBias RoiBias
    {
        get => _getRoiBias();
        set
        {
            if (_getRoiBias() == value) return;
            _setRoiBias(value);
            OnPropertyChanged();
        }
    }

    public Array RoiBiasOptions { get; } = Enum.GetValues(typeof(RoiBias));

    public StretchMode StretchMode
    {
        get => _getStretchMode();
        set
        {
            if (_getStretchMode() == value) return;
            _setStretchMode(value);
            OnPropertyChanged();
        }
    }

    public Array StretchModeOptions { get; } = Enum.GetValues(typeof(StretchMode));

    public bool SkipRejectedInPreview
    {
        get => _getSkipRejected();
        set
        {
            if (_getSkipRejected() == value) return;
            _setSkipRejected(value);
            OnPropertyChanged();
        }
    }

    public FramePreviewViewModel(
        FrameItem item,
        Func<double> getStretch,
        Action<double> setStretch,
        Func<RoiBias> getRoiBias,
        Action<RoiBias> setRoiBias,
        Func<StretchMode> getStretchMode,
        Action<StretchMode> setStretchMode,
        Action<WpfPoint> setManualRoi,
        Func<int, Task> navigate,
        Action toggleReject,
        Func<bool> getSkipRejected,
        Action<bool> setSkipRejected)
    {
        _item = item;
        _getStretch = getStretch;
        _setStretch = setStretch;
        _getRoiBias = getRoiBias;
        _setRoiBias = setRoiBias;
        _getStretchMode = getStretchMode;
        _setStretchMode = setStretchMode;
        _setManualRoi = setManualRoi;
        _navigate = navigate;
        _toggleReject = toggleReject;
        _getSkipRejected = getSkipRejected;
        _setSkipRejected = setSkipRejected;
        _image = null;
    }

    public void SetManualRoi(WpfPoint normalizedPoint)
    {
        _setManualRoi(normalizedPoint);
    }

    public async Task NavigateAsync(int direction)
    {
        await _navigate(direction);
    }

    public void ToggleReject()
    {
        _toggleReject();
    }

    public void SetItem(FrameItem item)
    {
        Item = item;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
