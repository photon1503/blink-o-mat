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
    private readonly Func<bool> _getUseGlobalTargetBackground;
    private readonly Action<bool> _setUseGlobalTargetBackground;
    private readonly Func<double> _getTargetBackground;
    private readonly Action<double> _setTargetBackground;
    private readonly Func<int, Task> _navigate;
    private readonly Func<int, Task> _navigateToIndex;
    private readonly Action _toggleReject;
    private readonly Func<bool> _getSkipRejected;
    private readonly Action<bool> _setSkipRejected;
    private FrameItem _item;
    private BitmapSource? _image;
    private double _zoom = 1.0;
    private double _frameSliderValue;
    private int _frameCount;
    private bool _isSynchronizingFrameSlider;

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

    public bool UseGlobalTargetBackground
    {
        get => _getUseGlobalTargetBackground();
        set
        {
            if (_getUseGlobalTargetBackground() == value) return;
            _setUseGlobalTargetBackground(value);
            OnPropertyChanged();
        }
    }

    public double TargetBackground
    {
        get => _getTargetBackground();
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.75);
            if (Math.Abs(_getTargetBackground() - clamped) < 0.0001) return;
            _setTargetBackground(clamped);
            OnPropertyChanged();
        }
    }

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

    public double FrameSliderValue
    {
        get => _frameSliderValue;
        set
        {
            var clamped = Math.Clamp(value, 0.0, FrameSliderMaximum);
            if (Math.Abs(_frameSliderValue - clamped) < 0.0001) return;
            _frameSliderValue = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FramePositionText));

            if (_isSynchronizingFrameSlider)
            {
                return;
            }

            _ = _navigateToIndex((int)Math.Round(clamped));
        }
    }

    public double FrameSliderMaximum => Math.Max(0, _frameCount - 1);

    public string FramePositionText
    {
        get
        {
            if (_frameCount <= 0)
            {
                return "0 / 0";
            }

            var current = Math.Clamp((int)Math.Round(_frameSliderValue) + 1, 1, _frameCount);
            return $"{current} / {_frameCount}";
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
        Func<bool> getUseGlobalTargetBackground,
        Action<bool> setUseGlobalTargetBackground,
        Func<double> getTargetBackground,
        Action<double> setTargetBackground,
        Action<WpfPoint> setManualRoi,
        Func<int, Task> navigate,
        Func<int, Task> navigateToIndex,
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
        _getUseGlobalTargetBackground = getUseGlobalTargetBackground;
        _setUseGlobalTargetBackground = setUseGlobalTargetBackground;
        _getTargetBackground = getTargetBackground;
        _setTargetBackground = setTargetBackground;
        _setManualRoi = setManualRoi;
        _navigate = navigate;
        _navigateToIndex = navigateToIndex;
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

    public void UpdateFramePosition(int currentIndex, int frameCount)
    {
        _isSynchronizingFrameSlider = true;
        _frameCount = Math.Max(0, frameCount);
        _frameSliderValue = Math.Clamp(currentIndex, 0, Math.Max(0, _frameCount - 1));
        OnPropertyChanged(nameof(FrameSliderMaximum));
        OnPropertyChanged(nameof(FrameSliderValue));
        OnPropertyChanged(nameof(FramePositionText));
        _isSynchronizingFrameSlider = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
