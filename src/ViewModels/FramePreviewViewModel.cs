using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using blink_o_mat.Infrastructure;
using blink_o_mat.Models;

namespace blink_o_mat.ViewModels;

public sealed class FramePreviewViewModel : INotifyPropertyChanged
{
    private readonly Func<double> _getStfShadows;
    private readonly Action<double> _setStfShadows;
    private readonly Func<double> _getStfMidtones;
    private readonly Action<double> _setStfMidtones;
    private readonly Func<double> _getStfHighlights;
    private readonly Action<double> _setStfHighlights;
    private readonly Func<double> _getStfTargetBackground;
    private readonly Action<double> _setStfTargetBackground;
    private readonly Action _applyAutoStretch;
    private readonly Action<(double Left, double Top, double Width, double Height)> _setManualRoi;
    private readonly Func<(double Left, double Top, double Width, double Height)?> _getCurrentRoi;
    private readonly Func<int, Task> _navigate;
    private readonly Func<int, Task> _navigateToIndex;
    private readonly Action _toggleReject;
    private readonly Func<bool> _getShowAccepted;
    private readonly Action<bool> _setShowAccepted;
    private readonly Func<bool> _getShowRejected;
    private readonly Action<bool> _setShowRejected;
    private readonly ObservableCollection<FilterChipViewModel> _filterChips;
    private readonly Func<IReadOnlyList<int>> _getVisibleFrameIndices;
    private readonly Func<IReadOnlyList<(double Score, bool IsRejected)>> _getVisibleFrameData;
    private readonly Action _refreshVisibleFrames;
    private readonly Action _beginInteractiveStretch;
    private readonly Action _endInteractiveStretch;
    private readonly Func<bool> _getAlignmentEnabled;
    private readonly Action<bool> _setAlignmentEnabled;
    private FrameItem _item;
    private BitmapSource? _image;
    private double _zoom = 1.0;
    private double _frameSliderValue;
    private int _frameCount;
    private bool _isSynchronizingFrameSlider;
    private string? _previewStatusMessage;
    private int _selectedVisibleFrameIndex;
    private bool _isRoiOverlayVisible;
    private bool _isStarDebugOverlayVisible;

    public bool IsStarDebugOverlayVisible
    {
        get => _isStarDebugOverlayVisible;
        set
        {
            if (_isStarDebugOverlayVisible == value) return;
            _isStarDebugOverlayVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsRoiOverlayVisible
    {
        get => _isRoiOverlayVisible;
        set
        {
            if (_isRoiOverlayVisible == value) return;
            _isRoiOverlayVisible = value;
            OnPropertyChanged();
        }
    }

    public (double Left, double Top, double Width, double Height)? CurrentManualRoi => _getCurrentRoi();

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

    public ObservableCollection<FilterChipViewModel> FilterChips => _filterChips;

    public bool HasFilterChips => _filterChips.Count > 0;

    public bool ShowAccepted
    {
        get => _getShowAccepted();
        set
        {
            if (_getShowAccepted() == value) return;
            _setShowAccepted(value);
            OnPropertyChanged();
        }
    }

    public bool ShowRejected
    {
        get => _getShowRejected();
        set
        {
            if (_getShowRejected() == value) return;
            _setShowRejected(value);
            OnPropertyChanged();
        }
    }

    public bool IsAlignmentEnabled
    {
        get => _getAlignmentEnabled();
        set
        {
            if (_getAlignmentEnabled() == value) return;
            _setAlignmentEnabled(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Raise <see cref="IsAlignmentEnabled"/> change notification when the
    /// underlying alignment state is toggled from outside this view-model
    /// (e.g. from the main window's Align toggle).</summary>
    public void NotifyAlignmentChanged() => OnPropertyChanged(nameof(IsAlignmentEnabled));

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

    public double StfTargetBackground
    {
        get => _getStfTargetBackground();
        set
        {
            var clamped = Math.Clamp(value, 0.01, 0.5);
            if (Math.Abs(_getStfTargetBackground() - clamped) < 0.001) return;
            _setStfTargetBackground(clamped);
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

            var visibleIndices = _getVisibleFrameIndices();
            if (visibleIndices.Count == 0)
            {
                return;
            }

            var visibleIndex = Math.Clamp((int)Math.Round(clamped), 0, visibleIndices.Count - 1);
            _ = _navigateToIndex(visibleIndices[visibleIndex]);
        }
    }

    public double FrameSliderMaximum => Math.Max(0, _frameCount - 1);

    public int FrameCount => _frameCount;

    public int CurrentFrameIndex => Math.Clamp(_selectedVisibleFrameIndex, 0, Math.Max(0, _frameCount - 1));

    public ObservableCollection<int> CachedFrameIndices { get; } = [];
    public int CachedFrameCount => CachedFrameIndices.Count;

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

    /// <summary>Raised when per-frame score/rejection state changes, so the slider can redraw.</summary>
    public void NotifyFrameStateChanged() => OnPropertyChanged(nameof(FrameStateChanged));
    public bool FrameStateChanged => false; // sentinel – only used for property-change notification

    public FramePreviewViewModel(
        FrameItem item,
        Func<double> getStfShadows,
        Action<double> setStfShadows,
        Func<double> getStfMidtones,
        Action<double> setStfMidtones,
        Func<double> getStfHighlights,
        Action<double> setStfHighlights,
        Func<double> getStfTargetBackground,
        Action<double> setStfTargetBackground,
        Action applyAutoStretch,
        Action beginInteractiveStretch,
        Action endInteractiveStretch,
        Action<(double Left, double Top, double Width, double Height)> setManualRoi,
        Func<(double Left, double Top, double Width, double Height)?> getCurrentRoi,
        Func<int, Task> navigate,
        Func<int, Task> navigateToIndex,
        Action toggleReject,
        Func<bool> getShowAccepted,
        Action<bool> setShowAccepted,
        Func<bool> getShowRejected,
        Action<bool> setShowRejected,
        ObservableCollection<FilterChipViewModel> filterChips,
        Func<IReadOnlyList<int>> getVisibleFrameIndices,
        Func<IReadOnlyList<(double Score, bool IsRejected)>> getVisibleFrameData,
        Action refreshVisibleFrames,
        Func<bool> getAlignmentEnabled,
        Action<bool> setAlignmentEnabled)
    {
        _item = item;
        _getStfShadows = getStfShadows;
        _setStfShadows = setStfShadows;
        _getStfMidtones = getStfMidtones;
        _setStfMidtones = setStfMidtones;
        _getStfHighlights = getStfHighlights;
        _setStfHighlights = setStfHighlights;
        _getStfTargetBackground = getStfTargetBackground;
        _setStfTargetBackground = setStfTargetBackground;
        _applyAutoStretch = applyAutoStretch;
        _beginInteractiveStretch = beginInteractiveStretch;
        _endInteractiveStretch = endInteractiveStretch;
        _setManualRoi = setManualRoi;
        _getCurrentRoi = getCurrentRoi;
        _navigate = navigate;
        _navigateToIndex = navigateToIndex;
        _toggleReject = toggleReject;
        _getShowAccepted = getShowAccepted;
        _setShowAccepted = setShowAccepted;
        _getShowRejected = getShowRejected;
        _setShowRejected = setShowRejected;
        _filterChips = filterChips;
        _getVisibleFrameIndices = getVisibleFrameIndices;
        _getVisibleFrameData = getVisibleFrameData;
        _refreshVisibleFrames = refreshVisibleFrames;
        _getAlignmentEnabled = getAlignmentEnabled;
        _setAlignmentEnabled = setAlignmentEnabled;
        _image = null;
        AutoStretchCommand = new RelayCommand(_ => ApplyAutoStretchAndRefresh());
        _filterChips.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFilterChips));
    }

    public void SetManualRoi((double Left, double Top, double Width, double Height) rect)
    {
        _setManualRoi(rect);
        OnPropertyChanged(nameof(CurrentManualRoi));
    }

    public async Task NavigateAsync(int direction)
    {
        await _navigate(direction);
    }

    public async Task NavigateToIndexAsync(int index)
    {
        var visibleIndices = _getVisibleFrameIndices();
        if (visibleIndices.Count == 0)
        {
            return;
        }

        var visibleIndex = Math.Clamp(index, 0, visibleIndices.Count - 1);
        await _navigateToIndex(visibleIndices[visibleIndex]);
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

    public IReadOnlyList<(double Score, bool IsRejected)> GetVisibleFrameData()
        => _getVisibleFrameData();

    public void SetItem(FrameItem item)
    {
        Item = item;
    }

    public void UpdateFramePosition(int currentVisibleIndex, int visibleFrameCount)
    {
        _isSynchronizingFrameSlider = true;
        _isBatchingFramePosition = true;
        _frameCount = Math.Max(0, visibleFrameCount);
        _selectedVisibleFrameIndex = Math.Clamp(currentVisibleIndex, 0, Math.Max(0, _frameCount - 1));
        _frameSliderValue = _selectedVisibleFrameIndex;
        OnPropertyChanged(nameof(FrameCount));
        OnPropertyChanged(nameof(FrameSliderMaximum));
        OnPropertyChanged(nameof(FrameSliderValue));
        OnPropertyChanged(nameof(CurrentFrameIndex));
        OnPropertyChanged(nameof(FramePositionText));
        _isBatchingFramePosition = false;
        _isSynchronizingFrameSlider = false;
        // Signal a single batch-update event so the cache indicator redraws once.
        OnPropertyChanged(nameof(FramePositionBatchUpdated));
    }

    /// <summary>True while UpdateFramePosition is emitting its individual property changes.</summary>
    public bool IsBatchingFramePosition => _isBatchingFramePosition;
    private bool _isBatchingFramePosition;

    /// <summary>
    /// A synthetic property-changed token fired after all frame-position fields
    /// are updated together, allowing the view to perform a single redraw pass
    /// instead of one per individual property change.
    /// </summary>
    public object? FramePositionBatchUpdated => null;

    public void UpdateCachedFrameIndices(IEnumerable<int> cachedVisibleIndices)
    {
        CachedFrameIndices.Clear();
        foreach (var index in cachedVisibleIndices)
        {
            CachedFrameIndices.Add(index);
        }

        OnPropertyChanged(nameof(CachedFrameIndices));
        OnPropertyChanged(nameof(CachedFrameCount));
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
        OnPropertyChanged(nameof(StfTargetBackground));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
