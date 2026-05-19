using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using blink_o_mat.Infrastructure;
using blink_o_mat.Models;
using WpfPoint = System.Windows.Point;

namespace blink_o_mat.ViewModels;

public sealed class FramePreviewViewModel : INotifyPropertyChanged
{
    private readonly Func<double> _getStfShadows;
    private readonly Action<double> _setStfShadows;
    private readonly Func<double> _getStfMidtones;
    private readonly Action<double> _setStfMidtones;
    private readonly Func<double> _getStfHighlights;
    private readonly Action<double> _setStfHighlights;
    private readonly Action _applyAutoStretch;
    private readonly Action<WpfPoint> _setManualRoi;
    private readonly Func<RoiBias> _getRoiBias;
    private readonly Action<RoiBias> _setRoiBias;
    private readonly Func<int, Task> _navigate;
    private readonly Func<int, Task> _navigateToIndex;
    private readonly Action _toggleReject;
    private readonly Func<bool> _getSkipRejected;
    private readonly Action<bool> _setSkipRejected;
    private readonly Action _beginInteractiveStretch;
    private readonly Action _endInteractiveStretch;
    private FrameItem _item;
    private BitmapSource? _image;
    private double _zoom = 1.0;
    private double _frameSliderValue;
    private int _frameCount;
    private bool _isSynchronizingFrameSlider;
    private string? _previewStatusMessage;

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

    public double StfShadows
    {
        get => _getStfShadows();
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_getStfShadows() - clamped) < 0.0001) return;
            _setStfShadows(clamped);
            OnPropertyChanged();
        }
    }

    public double StfMidtones
    {
        get => _getStfMidtones();
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_getStfMidtones() - clamped) < 0.0001) return;
            _setStfMidtones(clamped);
            OnPropertyChanged();
        }
    }

    public double StfHighlights
    {
        get => _getStfHighlights();
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_getStfHighlights() - clamped) < 0.0001) return;
            _setStfHighlights(clamped);
            OnPropertyChanged();
        }
    }

    public ICommand AutoStretchCommand { get; }

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

    public int FrameCount => _frameCount;

    public int CurrentFrameIndex => Math.Clamp((int)Math.Round(_frameSliderValue), 0, Math.Max(0, _frameCount - 1));

    public ObservableCollection<int> CachedFrameIndices { get; } = [];

    public string FramePositionText
    {
        get
        {
            if (_frameCount <= 0)
            {
                return "0 / 0";
            }

            var current = Math.Clamp(CurrentFrameIndex + 1, 1, _frameCount);
            return $"{current} / {_frameCount}";
        }
    }

    public string? PreviewStatusMessage
    {
        get => _previewStatusMessage;
        private set
        {
            if (string.Equals(_previewStatusMessage, value, StringComparison.Ordinal)) return;
            _previewStatusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPreviewStatusVisible));
        }
    }

    public bool IsPreviewStatusVisible => !string.IsNullOrWhiteSpace(PreviewStatusMessage);

    public FramePreviewViewModel(
        FrameItem item,
        Func<double> getStfShadows,
        Action<double> setStfShadows,
        Func<double> getStfMidtones,
        Action<double> setStfMidtones,
        Func<double> getStfHighlights,
        Action<double> setStfHighlights,
        Action applyAutoStretch,
        Func<RoiBias> getRoiBias,
        Action<RoiBias> setRoiBias,
        Action beginInteractiveStretch,
        Action endInteractiveStretch,
        Action<WpfPoint> setManualRoi,
        Func<int, Task> navigate,
        Func<int, Task> navigateToIndex,
        Action toggleReject,
        Func<bool> getSkipRejected,
        Action<bool> setSkipRejected)
    {
        _item = item;
        _getStfShadows = getStfShadows;
        _setStfShadows = setStfShadows;
        _getStfMidtones = getStfMidtones;
        _setStfMidtones = setStfMidtones;
        _getStfHighlights = getStfHighlights;
        _setStfHighlights = setStfHighlights;
        _applyAutoStretch = applyAutoStretch;
        _getRoiBias = getRoiBias;
        _setRoiBias = setRoiBias;
        _beginInteractiveStretch = beginInteractiveStretch;
        _endInteractiveStretch = endInteractiveStretch;
        _setManualRoi = setManualRoi;
        _navigate = navigate;
        _navigateToIndex = navigateToIndex;
        _toggleReject = toggleReject;
        _getSkipRejected = getSkipRejected;
        _setSkipRejected = setSkipRejected;
        _image = null;
        AutoStretchCommand = new RelayCommand(_ => ApplyAutoStretchAndRefresh());
    }

    public void SetManualRoi(WpfPoint normalizedPoint)
    {
        _setManualRoi(normalizedPoint);
    }

    public async Task NavigateAsync(int direction)
    {
        await _navigate(direction);
    }

    public async Task NavigateToIndexAsync(int index)
    {
        await _navigateToIndex(index);
    }

    public void ToggleReject()
    {
        _toggleReject();
    }

    public void BeginInteractiveStretch()
    {
        _beginInteractiveStretch();
    }

    public void EndInteractiveStretch()
    {
        _endInteractiveStretch();
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
        OnPropertyChanged(nameof(FrameCount));
        OnPropertyChanged(nameof(FrameSliderMaximum));
        OnPropertyChanged(nameof(FrameSliderValue));
        OnPropertyChanged(nameof(FramePositionText));
        _isSynchronizingFrameSlider = false;
    }

    public void UpdateCachedFrameIndices(IEnumerable<int> cachedIndices)
    {
        CachedFrameIndices.Clear();
        foreach (var index in cachedIndices)
        {
            CachedFrameIndices.Add(index);
        }

        OnPropertyChanged(nameof(CachedFrameIndices));
    }

    public void SetPreviewStatus(string? message)
    {
        PreviewStatusMessage = string.IsNullOrWhiteSpace(message) ? null : message;
    }

    private void ApplyAutoStretchAndRefresh()
    {
        _applyAutoStretch();
        OnPropertyChanged(nameof(StfShadows));
        OnPropertyChanged(nameof(StfMidtones));
        OnPropertyChanged(nameof(StfHighlights));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
